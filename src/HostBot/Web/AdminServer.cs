using System.Text;
using System.Text.Json;
using HostBot.Config;
using HostBot.Idents;
using HostBot.Lobby;
using HostBot.Maps;
using HostBot.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;

namespace HostBot.Web;

/// <summary>Embedded admin page and JSON API, protected by HTTP basic auth.</summary>
public sealed class AdminServer
{
    private const string Tag = "admin";
    private readonly BotConfig _cfg;
    private readonly RoomManager _rooms;
    private readonly BotDb _db;
    private readonly IdentStore _idents;
    private readonly IdentPolicy _policy;
    private readonly MapPoolFile _pool;
    private readonly ChatLogImporter _importer;
    private WebApplication? _app;
    private Task? _importTask;

    public AdminServer(BotConfig cfg, RoomManager rooms, BotDb db, IdentStore idents, IdentPolicy policy, MapPoolFile pool, ChatLogImporter importer)
    {
        _cfg = cfg;
        _rooms = rooms;
        _db = db;
        _idents = idents;
        _policy = policy;
        _pool = pool;
        _importer = importer;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(_cfg.Admin.Listen);
        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            if (!Authorized(ctx.Request.Headers.Authorization.ToString()))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"hostbot\"";
                await ctx.Response.WriteAsync("unauthorized");
                return;
            }
            await next();
        });

        string pagePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "admin.html");
        app.MapGet("/", () => Results.Content(File.ReadAllText(pagePath), "text/html; charset=utf-8"));

        app.MapGet("/api/status", () => Json(new
        {
            now = DateTime.UtcNow,
            idents = IdentStatsObject(),
            importRunning = _importTask is { IsCompleted: false },
            rooms = _rooms.Hosts.Select(HostInfo).ToList(),
            lobbyGames = _rooms.Hosts.Select(h => h.Observer).FirstOrDefault(o => o is not null)?.Games(),
            log = Log.Recent(200),
        }));

        app.MapGet("/api/rooms", () => Json(_rooms.Hosts.Select(HostInfo).ToList()));
        app.MapGet("/api/rooms/{id}", (string id) => _rooms.Get(id) is { } h ? Json(HostInfo(h)) : Results.NotFound());

        app.MapPost("/api/rooms", async (HttpContext ctx) =>
        {
            var def = await ReadJson<RoomConfig>(ctx);
            var host = _rooms.Add(def);
            return Json(HostInfo(host));
        });
        app.MapPut("/api/rooms/{id}", async (string id, HttpContext ctx) =>
        {
            var def = await ReadJson<RoomConfig>(ctx);
            bool restart = ctx.Request.Query["restart"] == "1";
            await _rooms.UpdateAsync(id, def, restart);
            return Json(HostInfo(_rooms.Get(id)!));
        });
        app.MapPost("/api/rooms/{id}/start", (string id) => { _rooms.StartRoom(id); return Results.Ok(); });
        app.MapPost("/api/rooms/{id}/stop", async (string id) => { await _rooms.StopAsync(id, "stopped from admin page"); return Results.Ok(); });
        app.MapDelete("/api/rooms/{id}", async (string id) => { await _rooms.DeleteAsync(id); return Results.Ok(); });

        app.MapPost("/api/rooms/{id}/chat", async (string id, HttpContext ctx) =>
        {
            var body = await ReadJson<Dictionary<string, string>>(ctx);
            var room = _rooms.Get(id)?.Room;
            if (room is null) return Results.BadRequest("room not open");
            room.SendChat(body.GetValueOrDefault("text", ""));
            return Results.Ok();
        });
        app.MapPost("/api/rooms/{id}/kick", async (string id, HttpContext ctx) =>
        {
            var body = await ReadJson<Dictionary<string, string>>(ctx);
            var room = _rooms.Get(id)?.Room;
            if (room is null) return Results.BadRequest("room not open");
            room.Kick(body.GetValueOrDefault("nick", ""), body.GetValueOrDefault("reason", "Kicked by an admin."), body.GetValueOrDefault("ban") == "1");
            return Results.Ok();
        });
        app.MapPost("/api/rooms/{id}/nextmap", async (string id, HttpContext ctx) =>
        {
            var body = await ReadJson<Dictionary<string, string>>(ctx);
            var room = _rooms.Get(id)?.Room;
            if (room is null) return Results.BadRequest("room not open");
            room.ForceNextMap(string.IsNullOrEmpty(body.GetValueOrDefault("map")) ? null : body["map"]);
            return Results.Ok();
        });

        app.MapGet("/api/maps", (string? mode, int? players) => Json(_pool.Maps
            .Where(m => (mode is null || m.GameModes.Contains(mode, StringComparer.OrdinalIgnoreCase)) && (players is null || m.MaxPlayers == players))
            .Select(m => new { m.Name, m.BaseFilePath, m.MaxPlayers, m.GameModes, teamMappings = m.TeamStartMappings.Count }).ToList()));
        app.MapGet("/api/gamemodes", () => Json(_pool.GameModes.Select(g => g.Name).ToList()));

        app.MapGet("/api/chat", (string? room, string? channel, long? after, int? limit) =>
            Json(_db.Query(
                "SELECT * FROM chat WHERE ($room IS NULL OR room = $room) AND ($ch IS NULL OR channel LIKE $ch) AND id > $after ORDER BY id DESC LIMIT $limit",
                ("$room", room), ("$ch", channel is null ? null : channel + "%"), ("$after", after ?? 0), ("$limit", Math.Clamp(limit ?? 200, 1, 2000)))));

        app.MapGet("/api/events", (string? room, string? type, int? limit) =>
            Json(_db.Query(
                "SELECT * FROM events WHERE ($room IS NULL OR room = $room) AND ($type IS NULL OR type = $type) ORDER BY id DESC LIMIT $limit",
                ("$room", room), ("$type", type), ("$limit", Math.Clamp(limit ?? 200, 1, 2000)))));

        app.MapGet("/api/matches", (string? room, int? limit) =>
            Json(_db.Query("SELECT * FROM matches WHERE ($room IS NULL OR room = $room) ORDER BY id DESC LIMIT $limit",
                ("$room", room), ("$limit", Math.Clamp(limit ?? 100, 1, 1000)))));

        app.MapGet("/api/idents", (string q, int? limit) =>
        {
            string like = "%" + q.Trim() + "%";
            var rows = _idents.Search(like, Math.Clamp(limit ?? 100, 1, 500));
            return Json(rows.Select(r => new
            {
                r.Ident, r.FirstSeen, r.LastSeen, r.Days, r.LastNick, r.LastHost,
                verdicts = _rooms.Hosts.Select(h => new
                {
                    room = h.Def.Name,
                    v = _policy.Evaluate(r.Ident, r.LastHost, h.Def.Id, h.Def.Tier, h.Def.MaxIdentAgeDays, h.Def.MinIdentAgeDays, h.Def.UseHostAsFallback),
                }).Select(x => new { x.room, x.v.Allowed, x.v.Reason }).ToList(),
            }).ToList());
        });
        app.MapGet("/api/idents/{ident}/days", (string ident) => Json(_idents.Days(ident)));
        app.MapPost("/api/idents/import", () =>
        {
            if (_importTask is { IsCompleted: false }) return Results.Ok("already running");
            _importTask = Task.Run(() => _importer.ImportAsync(_cfg.ChatLogs.Start, _cfg.ChatLogs.RefreshDays, CancellationToken.None));
            return Results.Ok("started");
        });

        app.MapGet("/api/whitelist", () => Json(_policy.Entries));
        app.MapPost("/api/whitelist", async (HttpContext ctx) =>
        {
            var body = await ReadJson<Dictionary<string, string>>(ctx);
            string ident = IdentStore.Normalise(body.GetValueOrDefault("ident", ""));
            string room = (body.GetValueOrDefault("room") ?? "").Trim().ToLowerInvariant();
            if (ident.Length == 0) return Results.BadRequest("ident required");
            bool remove = body.GetValueOrDefault("remove") == "1";
            if (remove)
                _policy.Remove(ident, room);
            else
            {
                DateTime? expires = int.TryParse(body.GetValueOrDefault("days"), out int days) && days > 0 ? DateTime.UtcNow.AddDays(days) : null;
                _policy.Add(new WhitelistEntry(ident, room, "admin", "admin", string.IsNullOrWhiteSpace(body.GetValueOrDefault("reason")) ? null : body["reason"].Trim(), expires, DateTime.UtcNow));
            }
            _db.LogEvent(room.Length == 0 ? null : room, "whitelist", null, ident, null, remove ? "removed" : "added");
            return Json(_policy.Entries);
        });

        _app = app;
        await app.StartAsync(ct);
        Log.Info(Tag, $"admin page on {_cfg.Admin.Listen}");
    }

    public async Task StopAsync()
    {
        if (_app is not null) await _app.StopAsync();
    }

    private bool Authorized(string header)
    {
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            int colon = decoded.IndexOf(':');
            string password = colon < 0 ? decoded : decoded[(colon + 1)..];
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(_cfg.Admin.Password));
        }
        catch { return false; }
    }

    private object IdentStatsObject()
    {
        var s = _idents.Stats();
        return new { idents = s.idents, days = s.days, first = s.first?.ToString("yyyy-MM-dd"), last = s.last?.ToString("yyyy-MM-dd") };
    }

    private static object HostInfo(RoomHost h) => new
    {
        def = h.Def,
        status = h.Status,
        running = h.Running,
        startedAt = h.StartedAt,
        nick = h.Irc?.Nick,
        room = h.Room?.Snapshot(),
    };

    private static IResult Json(object? value) =>
        Results.Text(JsonSerializer.Serialize(value, BotConfig.JsonOptions), "application/json; charset=utf-8");

    private static async Task<T> ReadJson<T>(HttpContext ctx) =>
        await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, BotConfig.JsonOptions) ?? throw new BadHttpRequestException("empty body");
}
