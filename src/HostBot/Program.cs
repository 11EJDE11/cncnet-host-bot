using HostBot.Config;
using HostBot.Idents;

namespace HostBot;

public static class Program
{
    private const string Usage = """
        hostbot run [config.json]              host the configured rooms (default command)
        hostbot idents import [config.json]    download / refresh the chat logs into the ident database
        hostbot idents check <ident> [config]  show what the policy knows about an ident
        hostbot maps build <game dir> [out]    build gamedata/maps.json from a game install (INI/MPMaps.ini + Maps/)
        hostbot capture [host nick] [config]   connect, print GAME broadcasts (client version / field count) and, with a
                                               host nick, join that room and print its GO/PO/PEO so the option layout can be checked
        options: -v (verbose logging)
        """;

    public static async Task<int> Main(string[] args)
    {
        var list = args.ToList();
        if (list.Remove("-v"))
            Log.Verbose = true;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            string cmd = list.Count > 0 ? list[0] : "run";
            switch (cmd)
            {
                case "run":
                    return await Runner.RunAsync(BotConfig.Load(Arg(list, 1, "config.json")), cts.Token);

                case "idents":
                    return await IdentsCommand(list, cts.Token);

                case "maps":
                    if (Arg(list, 1, "") != "build" || list.Count < 3)
                        break;
                    Maps.MapPoolBuilder.Build(list[2], Arg(list, 3, "gamedata/maps.json"));
                    return 0;

                case "capture":
                    return await Capture.RunAsync(BotConfig.Load(list.Count > 2 ? list[2] : "config.json"), list.Count > 1 ? list[1] : null, cts.Token);

                case "help":
                case "--help":
                case "-h":
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            return 130;
        }

        Console.WriteLine(Usage);
        return 1;
    }

    private static async Task<int> IdentsCommand(List<string> list, CancellationToken ct)
    {
        string sub = Arg(list, 1, "");
        switch (sub)
        {
            case "import":
            {
                var cfg = BotConfig.Load(Arg(list, 2, "config.json"));
                using var store = new IdentStore(cfg.IdentDb);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                var importer = new ChatLogImporter(store, cfg.ChatLogs.BaseUrl, http);
                await importer.ImportAsync(cfg.ChatLogs.Start, cfg.ChatLogs.RefreshDays, ct);
                return 0;
            }
            case "check":
            {
                if (list.Count < 3)
                    break;
                var cfg = BotConfig.Load(Arg(list, 3, "config.json"));
                using var store = new IdentStore(cfg.IdentDb);
                using var db = new HostBot.Storage.BotDb(cfg.BotDb);
                var policy = new IdentPolicy(store, db, cfg.Whitelist);
                string ident = list[2];
                var h = store.Lookup(ident);
                Console.WriteLine(h is null
                    ? $"{IdentStore.Normalise(ident)}: never seen"
                    : $"{h.Ident}: first {h.FirstSeen:yyyy-MM-dd}, last {h.LastSeen:yyyy-MM-dd}, {h.Days} days, last nick {h.LastNick}, host {h.LastHost}");
                foreach (var room in cfg.Rooms)
                {
                    var v = policy.Evaluate(ident, h?.LastHost, room.Id, room.Tier, room.MaxIdentAgeDays, room.MinIdentAgeDays, room.UseHostAsFallback);
                    Console.WriteLine($"  {room.Name} ({room.Tier}): {(v.Allowed ? "allowed" : "KICK")} - {v.Reason}");
                }
                return 0;
            }
        }
        Console.WriteLine(Usage);
        return 1;
    }

    private static string Arg(List<string> list, int index, string fallback) => list.Count > index ? list[index] : fallback;
}
