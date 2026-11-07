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
    private int? _identCount;          // from the summary table once it exists
    private Task? _summaryTask;
    /// <summary>True once the per-ident summary table (idents) is complete; Search needs it.</summary>
    public bool SummaryReady { get; private set; }

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
            -- One row per ident, kept in step by ImportDay and rebuilt in the background when empty:
            -- the admin search and the counts must not walk the 6.6 million sighting rows.
            CREATE TABLE IF NOT EXISTS idents (
                ident     TEXT PRIMARY KEY,
                first     TEXT NOT NULL,
                last      TEXT NOT NULL,
                days      INTEGER NOT NULL,
                last_nick TEXT NOT NULL,
                last_host TEXT
            ) WITHOUT ROWID;
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

    /// <summary>
    /// Idents whose ident, last nick or last host matches a LIKE pattern, most recently seen first.
    /// Reads the summary table only (earlier nicks of an ident are not searched).
    /// </summary>
    public List<IdentHistory> Search(string like, int limit)
    {
        if (!SummaryReady)
        {
            StartSummaryBuild();
            throw new InvalidOperationException("The ident index is still being built (a few minutes after start-up). Try again shortly.");
        }
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT ident, first, last, days, last_nick, last_host FROM idents
                WHERE ident LIKE $q OR last_nick LIKE $q OR last_host LIKE $q
                ORDER BY last DESC LIMIT $limit
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
            // Every ident whose rows change gets its summary row recomputed below.
            var touched = new HashSet<string>(sightings.Keys);
            using (var old = _db.CreateCommand())
            {
                old.Transaction = tx;
                old.CommandText = "SELECT ident FROM sightings WHERE day = $day";
                old.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd"));
                using var r = old.ExecuteReader();
                while (r.Read()) touched.Add(r.GetString(0));
            }
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
            using (var sum = _db.CreateCommand())
            {
                sum.Transaction = tx;
                sum.CommandText = SummaryUpsertSql;
                var ps = sum.Parameters.Add("$i", SqliteType.Text);
                using var gone = _db.CreateCommand();
                gone.Transaction = tx;
                gone.CommandText = "DELETE FROM idents WHERE ident = $i AND NOT EXISTS (SELECT 1 FROM sightings WHERE ident = $i)";
                var pg = gone.Parameters.Add("$i", SqliteType.Text);
                foreach (var ident in touched)
                {
                    ps.Value = ident;
                    if (sum.ExecuteNonQuery() == 0) { pg.Value = ident; gone.ExecuteNonQuery(); }
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

    /// <summary>Recomputes one ident's summary row from its sightings (no rows: nothing written, caller deletes).</summary>
    private const string SummaryUpsertSql = """
        INSERT OR REPLACE INTO idents (ident, first, last, days, last_nick, last_host)
        SELECT $i, MIN(day), MAX(day), COUNT(*),
               (SELECT nick FROM sightings WHERE ident = $i ORDER BY day DESC LIMIT 1),
               (SELECT host FROM sightings WHERE ident = $i ORDER BY day DESC LIMIT 1)
        FROM sightings WHERE ident = $i HAVING COUNT(*) > 0
        """;

    /// <summary>
    /// Totals. Days and the date range come from the imported_files table and the day index (instant);
    /// the ident count comes from the summary table and is null until that has been built.
    /// </summary>
    public (int? idents, int days, DateOnly? first, DateOnly? last) Stats()
    {
        StartSummaryBuild();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM imported_files), (SELECT MIN(day) FROM sightings), (SELECT MAX(day) FROM sightings)";
            using var r = cmd.ExecuteReader();
            r.Read();
            int days = r.GetInt32(0);
            DateOnly? first = r.IsDBNull(1) ? null : DateOnly.Parse(r.GetString(1));
            DateOnly? last = r.IsDBNull(2) ? null : DateOnly.Parse(r.GetString(2));
            if (_identCount is null && SummaryReady)
            {
                using var cnt = _db.CreateCommand();
                cnt.CommandText = "SELECT COUNT(*) FROM idents";
                _identCount = Convert.ToInt32(cnt.ExecuteScalar());
            }
            return (_identCount, days, first, last);
        }
    }

    /// <summary>Builds the summary table in the background if it is empty; a no-op once it exists.</summary>
    public void StartSummaryBuild()
    {
        lock (_gate)
        {
            if (SummaryReady || _summaryTask is { IsCompleted: false }) return;
            _summaryTask = Task.Run(BuildSummary);
        }
    }

    /// <summary>
    /// One ordered pass over the sightings primary key (ident, day) on a reader connection, written
    /// in short batches on a second connection so imports and look-ups are never blocked for long.
    /// </summary>
    private void BuildSummary()
    {
        try
        {
            using var reader = new SqliteConnection(_connectionString);
            using var writer = new SqliteConnection(_connectionString);
            reader.Open();
            writer.Open();
            using (var have = writer.CreateCommand())
            {
                have.CommandText = "SELECT COUNT(*) FROM idents";
                if (Convert.ToInt64(have.ExecuteScalar()) > 0)
                {
                    SummaryReady = true;
                    Log.Info("idents", "ident summary table present");
                    return;
                }
            }
            Log.Info("idents", "building the ident summary table (one pass over the sightings)");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var scan = reader.CreateCommand();
            // GROUP BY walks the primary key in order: one pass, no temporary b-tree.
            scan.CommandText = "SELECT ident, MIN(day), MAX(day), COUNT(*) FROM sightings GROUP BY ident";
            using var rows = scan.ExecuteReader();

            using var ins = writer.CreateCommand();
            ins.CommandText = """
                INSERT OR REPLACE INTO idents (ident, first, last, days, last_nick, last_host)
                SELECT $i, $f, $l, $n, nick, host FROM sightings WHERE ident = $i AND day = $l
                """;
            var pi = ins.Parameters.Add("$i", SqliteType.Text);
            var pf = ins.Parameters.Add("$f", SqliteType.Text);
            var pl = ins.Parameters.Add("$l", SqliteType.Text);
            var pn = ins.Parameters.Add("$n", SqliteType.Integer);
            int total = 0;
            SqliteTransaction? tx = null;
            while (rows.Read())
            {
                tx ??= writer.BeginTransaction();
                ins.Transaction = tx;
                pi.Value = rows.GetString(0);
                pf.Value = rows.GetString(1);
                pl.Value = rows.GetString(2);
                pn.Value = rows.GetInt32(3);
                ins.ExecuteNonQuery();
                if (++total % 5000 == 0)
                {
                    tx.Commit();
                    tx.Dispose();
                    tx = null;
                }
            }
            tx?.Commit();
            tx?.Dispose();
            lock (_gate) _identCount = total;
            SummaryReady = true;
            Log.Info("idents", $"ident summary built: {total} idents in {sw.Elapsed.TotalSeconds:F0} s");
        }
        catch (Exception ex)
        {
            Log.Warn("idents", $"ident summary build failed: {ex.Message}");
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
