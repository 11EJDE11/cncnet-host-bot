using System.Text.Json;
using HostBot.Config;
using HostBot.Idents;
using HostBot.Lobby;
using HostBot.Maps;
using Microsoft.Data.Sqlite;

namespace HostBot.Storage;

/// <summary>Rooms, chat log, lobby events and match history. All writes are small and synchronous.</summary>
public sealed class BotDb : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public BotDb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _db.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS rooms (
                id TEXT PRIMARY KEY, json TEXT NOT NULL, enabled INTEGER NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS chat (
                id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, room TEXT, channel TEXT NOT NULL,
                nick TEXT NOT NULL, ident TEXT, text TEXT NOT NULL, outgoing INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS chat_ts ON chat(ts);
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, room TEXT, type TEXT NOT NULL,
                nick TEXT, ident TEXT, host TEXT, detail TEXT);
            CREATE INDEX IF NOT EXISTS events_ts ON events(ts);
            CREATE TABLE IF NOT EXISTS matches (
                id INTEGER PRIMARY KEY AUTOINCREMENT, room TEXT NOT NULL, room_name TEXT NOT NULL,
                started TEXT NOT NULL, ended TEXT, map TEXT NOT NULL, map_path TEXT NOT NULL, mode TEXT NOT NULL,
                game_id INTEGER NOT NULL, seed INTEGER NOT NULL, tunnel_mode TEXT NOT NULL, tunnel TEXT,
                players TEXT NOT NULL, end_reason TEXT, end_players TEXT);
            CREATE INDEX IF NOT EXISTS matches_started ON matches(started);
            CREATE TABLE IF NOT EXISTS whitelist (
                ident TEXT NOT NULL, room TEXT NOT NULL DEFAULT '', kind TEXT NOT NULL, added_by TEXT, reason TEXT,
                expires TEXT, created_at TEXT NOT NULL, PRIMARY KEY (ident, room));
            """);
    }

    private static string Now => DateTime.UtcNow.ToString("o");

    // ---- rooms ----

    public List<RoomConfig> LoadRooms()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT json FROM rooms ORDER BY rowid";
            using var r = cmd.ExecuteReader();
            var list = new List<RoomConfig>();
            while (r.Read())
            {
                var room = JsonSerializer.Deserialize<RoomConfig>(r.GetString(0), BotConfig.JsonOptions);
                if (room is not null) list.Add(room);
            }
            return list;
        }
    }

    public void SaveRoom(RoomConfig room)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rooms (id, json, enabled, updated_at) VALUES ($id, $json, $enabled, $t)
                ON CONFLICT(id) DO UPDATE SET json = $json, enabled = $enabled, updated_at = $t
                """;
            cmd.Parameters.AddWithValue("$id", room.Id);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(room, BotConfig.JsonOptions));
            cmd.Parameters.AddWithValue("$enabled", room.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$t", Now);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteRoom(string id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM rooms WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- whitelist ----

    public List<WhitelistEntry> LoadWhitelist()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT ident, room, kind, added_by, reason, expires, created_at FROM whitelist";
            using var r = cmd.ExecuteReader();
            var list = new List<WhitelistEntry>();
            while (r.Read())
            {
                list.Add(new WhitelistEntry(r.GetString(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : DateTime.Parse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    DateTime.Parse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind)));
            }
            return list;
        }
    }

    public void SaveWhitelist(WhitelistEntry e)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO whitelist (ident, room, kind, added_by, reason, expires, created_at)
                VALUES ($ident, $room, $kind, $by, $reason, $exp, $created)
                ON CONFLICT(ident, room) DO UPDATE SET kind = $kind, added_by = $by, reason = $reason, expires = $exp, created_at = $created
                """;
            cmd.Parameters.AddWithValue("$ident", e.Ident);
            cmd.Parameters.AddWithValue("$room", e.Room);
            cmd.Parameters.AddWithValue("$kind", e.Kind);
            cmd.Parameters.AddWithValue("$by", (object?)e.AddedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$reason", (object?)e.Reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$exp", (object?)e.Expires?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", e.Created.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteWhitelist(string ident, string room)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM whitelist WHERE ident = $ident AND room = $room";
            cmd.Parameters.AddWithValue("$ident", ident);
            cmd.Parameters.AddWithValue("$room", room);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Number of events of a type by one ident in a room since a point in time (rate limits).</summary>
    public int CountEvents(string room, string type, string ident, DateTime since)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE room = $room AND type = $type AND ident = $ident AND ts >= $since";
            cmd.Parameters.AddWithValue("$room", room);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$ident", ident);
            cmd.Parameters.AddWithValue("$since", since.ToString("o"));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // ---- chat / events ----

    public void LogChat(string? room, string channel, string nick, string? ident, string text, bool outgoing)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO chat (ts, room, channel, nick, ident, text, outgoing) VALUES ($ts, $room, $ch, $nick, $ident, $text, $out)";
            cmd.Parameters.AddWithValue("$ts", Now);
            cmd.Parameters.AddWithValue("$room", (object?)room ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ch", channel);
            cmd.Parameters.AddWithValue("$nick", nick);
            cmd.Parameters.AddWithValue("$ident", (object?)ident ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$out", outgoing ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public void LogEvent(string? room, string type, string? nick, string? ident, string? host, string? detail)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO events (ts, room, type, nick, ident, host, detail) VALUES ($ts, $room, $type, $nick, $ident, $host, $detail)";
            cmd.Parameters.AddWithValue("$ts", Now);
            cmd.Parameters.AddWithValue("$room", (object?)room ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$nick", (object?)nick ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ident", (object?)ident ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$host", (object?)host ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- matches ----

    public long StartMatch(string room, string roomName, MapEntry map, string mode, int gameId, int seed, string tunnelMode, string? tunnel, IReadOnlyList<Player> players)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO matches (room, room_name, started, map, map_path, mode, game_id, seed, tunnel_mode, tunnel, players)
                VALUES ($room, $rn, $ts, $map, $mp, $mode, $gid, $seed, $tm, $tunnel, $players);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$room", room);
            cmd.Parameters.AddWithValue("$rn", roomName);
            cmd.Parameters.AddWithValue("$ts", Now);
            cmd.Parameters.AddWithValue("$map", map.Name);
            cmd.Parameters.AddWithValue("$mp", map.BaseFilePath);
            cmd.Parameters.AddWithValue("$mode", mode);
            cmd.Parameters.AddWithValue("$gid", gameId);
            cmd.Parameters.AddWithValue("$seed", seed);
            cmd.Parameters.AddWithValue("$tm", tunnelMode);
            cmd.Parameters.AddWithValue("$tunnel", (object?)tunnel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$players", JsonSerializer.Serialize(players.Select(p => new
            {
                p.Nick, p.Ident, p.Host, p.SideId, p.ColorId, p.StartingLocation, p.TeamId,
            })));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void EndMatch(long id, DateTime ended, string reason, IReadOnlyList<string> playersStillPresent)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE matches SET ended = $e, end_reason = $r, end_players = $p WHERE id = $id";
            cmd.Parameters.AddWithValue("$e", ended.ToString("o"));
            cmd.Parameters.AddWithValue("$r", reason);
            cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(playersStillPresent));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- admin queries (rows as dictionaries so the API can return them as-is) ----

    public List<Dictionary<string, object?>> Query(string sql, params (string name, object? value)[] args)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (r.Read())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < r.FieldCount; i++)
                    row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
