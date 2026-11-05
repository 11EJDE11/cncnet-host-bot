using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HostBot.Lobby;

public enum NegotiationStatus { NotStarted = 0, InProgress = 1, Succeeded = 2, Failed = 3 }

/// <summary>
/// Host-side bookkeeping for V3 dynamic tunnel mode (docs/protocol/tunnels.md section 4):
/// directional pair statuses reported by the players through NEGRPT.
/// </summary>
public sealed class NegotiationMatrix
{
    private readonly Dictionary<(string from, string to), NegotiationStatus> _status = new();
    private readonly Dictionary<(string from, string to), int> _ping = new();
    private readonly Dictionary<(string, string), DateTime> _updated = new();

    /// <summary>Deterministic player id every client computes: first 4 bytes of SHA1("name:channel"), little-endian.</summary>
    public static uint PlayerId(string name, string channelName)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(name + ":" + channelName));
        return BinaryPrimitives.ReadUInt32LittleEndian(hash);
    }

    /// <summary>Parses "hex8:status[:ping]|..." from <paramref name="sender"/>. Unknown ids are ignored.</summary>
    public void ApplyReport(string sender, string payload, IReadOnlyDictionary<uint, string> idToName)
    {
        foreach (var entry in payload.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = entry.Split(':');
            if (p.Length < 2) continue;
            if (!uint.TryParse(p[0], System.Globalization.NumberStyles.HexNumber, null, out uint id)) continue;
            if (!idToName.TryGetValue(id, out var target)) continue;
            if (!int.TryParse(p[1], out int s) || s < 0 || s > 3) continue;
            var key = (sender, target);
            _status[key] = (NegotiationStatus)s;
            _updated[key] = DateTime.UtcNow;
            if (p.Length >= 3 && int.TryParse(p[2], out int ping))
                _ping[key] = ping;
            else
                _ping.Remove(key);
        }
    }

    public void RemovePlayer(string name)
    {
        foreach (var key in _status.Keys.Where(k => k.from == name || k.to == name).ToList())
        {
            _status.Remove(key);
            _ping.Remove(key);
            _updated.Remove(key);
        }
    }

    /// <summary>Forgets both directions of a pair, so it reads NotStarted until fresh reports arrive.</summary>
    public void ResetPair(string a, string b)
    {
        foreach (var key in new[] { (a, b), (b, a) })
        {
            _status.Remove(key);
            _ping.Remove(key);
            _updated.Remove(key);
        }
    }

    public void Clear()
    {
        _status.Clear();
        _ping.Clear();
        _updated.Clear();
    }

    /// <summary>Pair status rule of NegotiationDataManager.GetNegotiationStatus.</summary>
    public NegotiationStatus PairStatus(string a, string b)
    {
        var s1 = _status.GetValueOrDefault((a, b), NegotiationStatus.NotStarted);
        var s2 = _status.GetValueOrDefault((b, a), NegotiationStatus.NotStarted);
        if (s1 == NegotiationStatus.Failed || s2 == NegotiationStatus.Failed) return NegotiationStatus.Failed;
        if (s1 == NegotiationStatus.InProgress || s2 == NegotiationStatus.InProgress) return NegotiationStatus.InProgress;
        if (s1 == NegotiationStatus.Succeeded && s2 == NegotiationStatus.Succeeded) return NegotiationStatus.Succeeded;
        if (s1 == NegotiationStatus.Succeeded || s2 == NegotiationStatus.Succeeded) return NegotiationStatus.InProgress;
        return NegotiationStatus.NotStarted;
    }

    public int? PairPing(string a, string b)
    {
        if (_ping.TryGetValue((a, b), out var p)) return p;
        if (_ping.TryGetValue((b, a), out p)) return p;
        return null;
    }

    public IEnumerable<(string a, string b, NegotiationStatus status)> Pairs(IReadOnlyList<string> names)
    {
        for (int i = 0; i < names.Count; i++)
            for (int j = i + 1; j < names.Count; j++)
                yield return (names[i], names[j], PairStatus(names[i], names[j]));
    }

    public bool AllSucceeded(IReadOnlyList<string> names) => Pairs(names).All(p => p.status == NegotiationStatus.Succeeded);
}
