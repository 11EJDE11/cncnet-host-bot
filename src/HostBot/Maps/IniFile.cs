namespace HostBot.Maps;

/// <summary>
/// Small ordered INI reader matching how the client reads its INIs: sections in file order,
/// keys in section order, ';' starts a comment, duplicate keys keep the last value.
/// </summary>
public sealed class IniFile
{
    public sealed class Section
    {
        public string Name { get; }
        public List<KeyValuePair<string, string>> Entries { get; } = new();
        private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

        public Section(string name) => Name = name;

        public void Set(string key, string value)
        {
            if (_map.ContainsKey(key))
            {
                int i = Entries.FindIndex(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
                Entries[i] = new(key, value);
            }
            else
                Entries.Add(new(key, value));
            _map[key] = value;
        }

        public string? Get(string key) => _map.TryGetValue(key, out var v) ? v : null;
        public string Get(string key, string fallback) => Get(key) ?? fallback;
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;
        public bool GetBool(string key, bool fallback)
        {
            var v = Get(key);
            if (v is null) return fallback;
            return v.Trim().ToLowerInvariant() is "true" or "yes" or "1";
        }
        public bool Has(string key) => _map.ContainsKey(key);

        public void Insert(int index, string key, string value)
        {
            Entries.Insert(index, new(key, value));
            _map[key] = value;
        }
    }

    private readonly Dictionary<string, Section> _sections = new(StringComparer.OrdinalIgnoreCase);
    public List<Section> Sections { get; } = new();

    public static IniFile Load(string path) => Parse(File.ReadAllText(path));

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        Section? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == ';')
                continue;
            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                if (end < 0) continue;
                current = ini.GetOrAdd(line[1..end]);
                continue;
            }
            if (current is null) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..];
            int comment = value.IndexOf(';');
            if (comment >= 0) value = value[..comment];
            current.Set(key, value.Trim());
        }
        return ini;
    }

    public Section GetOrAdd(string name)
    {
        if (!_sections.TryGetValue(name, out var s))
        {
            s = new Section(name);
            _sections[name] = s;
            Sections.Add(s);
        }
        return s;
    }

    public Section? Get(string name) => _sections.TryGetValue(name, out var s) ? s : null;

    /// <summary>
    /// Loads a client UI INI the way the client's CCIniFile does: [INISystem] BasedOn= files first,
    /// this file's keys merged on top, then $BaseSection= keys inserted at the front of each section.
    /// </summary>
    public static IniFile LoadClientIni(string path, string resourceDir)
    {
        var ini = Load(path);
        string basedOn = ini.Get("INISystem", "BasedOn", "");
        foreach (var name in basedOn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string basePath = name.Contains("$THEME_DIR$")
                ? name.Replace("$THEME_DIR$", Path.TrimEndingDirectorySeparator(resourceDir) + Path.DirectorySeparatorChar)
                : Path.Combine(Path.GetDirectoryName(path)!, name);
            if (!File.Exists(basePath)) continue;
            var merged = LoadClientIni(basePath, resourceDir);
            foreach (var s in ini.Sections)
            {
                var target = merged.GetOrAdd(s.Name);
                foreach (var e in s.Entries) target.Set(e.Key, e.Value);
            }
            ini = merged;
        }
        foreach (var s in ini.Sections)
        {
            if (s.Get("$BaseSection") is not { } baseName || ini.Get(baseName) is not { } baseSection) continue;
            int added = 0;
            foreach (var e in baseSection.Entries.ToList())
                if (!s.Has(e.Key)) s.Insert(added++, e.Key, e.Value);
        }
        return ini;
    }

    public string Get(string section, string key, string fallback) => Get(section)?.Get(key) ?? fallback;
}
