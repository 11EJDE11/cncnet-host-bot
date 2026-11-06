using System.Security.Cryptography;
using System.Text;
using HostBot.Config;
using HostBot.Irc;
using HostBot.Lobby;

namespace HostBot;

/// <summary>
/// Diagnostic: connects as a plain client, prints what other hosts broadcast and, optionally,
/// joins one host's room as a silent player to print the GO/PO/PEO messages it sends.
/// </summary>
public static class Capture
{
    public static async Task<int> RunAsync(BotConfig cfg, string? hostNick, string? password, CancellationToken ct)
    {
        string game = cfg.Irc.Game;
        string broadcast = $"#cncnet-{game.ToLowerInvariant()}-games";
        string nick = "cap" + Random.Shared.Next(1000, 9999);
        var irc = new IrcClient(nick, $"{game}.{Random.Shared.Next(0x100000, 0xFFFFFF):x6}", $"{cfg.Irc.ClientVersion} {game} CnCNet".Trim());
        var observer = new LobbyObserver(irc, broadcast);
        string? roomChannel = null;
        irc.MessageReceived += msg =>
        {
            if (msg.Command is "NOTICE" or "PRIVMSG" && msg.Trailing.StartsWith('\x01'))
            {
                string target = msg.Param(0);
                if (string.Equals(target, broadcast, StringComparison.OrdinalIgnoreCase))
                {
                    var f = msg.Trailing.Trim('\x01')[5..].Split(';');
                    Console.WriteLine($"GAME from {msg.Nick}: revision {f[0]}, {f.Length} fields, version '{f[1]}', tunnel '{(f.Length > 9 ? f[9] : "")}'{(f.Length > 13 ? $", options '{f[13]}'" : "")}: {msg.Trailing.Trim('\x01')}");
                }
                else if (roomChannel is not null && string.Equals(target, roomChannel, StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"ROOM {msg.Nick}: {msg.Trailing.Trim('\x01')}");
            }
        };
        await irc.ConnectAsync(ct);
        irc.Queue($"JOIN {broadcast}", 9);
        Console.WriteLine($"connected as {irc.Nick}; listening on {broadcast} (Ctrl+C to stop)");

        try
        {
            if (hostNick is not null)
            {
                HostedGame? target = null;
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (target is null && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500, ct);
                    target = observer.Games().FirstOrDefault(g => g.Host.Equals(hostNick, StringComparison.OrdinalIgnoreCase));
                }
                if (target is null)
                {
                    Console.WriteLine($"no game hosted by {hostNick} seen within 60 s");
                    return 1;
                }
                roomChannel = target.Channel;
                string key = target.Flags.Length > 1 && target.Flags[1] == '1'
                    ? password ?? throw new InvalidOperationException("room has a custom password; pass it as the third argument")
                    : Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(target.Channel)))[..10];
                Console.WriteLine($"joining {target.Channel} ({target.Name})");
                await irc.SendNowAsync($"JOIN {target.Channel} {key}", ct);
                irc.SendCtcp(target.Channel, "FHSH 0000000000000000000000000000000000000000", 10);
            }
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await irc.DisposeAsync();
        }
        return 0;
    }
}
