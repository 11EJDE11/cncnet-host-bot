using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HostBot.Irc;

/// <summary>A user as seen in a channel: ident and host are only known from a JOIN prefix or WHO reply.</summary>
public sealed class IrcUser
{
    public string Nick { get; set; } = "";
    public string? Ident { get; set; }
    public string? Host { get; set; }
    public bool IsOp { get; set; }
    public DateTime JoinedAt { get; set; }
}

public sealed class ChannelState
{
    public string Name { get; }
    public Dictionary<string, IrcUser> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Joined { get; set; }
    public ChannelState(string name) => Name = name;
}

/// <summary>
/// Plain-TCP IRC client speaking exactly what the CnCNet client speaks (docs/protocol/irc-layer.md):
/// USER/NICK registration, PONG replies, LAG keepalive pings, CTCP via NOTICE, and a priority send
/// queue with a fixed gap between lines so GameSurge's flood limits are not tripped.
/// </summary>
public sealed class IrcClient : IAsyncDisposable
{
    private const string Tag = "irc";

    public sealed record ServerEndpoint(string Host, int Port);

    /// <summary>GameSurge entries from the YR client's NetworkDefinitions.ini, in its order.</summary>
    public static readonly ServerEndpoint[] DefaultServers =
    {
        new("irc.gamesurge.net", 6667),
        new("LAN-Team.DE.EU.GameSurge.net", 6667),
        new("Stockholm.SE.EU.GameSurge.net", 6666),
        new("NuclearFallout.WA.US.GameSurge.net", 6667),
        new("Prothid.NY.US.GameSurge.Net", 6667),
        new("irc.gamesurge.net", 6660),
        new("irc.gamesurge.net", 6666),
        new("irc.gamesurge.net", 6668),
        new("irc.gamesurge.net", 6669),
    };

    private readonly string _ident;
    private readonly string _realName;
    private readonly int _maxNickLength;
    private readonly IReadOnlyList<ServerEndpoint> _servers;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _linkCts;
    private Task? _readTask;
    private Task? _sendTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly PriorityQueue<QueuedLine, (int, long)> _queue = new();
    private readonly object _queueGate = new();
    private readonly Dictionary<string, QueuedLine> _coalesce = new();
    private long _queueSeq;
    private DateTime _lastReceived = DateTime.UtcNow;
    private DateTime _lastKeepAlive = DateTime.UtcNow;
    private TaskCompletionSource _registeredTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Nick { get; private set; }
    public bool Connected => _tcp?.Connected == true;
    public bool Registered { get; private set; }
    public string? OwnHost { get; private set; }
    /// <summary>Minimum gap between queued lines; the flood budget below is what really paces sending.</summary>
    public TimeSpan SendGap { get; set; } = TimeSpan.FromMilliseconds(250);

    // ircu flood control: every line costs 2 s plus 1 s per 120 bytes of "lag"; a client more
    // than 10 s ahead of real time is killed with "Excess Flood". Stay inside an 8 s budget.
    private const double FloodBurstSeconds = 8.0;
    private double _floodSince;
    private static double NowSeconds => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
    public ConcurrentDictionary<string, ChannelState> Channels { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised on the read loop for every parsed line, after internal bookkeeping.</summary>
    public event Action<IrcMessage>? MessageReceived;
    /// <summary>Raised when the link drops unexpectedly (not on DisposeAsync).</summary>
    public event Action<Exception?>? Disconnected;

    public IrcClient(string nick, string ident, string realName, int maxNickLength = 15, IEnumerable<ServerEndpoint>? servers = null)
    {
        Nick = nick;
        _ident = ident;
        _realName = realName;
        _maxNickLength = maxNickLength;
        _servers = (servers ?? DefaultServers).ToList();
    }

    private sealed class QueuedLine
    {
        public string Line = "";
        public string? CoalesceKey;
        public bool Cancelled;
        public DateTime EnqueuedAt;
        /// <summary>Called after the line went out, with the time it spent in the queue.</summary>
        public Action<TimeSpan>? OnSent;
    }

    /// <summary>Connects to the first reachable server and completes once numeric 001 arrives.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        Exception? last = null;
        foreach (var server in _servers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var addresses = (await Dns.GetHostAddressesAsync(server.Host, ct))
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
                if (addresses.Length == 0)
                    throw new SocketException((int)SocketError.HostNotFound);

                var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(addresses, server.Port, timeout.Token);
                _tcp = tcp;
                _stream = tcp.GetStream();
                Log.Info(Tag, $"{Nick}: connected to {server.Host}:{server.Port} ({addresses[0]})");
                break;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                last = ex;
                Log.Warn(Tag, $"{Nick}: {server.Host}:{server.Port} failed: {ex.Message}");
            }
        }
        if (_stream is null)
            throw new IOException("no IRC server reachable", last);

