using System.Text.RegularExpressions;
using HostBot.Config;
using HostBot.Idents;
using HostBot.Maps;
using HostBot.Storage;
using HostBot.Tunnels;

namespace HostBot.Lobby;

/// <summary>Owns every RoomHost and persists their definitions.</summary>
public sealed class RoomManager
{
    private const string Tag = "rooms";
    private readonly BotConfig _cfg;
    private readonly TunnelList _tunnels;
    private readonly IdentPolicy _policy;
    private readonly MapPoolFile _pool;
    private readonly OptionLayout _layout;
    private readonly BotDb _db;
    private readonly Dictionary<string, RoomHost> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public RoomManager(BotConfig cfg, TunnelList tunnels, IdentPolicy policy, MapPoolFile pool, OptionLayout layout, BotDb db)
    {
        _cfg = cfg;
        _tunnels = tunnels;
        _policy = policy;
        _pool = pool;
        _layout = layout;
        _db = db;
    }

    public IReadOnlyList<RoomHost> Hosts { get { lock (_gate) return _hosts.Values.ToList(); } }

    /// <summary>Loads rooms from the database, seeding it from config.json on first run, and starts the enabled ones.</summary>
    public void StartAll()
    {
        var rooms = _db.LoadRooms();
        if (rooms.Count == 0 && _cfg.Rooms.Count > 0)
        {
            foreach (var r in _cfg.Rooms)
            {
                EnsureId(r);
                _db.SaveRoom(r);
            }
            rooms = _db.LoadRooms();
            Log.Info(Tag, $"seeded {rooms.Count} rooms from config");
        }
        foreach (var r in rooms)
        {
            var host = Create(r);
            if (r.Enabled) host.Start();
        }
    }

    private RoomHost Create(RoomConfig def)
    {
        lock (_gate)
        {
            bool first = _hosts.Count == 0;
            var host = new RoomHost(def, _cfg, _tunnels, _policy, _pool, _layout, _db, logLobbyChat: first && _cfg.Irc.LogLobbyChat);
            _hosts[def.Id] = host;
            return host;
        }
    }

    public RoomHost? Get(string id) { lock (_gate) return _hosts.GetValueOrDefault(id); }

    public RoomHost Add(RoomConfig def)
    {
        EnsureId(def);
        lock (_gate)
        {
            if (_hosts.ContainsKey(def.Id))
                throw new InvalidOperationException($"room id {def.Id} exists");
            if (_hosts.Values.Any(h => h.Def.Nick.Equals(def.Nick, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"nick {def.Nick} is used by another room");
        }
        Validate(def);
        _db.SaveRoom(def);
        var host = Create(def);
        if (def.Enabled) host.Start();
        Log.Info(Tag, $"added room {def.Id} ({def.Name})");
        return host;
    }

    public async Task UpdateAsync(string id, RoomConfig def, bool restart)
    {
        var host = Get(id) ?? throw new KeyNotFoundException(id);
        def.Id = id;
        Validate(def);
        _db.SaveRoom(def);
        host.Update(def);
        if (restart && host.Running)
        {
            await host.StopAsync("settings changed");
            var fresh = Create(def);
            if (def.Enabled) fresh.Start();
        }
        else if (def.Enabled && !host.Running)
        {
            Create(def).Start();
        }
        else if (!def.Enabled && host.Running)
        {
            await host.StopAsync("disabled");
        }
    }

    public async Task StopAsync(string id, string reason)
    {
        var host = Get(id) ?? throw new KeyNotFoundException(id);
        await host.StopAsync(reason);
        host.Def.Enabled = false;
        _db.SaveRoom(host.Def);
    }

    public void StartRoom(string id)
    {
        var host = Get(id) ?? throw new KeyNotFoundException(id);
        if (host.Running) return;
        host.Def.Enabled = true;
        _db.SaveRoom(host.Def);
        Create(host.Def).Start();
    }

    public async Task DeleteAsync(string id)
    {
        var host = Get(id) ?? throw new KeyNotFoundException(id);
        await host.StopAsync("deleted");
        lock (_gate) _hosts.Remove(id);
        _db.DeleteRoom(id);
    }

    public async Task StopAllAsync(string reason)
    {
        foreach (var h in Hosts)
            await h.StopAsync(reason);
    }

    private void Validate(RoomConfig def)
    {
        if (string.IsNullOrWhiteSpace(def.Nick) || def.Nick.Length > 15 || !Regex.IsMatch(def.Nick, @"^[A-Za-z_\[\]|\\{}^`][A-Za-z0-9\-_\[\]|\\{}^`]*$"))
            throw new ArgumentException("nick must be 1-15 IRC-safe characters and not start with a digit or '-'");
        if (string.IsNullOrWhiteSpace(def.Name) || def.Name.Length > 23)
            throw new ArgumentException("room name must be 1-23 characters");
        if (def.MaxPlayers is < 2 or > 8)
            throw new ArgumentException("maxPlayers must be 2-8");
        if (_pool.Select(def).Count == 0)
            throw new ArgumentException("no maps match the room's game mode / player count filters");
    }

    private static void EnsureId(RoomConfig def)
    {
        if (string.IsNullOrWhiteSpace(def.Id))
            def.Id = Regex.Replace(def.Nick.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    }
}
