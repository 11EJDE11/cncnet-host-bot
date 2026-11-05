namespace HostBot;

/// <summary>
/// Minimal timestamped logger: one line per event, prefixed by a source tag. Writes to the console
/// and, once <see cref="ConfigureFile"/> has run, to one file per UTC day (hostbot-yyyy-MM-dd.log),
/// deleting files older than the configured number of days.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Ring = new();
    private static string? _dir;
    private static int _keepDays;
    private static DateOnly _fileDate;
    private static StreamWriter? _file;

    public static bool Verbose { get; set; }
    /// <summary>Also write to standard output (off under a service manager that already captures the file).</summary>
    public static bool ToConsole { get; set; } = true;

    /// <summary>Current log file, for the admin page and the console banner.</summary>
    public static string? FilePath { get; private set; }

    /// <summary>Starts daily file logging in <paramref name="directory"/>, keeping <paramref name="keepDays"/> days (0 = forever).</summary>
    public static void ConfigureFile(string directory, int keepDays)
    {
        lock (Gate)
        {
            _dir = Path.GetFullPath(directory);
            _keepDays = keepDays;
            Directory.CreateDirectory(_dir);
            OpenFile(DateOnly.FromDateTime(DateTime.UtcNow));
        }
    }

    /// <summary>Last lines written, newest last, for the admin page.</summary>
    public static List<string> Recent(int count)
    {
        lock (Gate) return Ring.TakeLast(count).ToList();
    }

    public static void Info(string tag, string message) => Write("INFO ", tag, message);
    public static void Warn(string tag, string message) => Write("WARN ", tag, message);
    public static void Error(string tag, string message) => Write("ERROR", tag, message);

    public static void Debug(string tag, string message)
    {
        if (Verbose)
            Write("DEBUG", tag, message);
    }

    private static void Write(string level, string tag, string message)
    {
        var now = DateTime.UtcNow;
        string line = $"{now:yyyy-MM-dd HH:mm:ss} {level} [{tag}] {message}";
        lock (Gate)
        {
            if (ToConsole)
                Console.WriteLine(line);
            if (_dir is not null)
            {
                var today = DateOnly.FromDateTime(now);
                if (_file is null || today != _fileDate)
                    OpenFile(today);
                try { _file!.WriteLine(line); }
                catch (IOException) { /* disk problems must not take the bot down */ }
            }
            Ring.Enqueue(line);
            while (Ring.Count > 1000) Ring.Dequeue();
        }
    }

    private static void OpenFile(DateOnly day)
    {
        _file?.Dispose();
        _fileDate = day;
        FilePath = Path.Combine(_dir!, $"hostbot-{day:yyyy-MM-dd}.log");
        _file = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        if (_keepDays > 0)
        {
            var cutoff = day.AddDays(-_keepDays);
            foreach (var f in Directory.EnumerateFiles(_dir!, "hostbot-????-??-??.log"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (DateOnly.TryParseExact(name["hostbot-".Length..], "yyyy-MM-dd", out var d) && d < cutoff)
                {
                    try { File.Delete(f); } catch (IOException) { }
                }
            }
        }
    }
}