        _linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _floodSince = 0;
        _registeredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Registered = false;
        _lastReceived = DateTime.UtcNow;
        _readTask = Task.Run(() => ReadLoopAsync(_linkCts.Token));
        _sendTask = Task.Run(() => SendLoopAsync(_linkCts.Token));

        // The stock client registers before reading the banner.
        await SendNowAsync($"USER {_ident} 0 * :{_realName}", ct);
        await SendNowAsync($"NICK {Nick}", ct);

        using var regTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        regTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        await _registeredTcs.Task.WaitAsync(regTimeout.Token);
    }

    // ---- sending ----------------------------------------------------------------------------

    /// <summary>Writes a line immediately, bypassing the queue (JOIN/KICK/MODE in the stock client).</summary>
    public async Task SendNowAsync(string line, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await _writeLock.WaitAsync(ct);
        try
        {
            double cost = 2.0 + bytes.Length / 120.0;
            double now = NowSeconds;
            if (_floodSince < now) _floodSince = now;
            double ahead = _floodSince + cost - now;
            if (ahead > FloodBurstSeconds)
            {
                Log.Debug(Tag, $"{Nick}: flood budget full, waiting {ahead - FloodBurstSeconds:F1} s");
                await Task.Delay(TimeSpan.FromSeconds(ahead - FloodBurstSeconds), ct);
                now = NowSeconds;
                if (_floodSince < now) _floodSince = now;
            }
            await stream.WriteAsync(bytes, ct);
            _floodSince += cost;
            Log.Debug(Tag, $"{Nick} >> {line}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>How long the flood budget still has to recover before this line could be written now (≤ 0: right away).</summary>
    private double BudgetWaitSeconds(string line)
    {
        double cost = 2.0 + Encoding.UTF8.GetByteCount(line + "\r\n") / 120.0;
        double now = NowSeconds;
        return Math.Max(_floodSince, now) + cost - now - FloodBurstSeconds;
    }

    /// <summary>
    /// Queues a line. Higher priority goes first; equal priority is FIFO. A non-null coalesce key
    /// replaces a still-queued line with the same key (the client does this for GAME, PO, GO...).
    /// </summary>
    public void Queue(string line, int priority = 0, string? coalesceKey = null, Action<TimeSpan>? onSent = null)
    {
        lock (_queueGate)
        {
            if (coalesceKey is not null && _coalesce.TryGetValue(coalesceKey, out var old))
                old.Cancelled = true;
            var q = new QueuedLine { Line = line, CoalesceKey = coalesceKey, EnqueuedAt = DateTime.UtcNow, OnSent = onSent };
            if (coalesceKey is not null)
                _coalesce[coalesceKey] = q;
            _queue.Enqueue(q, (-priority, _queueSeq++));
        }
    }

    public void SendCtcp(string channel, string payload, int priority = 0, string? coalesceKey = null, Action<TimeSpan>? onSent = null)
        => Queue($"NOTICE {channel} :\x01{payload}\x01", priority, coalesceKey, onSent);

    public void SendChat(string channel, string text, int colorId = 10, int priority = 0, Action<TimeSpan>? onSent = null)
        => Queue($"PRIVMSG {channel} :\x03{colorId:D2}{text}", priority, null, onSent);

    public void SendPrivateMessage(string nick, string text) => Queue($"PRIVMSG {nick} :{text}", 0);

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                QueuedLine? next = null;
                double wait = 0;
                lock (_queueGate)
                {
                    // A line leaves the queue only once the flood budget can take it. While it
                    // waits it stays replaceable, so a newer line with the same coalesce key (the
                    // PO after a second join) goes out instead of the stale one followed by it.
                    while (_queue.TryPeek(out var q, out _))
                    {
                        if (q.Cancelled) { _queue.Dequeue(); continue; }
                        wait = BudgetWaitSeconds(q.Line);
                        if (wait > 0) break;
                        _queue.Dequeue();
                        if (q.CoalesceKey is not null && _coalesce.TryGetValue(q.CoalesceKey, out var cur) && cur == q)
                            _coalesce.Remove(q.CoalesceKey);
                        next = q;
                        break;
                    }
                }
                if (next is null)
                {
                    await Task.Delay(wait > 0 ? (int)Math.Clamp(wait * 1000, 20, 100) : 20, ct);
                    continue;
                }
                await SendNowAsync(next.Line, ct);
                if (next.OnSent is { } onSent)
                {
                    try { onSent(DateTime.UtcNow - next.EnqueuedAt); }
                    catch (Exception ex) { Log.Warn(Tag, $"{Nick}: send callback failed: {ex.Message}"); }
                }
                await Task.Delay(SendGap, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"{Nick}: send loop ended: {ex.Message}");
        }
    }

    // ---- receiving ---------------------------------------------------------------------------

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            var stream = _stream!;
            var buffer = new byte[4096];
            var pending = new MemoryStream();
            while (!ct.IsCancellationRequested)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(TimeSpan.FromSeconds(5));
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer, readCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await KeepAliveTickAsync(ct);
                    continue;
                }
                if (n == 0)
                    throw new IOException("connection closed by server");

                _lastReceived = DateTime.UtcNow;
                pending.Write(buffer, 0, n);
                var data = pending.GetBuffer();
                int len = (int)pending.Length, start = 0;
                for (int i = 0; i < len; i++)
                {
                    if (data[i] != (byte)'\n') continue;
                    int end = i > start && data[i - 1] == (byte)'\r' ? i - 1 : i;
                    string line = Encoding.UTF8.GetString(data, start, end - start);
                    start = i + 1;
                    if (line.Length > 0)
                        await HandleLineAsync(line, ct);
                }
                var rest = len - start;
                var tail = rest > 0 ? data.AsSpan(start, rest).ToArray() : Array.Empty<byte>();
                pending.SetLength(0);
                pending.Write(tail);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            error = ex;
        }
        if (!ct.IsCancellationRequested)
        {
            Log.Warn(Tag, $"{Nick}: disconnected: {error?.Message ?? "unknown"}");
            Registered = false;
            _registeredTcs.TrySetException(error ?? new IOException("disconnected"));
            Disconnected?.Invoke(error);
        }
    }

    private async Task KeepAliveTickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - _lastReceived > TimeSpan.FromSeconds(120))
            throw new IOException("no data from server for 120 s");
        if (now - _lastKeepAlive > TimeSpan.FromSeconds(30) && Registered)
        {
            _lastKeepAlive = now;
            await SendNowAsync($"PING LAG{Random.Shared.Next(100000, 999999)}", ct);
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken ct)
    {
        Log.Debug(Tag, $"{Nick} << {line}");
        IrcMessage msg;
        try { msg = IrcMessage.Parse(line); }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"unparsable line: {ex.Message}: {line}");
            return;
        }

        switch (msg.Command)
        {
            case "PING":
                await SendNowAsync(msg.Params.Count > 0 ? $"PONG :{msg.Trailing}" : "PONG", ct);
                break;
            case "001":
                Registered = true;
                if (msg.Params.Count > 0) Nick = msg.Params[0];
                _lastKeepAlive = DateTime.UtcNow;
                Log.Info(Tag, $"{Nick}: registered");
                _registeredTcs.TrySetResult();
                break;
            case "433": // nick in use: same rule as the stock client
            {
                string wanted = msg.Param(1);
                if (string.Equals(wanted, Nick, StringComparison.OrdinalIgnoreCase) && !Registered)
                {
                    string next = Nick.Length < _maxNickLength ? Nick + "_" : ReplaceLastNonUnderscore(Nick);
                    if (next == Nick)
                    {
                        _registeredTcs.TrySetException(new InvalidOperationException("no free nick"));
                        break;
                    }
                    Nick = next;
                    Log.Warn(Tag, $"nick in use, trying {Nick}");
                    await SendNowAsync($"NICK {Nick}", ct);
                }
                break;
            }
            case "451":
                await SendNowAsync($"USER {_ident} 0 * :{_realName}", ct);
                await SendNowAsync($"NICK {Nick}", ct);
                break;
            case "JOIN":
            {
                string chan = msg.Param(0);
                var state = Channels.GetOrAdd(chan, n => new ChannelState(n));
                var nick = msg.Nick ?? "";
                state.Users[nick] = new IrcUser { Nick = nick, Ident = msg.Ident, Host = msg.Host, JoinedAt = DateTime.UtcNow };
                if (string.Equals(nick, Nick, StringComparison.OrdinalIgnoreCase))
                {
                    state.Joined = true;
                    OwnHost ??= msg.Host;
                }
                break;
            }
            case "353":
            {
                var state = Channels.GetOrAdd(msg.Param(2), n => new ChannelState(n));
                foreach (var raw in msg.Trailing.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    bool op = raw.StartsWith('@');
                    string nick = raw.TrimStart('@', '+');
                    if (!state.Users.TryGetValue(nick, out var u))
                        state.Users[nick] = u = new IrcUser { Nick = nick, JoinedAt = DateTime.UtcNow };
                    u.IsOp |= op;
                }
                break;
            }
            case "352": // WHO reply: <chan> <ident> <host> <server> <nick> ... ("WHO nick" may answer with any channel or "*")
            {
                foreach (var state in Channels.Values)
                {
                    if (state.Users.TryGetValue(msg.Param(5), out var u))
                    {
                        u.Ident ??= msg.Param(2);
                        u.Host ??= msg.Param(3);
                    }
                }
                break;
            }
            case "PART":
                RemoveUser(msg.Param(0), msg.Nick);
                break;
            case "KICK":
                RemoveUser(msg.Param(0), msg.Param(1));
                break;
            case "QUIT":
                foreach (var ch in Channels.Values)
                    RemoveUser(ch.Name, msg.Nick);
                break;
            case "NICK":
            {
                string oldNick = msg.Nick ?? "", newNick = msg.Trailing;
                foreach (var ch in Channels.Values)
                {
                    if (ch.Users.Remove(oldNick, out var u))
                    {
                        u.Nick = newNick;
                        ch.Users[newNick] = u;
                    }
                }
                if (string.Equals(oldNick, Nick, StringComparison.OrdinalIgnoreCase))
                    Nick = newNick;
                break;
            }
            case "MODE":
            {
                // Track op status only; the lobby looks at the raw mode string itself.
                if (Channels.TryGetValue(msg.Param(0), out var state) && msg.Params.Count >= 3)
                {
                    string modes = msg.Param(1);
                    bool adding = true;
                    int arg = 2;
                    foreach (char c in modes)
                    {
                        if (c == '+') { adding = true; continue; }
                        if (c == '-') { adding = false; continue; }
                        if (c is 'o' or 'v' or 'k' or 'l' or 'b')
                        {
                            string a = msg.Param(arg++);
                            if (c == 'o' && state.Users.TryGetValue(a, out var u))
                                u.IsOp = adding;
                        }
                    }
                }
                break;
            }
        }

        try
        {
            MessageReceived?.Invoke(msg);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"{Nick}: handler failed on '{line}': {ex}");
        }
    }

    private void RemoveUser(string channel, string? nick)
    {
        if (nick is null) return;
        if (!Channels.TryGetValue(channel, out var state)) return;
        state.Users.Remove(nick);
        if (string.Equals(nick, Nick, StringComparison.OrdinalIgnoreCase))
            state.Joined = false;
    }

    private static string ReplaceLastNonUnderscore(string nick)
    {
        for (int i = nick.Length - 1; i >= 0; i--)
        {
            if (nick[i] != '_')
                return nick[..i] + "_" + nick[(i + 1)..];
        }
        return nick;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_stream is not null && Connected)
                await SendNowAsync("QUIT");
        }
        catch { }
        _linkCts?.Cancel();
        _tcp?.Dispose();
        if (_readTask is not null) try { await _readTask; } catch { }
        if (_sendTask is not null) try { await _sendTask; } catch { }
    }
}
