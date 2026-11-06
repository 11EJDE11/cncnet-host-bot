using Microsoft.Data.Sqlite;

namespace HostBot.Idents;

/// <summary>
/// Where an ident has been seen. Idents are normalised (leading '~' stripped, lower-cased) before
/// they reach this class, so the same machine always maps to one row.
/// </summary>
public sealed record IdentHistory(string Ident, DateOnly FirstSeen, DateOnly LastSeen, int Days, string LastNick, string? LastHost);

/// <summary>
/// SQLite-backed store of ident sightings built from the CnCNet chat logs. One row per
/// (ident, day), so re-importing a day's log is idempotent.
/// </summary>
public sealed class IdentStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly string _connectionString;
    private readonly object _gate = new();
    private int? _identCount;          // full-table count, computed in the background (minutes on a cold disk)
    private Task? _identCountTask;

    public IdentStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        _db = new SqliteConnection(_connectionString);
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS sightings (
                ident TEXT NOT NULL,
                day   TEXT NOT NULL,      -- yyyy-MM-dd
                nick  TEXT NOT NULL,      -- last nick seen that day
                host  TEXT,               -- last cloaked host seen that day
                joins INTEGER NOT NULL,
                PRIMARY KEY (ident, day)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS sightings_day ON sightings(day);
            CREATE INDEX IF NOT EXISTS sightings_host ON sightings(host);
            CREATE TABLE IF NOT EXISTS imported_files (
                file TEXT PRIMARY KEY,    -- yyyy_MM/dd.txt
                imported_at TEXT NOT NULL,
                lines INTEGER NOT NULL,
                joins INTEGER NOT NULL
            );
            """);
    }

    public static string Normalise(string ident)
    {
        ident = ident.Trim();
        if (ident.StartsWith('~'))
            ident = ident[1..];
        return ident.ToLowerInvariant();
    }

    public IdentHistory? Lookup(string ident)
    {
        lock (_gate)
        {
            ident = Normalise(ident);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT MIN(day), MAX(day), COUNT(*),
                       (SELECT nick FROM sightings s2 WHERE s2.ident = s.ident ORDER BY day DESC LIMIT 1),
                       (SELECT host FROM sightings s3 WHERE s3.ident = s.ident ORDER BY day DESC LIMIT 1)
                FROM sightings s WHERE ident = $ident
                """;
            cmd.Parameters.AddWithValue("$ident", ident);
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0))
                return null;
            return new IdentHistory(ident,
                DateOnly.Parse(r.GetString(0)), DateOnly.Parse(r.GetString(1)),
                r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
        }
    }

    /// <summary>Earliest day on which the cloaked host part was seen, regardless of ident.</summary>
    public DateOnly? FirstSeenHost(string host)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT MIN(day) FROM sightings WHERE host = $host";
            cmd.Parameters.AddWithValue("$host", host);
            var v = cmd.ExecuteScalar();
            return v is string s ? DateOnly.Parse(s) : null;
        }
    }

    /// <summary>Idents whose ident, nick or host matches a LIKE pattern, most recently seen first.</summary>
    public List<IdentHistory> Search(string like, int limit)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT ident, MIN(day), MAX(day), COUNT(*),
                       (SELECT nick FROM sightings s2 WHERE s2.ident = s.ident ORDER BY day DESC LIMIT 1),
                       (SELECT host FROM sightings s3 WHERE s3.ident = s.ident ORDER BY day DESC LIMIT 1)
                FROM sightings s
                WHERE ident IN (SELECT DISTINCT ident FROM sightings WHERE ident LIKE $q OR nick LIKE $q OR host LIKE $q LIMIT $limit)
                GROUP BY ident ORDER BY MAX(day) DESC
                """;
            cmd.Parameters.AddWithValue("$q", like);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<IdentHistory>();
            while (r.Read())
                list.Add(new IdentHistory(r.GetString(0), DateOnly.Parse(r.GetString(1)), DateOnly.Parse(r.GetString(2)),
                    r.GetInt32(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
            return list;
        }
    }

    /// <summary>Every day an ident was seen, with the nick and host used that day.</summary>
    public List<object> Days(string ident)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT day, nick, host, joins FROM sightings WHERE ident = $i ORDER BY day DESC";
            cmd.Parameters.AddWithValue("$i", Normalise(ident));
            using var r = cmd.ExecuteReader();
            var list = new List<object>();
            while (r.Read())
                list.Add(new { day = r.GetString(0), nick = r.GetString(1), host = r.IsDBNull(2) ? null : r.GetString(2), joins = r.GetInt32(3) });
            return list;
        }
    }

    public bool IsImported(string file)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM imported_files WHERE file = $f";
            cmd.Parameters.AddWithValue("$f", file);
            return cmd.ExecuteScalar() != null;
        }
    }

    /// <summary>Replaces the sightings of one day's file with the given set.</summary>
    public void ImportDay(string file, DateOnly day, IReadOnlyDictionary<string, DaySighting> sightings, int lines)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using (var del = _db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM sightings WHERE day = $day";
                del.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd"));
                del.ExecuteNonQuery();
            }
            using (var ins = _db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO sightings (ident, day, nick, host, joins) VALUES ($i, $d, $n, $h, $j)";
                var pi = ins.Parameters.Add("$i", SqliteType.Text);
                var pd = ins.Parameters.Add("$d", SqliteType.Text);
                var pn = ins.Parameters.Add("$n", SqliteType.Text);
                var ph = ins.Parameters.Add("$h", SqliteType.Text);
                var pj = ins.Parameters.Add("$j", SqliteType.Integer);
                pd.Value = day.ToString("yyyy-MM-dd");
                foreach (var (ident, s) in sightings)
                {
                    pi.Value = ident;
                    pn.Value = s.Nick;
                    ph.Value = (object?)s.Host ?? DBNull.Value;
                    pj.Value = s.Joins;
                    ins.ExecuteNonQuery();
                }
            }
            using (var mark = _db.CreateCommand())
            {
                mark.Transaction = tx;
                mark.CommandText = """
                    INSERT INTO imported_files (file, imported_at, lines, joins) VALUES ($f, $t, $l, $j)
                    ON CONFLICT(file) DO UPDATE SET imported_at = $t, lines = $l, joins = $j
                    """;
                mark.Parameters.AddWithValue("$f", file);
                mark.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                mark.Parameters.AddWithValue("$l", lines);
                mark.Parameters.AddWithValue("$j", sightings.Values.Sum(s => s.Joins));
                mark.ExecuteNonQuery();
            }
            tx.Commit();
            _identCount = null;
        }
    }

    /// <summary>
    /// Totals. Days and the date range come from the imported_files table and the day index (instant);
    /// the distinct-ident count needs a pass over the whole 1 GB table, so it is computed once in the
    /// background on its own connection and reported as null until it is ready.
    /// </summary>
    public (int? idents, int days, DateOnly? first, DateOnly? last) Stats()
    {
        int days; DateOnly? first, last;
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM imported_files), (SELECT MIN(day) FROM sightings), (SELECT MAX(day) FROM sightings)";
            using var r = cmd.ExecuteReader();
            r.Read();
            days = r.GetInt32(0);
            first = r.IsDBNull(1) ? null : DateOnly.Parse(r.GetString(1));
            last = r.IsDBNull(2) ? null : DateOnly.Parse(r.GetString(2));
            if (_identCount is null && (_identCountTask is null || _identCountTask.IsCompleted))
                _identCountTask = Task.Run(CountIdents);
            return (_identCount, days, first, last);
        }
    }

    private void CountIdents()
    {
        try
        {
            using var db = new SqliteConnection(_connectionString);
            db.Open();
            using var cmd = db.CreateCommand();
            // GROUP BY walks the primary key in order: one pass, no temporary b-tree.
            cmd.CommandText = "SELECT COUNT(*) FROM (SELECT 1 FROM sightings GROUP BY ident)";
            int n = Convert.ToInt32(cmd.ExecuteScalar());
            lock (_gate) _identCount = n;
            Log.Info("idents", $"ident database holds {n} distinct idents");
        }
        catch (Exception ex)
        {
            Log.Warn("idents", $"ident count failed: {ex.Message}");
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

public sealed class DaySighting
{
    public string Nick = "";
    public string? Host;
    public int Joins;
}
