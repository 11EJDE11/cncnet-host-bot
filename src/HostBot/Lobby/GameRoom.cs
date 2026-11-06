using System.Security.Cryptography;
using System.Text;
using HostBot.Config;
using HostBot.Idents;
using HostBot.Irc;
using HostBot.Maps;
using HostBot.Storage;
using HostBot.Tunnels;

namespace HostBot.Lobby;

public enum TunnelMode { V3Static = 0, V3Dynamic = 1, V2Legacy = 2 }

/// <summary>What the client population on the network speaks; learned from other hosts' GAME broadcasts.</summary>
public sealed class ClientDialect
{
    /// <summary>CNCNET_PROTOCOL_REVISION: first GAME field and the room topic ("A" for the 9.3.5 client, "B" in newer source).</summary>
    public string Revision { get; set; } = "A";
    public string GameVersion { get; set; } = "";
    /// <summary>Number of ';' fields in GAME (15 with the difficulty field, 14 without).</summary>
    public int GameFieldCount { get; set; } = 15;
    public bool GoHasDifficulty => GameFieldCount >= 15;
}

/// <summary>
/// Hosts one CnCNet game room as a non-playing host: creates the channel, broadcasts it,
/// admits or kicks joiners by ident policy, relays player options, and launches the game when
/// everyone is ready. Protocol details: docs/protocol/*.md.
/// </summary>
public sealed class GameRoom
{
    private const int LaunchNoticePriority = 0;
    /// <summary>Countdown, cancel and ready-warning lines jump ahead of the PO/GO traffic so they are not seconds late.</summary>
    private const int UrgentChatPriority = 5;

    /// <summary>Live settings; swapped by RoomHost.Update so policy and timing changes apply at once.</summary>
    public RoomConfig Def { get; set; }
    public string Tag => $"room:{Def.Nick}";

    private readonly IrcClient _irc;
    private readonly TunnelList _tunnels;
    private readonly IdentPolicy _policy;
    private readonly MapPoolFile _pool;
    private readonly OptionLayout _layout;
    private readonly BotDb _db;
    private readonly ClientDialect _dialect;
    private readonly string _game;           // "YR"
    private readonly string _chatChannel;    // #cncnet-yr
    private readonly string _broadcastChannel; // #cncnet-yr-games
    private readonly object _gate = new();
    private readonly Random _rng = new();

    public string Channel { get; private set; } = "";
    public string Password { get; private set; } = "";
    public List<Player> Players { get; } = new();
    public MapEntry? Map { get; private set; }
    public GameModeEntry? Mode { get; private set; }
    public OptionValues Options { get; private set; }
    public int Seed { get; private set; }
    public int GameId { get; private set; }
    public bool Locked { get; private set; }
    public TunnelMode TunnelMode { get; private set; }
    public TunnelInfo? StaticTunnel { get; private set; }
    public NegotiationMatrix Negotiations { get; } = new();
    public DateTime CreatedAt { get; private set; }
    public long? CurrentMatchId { get; private set; }
    public DateTime? MatchStartedAt { get; private set; }
    public bool Closed { get; private set; }
    private DateTime _lastBroadcast = DateTime.MinValue;
    private DateTime _emptySince = DateTime.UtcNow;
    private DateTime _lastLaunchNotice = DateTime.MinValue;
    private DateTime _lastTunnelCheck = DateTime.UtcNow;
    private DateTime? _countdownEndsAt;
    /// <summary>Whether the running countdown was announced in chat (a silent quick start is not).</summary>
    private bool _countdownAnnounced;
    /// <summary>A full announced countdown has already been shown for this line-up; later restarts are quick and silent.</summary>
    private bool _fullCountdownShown;
    private readonly Dictionary<string, HashSet<string>> _votes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>!invite targets whose ident is still being looked up with WHO: target nick -> (inviter nick, asked at).</summary>
    private readonly Dictionary<string, (string inviter, DateTime at)> _pendingInvites = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Recently invited nicks, to attribute an INVITATION_FAILED reply: target nick -> inviter nick.</summary>
    private readonly Dictionary<string, string> _recentInvites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Votable options: name -> (description when on, apply(on) values, read current state).</summary>
    private static readonly (string name, string label, string control, string onValue, string offValue)[] VoteOptions =
    {
        ("sw",     "superweapons",   "cmbSuperWeaponsModifier", "0", "3"),
        ("crates", "crates",         "chkCrates",               "true", "false"),
        ("yuri",   "Yuri",           "chkNoYuri",               "false", "true"),
        ("france", "France",         "chkNoFrance",             "false", "true"),
        ("spy",    "spies",          "chkNoSpy",                "false", "true"),
        ("ra2",    "RA2 mode",       "chkRA2Mode",              "true", "false"),
        ("balance", "balance patch",  "chkBalancePatch",         "true", "false"),
        ("random", "random sides",   "peoForceRandomSides",     "1", "0"),
    };
    /// <summary>Voted "random sides": PEO bit 0, which locks every client's side drop-down; all players are set to Random.</summary>
    private bool _forceRandomSides;
    private DateTime? _failedSince;
    private DateTime _lastRenegotiate = DateTime.MinValue;
    /// <summary>Set after an abandoned launch: the owner should close this room and open a fresh one at this time.</summary>
    public DateTime? RecreateAt { get; private set; }
    private readonly List<string> _recentMaps = new();
    private bool _roomJoined;
    private DateTime _lastFormatCheck = DateTime.MinValue;

    /// <summary>Current room size: Def.MaxPlayers, or 6 while the automatic 3v3 switch is active.</summary>
    public int MaxPlayers { get; private set; }
    /// <summary>Whether the room is currently enlarged to 3v3 by the lobby-size rule.</summary>
    public bool ThreeVsThreeActive => Def.AutoThreeVsThree && MaxPlayers == 6 && Def.MaxPlayers != 6;
    /// <summary>Players needed to start: the full room when the setting asks for a full room, else the configured number.</summary>
    private int MinPlayersToStart => Math.Max(1, Def.MinPlayersToStart >= Def.MaxPlayers ? MaxPlayers : Def.MinPlayersToStart);
    /// <summary>Room title as broadcast: the configured name with "2v2" turned into "3v3" while enlarged.</summary>
    public string RoomName => MaxPlayers == Def.MaxPlayers
        ? Def.Name
        : Def.Name.Replace($"{Def.MaxPlayers / 2}v{Def.MaxPlayers / 2}", $"{MaxPlayers / 2}v{MaxPlayers / 2}", StringComparison.OrdinalIgnoreCase);
    /// <summary>People in the main CnCNet chat channel, as far as this connection's NAMES/JOIN/QUIT tracking knows.</summary>
    public int LobbyUsers => _irc.Channels.TryGetValue(_chatChannel, out var ch) ? ch.Users.Count : 0;

    // Spectators pick "Spectator" in the side list like in any room; they sit in extra seats beyond the player slots.
    private bool IsSpectator(Player p) => p.SideId == _layout.SpectatorSideId;
    private int Humans => Players.Count(p => !IsSpectator(p));
    private int Spectators => Players.Count(IsSpectator);
    private int SpectatorSeats => Math.Clamp(Def.MaxSpectators, 0, 8 - MaxPlayers);
    /// <summary>Everyone who can be in the room at once: player slots plus spectator seats.</summary>
    private int Capacity => MaxPlayers + SpectatorSeats;
    private const int SpectateGraceSeconds = 30;
    /// <summary>!mappick votes: map section name -> voters.</summary>
    private readonly Dictionary<string, HashSet<string>> _mapVotes = new(StringComparer.OrdinalIgnoreCase);

    public GameRoom(RoomConfig def, IrcClient irc, TunnelList tunnels, IdentPolicy policy, MapPoolFile pool,
        OptionLayout layout, BotDb db, ClientDialect dialect, string game)
    {
        Def = def;
        _irc = irc;
        _tunnels = tunnels;
        _policy = policy;
        _pool = pool;
        _layout = layout;
        _db = db;
        _dialect = dialect;
        _game = game;
        _chatChannel = $"#cncnet-{game.ToLowerInvariant()}";
        _broadcastChannel = $"#cncnet-{game.ToLowerInvariant()}-games";
        Options = new OptionValues(layout);
        MaxPlayers = def.MaxPlayers;
        if (pool.Select(def, def.MaxPlayers).Count == 0)
            throw new InvalidOperationException($"{def.Name}: no maps match the room filters");
        TunnelMode = def.Tunnel.Mode.ToLowerInvariant() switch
        {
            "v2" => TunnelMode.V2Legacy,
            "v3dynamic" or "dynamic" => TunnelMode.V3Dynamic,
            _ => TunnelMode.V3Static,
        };
        Mode = pool.GameModes.FirstOrDefault(m => m.Name.Equals(def.GameMode, StringComparison.OrdinalIgnoreCase))
               ?? new GameModeEntry { Name = def.GameMode };
    }

