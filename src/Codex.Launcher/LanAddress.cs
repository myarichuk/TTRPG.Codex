using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Codex.Launcher;

internal static class LanAddress
{
    /// <summary>The IPv4 address other computers on the network would use to reach this one, or <c>null</c>.
    /// Ethernet and Wi-Fi come before virtual adapters (VPNs, Docker, VM bridges).</summary>
    public static string? Best()
    {
        try
        {
            return Rank(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => (n.Name, n.NetworkInterfaceType, a.Address))));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>This computer's Tailscale address (100.64.0.0/10), or <c>null</c>. Players who have Tailscale
    /// can join from outside the home network with it, with no router settings.</summary>
    public static string? Tailnet()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                .FirstOrDefault(IsTailnet)?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    internal static bool IsTailnet(IPAddress a) => a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [100, >= 64 and <= 127, ..];

    /// <summary>Every usable address, best first.</summary>
    public static IReadOnlyList<string> All()
    {
        try
        {
            return RankAll(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => (n.Name, n.NetworkInterfaceType, a.Address)))).ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<string>();
        }
    }

    internal static string? Rank(IEnumerable<(string Name, NetworkInterfaceType Type, IPAddress Address)> candidates) => RankAll(candidates).FirstOrDefault();

    internal static IEnumerable<string> RankAll(IEnumerable<(string Name, NetworkInterfaceType Type, IPAddress Address)> candidates) =>
        candidates
            .Where(c => !IPAddress.IsLoopback(c.Address) && !IsLinkLocal(c.Address))
            .OrderBy(c => IsVirtual(c.Name) ? 2 : c.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .ThenBy(c => IsPrivate(c.Address) ? 0 : 1)
            .Select(c => c.Address.ToString());

    private static bool IsLinkLocal(IPAddress a) => a.GetAddressBytes() is [169, 254, ..];

    internal static bool IsPrivate(IPAddress a) => a.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..];

    private static bool IsVirtual(string name) =>
        new[] { "docker", "veth", "virbr", "vbox", "vmnet", "vethernet", "wsl", "tailscale", "zerotier", "utun", "br-" }
            .Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase));
}
