using System.Text.Json;
using HostBot.Config;

namespace HostBot.Maps;

public sealed class CheckBoxDef
{
    public string Name { get; set; } = "";
    public bool Default { get; set; }
    public bool Broadcast { get; set; }
    /// <summary>0-based side indices (into the Sides= list) this box disallows when checked.</summary>
    public List<int> DisallowedSides { get; set; } = new();
}

public sealed class DropDownDef
{
    public string Name { get; set; } = "";
    public int Items { get; set; }
    public int Default { get; set; }
    public bool Broadcast { get; set; }
}

/// <summary>
/// The client package's game-option controls in registration order. Values are kept per room as
/// name -> value and serialised into the GO message and GAME broadcast exactly as the client does.
/// </summary>
public sealed class OptionLayout
{
    public List<CheckBoxDef> CheckBoxes { get; set; } = new();
    public List<DropDownDef> DropDowns { get; set; } = new();
    public int SideCount { get; set; } = 10;
    public int RandomSelectorCount { get; set; } = 4;
    public int ColorCount { get; set; } = 8;
    public int FrameSendRate { get; set; } = 7;
    public int MaxAhead { get; set; }
    public int ProtocolVersion { get; set; } = 2;

    /// <summary>SideId of the Spectator entry in the side drop-down.</summary>
    public int SpectatorSideId => SideCount + RandomSelectorCount;

    public static OptionLayout Load(string path) =>
        JsonSerializer.Deserialize<OptionLayout>(File.ReadAllText(path), BotConfig.JsonOptions)
        ?? throw new InvalidDataException($"{path}: empty option layout");

    public bool IsCheckBox(string name) => CheckBoxes.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public bool IsDropDown(string name) => DropDowns.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Current values of every option in a room.</summary>
public sealed class OptionValues
{
    private readonly OptionLayout _layout;
    private readonly Dictionary<string, bool> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _drops = new(StringComparer.OrdinalIgnoreCase);

    public OptionValues(OptionLayout layout)
    {
        _layout = layout;
        Reset();
    }

    public void Reset()
    {
        foreach (var c in _layout.CheckBoxes) _checks[c.Name] = c.Default;
        foreach (var d in _layout.DropDowns) _drops[d.Name] = d.Default;
    }

    /// <summary>Applies "name=value" pairs (room config, game-mode or map forced options). Unknown names are reported and ignored.</summary>
    public void Apply(IEnumerable<KeyValuePair<string, string>> values, string source)
    {
        foreach (var (name, raw) in values)
        {
            if (_layout.IsCheckBox(name))
            {
                var v = raw.Trim().ToLowerInvariant();
                _checks[name] = v is "true" or "yes" or "1";
            }
            else if (_layout.IsDropDown(name) && int.TryParse(raw, out int idx))
            {
                var def = _layout.DropDowns.First(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && idx < def.Items) _drops[name] = idx;
                else Log.Warn("options", $"{source}: {name}={raw} out of range, ignored");
            }
            else
                Log.Warn("options", $"{source}: unknown option {name}, ignored");
        }
    }

    public bool IsChecked(string name) => _checks.TryGetValue(name, out var v) && v;
    public int DropDownIndex(string name) => _drops.TryGetValue(name, out var v) ? v : -1;

    /// <summary>Packed check-box ints for the GO message: box k is bit k of int k/32; count = n/32 + 1.</summary>
    public IEnumerable<int> PackedCheckBoxes()
    {
        int n = _layout.CheckBoxes.Count;
        int ints = n / 32 + 1;
        for (int i = 0; i < ints; i++)
        {
            int packed = 0;
            for (int bit = 0; bit < 32; bit++)
            {
                int k = i * 32 + bit;
                if (k < n && _checks[_layout.CheckBoxes[k].Name])
                    packed |= 1 << bit;
            }
            yield return packed;
        }
    }

    public IEnumerable<int> DropDownIndices() => _layout.DropDowns.Select(d => _drops[d.Name]);

    /// <summary>
    /// GAME broadcast field 13: the broadcastable check boxes packed in their relative order,
    /// then the broadcastable drop-down indices, comma-separated.
    /// </summary>
    public string BroadcastString()
    {
        var boxes = _layout.CheckBoxes.Where(c => c.Broadcast).ToList();
        var parts = new List<string>();
        for (int i = 0; i < (boxes.Count + 31) / 32; i++)
        {
            int packed = 0;
            for (int bit = 0; bit < 32 && i * 32 + bit < boxes.Count; bit++)
                if (_checks[boxes[i * 32 + bit].Name]) packed |= 1 << bit;
            parts.Add(packed.ToString());
        }
        foreach (var d in _layout.DropDowns.Where(d => d.Broadcast))
            parts.Add(_drops[d.Name].ToString());
        return string.Join(",", parts);
    }

    /// <summary>Side indices (0-based into Sides=) disallowed by the checked boxes.</summary>
    public HashSet<int> DisallowedSides()
    {
        var set = new HashSet<int>();
        foreach (var c in _layout.CheckBoxes)
            if (_checks[c.Name]) foreach (var s in c.DisallowedSides) set.Add(s);
        return set;
    }
}