    // ---- lifecycle ---------------------------------------------------------------------------

    /// <summary>Starts from the previous room's size so the 3v3 hysteresis carries across a recreation after launch.</summary>
    public void InheritFormat(GameRoom previous) => MaxPlayers = previous.MaxPlayers;

    /// <summary>Creates the IRC channel for the room. The IRC client must be registered and in the broadcast channel.</summary>
    public async Task OpenAsync(CancellationToken ct)
    {
        if (TunnelMode != TunnelMode.V3Dynamic)
            await ChooseStaticTunnelAsync(ct);

        Channel = $"#cncnet-{_game.ToLowerInvariant()}-game{_rng.Next(1000000, 9999999)}";
        Password = string.IsNullOrEmpty(Def.Password)
            ? Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(Channel)))[..10]
            : Def.Password;
        CreatedAt = DateTime.UtcNow;
        _emptySince = CreatedAt;
        MaxPlayers = DesiredMaxPlayers();
        _lastSettings = SettingsString(); // what joiners already know from the game list; only changes are sent
        PickNextMap();
        Seed = _rng.Next();
        GameId = NewGameId();
        _irc.MessageReceived += OnIrcMessage;

        Log.Info(Tag, $"creating {Channel} \"{RoomName}\" ({MaxPlayers / 2}v{MaxPlayers / 2}, lobby {LobbyUsers} users) map {Map!.Name} mode {Mode!.Name} tunnel {TunnelMode}{(StaticTunnel is null ? "" : " " + StaticTunnel)}");
        await _irc.SendNowAsync($"JOIN {Channel} {Password}", ct);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!_roomJoined && DateTime.UtcNow < deadline)
            await Task.Delay(100, ct);
        if (!_roomJoined)
            throw new IOException($"{Def.Nick}: could not join {Channel}");

        // What the client sends from OnJoined(): the user limit counts the bot itself.
        _irc.Queue($"MODE {Channel} +klnNs {Password} {Capacity + 1}", 50);
        _irc.Queue($"TOPIC {Channel} :{_dialect.Revision};{_game.ToLowerInvariant()}", 50);
        _db.LogEvent(Def.Id, "room_open", null, null, null, $"{Channel} {Map!.Name}");
    }

    /// <summary>Announces the room closed and leaves the channel.</summary>
    public async Task CloseAsync(string reason, bool announceClosed = true)
    {
        lock (_gate)
        {
            if (Closed) return;
            Closed = true;
        }
        _irc.MessageReceived -= OnIrcMessage;
        try
        {
            if (_irc.Connected && _irc.Registered)
            {
                if (announceClosed)
                    await _irc.SendNowAsync($"NOTICE {_broadcastChannel} :\x01{BuildGameBroadcast(closed: true)}\x01");
                await _irc.SendNowAsync($"PART {Channel}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"close: {ex.Message}");
        }
        _db.LogEvent(Def.Id, "room_close", null, null, null, reason);
        Log.Info(Tag, $"closed: {reason}");
    }

    /// <summary>Called once a second by the owner.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            if (Closed || !_roomJoined) return;
            var now = DateTime.UtcNow;

            if (now - _lastBroadcast >= TimeSpan.FromSeconds(30) && now - CreatedAt >= TimeSpan.FromSeconds(10))
                Broadcast();

            if (Players.Count == 0 && now - _emptySince >= TimeSpan.FromMinutes(Math.Max(1, Def.IdleMapRotateMinutes)))
            {
                _emptySince = now;
                PickNextMap();
                Log.Info(Tag, $"idle rotation -> {Map!.Name}");
                Broadcast();
            }

            if (Players.Count == 0 && CurrentMatchId is null && now - _lastFormatCheck >= TimeSpan.FromSeconds(30))
            {
                _lastFormatCheck = now;
                ReconsiderFormat();
            }

            if (CurrentMatchId is not null)
                CheckMatchEnd(now);
            else
            {
                MaintainNegotiations(now);
                EnforceSpectatorGrace(now);
                KickUnready(now);
                TryLaunch(now);
            }

            if (TunnelMode != TunnelMode.V3Dynamic && now - _lastTunnelCheck > TimeSpan.FromMinutes(10))
            {
                _lastTunnelCheck = now;
                if (StaticTunnel is not null && _tunnels.Find(StaticTunnel.Endpoint) is null)
                {
                    Log.Warn(Tag, $"tunnel {StaticTunnel} vanished from the master list, re-selecting");
                    _ = ChooseStaticTunnelAsync(CancellationToken.None).ContinueWith(_ =>
                    {
                        lock (_gate)
                        {
                            if (StaticTunnel is not null)
                            {
                                _irc.SendCtcp(Channel, $"CHTNL {StaticTunnel.Endpoint}", 10);
                                SendGameOptions();
                            }
                        }
                    });
                }
            }
        }
    }

    // ---- admin actions -------------------------------------------------------------------------

    public void SendChat(string text, int priority = 0)
    {
        _irc.SendChat(Channel, text, priority: priority);
        _db.LogChat(Def.Id, Channel, _irc.Nick, null, text, outgoing: true);
    }

    public void Kick(string nick, string reason, bool ban = false)
    {
        lock (_gate)
        {
            var p = Players.FirstOrDefault(x => x.Nick.Equals(nick, StringComparison.OrdinalIgnoreCase));
            string? host = p?.Host ?? (_irc.Channels.TryGetValue(Channel, out var ch) && ch.Users.TryGetValue(nick, out var u) ? u.Host : null);
            if (!string.IsNullOrEmpty(reason))
                SendPrivateMessage(nick, reason);
            if (ban && !string.IsNullOrEmpty(host))
                _ = _irc.SendNowAsync($"MODE {Channel} +b *!*@{host}");
            _ = _irc.SendNowAsync($"KICK {Channel} {nick}");
            _db.LogEvent(Def.Id, ban ? "ban" : "kick", nick, p?.Ident, host, reason);
            if (p is not null)
                RemovePlayer(p, "kicked");
        }
    }

    public void ForceNextMap(string? mapName = null)
    {
        lock (_gate)
        {
            if (mapName is not null)
            {
                var m = _pool.Maps.FirstOrDefault(x => x.Name.Equals(mapName, StringComparison.OrdinalIgnoreCase) || x.BaseFilePath.Equals(mapName, StringComparison.OrdinalIgnoreCase));
                if (m is null) return;
                ApplyMap(m);
            }
            else
                PickNextMap();
            ClearReadyStatuses();
            SendGameOptions();
            SendPlayerOptions();
            SendPlayerExtraOptions();
            Broadcast();
            Log.Info(Tag, $"map forced -> {Map!.Name}");
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                id = Def.Id,
                name = Def.Name,
                nick = _irc.Nick,
                channel = Channel,
                tier = Def.Tier.ToString(),
                roomName = RoomName,
                maxPlayers = MaxPlayers,
                threeVsThree = ThreeVsThreeActive,
                lobbyUsers = LobbyUsers,
                map = Map?.Name,
                mapPath = Map?.BaseFilePath,
                mode = Mode?.Name,
                tunnelMode = TunnelMode.ToString(),
                tunnel = StaticTunnel?.ToString(),
                locked = Locked,
                teamMapping = ActiveMapping is { } tm ? $"{tm.Name}: {tm.Mapping}" : null,
                seed = Seed,
                gameId = GameId,
                inMatch = CurrentMatchId is not null,
                countdownEndsAt = _countdownEndsAt,
                matchStartedAt = MatchStartedAt,
                createdAt = CreatedAt,
                connected = _irc.Connected && _irc.Registered,
                players = Players.Select(p => new
                {
                    p.Nick, p.Ident, p.Host, p.SideId, p.ColorId, p.StartingLocation, p.TeamId,
                    p.Ready, p.AutoReady, p.HashReceived, p.IsInGame, p.JoinedAt, p.ReadyWarnedAt, p.InvitedBy,
                    spectator = IsSpectator(p), p.MustSpectateBy,
                }).ToList(),
                negotiations = TunnelMode == TunnelMode.V3Dynamic
                    ? Negotiations.Pairs(Players.Select(p => p.Nick).ToList()).Select(x => new { x.a, x.b, status = x.status.ToString(), ping = Negotiations.PairPing(x.a, x.b) }).ToList()
                    : null,
            };
        }
    }

    // ---- IRC events ----------------------------------------------------------------------------

    private void OnIrcMessage(IrcMessage msg)
    {
        lock (_gate)
        {
            if (Closed) return;
            switch (msg.Command)
            {
                case "JOIN":
                    if (!IsRoom(msg.Param(0))) break;
                    if (string.Equals(msg.Nick, _irc.Nick, StringComparison.OrdinalIgnoreCase))
                        _roomJoined = true;
                    else
                        OnUserJoined(msg.Nick ?? "", msg.Ident, msg.Host);
                    break;
                case "PART":
                    if (IsRoom(msg.Param(0))) OnUserLeft(msg.Nick, "left");
                    break;
                case "KICK":
                    if (IsRoom(msg.Param(0))) OnUserLeft(msg.Param(1), "kicked");
                    break;
                case "QUIT":
                    OnUserLeft(msg.Nick, "quit");
                    break;
                case "352": // WHO reply for a pending !invite target (the IRC client has filled in ident/host already)
                    if (_pendingInvites.Remove(msg.Param(5), out var pending))
                    {
                        var inviter = FindPlayer(pending.inviter);
                        if (inviter is not null && _irc.Channels.TryGetValue(_chatChannel, out var lobby) && lobby.Users.TryGetValue(msg.Param(5), out var target))
                            CompleteInvite(inviter, target);
                    }
                    break;
                case "315": // end of WHO: nobody answered
                    if (_pendingInvites.Remove(msg.Param(1), out _))
                        SendChat($"Could not find {msg.Param(1)} in the CnCNet lobby.");
                    break;
                case "NICK":
                {
                    var p = FindPlayer(msg.Nick);
                    if (p is not null)
                    {
                        p.Nick = msg.Trailing;
                        SendPlayerOptions();
                    }
                    break;
                }
                case "NOTICE":
                case "PRIVMSG":
                {
                    string target = msg.Param(0);
                    string text = msg.Trailing;
                    if (text.Length >= 2 && text[0] == '\x01' && text[^1] == '\x01' && !text.Contains("ACTION"))
                    {
                        if (IsRoom(target) && msg.Nick is not null)
                            HandleCtcp(msg.Nick, text[1..^1]);
                        else if (string.Equals(target, _irc.Nick, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Debug(Tag, $"private CTCP from {msg.Nick}: {text[1..^1]}");
                            if (text[1..^1] == "INVITATION_FAILED" && msg.Nick is not null && _recentInvites.Remove(msg.Nick, out var inviter))
                                SendChat($"{inviter}: {msg.Nick} cannot receive the invitation right now (in a game, or accepting invites from friends only). They can still join this room from the game list.");
                        }
                        break;
                    }
                    if (msg.Command != "PRIVMSG" || msg.Nick is null)
                        break;
                    string plain = StripFormatting(text);
                    if (IsRoom(target))
                    {
                        var p = FindPlayer(msg.Nick);
                        _db.LogChat(Def.Id, Channel, msg.Nick, p?.Ident ?? msg.Ident, plain, outgoing: false);
                        HandleChatCommand(msg.Nick, plain);
                    }
                    else if (string.Equals(target, _irc.Nick, StringComparison.OrdinalIgnoreCase))
                    {
                        _db.LogChat(Def.Id, "pm:" + msg.Nick, msg.Nick, msg.Ident, plain, outgoing: false);
                        HandlePrivateMessage(msg.Nick, msg.Ident, plain);
                    }
                    break;
                }
            }
        }
    }

    private bool IsRoom(string channel) => string.Equals(channel, Channel, StringComparison.OrdinalIgnoreCase);

    private void SendPrivateMessage(string nick, string text)
    {
        _irc.SendPrivateMessage(nick, text);
        _db.LogChat(Def.Id, "pm:" + nick, _irc.Nick, null, text, outgoing: true);
    }

    private Player? FindPlayer(string? nick) =>
        nick is null ? null : Players.FirstOrDefault(p => p.Nick.Equals(nick, StringComparison.OrdinalIgnoreCase));

    private void OnUserJoined(string nick, string? ident, string? host)
    {
        if (FindPlayer(nick) is not null)
            return;

        var verdict = _policy.Evaluate(ident ?? "", host, Def.Id, Def.Tier, Def.MaxIdentAgeDays, Def.MinIdentAgeDays, Def.UseHostAsFallback);
        string identInfo = verdict.History is { } h ? $"first seen {h.FirstSeen:yyyy-MM-dd}, {h.Days} days, last nick {h.LastNick}" : "no history";
        if (!verdict.Allowed)
        {
            Log.Info(Tag, $"KICK {nick} ({ident}@{host}): {verdict.Reason}; {identInfo}");
            _db.LogEvent(Def.Id, "kick_policy", nick, ident, host, verdict.Reason);
            string msg = Def.KickMessage ?? $"You cannot play in this room: {verdict.Reason}.";
            SendPrivateMessage(nick, msg);
            _ = _irc.SendNowAsync($"KICK {Channel} {nick}");
            return;
        }

        if (Players.Count >= Capacity || Players.Count >= 8)
        {
            Log.Info(Tag, $"room full, kicking {nick}");
            _ = _irc.SendNowAsync($"KICK {Channel} {nick}");
            return;
        }

        var p = new Player { Nick = nick, Ident = ident, Host = host, InvitedBy = verdict.Whitelist is { IsInvite: true } w ? w.AddedBy : null };
        bool playersFull = Humans >= MaxPlayers;
        if (playersFull)
            p.MustSpectateBy = DateTime.UtcNow.AddSeconds(SpectateGraceSeconds);
        Players.Add(p);
        Log.Info(Tag, $"join {nick} ({ident}@{host}): {verdict.Reason}; {identInfo}; {Humans}/{MaxPlayers}{(Spectators > 0 || playersFull ? $" +{Spectators} spectators" : "")}");
        _db.LogEvent(Def.Id, "join", nick, ident, host, verdict.Reason);

        // Joining does not un-ready the others (the stock host does not either); the newcomer just has to ready up.
        SendGameOptions();
        SendPlayerOptions();
        SendPlayerExtraOptions(force: true);
        if (Players.Count == 1)
            SendChat($"Hi {nick}, I am the host bot. Pick a spot and press Ready; the game starts when {MinPlayersToStart} players are ready. Type !help for commands.");
        else if (playersFull)
            SendChat($"{nick}: the player slots are taken. Choose Spectator in the side list within {SpectateGraceSeconds} seconds to watch, or you will be removed.", UrgentChatPriority);
        if (Players.Count >= Capacity)
            SetLocked(true);
        _lastBroadcast = DateTime.MinValue; // refresh the list entry promptly
    }

    private void OnUserLeft(string? nick, string how)
    {
        var p = FindPlayer(nick);
        if (p is null) return;
        _db.LogEvent(Def.Id, "leave", p.Nick, p.Ident, p.Host, how);
        RemovePlayer(p, how);
    }

    private void RemovePlayer(Player p, string how)
    {
        Players.Remove(p);
        Negotiations.RemovePlayer(p.Nick);
        _fullCountdownShown = false;
        Log.Info(Tag, $"{how}: {p.Nick}; {Humans}/{MaxPlayers}{(Spectators > 0 ? $" +{Spectators} spectators" : "")}");
        if (Humans <= MaxPlayers)
            foreach (var x in Players) x.MustSpectateBy = null; // the slots are no longer over-subscribed; late joiners may keep theirs
        SendPlayerOptions();
        if (Players.Count == 0)
            _emptySince = DateTime.UtcNow;
        if (Locked && CurrentMatchId is null && Players.Count < Capacity)
            SetLocked(false);
        _lastBroadcast = DateTime.MinValue;
    }

    // ---- CTCP from players ---------------------------------------------------------------------

    private void HandleCtcp(string sender, string payload)
    {
        var p = FindPlayer(sender);
        int sp = payload.IndexOf(' ');
        string cmd = sp < 0 ? payload : payload[..sp];
        string arg = sp < 0 ? "" : payload[(sp + 1)..];

        switch (cmd)
        {
            case "OR" when p is not null && int.TryParse(arg, out int packed):
                HandleOptionsRequest(p, packed);
                break;
            case "R" when p is not null && int.TryParse(arg, out int ready):
            {
                bool wasReady = p.Ready;
                p.Ready = ready > 0;
                p.AutoReady = ready > 1;
                if (wasReady && !p.Ready && _countdownEndsAt is not null)
                    NoteCancelledStart(p);
                SendPlayerOptions();
                break;
            }
            case "FHSH" when p is not null:
                p.HashReceived = true;
                p.FileHash = arg;
                break;
            case "STRTD" when p is not null:
                p.IsInGame = true;
                p.StartedAt ??= DateTime.UtcNow;
                break;
            case "RETURN" when p is not null:
                p.IsInGame = false;
                p.Ready = false;
                if (CurrentMatchId is null)
                    SendPlayerOptions();
                break;
            case "NEGRPT" when p is not null:
                Negotiations.ApplyReport(sender, arg, IdTable());
                break;
            case "MAPREQ":
            case "MAPFAIL":
            case "MAPSDISABLED":
                Log.Warn(Tag, $"{sender}: {payload} (official maps only; the player's game files differ)");
                _db.LogEvent(Def.Id, "map_problem", sender, p?.Ident, p?.Host, payload);
                break;
            case "TNLPNG":
            case "DR":
            case "CD":
                break;
            default:
                Log.Debug(Tag, $"ctcp from {sender}: {payload}");
                break;
        }
    }

    private Dictionary<uint, string> IdTable() =>
        Players.ToDictionary(p => NegotiationMatrix.PlayerId(p.Nick, Channel), p => p.Nick);

    private void HandleOptionsRequest(Player p, int packed)
    {
        if (p.IsInGame) return;
        int side = packed & 0xFF, color = (packed >> 8) & 0xFF, start = (packed >> 16) & 0xFF, team = (packed >> 24) & 0xFF;
        if (side < 0 || side > _layout.SpectatorSideId) return;
        if (color < 0 || color > _layout.ColorCount) return;
        if (team < 0 || team > 4) return;
        if (start != 0 && (Map is null || start > Map.MaxPlayers)) return;

        // Same (quirky) check the client makes: raw drop-down index against the selector-padded array.
        var disallowed = Options.DisallowedSides();
        if (side > 0 && side < _layout.SideCount && side >= _layout.RandomSelectorCount && disallowed.Contains(side - _layout.RandomSelectorCount))
            return;

        bool wantsSpectator = side == _layout.SpectatorSideId;
        if (wantsSpectator)
        {
            start = 0;
            team = 0;
            p.MustSpectateBy = null;
        }
        else if (IsSpectator(p) || p.MustSpectateBy is not null)
        {
            // A spectator (or a late joiner who still has to become one) may only take a player slot while one is free.
            if (Humans >= MaxPlayers)
            {
                if (p.PlayRefusedAt is null || DateTime.UtcNow - p.PlayRefusedAt.Value > TimeSpan.FromSeconds(10))
                {
                    p.PlayRefusedAt = DateTime.UtcNow;
                    SendChat($"{p.Nick}: all {MaxPlayers} player slots are taken, you can only spectate right now.", UrgentChatPriority);
                }
                SendPlayerOptions(); // put their side list back
                return;
            }
            p.MustSpectateBy = null;
            Log.Info(Tag, $"{p.Nick} switches from spectator to player");
        }
        if (ActiveMapping is not null)
            team = 0; // teams follow start positions; the clients' team drop-down is disabled anyway
        if (_forceRandomSides && !wantsSpectator)
            side = 0;
        // Unlike the stock host, a spot/side/colour change does not un-ready anyone: players may
        // adjust during the countdown and the game starts with whatever they chose.
        if (wantsSpectator != IsSpectator(p))
            _lastBroadcast = DateTime.MinValue; // the game list's player count includes spectators; refresh it now
        p.SideId = side;
        p.ColorId = color;
        p.StartingLocation = start;
        p.TeamId = team;
        SendPlayerOptions();
    }

    // ---- chat --------------------------------------------------------------------------------

    private void HandleChatCommand(string nick, string text)
    {
        if (!text.StartsWith('!')) return;
        var p = FindPlayer(nick);
        string cmd = text.Split(' ')[0].ToLowerInvariant();
        switch (cmd)
        {
            case "!help":
                SendChat("Commands: !map (current map), !next (vote for a random other map), !mappick <name> (vote for a map), !vote sw|crates|yuri|france|spy|ra2|balance|random (toggle an option), !rules"
                         + (Def.AllowInvites && Def.Tier != RoomTier.Open ? ", !invite <nick> (let a player in who the ident check would refuse)" : "")
                         + (SpectatorSeats > 0 ? ". To watch, choose Spectator in the side list." : "."));
                break;
            case "!mappick" when p is not null && CurrentMatchId is null:
                if (DateTime.UtcNow - _mapChangedAt >= TimeSpan.FromSeconds(5))
                    HandleMapPick(p, text.Length > cmd.Length ? text[(cmd.Length + 1)..].Trim() : "");
                break;
            case "!invite" when p is not null:
                HandleInvite(p, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault());
                break;
            case "!vote" when p is not null && CurrentMatchId is null:
                HandleVote(p, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault());
                break;
            case "!map":
                SendChat($"Map: {Map?.Name} ({Mode?.Name}), {Humans}/{MaxPlayers} players{(Spectators > 0 ? $" + {Spectators} spectating" : "")}. Game starts when everyone is ready.");
                break;
            case "!rules":
                SendChat(Def.Tier switch
                {
                    RoomTier.Noob => $"Beginner room: players whose ident has been seen on CnCNet for more than {Def.MaxIdentAgeDays} days are kicked.",
                    RoomTier.Pro => $"Pro room: players whose ident has been seen on CnCNet for less than {Def.MinIdentAgeDays} days are kicked.",
                    _ => "Open room: anyone may play.",
                });
                break;
            case "!next" when p is not null && CurrentMatchId is null:
            {
                if (DateTime.UtcNow - _mapChangedAt < TimeSpan.FromSeconds(5))
                    break; // crossed with a map change that was already on its way to the players
                p.WantsNextMap = true;
                int votes = Players.Count(x => x.WantsNextMap);
                int needed = Def.VotesNeeded > 0 ? Math.Min(Def.VotesNeeded, Players.Count) : Players.Count / 2 + 1;
                if (votes >= needed)
                {
                    foreach (var x in Players) x.WantsNextMap = false;
                    if (Map is not null)
                        _db.BumpMapStat(Def.Id, Map.BaseFilePath, skip: true); // ranks the map a little lower for future picks
                    PickNextMap();
                    ClearReadyStatuses();
                    SendGameOptions();
                    SendPlayerOptions();
                    SendPlayerExtraOptions();
                    SendChat($"Map changed to {Map!.Name}.");
                    _lastBroadcast = DateTime.MinValue;
                }
                else
                    SendChat($"{nick} wants a different map ({votes}/{needed} votes).");
                break;
            }
        }
    }

    /// <summary>"!mappick &lt;part of a name&gt;": half the room voting for the same pool map switches to it.</summary>
    private void HandleMapPick(Player p, string query)
    {
        if (query.Length == 0)
        {
            SendChat("Usage: !mappick <part of the map name> - half the room voting for the same map switches to it.");
            return;
        }
        static string Norm(string s)
        {
            s = s.Trim().ToLowerInvariant();
            if (s.StartsWith('[') && s.IndexOf(']') is > 0 and var i) s = s[(i + 1)..].Trim(); // "[4] Tour of Egypt" -> "tour of egypt"
            return s;
        }
        string q = Norm(query);
        var pool = MapChoices();
        var matches = pool.Where(m => Norm(m.Name).Contains(q)).ToList();
        if (matches.Count == 0)
        {
            SendChat($"No {MaxPlayers / 2}v{MaxPlayers / 2} map in this room's pool matches \"{query}\".");
            return;
        }
        // Best match wins without asking (the pool has duplicate names such as two "[4] Stronghold"):
        // exact name, then a name starting with the query, then the shortest name, then pool order.
        var map = matches
            .Select((m, i) => (m, i))
            .OrderByDescending(x => Norm(x.m.Name) == q)
            .ThenByDescending(x => Norm(x.m.Name).StartsWith(q))
            .ThenBy(x => x.m.Name.Length)
            .ThenBy(x => x.i)
            .First().m;
        if (Map is not null && map.BaseFilePath == Map.BaseFilePath)
        {
            SendChat($"{map.Name} is the current map.");
            return;
        }
        foreach (var voters in _mapVotes.Values) voters.Remove(p.Nick); // one map vote per player
        if (!_mapVotes.TryGetValue(map.BaseFilePath, out var set))
            _mapVotes[map.BaseFilePath] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(p.Nick);
        int needed = Math.Max(1, (Players.Count + 1) / 2);
        if (set.Count < needed)
        {
            SendChat($"{p.Nick} votes for {map.Name} ({set.Count}/{needed}). Type !mappick {Norm(map.Name)} to agree.");
            return;
        }
        foreach (var x in Players) x.WantsNextMap = false;
        ApplyMap(map);
        ClearReadyStatuses();
        SendGameOptions();
        SendPlayerOptions();
        SendPlayerExtraOptions();
        SendChat($"Map changed to {Map!.Name} by vote.");
        _db.LogEvent(Def.Id, "mappick", p.Nick, p.Ident, p.Host, map.Name);
        _lastBroadcast = DateTime.MinValue;
    }

    /// <summary>Removes late joiners who neither became spectators nor got a player slot within the grace period.</summary>
    private void EnforceSpectatorGrace(DateTime now)
    {
        foreach (var p in Players.ToList())
        {
            if (p.MustSpectateBy is not { } by || IsSpectator(p) || now < by)
                continue;
            if (Humans <= MaxPlayers) // their own slot counts: only remove while the slots are over-subscribed
            {
                p.MustSpectateBy = null;
                continue;
            }
            Log.Info(Tag, $"removing {p.Nick}: player slots full and did not choose Spectator");
            _db.LogEvent(Def.Id, "kick_full", p.Nick, p.Ident, p.Host, "did not choose Spectator while the player slots were full");
            SendPrivateMessage(p.Nick, "The player slots were full and you did not choose Spectator. You are welcome to rejoin when a slot is free.");
            _ = _irc.SendNowAsync($"KICK {Channel} {p.Nick}");
            RemovePlayer(p, "kicked (no spectator choice)");
        }
    }

    private void HandleVote(Player p, string? option)
    {
        if (Def.VotesNeeded <= 0)
        {
            SendChat("Voting is off in this room.");
            return;
        }
        var opt = VoteOptions.FirstOrDefault(v => v.name.Equals(option ?? "", StringComparison.OrdinalIgnoreCase));
        if (opt.name is null)
        {
            SendChat("Vote options: " + string.Join(", ", VoteOptions.Select(v => $"!vote {v.name} ({v.label} {(IsVoteOptionOn(v) ? "on" : "off")})")) + $". {Def.VotesNeeded} votes flip an option for this game.");
            return;
        }
        if (!_votes.TryGetValue(opt.name, out var voters))
            _votes[opt.name] = voters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        voters.Add(p.Nick);
        bool turnOn = !IsVoteOptionOn(opt);
        int needed = Math.Min(Def.VotesNeeded, Players.Count);
        if (voters.Count < needed)
        {
            SendChat($"{p.Nick} votes to turn {opt.label} {(turnOn ? "on" : "off")} ({voters.Count}/{needed}).");
            return;
        }
        voters.Clear();
        if (opt.control == "peoForceRandomSides")
        {
            _forceRandomSides = turnOn;
            if (turnOn)
                foreach (var x in Players) x.SideId = 0;
            SendPlayerExtraOptions();
        }
        else
            Options.Apply(new[] { new KeyValuePair<string, string>(opt.control, turnOn ? opt.onValue : opt.offValue) }, "vote");
        ClearReadyStatuses();
        SendGameOptions();
        SendPlayerOptions();
        SendChat($"Vote passed: {opt.label} {(turnOn ? "on" : "off")} for this game. Ready up again.");
        _db.LogEvent(Def.Id, "vote", p.Nick, p.Ident, p.Host, $"{opt.name} {(turnOn ? "on" : "off")}");
        _lastBroadcast = DateTime.MinValue;
    }

    // ---- !invite: a player who passed the ident check vouches for someone the check would refuse ----

    private void HandleInvite(Player inviter, string? targetNick)
    {
        if (!Def.AllowInvites || Def.Tier == RoomTier.Open)
        {
            SendChat(Def.Tier == RoomTier.Open ? "Anyone can join this room already; just tell them the room name." : "Invites are not enabled in this room.");
            return;
        }
        if (string.IsNullOrWhiteSpace(targetNick))
        {
            SendChat($"Usage: !invite <nick> - whitelists that player for this room for {Math.Max(1, Def.InviteDays)} days and sends them a game invitation.");
            return;
        }
        if (inviter.InvitedBy is not null)
        {
            SendChat($"{inviter.Nick}: players who are here by invitation cannot invite others.");
            return;
        }
        targetNick = targetNick.Trim().TrimStart('@', '+');
        if (targetNick.Equals(_irc.Nick, StringComparison.OrdinalIgnoreCase) || targetNick.Equals(inviter.Nick, StringComparison.OrdinalIgnoreCase))
        {
            SendChat($"{inviter.Nick}: you cannot invite yourself.");
            return;
        }
        if (FindPlayer(targetNick) is not null)
        {
            SendChat($"{targetNick} is already in this room.");
            return;
        }
        if (!_irc.Channels.TryGetValue(_chatChannel, out var lobby) || !lobby.Users.TryGetValue(targetNick, out var target))
        {
            SendChat($"{targetNick} is not in the CnCNet lobby right now. Check the spelling (capitals do not matter).");
            return;
        }
        if (target.Ident is null)
        {
            // Was in the lobby before this connection joined: the NAMES list has no idents, ask the server.
            foreach (var stale in _pendingInvites.Where(kv => DateTime.UtcNow - kv.Value.at > TimeSpan.FromSeconds(30)).Select(kv => kv.Key).ToList())
                _pendingInvites.Remove(stale);
            _pendingInvites[target.Nick] = (inviter.Nick, DateTime.UtcNow);
            _irc.Queue($"WHO {target.Nick}", 1);
            return;
        }
        CompleteInvite(inviter, target);
    }

    private void CompleteInvite(Player inviter, IrcUser target)
    {
        string ident = IdentStore.Normalise(target.Ident ?? "");
        if (ident.Length == 0)
        {
            SendChat($"Could not determine {target.Nick}'s ident.");
            return;
        }
        if (inviter.Ident is not null && IdentStore.Normalise(inviter.Ident) == ident)
        {
            SendChat($"{inviter.Nick}: you cannot invite yourself.");
            return;
        }
        int max = Math.Max(0, Def.MaxInvitesPerDay);
        if (max > 0 && inviter.Ident is not null && _db.CountEvents(Def.Id, "invite", inviter.Ident, DateTime.UtcNow.AddHours(-24)) >= max)
        {
            SendChat($"{inviter.Nick}: you have used your {max} invites for today.");
            return;
        }

        var verdict = _policy.Evaluate(ident, target.Host, Def.Id, Def.Tier, Def.MaxIdentAgeDays, Def.MinIdentAgeDays, Def.UseHostAsFallback);
        int days = Math.Max(1, Def.InviteDays);
        if (!verdict.Allowed)
            _policy.Add(new WhitelistEntry(ident, Def.Id, "invite", $"{inviter.Nick} ({IdentStore.Normalise(inviter.Ident ?? "?")})",
                $"!invite {target.Nick} in {Def.Name}", DateTime.UtcNow.AddDays(days), DateTime.UtcNow));

        // Same CTCP the client's "Invite" context-menu item sends; the password is only given for password rooms,
        // the client derives the key for the others itself.
        string body = $"INVITE {Channel};{RoomName}" + (string.IsNullOrEmpty(Def.Password) ? "" : ";" + Password);
        _irc.Queue($"PRIVMSG {target.Nick} :\x01{body}\x01", 1);
        _recentInvites[target.Nick] = inviter.Nick;
        _db.LogEvent(Def.Id, "invite", inviter.Nick, inviter.Ident, inviter.Host, $"{target.Nick} ({ident}){(verdict.Allowed ? ", already allowed" : $", whitelisted {days} days")}");
        Log.Info(Tag, $"invite: {inviter.Nick} -> {target.Nick} ({ident}){(verdict.Allowed ? " (already allowed)" : $" whitelisted {days} days")}");
        SendChat(verdict.Allowed
            ? $"Invitation sent to {target.Nick} (they could already join this room)."
            : $"Invitation sent to {target.Nick}; they may join this room for the next {days} days.");
    }

    private bool IsVoteOptionOn((string name, string label, string control, string onValue, string offValue) opt) =>
        opt.control == "peoForceRandomSides" ? _forceRandomSides
        : opt.control.StartsWith("chk") ? Options.IsChecked(opt.control) == (opt.onValue == "true")
        : Options.DropDownIndex(opt.control).ToString() == opt.onValue;

    private void HandlePrivateMessage(string nick, string? ident, string text)
    {
        if (text.Trim().Equals("!help", StringComparison.OrdinalIgnoreCase))
            SendPrivateMessage(nick, "I host games automatically. Join my room from the game list; type !help in the room for commands.");
    }

    private static string StripFormatting(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\x03')
            {
                int j = i + 1;
                while (j < text.Length && j < i + 3 && char.IsDigit(text[j])) j++;
                if (j < text.Length && text[j] == ',') { j++; while (j < text.Length && j < i + 6 && char.IsDigit(text[j])) j++; }
                i = j - 1;
                continue;
            }
            if (c is '\x02' or '\x1D' or '\x1F' or '\x16' or '\x0F') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- outgoing lobby messages ---------------------------------------------------------------

    private void SendGameOptions()
    {
        if (Map is null || Mode is null) return;
        var parts = new List<string>();
        parts.AddRange(Options.PackedCheckBoxes().Select(i => i.ToString()));
        parts.AddRange(Options.DropDownIndices().Select(i => i.ToString()));
        parts.Add("1");                 // map is official
        parts.Add(Map.Sha1);
        parts.Add(Mode.Name);
        parts.Add(_layout.FrameSendRate.ToString());
        parts.Add(_layout.MaxAhead.ToString());
        parts.Add(_layout.ProtocolVersion.ToString());
        parts.Add(Seed.ToString());
        parts.Add("0");                 // RemoveStartingLocations
        parts.Add(Map.Name);
        parts.Add(((int)TunnelMode).ToString());
        if (_dialect.GoHasDifficulty)
            parts.Add("-1");
        _irc.SendCtcp(Channel, "GO " + string.Join(";", parts), 11, "GO");
    }

    private void SendPlayerOptions()
    {
        var sb = new StringBuilder("PO ");
        foreach (var p in Players)
            sb.Append(p.Nick).Append(';').Append(p.PackedOptions).Append(';').Append(p.ReadyStatus(mapInvalid: Map is null)).Append(';');
        _irc.SendCtcp(Channel, sb.ToString(), 11, "PO");
        SendGameSettingsIfChanged();
    }

    /// <summary>
    /// The client's GSETTINGS (room name; player limit; skill level; custom password): clients draw
    /// "limit" slots as free and the rest as unavailable, and they learned the limit from the game list
    /// when they joined. Spectators sit beyond the player slots, so the limit grows with them.
    /// </summary>
    private string? _lastSettings;

    private string SettingsString() =>
        $"GSETTINGS {RoomName.Replace(";", "")};{MaxPlayers + Spectators};{Math.Clamp(Def.SkillLevel, 0, 3)};{(string.IsNullOrEmpty(Def.Password) ? 0 : 1)}";

    private void SendGameSettingsIfChanged()
    {
        string s = SettingsString();
        if (s == _lastSettings) return;
        _lastSettings = s;
        if (Players.Count > 0)
            _irc.SendCtcp(Channel, s, 11, "GSETTINGS");
    }

    /// <summary>The map's two-team layout for the current room size (2v2 or 3v3), when auto-ally is on.</summary>
    private TeamStartMapping? ActiveMapping =>
        Def.UseTeamStartMappings && Map is not null ? MapPoolFile.TeamMapping(Map, MaxPlayers / 2) : null;

    /// <summary>Auto-ally: bit 5 on and the map's first team layout, so each start position carries its team.</summary>
    private string? _lastPeo;

    private void SendPlayerExtraOptions(bool force = false)
    {
        var m = ActiveMapping;
        // Bits: forceRandomSides, forceRandomColors, forceNoTeams, forceRandomStarts, useTeamStartMappings.
        // The client ticks "no teams" together with "use team start mappings"; only that flag disables the team drop-down.
        char rs = _forceRandomSides ? '1' : '0';
        string peo = m is null ? $"PEO {rs}0000;" : $"PEO {rs}0101;{m.Mapping}";
        if (!force && peo == _lastPeo)
            return; // every line costs ~2.5 s of GameSurge's per-connection budget; identical PEO carries nothing new
        _lastPeo = peo;
        _irc.SendCtcp(Channel, peo, 11, "PEO");
    }

    /// <summary>
    /// A failed tunnel pair never recovers unless its keepalive comes back, and players rarely
    /// ready up while their client shows a failed negotiation. So, independent of readiness: once a
    /// pair has been failed for 15 s, ask the clients to redo their negotiations (the host's
    /// /renegotiate), at most once a minute.
    /// </summary>
    private void MaintainNegotiations(DateTime now)
    {
        if (TunnelMode != TunnelMode.V3Dynamic || Players.Count < 2)
        {
            _failedSince = null;
            return;
        }
        var names = Players.Where(p => !p.IsInGame).Select(p => p.Nick).ToList();
        var failed = Negotiations.Pairs(names).Where(x => x.status == NegotiationStatus.Failed).ToList();
        if (failed.Count == 0)
        {
            _failedSince = null;
            return;
        }
        _failedSince ??= now;
        if (now - _failedSince.Value < TimeSpan.FromSeconds(15) || now - _lastRenegotiate < TimeSpan.FromSeconds(60))
            return;
        _lastRenegotiate = now;
        _failedSince = null;
        foreach (var f in failed)
            Negotiations.ResetPair(f.a, f.b);
        _irc.SendCtcp(Channel, "RENEGALL " + string.Join(",", names), 10);
        Log.Info(Tag, "RENEGALL after failed pairs: " + string.Join(", ", failed.Select(x => $"{x.a}<->{x.b}")));
        _db.LogEvent(Def.Id, "renegotiate", null, null, null, string.Join(", ", failed.Select(x => $"{x.a}<->{x.b}")));
    }

    private bool TunnelsUnresolved() =>
        TunnelMode == TunnelMode.V3Dynamic && Players.Count >= 2 &&
        !Negotiations.AllSucceeded(Players.Where(p => !p.IsInGame).Select(p => p.Nick).ToList());

    /// <summary>Warns, then kicks, players who stay not-ready while the room is full. Paused while tunnels are still being sorted out.</summary>
    private void KickUnready(DateTime now)
    {
        if (Def.KickUnreadySeconds <= 0 || Humans < MaxPlayers || TunnelsUnresolved())
        {
            foreach (var p in Players) { p.UnreadySince = null; p.ReadyWarnedAt = null; }
            return;
        }
        // One warning line names everyone who is not ready as soon as the first player's grace runs
        // out, instead of a line (and a private message) per player as each one's grace expires.
        bool warnDue = false;
        foreach (var p in Players)
        {
            if (p.Ready || p.IsInGame)
            {
                p.UnreadySince = null;
                p.ReadyWarnedAt = null;
                continue;
            }
            p.UnreadySince ??= now;
            if (p.ReadyWarnedAt is null && now - p.UnreadySince.Value >= TimeSpan.FromSeconds(Math.Max(0, Def.ReadyGraceSeconds)))
                warnDue = true;
        }
        var toWarn = new List<Player>();
        foreach (var p in Players.ToList())
        {
            if (p.Ready || p.IsInGame)
                continue;
            if (p.ReadyWarnedAt is null)
            {
                if (warnDue)
                {
                    p.ReadyWarnedAt = now;
                    toWarn.Add(p);
                }
            }
            else if (now - p.ReadyWarnedAt.Value >= TimeSpan.FromSeconds(Def.KickUnreadySeconds))
            {
                Log.Info(Tag, $"kicking {p.Nick}: not ready while the room is full");
                _db.LogEvent(Def.Id, "kick_unready", p.Nick, p.Ident, p.Host, "not ready while the room was full");
                SendPrivateMessage(p.Nick, "You were kicked for not readying up while the room was full. You are welcome to rejoin.");
                _ = _irc.SendNowAsync($"KICK {Channel} {p.Nick}");
                RemovePlayer(p, "kicked (not ready)");
            }
        }
        if (toWarn.Count > 0)
        {
            SendChat($"{string.Join(", ", toWarn.Select(p => p.Nick))} you are not ready. Please ready up so the game can begin, or you will be kicked in {Def.KickUnreadySeconds} seconds.", UrgentChatPriority);
        }
    }

    /// <summary>A player un-readied while the countdown was running: warn, then kick repeat offenders.</summary>
    private void NoteCancelledStart(Player p)
    {
        p.CancelledStarts++;
        int limit = Def.KickAfterCancelledStarts;
        Log.Info(Tag, $"{p.Nick} un-readied during the countdown ({p.CancelledStarts}x)");
        if (limit <= 0) return;
        if (p.CancelledStarts >= limit)
        {
            Log.Info(Tag, $"kicking {p.Nick}: cancelled the start {p.CancelledStarts} times");
            _db.LogEvent(Def.Id, "kick_cancel", p.Nick, p.Ident, p.Host, $"un-readied during the countdown {p.CancelledStarts} times");
            SendChat($"{p.Nick} kicked for cancelling the start {p.CancelledStarts} times.", UrgentChatPriority);
            SendPrivateMessage(p.Nick, $"You were kicked for un-readying during the start countdown {p.CancelledStarts} times. You are welcome to rejoin.");
            _ = _irc.SendNowAsync($"KICK {Channel} {p.Nick}");
            RemovePlayer(p, "kicked (cancelled starts)");
        }
        else if (p.CancelledStarts == limit - 1)
            SendChat($"{p.Nick}: you have cancelled the start {p.CancelledStarts} times. Once more and you will be kicked.", UrgentChatPriority);
    }

    private void ClearReadyStatuses()
    {
        foreach (var p in Players)
            if (!p.AutoReady || p.IsInGame)
                p.Ready = false;
    }

    private void SetLocked(bool locked)
    {
        if (Locked == locked) return;
        Locked = locked;
        _ = _irc.SendNowAsync($"MODE {Channel} {(locked ? "+i" : "-i")}");
        _lastBroadcast = DateTime.MinValue;
    }

    private void Broadcast()
    {
        _lastBroadcast = DateTime.UtcNow;
        _irc.SendCtcp(_broadcastChannel, BuildGameBroadcast(closed: false), 20, "GAME");
    }

    private string BuildGameBroadcast(bool closed)
    {
        var f = new List<string>
        {
            _dialect.Revision,
            _dialect.GameVersion,
            (MaxPlayers + Spectators).ToString(), // the list shows players.Count/this; spectators are in the list too
            Channel,
            RoomName.Replace(";", ""),
            $"{(Locked ? 1 : 0)}{(string.IsNullOrEmpty(Def.Password) ? 0 : 1)}{(closed ? 1 : 0)}00",
            string.Join(",", Players.Select(p => p.Nick)),
            Map?.Name ?? "",
            Mode?.Name ?? "",
            TunnelMode == TunnelMode.V3Dynamic ? "[DYN]" : StaticTunnel?.Endpoint ?? "0.0.0.0:0",
            "0",
            Math.Clamp(Def.SkillLevel, 0, 3).ToString(),
            Map?.Sha1 ?? "",
            Options.BroadcastString(),
        };
        if (_dialect.GameFieldCount >= 15)
            f.Add("-1");
        return "GAME " + string.Join(";", f);
    }

    // ---- maps ------------------------------------------------------------------------------------

    /// <summary>The room's map pool for its current size; falls back to the plain filters if the admin excluded everything.</summary>
    private List<MapEntry> MapChoices()
    {
        var choices = _pool.Select(Def, MaxPlayers);
        if (choices.Count == 0)
        {
            Log.Warn(Tag, $"no maps left in the pool for {MaxPlayers} players after exclusions; using every map that matches the filters");
            choices = _pool.Maps.Where(m => MapPoolFile.MatchesFilters(m, Def, MaxPlayers)).ToList();
        }
        if (choices.Count == 0)
            choices = _pool.Maps.Where(m => m.MaxPlayers == MaxPlayers && m.GameModes.Contains(Def.GameMode, StringComparer.OrdinalIgnoreCase)).ToList();
        if (choices.Count == 0)
            choices = _pool.Maps.Where(m => m.MaxPlayers == MaxPlayers).ToList();
        if (choices.Count == 0)
            throw new InvalidOperationException($"no {MaxPlayers}-player maps in the pool");
        return choices;
    }

    /// <summary>Pick weight of a map from its !next history: 1 / (1 + penalty * max(0, skips - plays / 2)), never below 0.2.</summary>
    public static double MapWeight(double penalty, BotDb.MapStat? stat)
    {
        if (penalty <= 0 || stat is null) return 1;
        double net = Math.Max(0, stat.Skips - stat.Plays / 2.0);
        return Math.Max(0.2, 1 / (1 + penalty * net));
    }

    /// <summary>Random map from the pool, avoiding the last few and preferring maps players have not voted away.</summary>
    private void PickNextMap()
    {
        var choices = MapChoices();
        var candidates = choices.Where(m => !_recentMaps.Contains(m.BaseFilePath)).ToList();
        if (candidates.Count == 0)
        {
            _recentMaps.Clear();
            candidates = choices;
        }
        var stats = Def.NextMapPenalty > 0 ? _db.LoadMapStats(Def.Id) : new Dictionary<string, BotDb.MapStat>();
        var weights = candidates.Select(m => MapWeight(Def.NextMapPenalty, stats.GetValueOrDefault(m.BaseFilePath))).ToList();
        double roll = _rng.NextDouble() * weights.Sum();
        int pick = candidates.Count - 1;
        for (int i = 0; i < candidates.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0) { pick = i; break; }
        }
        ApplyMap(candidates[pick], choices.Count);
    }

    /// <summary>
    /// Size the room should have now: 6 while the lobby is busy and the automatic 3v3 rule is on,
    /// otherwise the configured size. Dropping back needs the lobby 10% below the threshold so a
    /// lobby hovering at the threshold does not flip the room every check.
    /// </summary>
    private int DesiredMaxPlayers()
    {
        if (!Def.AutoThreeVsThree || Def.MaxPlayers >= 6)
            return Def.MaxPlayers;
        int users = LobbyUsers, threshold = Math.Max(1, Def.AutoThreeVsThreeLobbyUsers);
        bool big = MaxPlayers == 6 ? users > threshold * 0.9 : users > threshold;
        return big ? 6 : Def.MaxPlayers;
    }

    /// <summary>Applies a size change (settings edit or lobby-size rule) while the room is empty.</summary>
    private void ReconsiderFormat()
    {
        int wanted = DesiredMaxPlayers();
        if (wanted == MaxPlayers) return;
        if (_pool.Select(Def, wanted).Count == 0)
        {
            Log.Warn(Tag, $"cannot switch to {wanted} players: no maps in the pool for that size");
            return;
        }
        Log.Info(Tag, $"room size {MaxPlayers} -> {wanted} ({wanted / 2}v{wanted / 2}; lobby {LobbyUsers} users, threshold {Def.AutoThreeVsThreeLobbyUsers}, auto {Def.AutoThreeVsThree})");
        _db.LogEvent(Def.Id, "format", null, null, null, $"{MaxPlayers} -> {wanted} players, lobby {LobbyUsers} users");
        MaxPlayers = wanted;
        _lastSettings = SettingsString(); // the room is empty; the next joiner reads the new limit from the game list
        _recentMaps.Clear();
        _irc.Queue($"MODE {Channel} +l {Capacity + 1}", 50);
        PickNextMap();
        SendGameOptions();
        SendPlayerExtraOptions();
        Broadcast();
    }

    private DateTime _mapChangedAt = DateTime.MinValue;

    private void ApplyMap(MapEntry map) => ApplyMap(map, MapChoices().Count);

    private void ApplyMap(MapEntry map, int poolSize)
    {
        Map = map;
        _mapChangedAt = DateTime.UtcNow;
        _votes.Clear();
        _mapVotes.Clear();
        _forceRandomSides = false;
        _fullCountdownShown = false;
        _recentMaps.Add(map.BaseFilePath);
        while (_recentMaps.Count > Math.Min(10, poolSize - 1))
            _recentMaps.RemoveAt(0);

        Options.Reset();
        Options.Apply(Def.GameOptions, "room config");
        if (Mode is not null) Options.Apply(Mode.ForcedOptions, $"game mode {Mode.Name}");
        Options.Apply(map.ForcedOptions, $"map {map.Name}");

        foreach (var p in Players)
            if (p.StartingLocation > map.MaxPlayers)
                p.StartingLocation = 0;
    }

    // ---- launching -------------------------------------------------------------------------------

    private void TryLaunch(DateTime now)
    {
        int minPlayers = MinPlayersToStart;
        var humans = Players.Where(p => p.SideId != _layout.SpectatorSideId).ToList();
        if (humans.Count < minPlayers || Players.Any(p => p.IsInGame) || !Players.All(p => p.Ready) || humans.Count < Math.Min(2, minPlayers))
        {
            CancelCountdown();
            return;
        }

        string? notice = null;
        if (Players.Where(p => p.ColorId > 0).GroupBy(p => p.ColorId).Any(g => g.Count() > 1))
            notice = "CLRS";
        else if (Map!.EnforceMaxPlayers && Players.Where(p => p.StartingLocation > 0).GroupBy(p => p.StartingLocation).Any(g => g.Count() > 1))
            notice = "SLOC";
        else if (Map.EnforceMaxPlayers && humans.Count > Map.MaxPlayers)
            notice = "TMPLRS";
        else
        {
            for (int i = 0; i < Players.Count; i++)
            {
                if (!Players[i].HashReceived) { notice = $"NVRFY {i}"; break; }
            }
        }
        if (notice is null && TunnelMode == TunnelMode.V3Dynamic)
        {
            var names = Players.Select(p => p.Nick).ToList();
            if (!Negotiations.AllSucceeded(names))
            {
                CancelCountdown();
                var pairs = Negotiations.Pairs(names).Where(x => x.status != NegotiationStatus.Succeeded).ToList();
                if (now - _lastLaunchNotice > TimeSpan.FromSeconds(30))
                {
                    _lastLaunchNotice = now;
                    SendChat("Everyone is ready - waiting for tunnel connections: " + string.Join(", ", pairs.Select(x => $"{x.a}-{x.b} ({x.status})")));
                }
                return;
            }
        }
        if (notice is not null)
        {
            CancelCountdown();
            if (now - _lastLaunchNotice > TimeSpan.FromSeconds(20))
            {
                _lastLaunchNotice = now;
                _irc.SendCtcp(Channel, notice, LaunchNoticePriority, "NOTIFY");
            }
            return;
        }

        if (Def.StartDelaySeconds > 0)
        {
            if (_countdownEndsAt is null)
            {
                if (_fullCountdownShown)
                {
                    // The full countdown was already announced for this line-up; after a cancel, just wait briefly and go.
                    _countdownEndsAt = now.AddSeconds(Math.Max(0, Def.QuickStartSeconds));
                    _countdownAnnounced = false;
                }
                else
                {
                    _countdownEndsAt = now.AddSeconds(Def.StartDelaySeconds);
                    _countdownAnnounced = true;
                    _fullCountdownShown = true;
                    SendChat($"Everyone is ready. The game starts in {Def.StartDelaySeconds} seconds - change your spot, side or colour now if you want.", UrgentChatPriority);
                }
                return;
            }
            if (now < _countdownEndsAt.Value)
                return;
        }
        _countdownEndsAt = null;
        _fullCountdownShown = false;
        _ = LaunchAsync();
    }

    private void CancelCountdown()
    {
        if (_countdownEndsAt is null) return;
        _countdownEndsAt = null;
        if (_countdownAnnounced)
            SendChat("Start cancelled - waiting for everyone to be ready again.", UrgentChatPriority);
        _countdownAnnounced = false;
    }

    private async Task LaunchAsync()
    {
        List<Player> roster;
        lock (_gate)
        {
            roster = Players.ToList();
            SetLocked(true);
        }

        string start;
        try
        {
            if (TunnelMode == TunnelMode.V2Legacy)
            {
                if (StaticTunnel is null) throw new InvalidOperationException("no V2 tunnel selected");
                var ports = await _tunnels.RequestV2PortsAsync(StaticTunnel, roster.Count, CancellationToken.None);
                if (ports.Count < roster.Count) throw new InvalidOperationException($"tunnel gave {ports.Count} ports for {roster.Count} players");
                var sb = new StringBuilder($"STARTV2 {GameId}");
                for (int i = 0; i < roster.Count; i++)
                    sb.Append(';').Append(roster[i].Nick).Append(';').Append(StaticTunnel.Address).Append(':').Append(ports[i]);
                start = sb.ToString();
            }
            else
            {
                string endpoint = TunnelMode == TunnelMode.V3Dynamic ? "0.0.0.0:0" : StaticTunnel?.Endpoint ?? throw new InvalidOperationException("no V3 tunnel selected");
                var sb = new StringBuilder($"STARTV3 {GameId}");
                foreach (var p in roster)
                    sb.Append(';').Append(NegotiationMatrix.PlayerId(p.Nick, Channel)).Append(';').Append(p.Nick).Append(';').Append(endpoint);
                start = sb.ToString();
            }
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"launch failed: {ex.Message}");
            lock (_gate)
            {
                _lastLaunchNotice = DateTime.UtcNow;
                SendChat("Could not start the game: " + ex.Message);
                SetLocked(Players.Count >= Capacity);
            }
            return;
        }

        lock (_gate)
        {
            if (Closed || Players.Count != roster.Count) return;
            // Written straight to the socket so nothing queued (or a PART when abandoning) can overtake it.
            _ = _irc.SendNowAsync($"NOTICE {Channel} :\x01{start}\x01");
            MatchStartedAt = DateTime.UtcNow;
            foreach (var p in Players)
            {
                p.IsInGame = true;
                p.StartedAt = MatchStartedAt;
            }
            CurrentMatchId = _db.StartMatch(Def.Id, RoomName, Map!, Mode!.Name, GameId, Seed, TunnelMode.ToString(), StaticTunnel?.Endpoint, roster);
            _db.BumpMapStat(Def.Id, Map!.BaseFilePath, skip: false);
            Log.Info(Tag, $"LAUNCH match {CurrentMatchId}: {Map.Name} with {string.Join(", ", roster.Select(p => p.Nick))}");
            _db.LogEvent(Def.Id, "launch", null, null, null, $"{Map.Name}: {string.Join(", ", roster.Select(p => p.Nick))}");
            _lastBroadcast = DateTime.MinValue;
            if (Def.AbandonAfterLaunch)
            {
                _db.EndMatch(CurrentMatchId.Value, MatchStartedAt.Value, "abandoned after launch", roster.Select(p => p.Nick).ToList());
                RecreateAt = DateTime.UtcNow.AddSeconds(Math.Max(5, Def.AbandonDelaySeconds));
                Log.Info(Tag, $"abandoning the room; a fresh one opens in {Math.Max(5, Def.AbandonDelaySeconds)} s");
            }
        }
    }

    private void CheckMatchEnd(DateTime now)
    {
        // Clients send RETURN when their game exits; a client that quits outright leaves the channel.
        bool anyInGame = Players.Any(p => p.IsInGame);
        bool stale = MatchStartedAt is { } s && now - s > TimeSpan.FromHours(4);
        if (anyInGame && !stale)
            return;

        var endedPlayers = Players.Select(p => p.Nick).ToList();
        _db.EndMatch(CurrentMatchId!.Value, now, stale ? "timeout" : "returned", endedPlayers);
        Log.Info(Tag, $"match {CurrentMatchId} ended ({(stale ? "timeout" : "all returned")}) after {(now - MatchStartedAt!.Value).TotalMinutes:F0} min");
        CurrentMatchId = null;
        MatchStartedAt = null;

        foreach (var p in Players)
        {
            p.IsInGame = false;
            p.StartedAt = null;
            p.WantsNextMap = false;
        }
        PickNextMap();
        Seed = _rng.Next();
        GameId = NewGameId();
        ClearReadyStatuses();
        SendGameOptions();
        SendPlayerOptions();
        SendPlayerExtraOptions();
        SetLocked(Players.Count >= Capacity);
        if (Players.Count > 0)
            SendChat($"Next map: {Map!.Name}. Ready up when you want to play again.");
        _lastBroadcast = DateTime.MinValue;
    }

    private int NewGameId()
    {
        // The client uses digits of the local time; any non-negative int is accepted.
        var t = DateTime.Now;
        string s = $"{_rng.Next(0, 20)}{t.Day}{t.Month}{t.Hour}{t.Minute}";
        return int.TryParse(s, out int id) ? id : _rng.Next(1, int.MaxValue);
    }

    private async Task ChooseStaticTunnelAsync(CancellationToken ct)
    {
        try
        {
            if (_tunnels.Tunnels.Count == 0 || DateTime.UtcNow - _tunnels.FetchedAt > TimeSpan.FromMinutes(5))
            {
                await _tunnels.RefreshAsync(ct);
                await _tunnels.PingAllAsync(ct);
            }
            int version = TunnelMode == TunnelMode.V2Legacy ? 2 : 3;
            var chosen = _tunnels.Choose(version, Def.Tunnel.Preferred);
            if (chosen is null)
                throw new InvalidOperationException($"no v{version} tunnel available");
            lock (_gate) StaticTunnel = chosen;
            Log.Info(Tag, $"tunnel: {chosen}");
        }
        catch (Exception ex) when (ct.IsCancellationRequested == false)
        {
            Log.Error(Tag, $"tunnel selection failed: {ex.Message}");
            throw;
        }
    }
}
