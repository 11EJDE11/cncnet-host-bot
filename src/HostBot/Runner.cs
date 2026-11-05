using HostBot.Config;
using HostBot.Idents;
using HostBot.Lobby;
using HostBot.Maps;
using HostBot.Storage;
using HostBot.Tunnels;
using HostBot.Web;

namespace HostBot;

public static class Runner
{
    private const string Tag = "main";

    public static async Task<int> RunAsync(BotConfig config, CancellationToken ct)
    {
        using var identStore = new IdentStore(config.IdentDb);
        using var db = new BotDb(config.BotDb);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("cncnet-host-bot/1.0");

        var pool = MapPoolFile.Load(config.MapPool);
        var layout = OptionLayout.Load(config.OptionLayout);
        var tunnels = new TunnelList(http);
        var policy = new IdentPolicy(identStore, db, config.Whitelist);
        var importer = new ChatLogImporter(identStore, config.ChatLogs.BaseUrl, http);

        var stats = identStore.Stats();
        Log.Info(Tag, $"ident db: {stats.idents} idents, {stats.days} days ({stats.first:yyyy-MM-dd}..{stats.last:yyyy-MM-dd}); map pool: {pool.Maps.Count} maps");
        if (stats.days == 0)
            Log.Warn(Tag, "ident database is empty: run 'hostbot idents import' first, or every policy room will treat everyone as unknown");

        var manager = new RoomManager(config, tunnels, policy, pool, layout, db);
        manager.StartAll();

        var identRefresh = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(Math.Max(10, config.ChatLogs.UpdateIntervalMinutes)), ct);
                    await importer.ImportAsync(config.ChatLogs.Start, config.ChatLogs.RefreshDays, ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log.Warn(Tag, $"ident refresh failed: {ex.Message}");
                }
            }
        }, ct);

        AdminServer? admin = null;
        if (!string.IsNullOrEmpty(config.Admin.Password))
        {
            admin = new AdminServer(config, manager, db, identStore, policy, pool, importer);
            await admin.StartAsync(ct);
        }
        else
            Log.Warn(Tag, "admin page disabled (no admin.password in config)");

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }

        Log.Info(Tag, "shutting down");
        await manager.StopAllAsync("bot shutting down");
        if (admin is not null) await admin.StopAsync();
        try { await identRefresh; } catch { }
        return 0;
    }
}
