using System.Security.Cryptography;
using System.Text.Json;
using HostBot.Config;

namespace HostBot.Maps;

/// <summary>One official map as the client sees it: the MPMaps.ini section plus the file's SHA1.</summary>
public sealed class MapEntry
{
    /// <summary>MPMaps.ini section name and path relative to the game folder without extension, e.g. Maps\Yuri's Revenge\4_face_down_le.</summary>
    public string BaseFilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public List<string> GameModes { get; set; } = new();
    public int MinPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public bool EnforceMaxPlayers { get; set; }
    public int Waypoints { get; set; }
    public List<TeamStartMapping> TeamStartMappings { get; set; } = new();
    /// <summary>ForcedOptions sections of the map (option name -> value), applied on top of the game mode's.</summary>
    public Dictionary<string, string> ForcedOptions { get; set; } = new();

    public override string ToString() => $"{Name} [{MaxPlayers}] ({BaseFilePath})";
}

public sealed class TeamStartMapping
{
    public string Name { get; set; } = "";
    /// <summary>One letter per start waypoint: A/B = team, "-" = unused.</summary>
    public string Mapping { get; set; } = "";
}

public sealed class GameModeEntry
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> ForcedOptions { get; set; } = new();
    public Dictionary<string, string> ForcedSpawnIniOptions { get; set; } = new();
}

public sealed class MapPoolFile
{
    public string SourceGameDir { get; set; } = "";
    public DateTime BuiltAt { get; set; }
    public List<GameModeEntry> GameModes { get; set; } = new();
    public List<MapEntry> Maps { get; set; } = new();

    public static MapPoolFile Load(string path) =>
        JsonSerializer.Deserialize<MapPoolFile>(File.ReadAllText(path), BotConfig.JsonOptions)
        ?? throw new InvalidDataException($"{path}: empty map pool");

    /// <summary>
    /// A room's map pool for a given room size, in MPMaps.ini order: every map matching the room's
    /// filters plus the explicitly added ones, minus the excluded ones.
    /// </summary>
    public List<MapEntry> Select(RoomConfig room, int maxPlayers, string? gameMode = null)
    {
        gameMode ??= room.GameMode;
        // The maps ticked on the admin page belong to the configured mode; a voted mode (!blitz) plays its own maps only.
        bool configuredMode = gameMode.Equals(room.GameMode, StringComparison.OrdinalIgnoreCase);
        var extra = new HashSet<string>(configuredMode ? room.Maps : new List<string>(), StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(room.ExcludedMaps, StringComparer.OrdinalIgnoreCase);
        return Maps.Where(m => !excluded.Contains(m.BaseFilePath)
                               && (extra.Contains(m.BaseFilePath) || extra.Contains(m.Name) || MatchesFilters(m, room, maxPlayers, gameMode)))
                   .ToList();
    }

    public List<MapEntry> Select(RoomConfig room) => Select(room, room.MaxPlayers);

    /// <summary>Whether the room's filters (player count, game mode, team layout) admit the map at this room size.</summary>
    public static bool MatchesFilters(MapEntry m, RoomConfig room, int maxPlayers, string? gameMode = null) =>
        m.MaxPlayers == maxPlayers
        && m.GameModes.Contains(gameMode ?? room.GameMode, StringComparer.OrdinalIgnoreCase)
        && (!room.RequireTeamStartMappings || TeamMapping(m, maxPlayers / 2) is not null);

    /// <summary>
    /// The map's first start-position layout that forms exactly two teams of the given size (A and B,
    /// no unused spots), e.g. "A,A,B,B" for 2v2 or "A,A,A,B,B,B" for 3v3. Null if the map has none.
    /// </summary>
    public static TeamStartMapping? TeamMapping(MapEntry m, int teamSize)
    {
        foreach (var t in m.TeamStartMappings)
        {
            var letters = t.Mapping.Split(',', StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant()).ToList();
            if (letters.Count == teamSize * 2 && letters.Count(l => l == "A") == teamSize && letters.Count(l => l == "B") == teamSize)
                return t;
        }
        return null;
    }

    /// <summary>Team sizes (2 for 2v2, 3 for 3v3, ...) for which the map has a two-team layout.</summary>
    public static List<int> TeamSizes(MapEntry m) =>
        Enumerable.Range(1, 4).Where(n => TeamMapping(m, n) is not null).ToList();
}

public static class MapPoolBuilder
{
    private const string Tag = "maps";

