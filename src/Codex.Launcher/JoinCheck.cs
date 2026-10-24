using System.Globalization;
using System.Net;
using System.Net.Sockets;
using static Codex.Launcher.Strings;

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
        var start = L["Launcher.StartServer"];
        var lanToggle = L["Launcher.Lan.Toggle"];

        switch (state)
        {
            case ServerState.Running:
                results.Add(new(CheckStatus.Ok, L["Launcher.Join.Running.Title"], L["Launcher.Join.Running.Detail", Isolate(settings.LocalUrl)]));
                break;
            case ServerState.Starting:
                results.Add(new(CheckStatus.Warn, L["Launcher.Join.Starting.Title"], L["Launcher.Join.Starting.Detail"]));
                break;
            case ServerState.Failed:
                results.Add(new(CheckStatus.Fail, L["Launcher.Status.Failed"], problem ?? L["Launcher.Join.SeeLog"]));
                if (!portIsFree(settings.Port))
                {
                    results.Add(new(CheckStatus.Fail, L["Launcher.Join.PortBusy.Title", settings.Port.ToString(CultureInfo.InvariantCulture)], L["Launcher.Join.PortBusy.Detail", start]));
                }

                break;
            default:
                results.Add(new(CheckStatus.Fail, L["Launcher.Join.Stopped.Title"], L["Launcher.Join.Stopped.Detail", start]));
                break;
        }

        if (settings.AllowLan)
        {
            results.Add(new(CheckStatus.Ok, L["Launcher.Join.LanOn.Title"], L["Launcher.Join.LanOn.Detail"]));
        }
        else
        {
            results.Add(new(CheckStatus.Fail, L["Launcher.Join.LanOff.Title"], L["Launcher.Join.LanOff.Detail", lanToggle]));
        }

        if (addresses.Count == 0)
        {
            results.Add(new(CheckStatus.Fail, L["Launcher.Join.NoAddress.Title"], L["Launcher.Join.NoAddress.Detail"]));
        }
        else
        {
            var best = $"http://{addresses[0]}:{settings.Port}";
            results.Add(new(CheckStatus.Ok, L["Launcher.Join.UseLink.Title"], Isolate(best)));
            if (!LanAddress.IsPrivate(IPAddress.Parse(addresses[0])))
            {
                results.Add(new(CheckStatus.Warn, L["Launcher.Join.Odd.Title"], L["Launcher.Join.Odd.Detail"]));
            }

            if (addresses.Count > 1)
            {
                var others = string.Join(", ", addresses.Skip(1).Select(a => Isolate($"http://{a}:{settings.Port}")));
                results.Add(new(CheckStatus.Info, L["Launcher.Join.Multi.Title"], L["Launcher.Join.Multi.Detail", others]));
            }

            if (running && settings.AllowLan)
            {
                if (await reaches(best))
                {
                    results.Add(new(CheckStatus.Ok, L["Launcher.Join.Answers.Title"], L["Launcher.Join.Answers.Detail"]));
                }
                else
                {
                    results.Add(new(CheckStatus.Fail, L["Launcher.Join.NoAnswer.Title"], L["Launcher.Join.NoAnswer.Detail", L["Launcher.StopWord"], start]));
                }
            }
        }

        if (tailnet != null)
        {
            results.Add(new(CheckStatus.Info, L["Launcher.Join.Tailscale.Title"], L["Launcher.Join.Tailscale.Detail", Isolate($"http://{tailnet}:{settings.Port}")]));
        }

        results.Add(new(CheckStatus.Info, L["Launcher.Join.Unknown.Title"], L["Launcher.Join.Unknown.Detail"]));
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
