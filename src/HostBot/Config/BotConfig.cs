using System.Text.Json;
using System.Text.Json.Serialization;
using HostBot.Idents;

namespace HostBot.Config;

public sealed class BotConfig
{
    /// <summary>SQLite file holding ident sightings built from the chat logs.</summary>
    public string IdentDb { get; set; } = "data/idents.db";

    public ChatLogConfig ChatLogs { get; set; } = new();

    /// <summary>Map pool produced by "hostbot maps build".</summary>
    public string MapPool { get; set; } = "gamedata/maps.json";

    /// <summary>Idents that are never kicked in any room (the owner's machine, helpers). Copied into the database on start; the admin page manages the rest.</summary>
    public List<string> Whitelist { get; set; } = new();

    public IrcConfig Irc { get; set; } = new();

    public AdminConfig Admin { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    /// <summary>Bot state database: rooms, chat log, events, match history.</summary>
    public string BotDb { get; set; } = "data/bot.db";

    /// <summary>Option layout of the players' client package (see gamedata/options.json).</summary>
    public string OptionLayout { get; set; } = "gamedata/options.json";

    /// <summary>Rooms seeded into the database on first start; afterwards rooms are managed from the admin page.</summary>
    public List<RoomConfig> Rooms { get; set; } = new();

    public static BotConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BotConfig>(json, JsonOptions)
               ?? throw new InvalidDataException($"{path}: empty config");
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class ChatLogConfig
{
    public string BaseUrl { get; set; } = "https://files.cncnet.org/logs/yr/ChatLogs/";
    /// <summary>First day to import. Logs on the server start in early 2022; earlier days 404 and are skipped.</summary>
    public string StartDate { get; set; } = "2022-01-01";
    /// <summary>The newest files are still being written when published, so re-import this many trailing days.</summary>
    public int RefreshDays { get; set; } = 3;
    /// <summary>How often the running bot refreshes the ident database.</summary>
    public int UpdateIntervalMinutes { get; set; } = 360;

    public DateOnly Start => DateOnly.Parse(StartDate);
}

public sealed class IrcConfig
{
    /// <summary>Game identifier used in the ident ("YR.xxxxxx") and channel names.</summary>
    public string Game { get; set; } = "YR";
    /// <summary>Client version string of the players (GAME field 1). Empty = learn it from other hosts' broadcasts.</summary>
    public string ClientVersion { get; set; } = "";
    /// <summary>Number of fields in the GAME broadcast (14 for the 9.3.5 client, 15 once the difficulty field ships). 0 = learn from other hosts.</summary>
    public int GameBroadcastFields { get; set; }
    /// <summary>Protocol revision letter in GAME and the room topic ("A" for 9.3.5). Empty = learn from other hosts.</summary>
    public string ProtocolRevision { get; set; } = "";
    /// <summary>Minimum milliseconds between queued IRC lines; the ircu flood budget (2 s + 1 s per 120 bytes per line, 8 s burst) does the real pacing.</summary>
    public int SendGapMs { get; set; } = 250;
    /// <summary>Log chat of the main lobby channel (#cncnet-yr) too, from the first room's connection.</summary>
    public bool LogLobbyChat { get; set; } = true;
    /// <summary>Optional fixed server; empty means use the client's built-in list.</summary>
    public string? Server { get; set; }
    public int? Port { get; set; }
}

public sealed class LoggingConfig
{
    /// <summary>Folder for the daily log files (hostbot-yyyy-MM-dd.log, UTC days). Empty disables file logging.</summary>
    public string Directory { get; set; } = "logs";
    /// <summary>Delete log files older than this many days. 0 keeps everything.</summary>
    public int KeepDays { get; set; } = 14;
    /// <summary>Log every IRC line (same as the -v switch). Large: roughly 70 MB a day on a busy lobby.</summary>
    public bool Debug { get; set; }
    /// <summary>Also write to standard output. Turn off under systemd to avoid duplicating the file into the journal.</summary>
    public bool Console { get; set; } = true;
}

public sealed class AdminConfig
{
    /// <summary>Listen URL of the admin page; put a reverse proxy with TLS in front for remote access.</summary>
    public string Listen { get; set; } = "http://127.0.0.1:8585";
    /// <summary>HTTP basic-auth password (user name is ignored). Empty disables the admin page.</summary>
    public string Password { get; set; } = "";
}

public sealed class RoomConfig
{
    /// <summary>Stable identifier used in the database and admin API; generated from the nick when empty.</summary>
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Launch as soon as this many players are present and everyone is ready. 1 allows a solo test game.</summary>
    public int MinPlayersToStart { get; set; } = 2;
    /// <summary>Game room title shown in the lobby list.</summary>
    public string Name { get; set; } = "2v2";
    /// <summary>IRC nick of this room's host bot. Each room has its own connection and nick.</summary>
    public string Nick { get; set; } = "HostBot";
    public string? Password { get; set; }
    /// <summary>Skill level shown in the game list: 0 Any, 1 Beginner, 2 Intermediate, 3 Pro.</summary>
    public int SkillLevel { get; set; }

    public RoomTier Tier { get; set; } = RoomTier.Open;
    /// <summary>Noob rooms: kick idents first seen more than this many days ago.</summary>
    public int MaxIdentAgeDays { get; set; } = 90;
    /// <summary>Pro rooms: kick idents first seen less than this many days ago, or never seen.</summary>
    public int MinIdentAgeDays { get; set; } = 90;
    /// <summary>Noob rooms: treat a fresh ident on a long-known cloaked host as old.</summary>
    public bool UseHostAsFallback { get; set; } = true;
    /// <summary>
    /// Noob rooms: also kick a nick that was handed over from an older ident (a veteran on a new PC): another ident
    /// played the nick on at least this many days, was first seen more than MaxIdentAgeDays ago, and went quiet when
    /// the joining ident appeared. Shared names stay unaffected because their other users keep playing. 0 = ident only.
    /// </summary>
    public int NickHistoryMinDays { get; set; } = 30;
    /// <summary>Any tier: kick idents seen in the chat logs on fewer than this many days (a handed-over ident's days count). 0 = off.</summary>
    public int MinActiveDays { get; set; }
    /// <summary>Message sent to the player before the kick, as room chat.</summary>
    public string? KickMessage { get; set; }
    /// <summary>Let players who passed the ident check type "!invite &lt;nick&gt;" to whitelist someone for this room and send them a game invitation.</summary>
    public bool AllowInvites { get; set; }
    /// <summary>How long an invited ident stays whitelisted for this room.</summary>
    public int InviteDays { get; set; } = 7;
    /// <summary>Invites one player may send per 24 hours. 0 = unlimited.</summary>
    public int MaxInvitesPerDay { get; set; } = 3;

    public string GameMode { get; set; } = "Battle";
    /// <summary>Game mode that "!blitz" switches the room to by vote, playing that mode's maps; the room returns to GameMode once empty. Empty disables the command.</summary>
    public string BlitzGameMode { get; set; } = "Blitz 2v2";
    /// <summary>Option overrides applied on top of the Blitz mode's forced options while a !blitz vote is in effect (superweapons on by default).</summary>
    public Dictionary<string, string> BlitzGameOptions { get; set; } = new() { ["cmbSuperWeaponsModifier"] = "0" };
    /// <summary>Room size: 4 for 2v2, 6 for 3v3. Teams are always two equal halves.</summary>
    public int MaxPlayers { get; set; } = 4;
    /// <summary>Open the room as 3v3 (6 players) instead while the CnCNet lobby is busy; the switch happens whenever the room is empty.</summary>
    public bool AutoThreeVsThree { get; set; }
    /// <summary>Lobby user count (people in the main chat channel) from which the room becomes 3v3; it drops back to the configured size 10% below that.</summary>
    public int AutoThreeVsThreeLobbyUsers { get; set; } = 550;
    /// <summary>Seats beyond the player slots for people who pick Spectator in the side list (capped so players + spectators stay within 8). 0 = no extra seats.</summary>
    public int MaxSpectators { get; set; } = 2;
    /// <summary>Only use maps that define a two-team start layout for the room size (2v2 or 3v3).</summary>
    public bool RequireTeamStartMappings { get; set; } = true;
    /// <summary>
    /// How much each passed "!next" vote lowers a map's chance of being picked again in this room.
    /// Pick weight = 1 / (1 + penalty * max(0, skips - plays / 2)), never below 0.2. 0 disables the ranking.
    /// </summary>
    public double NextMapPenalty { get; set; } = 0.25;
    /// <summary>Auto-ally: send the map's team start mapping so teams follow start positions (players pick a spot, not a team).</summary>
    public bool UseTeamStartMappings { get; set; } = true;
    /// <summary>
    /// Lock every player's spot to Random (the client's "force random starts"); with auto-ally that also makes the
    /// teams random. "!vote spots" flips it for the current map; each new map starts from this setting again.
    /// </summary>
    public bool ForceRandomStarts { get; set; }
    /// <summary>When the room is full, a player who is still not ready after this many seconds is warned.</summary>
    public int ReadyGraceSeconds { get; set; } = 20;
    /// <summary>Seconds between the warning and the kick. 0 disables the ready kick.</summary>
    public int KickUnreadySeconds { get; set; } = 12;
    /// <summary>Countdown announced once everyone is ready, so players can still change spot/side/colour. 0 = start at once.</summary>
    public int StartDelaySeconds { get; set; } = 20;
    /// <summary>After a cancelled countdown that was already announced, the next start waits only this long, silently.</summary>
    public int QuickStartSeconds { get; set; } = 3;
    /// <summary>Kick a player who un-readies during a running countdown this many times (warned one before). 0 = off.</summary>
    public int KickAfterCancelledStarts { get; set; } = 3;
    /// <summary>After launching, leave the old channel and open a fresh room immediately instead of waiting for the players to return.</summary>
    public bool AbandonAfterLaunch { get; set; } = true;
    /// <summary>How long after the start message the bot leaves the old channel; long enough for the game to be running so the "player left" sound is not mistaken for a quitter.</summary>
    public int AbandonDelaySeconds { get; set; } = 25;
    /// <summary>Maps (MPMaps.ini section names) added to the pool although they do not match the filters.</summary>
    public List<string> Maps { get; set; } = new();
    /// <summary>Maps (MPMaps.ini section names) ticked off on the admin page; never picked even if the filters match.</summary>
    public List<string> ExcludedMaps { get; set; } = new();
    /// <summary>Change the map after this long with nobody in the room.</summary>
    public int IdleMapRotateMinutes { get; set; } = 20;

    public TunnelConfig Tunnel { get; set; } = new();

    /// <summary>Checkbox / dropdown values that differ from the game package defaults, keyed by control name.</summary>
    public Dictionary<string, string> GameOptions { get; set; } = new();
    /// <summary>Votes needed for "!vote &lt;option&gt;" to flip an option for the current game. 0 disables voting.</summary>
    public int VotesNeeded { get; set; } = 2;
    /// <summary>How long a player removed by "!votekick" (half of the other players, at least two) stays out of this room. 0 disables the command.</summary>
    public int VoteKickBanHours { get; set; } = 24;
}

public sealed class TunnelConfig
{
    /// <summary>"V3", "V3Dynamic" or "V2". The bot falls back from V3 to V2 if no V3 tunnel answers.</summary>
    public string Mode { get; set; } = "V3";
    /// <summary>Preferred tunnel names (substring match, case-insensitive), in order. Empty means best available.</summary>
    public List<string> Preferred { get; set; } = new() { "Europe", "Singapore", "Silicon" };
}
