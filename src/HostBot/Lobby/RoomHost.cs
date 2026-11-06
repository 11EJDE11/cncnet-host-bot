using HostBot.Config;
using HostBot.Idents;
using HostBot.Irc;
using HostBot.Maps;
using HostBot.Storage;
using HostBot.Tunnels;

namespace HostBot.Lobby;

/// <summary>
/// One IRC connection and one game room per configured room, kept alive forever: reconnects
/// and recreates the room after any disconnect.
/// </summary>
public sealed class RoomHost
{
    private readonly BotConfig _cfg;
    private readonly TunnelList _tunnels;
    private readonly IdentPolicy _policy;
    private readonly MapPoolFile _pool;
    private readonly OptionLayout _layout;
    private readonly BotDb _db;
    private readonly bool _logLobbyChat;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    public RoomConfig Def { get; private set; }
    public GameRoom? Room { get; private set; }
    public IrcClient? Irc { get; private set; }
    public LobbyObserver? Observer { get; private set; }
    public string Status { get; private set; } = "stopped";
    public DateTime? StartedAt { get; private set; }
    public bool Running => _task is { IsCompleted: false };

    public RoomHost(RoomConfig def, BotConfig cfg, TunnelList tunnels, IdentPolicy policy, MapPoolFile pool, OptionLayout layout, BotDb db, bool logLobbyChat)
    {
        Def = def;
        _cfg = cfg;
        _tunnels = tunnels;
        _policy = policy;
        _pool = pool;
        _layout = layout;
        _db = db;
        _logLobbyChat = logLobbyChat;
    }

    public string Tag => $"host:{Def.Nick}";

    public void Start()
    {
        if (Running) return;
        StartedAt = DateTime.UtcNow;
        _task = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync(string reason)
    {
        _cts.Cancel();
        if (Room is not null)
            await Room.CloseAsync(reason);
        if (Irc is not null)
            await Irc.DisposeAsync();
        if (_task is not null)
            try { await _task; } catch { }
        Status = "stopped";
        Log.Info(Tag, $"stopped: {reason}");
    }

    /// <summary>Applies a changed definition; name/limits take effect on the next room recreation, policy settings immediately.</summary>
    public void Update(RoomConfig def)
    {
        Def = def;
        if (Room is { } room)
            room.Def = def;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            IrcClient? irc = null;
            try
            {
                Status = "connecting";
                string game = _cfg.Irc.Game;
                string ident = $"{game}.{BotIdent(Def.Nick)}";
                irc = new IrcClient(Def.Nick, ident, $"{_cfg.Irc.ClientVersion} {game} CnCNet".Trim(), 15,
                    _cfg.Irc.Server is { } s ? new[] { new IrcClient.ServerEndpoint(s, _cfg.Irc.Port ?? 6667) } : null)
                {
                    SendGap = TimeSpan.FromMilliseconds(Math.Max(100, _cfg.Irc.SendGapMs)),
                };
                Irc = irc;
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                irc.Disconnected += _ => disconnected.TrySetResult();

                string chatChannel = $"#cncnet-{game.ToLowerInvariant()}";
                string broadcastChannel = $"#cncnet-{game.ToLowerInvariant()}-games";
                Observer = new LobbyObserver(irc, broadcastChannel);
                if (_logLobbyChat)
                    irc.MessageReceived += LogLobbyChat;

                await irc.ConnectAsync(ct);
                irc.Queue($"JOIN {chatChannel} ra1-derp", 9);
                irc.Queue($"JOIN {broadcastChannel}", 9);

                Status = "learning client version";
                var dialect = await LearnDialectAsync(irc, broadcastChannel, ct);
                Log.Info(Tag, $"client dialect: revision {dialect.Revision}, version '{dialect.GameVersion}', {dialect.GameFieldCount} GAME fields");

                var room = new GameRoom(Def, irc, _tunnels, _policy, _pool, _layout, _db, dialect, game);
                Room = room;
                await room.OpenAsync(ct);
                Status = "hosting";
                failures = 0;

                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(ct))
                {
                    if (disconnected.Task.IsCompleted)
                        break;
                    room.Tick();
                    if (room.RecreateAt is { } at && at <= DateTime.UtcNow)
                    {
                        await room.CloseAsync("abandoned after launch", announceClosed: false);
                        var previous = room;
                        room = new GameRoom(Def, irc, _tunnels, _policy, _pool, _layout, _db, dialect, game);
                        room.InheritFormat(previous);
                        Room = room;
                        await room.OpenAsync(ct);
                    }
                }
                await room.CloseAsync("connection lost");
                Room = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Error(Tag, $"run failed: {ex.Message}");
                if (Room is not null)
                {
                    try { await Room.CloseAsync("error: " + ex.Message); } catch { }
                    Room = null;
                }
            }
            finally
            {
                if (irc is not null)
                {
                    try { await irc.DisposeAsync(); } catch { }
                    Irc = null;
                }
            }
            if (ct.IsCancellationRequested) break;
            int wait = Math.Min(300, 10 * (1 << Math.Min(failures, 5)));
            Status = $"reconnecting in {wait} s";
            Log.Info(Tag, Status);
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); } catch (OperationCanceledException) { break; }
        }
        Status = "stopped";
    }

    private async Task<ClientDialect> LearnDialectAsync(IrcClient irc, string broadcastChannel, CancellationToken ct)
    {
        var dialect = new ClientDialect
        {
            GameVersion = _cfg.Irc.ClientVersion,
            GameFieldCount = _cfg.Irc.GameBroadcastFields > 0 ? _cfg.Irc.GameBroadcastFields : 14,
            Revision = string.IsNullOrEmpty(_cfg.Irc.ProtocolRevision) ? "A" : _cfg.Irc.ProtocolRevision,
        };
        bool needVersion = string.IsNullOrEmpty(_cfg.Irc.ClientVersion);
        bool needFields = _cfg.Irc.GameBroadcastFields <= 0;
        bool needRevision = string.IsNullOrEmpty(_cfg.Irc.ProtocolRevision);
        if (!needVersion && !needFields && !needRevision)
            return dialect;

        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline && Observer!.SampleCount < 3)
            await Task.Delay(500, ct);

        var dominant = Observer!.Dominant();
        if (dominant is null)
        {
            if (needVersion)
                throw new InvalidOperationException("no GAME broadcasts seen on " + broadcastChannel + " and no clientVersion configured");
            Log.Warn(Tag, "no GAME broadcasts seen; assuming revision A with 14 fields");
            return dialect;
        }
        if (needVersion) dialect.GameVersion = dominant.Value.version;
        if (needFields) dialect.GameFieldCount = dominant.Value.fields;
        if (needRevision) dialect.Revision = dominant.Value.revision;
        return dialect;
    }

    private void LogLobbyChat(IrcMessage msg)
    {
        if (msg.Command != "PRIVMSG" || msg.Nick is null) return;
        string target = msg.Param(0);
        if (!target.StartsWith('#') || target.Contains("-game")) return;
        string text = msg.Trailing;
        if (text.StartsWith('\x01')) return;
        _db.LogChat(null, target, msg.Nick, msg.Ident, StripColor(text), outgoing: false);
    }

    private static string StripColor(string text)
    {
        if (text.Length >= 3 && text[0] == '\x03' && char.IsDigit(text[1]) && char.IsDigit(text[2]))
            return text[3..];
        return text;
    }

    /// <summary>Stable 6-hex ident per nick, so the bot's own ident never changes between runs.</summary>
    private static string BotIdent(string nick) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes("hostbot:" + nick)))[..6];
}
