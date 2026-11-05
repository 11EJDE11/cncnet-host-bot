using HostBot.Irc;

namespace HostBot.Lobby;

/// <summary>A game another host is broadcasting, as parsed from its GAME CTCP.</summary>
public sealed record HostedGame(string Host, string Revision, string Version, int MaxPlayers, string Channel, string Name,
    string Flags, string[] Players, string Map, string Mode, string Tunnel, int FieldCount, DateTime Seen);

/// <summary>
/// Watches the broadcast channel: learns which client version and GAME field count the
/// population uses, and keeps the current game list for the admin page.
/// </summary>
public sealed class LobbyObserver
{
    private readonly string _broadcastChannel;
    private readonly object _gate = new();
    private readonly Dictionary<string, HostedGame> _games = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string revision, string version, int fields), int> _samples = new();

    public LobbyObserver(IrcClient irc, string broadcastChannel)
    {
        _broadcastChannel = broadcastChannel;
        irc.MessageReceived += OnMessage;
    }

    public int SampleCount { get { lock (_gate) return _samples.Values.Sum(); } }

    /// <summary>The most common (revision, version, field count) triple seen so far, or null.</summary>
    public (string revision, string version, int fields)? Dominant()
    {
        lock (_gate)
        {
            return _samples.Count == 0 ? null : _samples.OrderByDescending(kv => kv.Value).First().Key;
        }
    }

    public List<HostedGame> Games()
    {
        lock (_gate)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-35);
            foreach (var k in _games.Where(kv => kv.Value.Seen < cutoff || kv.Value.Flags.Length > 2 && kv.Value.Flags[2] == '1').Select(kv => kv.Key).ToList())
                _games.Remove(k);
            return _games.Values.OrderBy(g => g.Name).ToList();
        }
    }

    private void OnMessage(IrcMessage msg)
    {
        if (msg.Command != "NOTICE" || !string.Equals(msg.Param(0), _broadcastChannel, StringComparison.OrdinalIgnoreCase))
            return;
        string text = msg.Trailing;
        if (text.Length < 8 || text[0] != '\x01' || !text.StartsWith("\x01GAME ")) return;
        var fields = text[6..].TrimEnd('\x01').Split(';');
        if (fields.Length < 10) return;
        var game = new HostedGame(msg.Nick ?? "", fields[0], fields[1], int.TryParse(fields[2], out var mp) ? mp : 0, fields[3], fields[4], fields[5],
            fields[6].Split(',', StringSplitOptions.RemoveEmptyEntries), fields[7], fields[8], fields[9], fields.Length, DateTime.UtcNow);
        lock (_gate)
        {
            _games[game.Host] = game;
            var key = (game.Revision, game.Version, fields.Length);
            _samples[key] = _samples.GetValueOrDefault(key) + 1;
        }
    }
}