    /// <summary>
    /// Reads INI/MPMaps.ini from a game install, hashes every listed map file that exists, and
    /// writes the result as JSON. Maps whose file is missing are skipped (the bot cannot host
    /// a map players would then fail to validate).
    /// </summary>
    public static void Build(string gameDir, string outPath)
    {
        string mpMaps = Path.Combine(gameDir, "INI", "MPMaps.ini");
        if (!File.Exists(mpMaps))
            throw new FileNotFoundException("MPMaps.ini not found", mpMaps);
        var ini = IniFile.Load(mpMaps);

        var pool = new MapPoolFile { SourceGameDir = gameDir, BuiltAt = DateTime.UtcNow };
        string defs = Path.Combine(gameDir, "Resources", "ClientDefinitions.ini");
        string ext = "." + (File.Exists(defs) ? IniFile.Load(defs).Get("Settings", "MapFileExtension", "map") : "map"); // RA1 uses .mpr

        var gameModes = ini.Get("GameModes");
        if (gameModes is not null)
            foreach (var (_, modeName) in gameModes.Entries)
                pool.GameModes.Add(LoadGameMode(ini, modeName));

        int missing = 0;
        foreach (var sec in ini.Sections)
        {
            if (!sec.Name.StartsWith("Maps", StringComparison.OrdinalIgnoreCase))
                continue;

            // BaseSection= lets a map inherit another section's keys.
            var merged = new IniFile.Section(sec.Name);
            var baseName = sec.Get("BaseSection");
            if (baseName is not null && ini.Get(baseName) is { } baseSec)
                foreach (var e in baseSec.Entries) merged.Set(e.Key, e.Value);
            foreach (var e in sec.Entries) merged.Set(e.Key, e.Value);

            string file = Path.Combine(gameDir, sec.Name.Replace('\\', Path.DirectorySeparatorChar) + ext);
            if (!File.Exists(file))
            {
                missing++;
                Log.Debug(Tag, $"missing map file for {sec.Name}");
                continue;
            }

            var entry = new MapEntry
            {
                BaseFilePath = sec.Name,
                Name = merged.Get("Description", sec.Name),
                Author = merged.Get("Author", ""),
                Sha1 = Sha1Hex(file),
                GameModes = merged.Get("GameModes", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                MinPlayers = merged.GetInt("MinPlayers", 0),
                MaxPlayers = merged.GetInt("MaxPlayers", 0),
                EnforceMaxPlayers = merged.GetBool("EnforceMaxPlayers", false),
            };
            for (int i = 0; merged.Has("Waypoint" + i); i++)
                entry.Waypoints = i + 1;
            for (int i = 0; merged.Has("TeamStartMapping" + i); i++)
            {
                entry.TeamStartMappings.Add(new TeamStartMapping
                {
                    Mapping = merged.Get("TeamStartMapping" + i, ""),
                    Name = merged.Get($"TeamStartMapping{i}Name", $"Mapping {i + 1}"),
                });
            }
            CollectForced(ini, merged.Get("ForcedOptions"), entry.ForcedOptions);
            pool.Maps.Add(entry);
        }

        // Like the client (MapLoader.AddMapToGameModes), a mode that only appears in maps' GameModes= lists is a
        // game mode too, with its own [<Name>] section: YR's "Blitz 2v2" is one, the [GameModes] list names only "Blitz".
        foreach (var modeName in pool.Maps.SelectMany(m => m.GameModes).Distinct(StringComparer.Ordinal))
            if (!pool.GameModes.Any(g => g.Name == modeName))
                pool.GameModes.Add(LoadGameMode(ini, modeName));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(pool, BotConfig.JsonOptions));
        Log.Info(Tag, $"wrote {pool.Maps.Count} maps ({missing} listed without a file) and {pool.GameModes.Count} game modes to {outPath}");
    }

    private static GameModeEntry LoadGameMode(IniFile ini, string modeName)
    {
        var gm = new GameModeEntry { Name = modeName };
        var sec = ini.Get(modeName);
        if (sec is not null)
        {
            CollectForced(ini, sec.Get("ForcedOptions"), gm.ForcedOptions);
            CollectForced(ini, sec.Get("ForcedSpawnIniOptions"), gm.ForcedSpawnIniOptions);
        }
        return gm;
    }

    private static void CollectForced(IniFile ini, string? sectionList, Dictionary<string, string> into)
    {
        if (string.IsNullOrWhiteSpace(sectionList))
            return;
        foreach (var name in sectionList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sec = ini.Get(name);
            if (sec is null) continue;
            foreach (var e in sec.Entries)
                into[e.Key] = e.Value;
        }
    }

    /// <summary>Lower-case hex SHA1 of the whole file, the same value the client broadcasts for a map.</summary>
    public static string Sha1Hex(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(s));
    }
}
