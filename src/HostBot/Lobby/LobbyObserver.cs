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
    /// <summary>Hosts seen broadcasting each (revision, version, field count) triple.</summary>
    private readonly Dictionary<(string revision, string version, int fields), HashSet<string>> _samples = new();
    /// <summary>Distinct hosts a version needs before it can be chosen over the majority's.</summary>
    private const int MinHostsForVersion = 3;

    public LobbyObserver(IrcClient irc, string broadcastChannel)
    {
        _broadcastChannel = broadcastChannel;
        irc.MessageReceived += OnMessage;
    }

    public int SampleCount { get { lock (_gate) return _samples.Values.Sum(s => s.Count); } }

    /// <summary>
    /// The client to host for: the revision and field count most hosts use, and within those the newest
    /// version at least <see cref="MinHostsForVersion"/> hosts broadcast. During a rollout the majority
    /// still runs the old build, but the client updates itself at launch, so everyone arriving from now on
    /// is on the new one and a room broadcast with the old version string shows them "Incompatible".
    /// </summary>
    public (string revision, string version, int fields)? Dominant()
    {
        lock (_gate)
        {
            if (_samples.Count == 0) return null;
            var (revision, fields) = _samples.GroupBy(kv => (kv.Key.revision, kv.Key.fields))
                .OrderByDescending(g => g.Sum(kv => kv.Value.Count)).First().Key;
            var family = _samples.Where(kv => kv.Key.revision == revision && kv.Key.fields == fields).ToList();
            var attested = family.Where(kv => kv.Value.Count >= MinHostsForVersion).ToList();
            var pool = attested.Count > 0 ? attested : family;
            return pool.OrderByDescending(kv => VersionNumber(kv.Key.version)).ThenByDescending(kv => kv.Value.Count).First().Key;
        }
    }

    /// <summary>"9.4.0" → 9.4.0; anything that does not start with dotted digits sorts lowest.</summary>
    private static Version VersionNumber(string version)
    {
        int end = 0;
        while (end < version.Length && (char.IsDigit(version[end]) || version[end] == '.')) end++;
        var parts = version[..end].Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && Version.TryParse(string.Join('.', parts.Take(4)), out var v) ? v : new Version(0, 0);
    }

    /// <summary>"A 9.3.5 x14: 57 hosts, A 9.4.0 x14: 10 hosts" for the log.</summary>
    public string Summary()
    {
        lock (_gate)
        {
            return string.Join(", ", _samples.OrderByDescending(kv => kv.Value.Count)
                .Select(kv => $"{kv.Key.revision} {kv.Key.version} x{kv.Key.fields}: {kv.Value.Count} hosts"));
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
            if (!_samples.TryGetValue(key, out var hosts))
                _samples[key] = hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            hosts.Add(game.Host);
        }
    }
}
