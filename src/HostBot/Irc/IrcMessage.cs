namespace HostBot.Irc;

/// <summary>One parsed IRC line: optional ":prefix", command, middle params and trailing param.</summary>
public sealed class IrcMessage
{
    public string? Prefix { get; init; }
    public string Command { get; init; } = "";
    public List<string> Params { get; init; } = new();
    public string Raw { get; init; } = "";

    /// <summary>Nick part of "nick!ident@host" (or the whole prefix for servers).</summary>
    public string? Nick => Prefix is null ? null : Prefix.Split('!', 2)[0];
    public string? Ident
    {
        get
        {
            if (Prefix is null) return null;
            int bang = Prefix.IndexOf('!');
            if (bang < 0) return null;
            int at = Prefix.IndexOf('@', bang);
            return at < 0 ? Prefix[(bang + 1)..] : Prefix[(bang + 1)..at];
        }
    }
    public string? Host
    {
        get
        {
            if (Prefix is null) return null;
            int at = Prefix.IndexOf('@');
            return at < 0 ? null : Prefix[(at + 1)..];
        }
    }

    public string Param(int i) => i < Params.Count ? Params[i] : "";
    public string Trailing => Params.Count > 0 ? Params[^1] : "";

    public static IrcMessage Parse(string line)
    {
        var msg = new List<string>();
        string? prefix = null;
        int pos = 0;
        if (line.StartsWith(':'))
        {
            int sp = line.IndexOf(' ');
            if (sp < 0) return new IrcMessage { Prefix = line[1..], Raw = line };
            prefix = line[1..sp];
            pos = sp + 1;
        }
        while (pos < line.Length && line[pos] == ' ') pos++;
        int cmdEnd = line.IndexOf(' ', pos);
        string command = cmdEnd < 0 ? line[pos..] : line[pos..cmdEnd];
        pos = cmdEnd < 0 ? line.Length : cmdEnd + 1;
        while (pos < line.Length)
        {
            while (pos < line.Length && line[pos] == ' ') pos++;
            if (pos >= line.Length) break;
            if (line[pos] == ':')
            {
                msg.Add(line[(pos + 1)..]);
                break;
            }
            int next = line.IndexOf(' ', pos);
            if (next < 0)
            {
                msg.Add(line[pos..]);
                break;
            }
            msg.Add(line[pos..next]);
            pos = next + 1;
        }
        return new IrcMessage { Prefix = prefix, Command = command.ToUpperInvariant(), Params = msg, Raw = line };
    }
}
