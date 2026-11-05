using System.Globalization;
using System.Net.NetworkInformation;

namespace HostBot.Tunnels;

/// <summary>One line of https://cncnet.org/master-list (docs/protocol/tunnels.md section 1).</summary>
public sealed class TunnelInfo
{
    public string Address { get; init; } = "";
    public int Port { get; init; }
    public string Country { get; init; } = "";
    public string CountryCode { get; init; } = "";
    public string Name { get; init; } = "";
    public bool RequiresPassword { get; init; }
    public int Clients { get; init; }
    public int MaxClients { get; init; }
    public bool Official { get; init; }
    public bool Recommended { get; init; }
    public int Version { get; init; }
    public long? PingMs { get; set; }

    public string Endpoint => $"{Address}:{Port}";
    public override string ToString() => $"{Name} {Endpoint} v{Version} {Clients}/{MaxClients}{(PingMs is { } p ? $" {p} ms" : "")}";

    public static TunnelInfo? Parse(string line)
    {
        var p = line.Split(';');
        if (p.Length < 11) return null;
        var addr = p[0].Split(':');
        if (addr.Length != 2 || !int.TryParse(addr[1], out int port)) return null;
        int.TryParse(p[7], out int status);
        return new TunnelInfo
        {
            Address = addr[0],
            Port = port,
            Country = p[1],
            CountryCode = p[2],
            Name = p[3],
            RequiresPassword = p[4] != "0",
            Clients = int.TryParse(p[5], out var c) ? c : 0,
            MaxClients = int.TryParse(p[6], out var m) ? m : 0,
            Official = status == 2,
            Recommended = status == 1,
            Version = int.TryParse(p[10], out var v) ? v : 0,
        };
    }
}

public sealed class TunnelList
{
    private const string Tag = "tunnels";
    public const string DefaultUrl = "https://cncnet.org/master-list";

    private readonly HttpClient _http;
    private readonly string _url;
    public List<TunnelInfo> Tunnels { get; private set; } = new();
    public DateTime FetchedAt { get; private set; }

    public TunnelList(HttpClient http, string url = DefaultUrl)
    {
        _http = http;
        _url = url;
    }

    /// <summary>Downloads the list, keeping only v2/v3 tunnels without a password, like the client.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        string text = await _http.GetStringAsync(_url, ct);
        var seen = new HashSet<string>();
        var list = new List<TunnelInfo>();
        foreach (var line in text.Split('\n').Skip(1))
        {
            var t = TunnelInfo.Parse(line.Trim());
            if (t is null || t.RequiresPassword || t.Version is not (2 or 3)) continue;
            if (!seen.Add(t.Endpoint)) continue;
            list.Add(t);
        }
        Tunnels = list;
        FetchedAt = DateTime.UtcNow;
        Log.Info(Tag, $"master list: {list.Count(t => t.Version == 2)} v2, {list.Count(t => t.Version == 3)} v3 tunnels");
    }

    /// <summary>ICMP-pings every distinct address once (the client does the same, 1 s timeout).</summary>
    public async Task PingAllAsync(CancellationToken ct)
    {
        var byIp = Tunnels.GroupBy(t => t.Address).ToList();
        await Parallel.ForEachAsync(byIp, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (group, token) =>
        {
            long? ms = null;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(group.Key, 1000);
                if (reply.Status == IPStatus.Success)
                    ms = reply.RoundtripTime;
            }
            catch { }
            foreach (var t in group) t.PingMs = ms;
        });
    }

    /// <summary>
    /// Picks a tunnel of the wanted version. Preferred name fragments are tried in order first;
    /// otherwise the best-rated official/recommended one (lowest ping^2 * usage, as the client's UI does).
    /// </summary>
    public TunnelInfo? Choose(int version, IReadOnlyList<string> preferredNames)
    {
        var candidates = Tunnels.Where(t => t.Version == version && (t.Official || t.Recommended) && t.Clients < t.MaxClients).ToList();
        foreach (var pref in preferredNames)
        {
            var hit = candidates
                .Where(t => t.Name.Contains(pref, StringComparison.OrdinalIgnoreCase) || t.Country.Contains(pref, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.PingMs ?? long.MaxValue)
                .FirstOrDefault();
            if (hit is not null)
                return hit;
        }
        return candidates
            .Where(t => t.PingMs is not null)
            .OrderBy(t => Rating(t))
            .FirstOrDefault() ?? candidates.FirstOrDefault();

        static double Rating(TunnelInfo t)
        {
            double usage = t.MaxClients > 0 ? (double)t.Clients / t.MaxClients : 0.1;
            if (usage <= 0) usage = 0.1;
            double ping = t.PingMs ?? 1000;
            return Math.Round(ping * ping * usage * 100);
        }
    }

    public TunnelInfo? Find(string endpoint) => Tunnels.FirstOrDefault(t => t.Endpoint == endpoint);

    /// <summary>V2 only: asks the tunnel for N player ids. The ids expire after 30 s without traffic.</summary>
    public async Task<List<int>> RequestV2PortsAsync(TunnelInfo tunnel, int playerCount, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        string body = await _http.GetStringAsync($"http://{tunnel.Address}:{tunnel.Port}/request?clients={playerCount}", timeout.Token);
        return body.Trim().Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToList();
    }
}
