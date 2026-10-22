using System.Net;
using System.Net.Sockets;

namespace Codex.Launcher;

internal enum CheckStatus { Ok, Warn, Fail, Info }

internal sealed record CheckResult(CheckStatus Status, string Title, string Detail);

/// <summary>Answers "why can't my players join?" in plain words. It checks what the launcher can see from
/// this computer; what it cannot see (a firewall, the Wi-Fi router) it says so rather than guessing.</summary>
internal static class JoinCheck
{
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(
        LauncherSettings settings,
        ServerState state,
        string? problem,
        IReadOnlyList<string> addresses,
        string? tailnet,
        Func<string, Task<bool>> reaches,
        Func<int, bool> portIsFree)
    {
        var results = new List<CheckResult>();
        var running = state == ServerState.Running;

        switch (state)
        {
            case ServerState.Running:
                results.Add(new(CheckStatus.Ok, "The server is running", $"It is up on this computer at {settings.LocalUrl}."));
                break;
            case ServerState.Starting:
                results.Add(new(CheckStatus.Warn, "The server is still starting", "Give it a minute (the first start is slower), then check again."));
                break;
            case ServerState.Failed:
                results.Add(new(CheckStatus.Fail, "The server would not start", problem ?? "See the log for details."));
                if (!portIsFree(settings.Port))
                {
                    results.Add(new(CheckStatus.Fail, $"Another program is using port {settings.Port}", "Close the other program (it may be another copy of TTRPG.Codex), then press Start server."));
                }

                break;
            default:
                results.Add(new(CheckStatus.Fail, "The server is stopped", "Press Start server."));
                break;
        }

        if (settings.AllowLan)
        {
            results.Add(new(CheckStatus.Ok, "Network access is on", "Players on your Wi-Fi are allowed in."));
        }
        else
        {
            results.Add(new(CheckStatus.Fail, "Network access is off", "Only this computer can open the table. Turn on “Let players on my network join”."));
        }

        if (addresses.Count == 0)
        {
            results.Add(new(CheckStatus.Fail, "This computer has no network address",
                "It does not look connected to Wi-Fi or a cable. Connect to your network, then check again."));
        }
        else
        {
            var best = $"http://{addresses[0]}:{settings.Port}";
            results.Add(new(CheckStatus.Ok, "Players use this link", best));
            if (!LanAddress.IsPrivate(IPAddress.Parse(addresses[0])))
            {
                results.Add(new(CheckStatus.Warn, "That address does not look like a home network",
                    "Home and office networks usually start with 192.168 or 10. A VPN may be on, or you may be on a public network. Players on your Wi-Fi may need to use a different address."));
            }

            if (addresses.Count > 1)
            {
                results.Add(new(CheckStatus.Info, "This computer has more than one address",
                    "If the first link does not work, try one of these: " + string.Join(", ", addresses.Skip(1).Select(a => $"http://{a}:{settings.Port}")) + "."));
            }

            if (running && settings.AllowLan)
            {
                if (await reaches(best))
                {
                    results.Add(new(CheckStatus.Ok, "The server answers on your network address", "This computer can reach itself at that link."));
                }
                else
                {
                    results.Add(new(CheckStatus.Fail, "The server does not answer on your network address",
                        "Network access may have been switched on after the server started: press Stop, then Start server."));
                }
            }
        }

        if (tailnet != null)
        {
            results.Add(new(CheckStatus.Info, "Tailscale is on",
                $"Friends who are not on your Wi-Fi can use http://{tailnet}:{settings.Port} if they have Tailscale and you have shared this computer with them."));
        }

        results.Add(new(CheckStatus.Info, "What I cannot check from here",
            "Your firewall and your Wi-Fi router. If everything above is fine and a player still cannot connect: allow TTRPG.Codex through the firewall for private networks when your computer asks, make sure their phone is on the same Wi-Fi (not mobile data), and avoid guest Wi-Fi, which often blocks devices from seeing each other."));
        return results;
    }

    /// <summary>True when a listener could bind the port, so nothing else holds it.</summary>
    public static bool PortIsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static async Task<bool> ReachesAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await http.GetAsync(url + "/login");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
