using System.Text.RegularExpressions;

namespace HostBot.Idents;

/// <summary>
/// Downloads the public CnCNet YR chat logs (one file per day) and records every
/// "X has joined the channel (IDENT@HOST)" line. Only join lines carry idents; quit, part,
/// kick and chat lines do not, so a player's history is the set of days they joined the lobby.
/// </summary>
public sealed partial class ChatLogImporter
{
    private const string Tag = "idents";
    private readonly IdentStore _store;
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public ChatLogImporter(IdentStore store, string baseUrl, HttpClient http)
    {
        _store = store;
        _baseUrl = baseUrl.TrimEnd('/') + "/";
        _http = http;
    }

    public static string FileNameFor(DateOnly day) => $"{day:yyyy}_{day:MM}/{day:dd}.txt";

    /// <summary>
    /// Imports every day from <paramref name="from"/> up to today (UTC) that is not yet in the
    /// store, and always re-imports the last <paramref name="refreshDays"/> days because the newest
    /// files are still being appended to when published.
    /// </summary>
    public async Task ImportAsync(DateOnly from, int refreshDays, CancellationToken ct)
    {
        if (_baseUrl == "/")
            return; // chatLogs.baseUrl is empty: this game has no public logs
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var refreshFrom = today.AddDays(-refreshDays);
        int imported = 0, missing = 0, skipped = 0;

        for (var day = from; day <= today; day = day.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            string file = FileNameFor(day);
            if (day < refreshFrom && _store.IsImported(file))
            {
                skipped++;
                continue;
            }

            string? text;
            try
            {
                using var resp = await _http.GetAsync(_baseUrl + file, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    missing++;
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                text = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex)
            {
                Log.Warn(Tag, $"{file}: {ex.Message}");
                continue;
            }

            var (sightings, lines) = Parse(text);
            _store.ImportDay(file, day, sightings, lines);
            imported++;
            if (imported % 50 == 0)
                Log.Info(Tag, $"imported {imported} days so far (at {day:yyyy-MM-dd})");
        }

        var stats = _store.Stats();
        Log.Info(Tag, $"import done: {imported} files imported, {skipped} already present, {missing} not on server; " +
                      $"{stats.idents} idents over {stats.days} days ({stats.first:yyyy-MM-dd}..{stats.last:yyyy-MM-dd})");
    }

    [GeneratedRegex(@"^\[\d\d:\d\d\] (?<nick>\S+) has joined the channel \((?<ident>[^@)]+)@(?<host>[^)]*)\)\s*$")]
    private static partial Regex JoinLine();

    public static (Dictionary<string, DaySighting> sightings, int lines) Parse(string text)
    {
        var result = new Dictionary<string, DaySighting>(StringComparer.Ordinal);
        int lines = 0;
        foreach (var raw in text.Split('\n'))
        {
            lines++;
            var line = raw.TrimEnd('\r');
            if (!line.Contains(" has joined the channel ("))
                continue;
            var m = JoinLine().Match(line);
            if (!m.Success)
                continue;
            string ident = IdentStore.Normalise(m.Groups["ident"].Value);
            if (!result.TryGetValue(ident, out var s))
                result[ident] = s = new DaySighting();
            s.Nick = m.Groups["nick"].Value;
            s.Host = m.Groups["host"].Value;
            s.Joins++;
        }
        return (result, lines);
    }
}
