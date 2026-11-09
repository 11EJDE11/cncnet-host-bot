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
    /// <summary>Temporary room bans (!votekick, admin kick with ban): kept in memory, so they end at a restart.</summary>
    private readonly Dictionary<(string ident, string room), (DateTime until, string by)> _bans = new();

    /// <summary>Keeps the ident out of the room until the duration has passed, whatever the whitelist says.</summary>
    public void Ban(string ident, string roomId, TimeSpan duration, string by)
    {
        ident = IdentStore.Normalise(ident);
        if (ident.Length == 0) return;
        lock (_bans) _bans[(ident, roomId)] = (DateTime.UtcNow + duration, by);
    }

    private PolicyVerdict? BannedVerdict(string ident, string roomId)
    {
        lock (_bans)
        {
            if (!_bans.TryGetValue((ident, roomId), out var ban)) return null;
            if (ban.until <= DateTime.UtcNow) { _bans.Remove((ident, roomId)); return null; }
            return new PolicyVerdict(false, $"removed from this room by {ban.by}; you may come back after {ban.until:HH:mm} UTC", null);
        }
    }

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

    /// <summary>Whether this ident may join the room. The parameters mirror the room settings of the same names.</summary>
    public PolicyVerdict Evaluate(string ident, string? host, string roomId, RoomTier tier, int maxAgeDays, int minAgeDays, bool useHostAsFallback,
        string? nick = null, int nickMinDays = 0, int minActiveDays = 0)
    {
        ident = IdentStore.Normalise(ident);
        if (ident.Length > 0 && BannedVerdict(ident, roomId) is { } banned)
            return banned;
        if (ident.Length > 0 && Find(ident, roomId) is { } entry)
            return new PolicyVerdict(true, entry.IsInvite ? $"invited by {entry.AddedBy}" : "whitelisted", null, entry);

        var history = _store.Lookup(ident);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Activity floor: days seen in the chat logs, not ident age, so a regular on a new PC is not
        // mistaken for a newcomer (the ident the nick was handed over from lends its days).
        if (minActiveDays > 0)
        {
            int days = history?.Days ?? 0;
            if (days < minActiveDays && !string.IsNullOrWhiteSpace(nick) && nickMinDays > 0
                && HandedOver(nick, ident, history?.FirstSeen ?? today, today, 0, nickMinDays) is { } carried)
                days += carried.Days;
            if (days < minActiveDays)
                return new PolicyVerdict(false, history is null
                    ? $"ident never seen in the chat logs; this room needs {minActiveDays} days of play on CnCNet"
                    : $"seen on CnCNet on {days} days, this room needs {minActiveDays}", history);
        }

        if (tier == RoomTier.Open)
            return new PolicyVerdict(true, "open room", history);

        DateOnly? firstSeen = history?.FirstSeen;

        if (tier == RoomTier.Noob && useHostAsFallback && !string.IsNullOrEmpty(host))
        {
            var hostFirst = _store.FirstSeenHost(host);
            if (hostFirst is { } hf && (firstSeen is null || hf < firstSeen))
                firstSeen = hf;
        }

        int? ageDays = firstSeen is { } f ? today.DayNumber - f.DayNumber : null;

        if (tier == RoomTier.Noob && nickMinDays > 0 && !string.IsNullOrWhiteSpace(nick)
            && HandedOver(nick, ident, history?.FirstSeen ?? today, today, maxAgeDays, nickMinDays) is { } prev)
        {
            int prevAge = today.DayNumber - prev.FirstSeen.DayNumber;
            return new PolicyVerdict(false,
                $"the nick played on another ident for {prev.Days} days until {prev.LastSeen:yyyy-MM-dd} (first seen {prev.FirstSeen:yyyy-MM-dd}, {prevAge} days ago), limit {maxAgeDays} days", history);
        }

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

    /// <summary>Days the old ident may still appear after the new one (both machines in use for a moment).</summary>
    private const int HandoverOverlapDays = 3;
    /// <summary>How long the old ident may have been quiet before the new one appeared; a nick dropped for longer may be someone else's now.</summary>
    private const int HandoverGapDays = 180;

    /// <summary>
    /// The ident this nick was handed over from (a player on a new PC), or null. Nicks are not registered,
    /// so only a handover counts: the nick's last established ident went quiet when this one appeared.
    /// Someone else using a common name keeps playing on their own ident and does not match.
    /// </summary>
    private NickIdent? HandedOver(string nick, string ident, DateOnly since, DateOnly today, int minPrevAgeDays, int nickMinDays)
    {
        // Only the nick's most recently active established ident is judged: a shared name always has some
        // long-gone user whose ident went quiet, but its current owner keeps playing, which rules out a handover.
        foreach (var prev in _store.NickIdents(nick))
        {
            if (prev.Ident == ident || prev.Days < nickMinDays) continue;
            if (today.DayNumber - prev.FirstSeen.DayNumber <= minPrevAgeDays) continue;
            int overlap = prev.LastSeen.DayNumber - since.DayNumber;
            return overlap > HandoverOverlapDays || overlap < -HandoverGapDays ? null : prev;
        }
        return null;
    }
}
