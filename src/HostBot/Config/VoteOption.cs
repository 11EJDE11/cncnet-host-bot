namespace HostBot.Config;

/// <summary>One "!vote" option: flips a check box or drop-down of the option layout, or a PEO flag.</summary>
public sealed class VoteOption
{
    public const string RandomSides = "randomSides";
    public const string RandomStarts = "randomStarts";

    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Control name from the option layout, or randomSides / randomStarts.</summary>
    public string Option { get; set; } = "";
    public string On { get; set; } = "true";
    public string Off { get; set; } = "false";
    /// <summary>Works, but is not listed in !help or the !vote list.</summary>
    public bool Hidden { get; set; }
    public List<string> Aliases { get; set; } = new();

    public bool IsFlag => Option is RandomSides or RandomStarts;

    public static List<VoteOption> Defaults() => new()
    {
        new() { Name = "sw", Label = "superweapons", Option = "cmbSuperWeaponsModifier", On = "0", Off = "3" },
        new() { Name = "crates", Label = "crates", Option = "chkCrates" },
        new() { Name = "yuri", Label = "Yuri", Option = "chkNoYuri", On = "false", Off = "true" },
        new() { Name = "france", Label = "France", Option = "chkNoFrance", On = "false", Off = "true" },
        new() { Name = "spy", Label = "spies", Option = "chkNoSpy", On = "false", Off = "true" },
        new() { Name = "ra2", Label = "RA2 mode", Option = "chkRA2Mode" },
        new() { Name = "balance", Label = "balance patch", Option = "chkBalancePatch" },
        new() { Name = "random", Label = "random sides", Option = RandomSides },
        new() { Name = "spots", Label = "random spots", Option = RandomStarts, Hidden = true, Aliases = { "spot", "start", "starts", "positions" } },
    };
}
