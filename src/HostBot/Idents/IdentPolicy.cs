using HostBot.Storage;

namespace HostBot.Idents;

public enum RoomTier
{
    /// <summary>Only players whose ident is young (or unknown) may stay.</summary>
    Noob,
    /// <summary>Only players whose ident is old enough may stay.</summary>
    Pro,
    /// <summary>No ident checks.</summary>
    Open,
}

/// <summary>
/// One whitelist entry. <see cref="Room"/> is the room id it applies to, or "" for every room.
/// <see cref="Kind"/> is "config" (from config.json), "admin" (admin page) or "invite" (!invite by a player).
/// </summary>
public sealed record WhitelistEntry(string Ident, string Room, string Kind, string? AddedBy, string? Reason, DateTime? Expires, DateTime Created)
{
    public bool Expired => Expires is { } e && e <= DateTime.UtcNow;
    public bool IsInvite => Kind == "invite";
}

public sealed record PolicyVerdict(bool Allowed, string Reason, IdentHistory? History, WhitelistEntry? Whitelist = null);

/// <summary>
/// Decides whether a player may stay in a room from how long their ident has been seen in the
/// public chat logs. "Age" is the number of days since the ident was first seen. Whitelist entries
/// (global or per room, optionally expiring) bypass the check and are persisted in the bot database.
/// </summary>
public sealed class IdentPolicy
{
    private readonly IdentStore _store;
    private readonly BotDb _db;
    private readonly Dictionary<(string ident, string room), WhitelistEntry> _whitelist = new();

    public IdentPolicy(IdentStore store, BotDb db, IEnumerable<string> configWhitelist)
    {
        _store = store;
        _db = db;
        foreach (var e in db.LoadWhitelist())
            _whitelist[Key(e.Ident, e.Room)] = e;
        foreach (var raw in configWhitelist)
        {
            string ident = IdentStore.Normalise(raw);
            if (ident.Length > 0 && !_whitelist.ContainsKey(Key(ident, "")))
                Add(new WhitelistEntry(ident, "", "config", null, "config.json", null, DateTime.UtcNow));
        }
    }

    private static (string ident, string room) Key(string ident, string room) => (IdentStore.Normalise(ident), room.Trim().ToLowerInvariant());

    /// <summary>All live entries, global first then by room and ident. Expired entries are dropped on the way.</summary>
    public List<WhitelistEntry> Entries
    {
        get
        {
            lock (_whitelist)
            {
                PurgeExpired();
                return _whitelist.Values.OrderBy(e => e.Room).ThenBy(e => e.Ident).ToList();
            }
        }
    }

    public WhitelistEntry Add(WhitelistEntry entry)
    {
        entry = entry with { Ident = IdentStore.Normalise(entry.Ident), Room = entry.Room.Trim().ToLowerInvariant() };
        lock (_whitelist)
        {
            _whitelist[Key(entry.Ident, entry.Room)] = entry;
            _db.SaveWhitelist(entry);
        }
        return entry;
    }

    public bool Remove(string ident, string room)
    {
        var key = Key(ident, room);
        lock (_whitelist)
        {
            bool had = _whitelist.Remove(key);
            _db.DeleteWhitelist(key.ident, key.room);
            return had;
        }
    }

    /// <summary>The entry that lets this ident into this room: a global one, else the room's own.</summary>
    public WhitelistEntry? Find(string ident, string roomId)
    {
        lock (_whitelist)
        {
            PurgeExpired();
            if (_whitelist.TryGetValue(Key(ident, ""), out var g)) return g;
            if (_whitelist.TryGetValue(Key(ident, roomId), out var r)) return r;
            return null;
        }
    }

    private void PurgeExpired()
    {
        foreach (var (key, e) in _whitelist.Where(kv => kv.Value.Expired).ToList())
        {
            _whitelist.Remove(key);
            _db.DeleteWhitelist(key.ident, key.room);
        }
    }

    /// <param name="roomId">Room the player is joining; selects the room-scoped whitelist entries.</param>
    /// <param name="tier">Which rule set applies.</param>
    /// <param name="maxAgeDays">Noob rooms: idents first seen more than this many days ago are kicked.</param>
    /// <param name="minAgeDays">Pro rooms: idents first seen less than this many days ago (or never) are kicked.</param>
    /// <param name="useHostAsFallback">Also consider the cloaked host's first-seen date, so a fresh
    /// ident on a long-known connection still counts as old (noob rooms only).</param>
    public PolicyVerdict Evaluate(string ident, string? host, string roomId, RoomTier tier, int maxAgeDays, int minAgeDays, bool useHostAsFallback)
    {
        ident = IdentStore.Normalise(ident);
        if (ident.Length > 0 && Find(ident, roomId) is { } entry)
            return new PolicyVerdict(true, entry.IsInvite ? $"invited by {entry.AddedBy}" : "whitelisted", null, entry);
        if (tier == RoomTier.Open)
            return new PolicyVerdict(true, "open room", null);

        var history = _store.Lookup(ident);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly? firstSeen = history?.FirstSeen;

        if (tier == RoomTier.Noob && useHostAsFallback && !string.IsNullOrEmpty(host))
        {
            var hostFirst = _store.FirstSeenHost(host);
            if (hostFirst is { } hf && (firstSeen is null || hf < firstSeen))
                firstSeen = hf;
        }

        int? ageDays = firstSeen is { } f ? today.DayNumber - f.DayNumber : null;

        return tier switch
        {
            RoomTier.Noob when ageDays is { } a && a > maxAgeDays
                => new PolicyVerdict(false, $"ident first seen {firstSeen:yyyy-MM-dd} ({a} days ago), limit {maxAgeDays} days", history),
            RoomTier.Noob
                => new PolicyVerdict(true, ageDays is null ? "ident never seen before" : $"ident age {ageDays} days", history),
            RoomTier.Pro when ageDays is null
                => new PolicyVerdict(false, "ident never seen in the chat logs", history),
            RoomTier.Pro when ageDays < minAgeDays
                => new PolicyVerdict(false, $"ident first seen {firstSeen:yyyy-MM-dd} ({ageDays} days ago), need {minAgeDays} days", history),
            RoomTier.Pro
                => new PolicyVerdict(true, $"ident age {ageDays} days", history),
            _ => new PolicyVerdict(true, "open", history),
        };
    }
}
