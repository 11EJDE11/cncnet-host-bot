namespace HostBot;

/// <summary>Minimal timestamped console logger. One line per event, prefixed by a source tag.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Ring = new();
    public static bool Verbose { get; set; }

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
        string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {level} [{tag}] {message}";
        lock (Gate)
        {
            Console.WriteLine(line);
            Ring.Enqueue(line);
            while (Ring.Count > 1000) Ring.Dequeue();
        }
    }
}
