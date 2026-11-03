using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codex.Launcher;

/// <summary>What the DM chooses in the launcher window; saved beside the campaign data.</summary>
internal sealed class LauncherSettings
{
    public const int DefaultPort = 5080;

    /// <summary>Listen on every network interface so players on the same network can join.</summary>
    public bool AllowLan { get; set; }

    public int Port { get; set; } = DefaultPort;

    public bool OpenBrowserOnStart { get; set; } = true;

    /// <summary>Whether the first-run welcome has been answered.</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>The launcher's (and the server's default) language tag; <c>null</c> follows the operating system.</summary>
    public string? Language { get; set; }

    public const int DefaultHttpsPort = 5443;

    /// <summary>Serve players on the network over HTTPS, with a certificate the launcher makes itself. The DM's own
    /// browser keeps the plain loopback address, which browsers treat as secure and which never warns.</summary>
    public bool Https { get; set; } = true;

    public int HttpsPort { get; set; } = DefaultHttpsPort;

    /// <summary>Whether the server was started with its HTTPS address. Not saved: it depends on whether the launcher
    /// could make a certificate this time, and the links and invites follow what the server really serves.</summary>
    [JsonIgnore]
    public bool TlsActive { get; set; }

    /// <summary>What Kestrel binds to: loopback only, unless players on the network are allowed in; then the network
    /// gets HTTPS when there is a certificate for it, and loopback keeps its plain address.</summary>
    [JsonIgnore]
    public string BindUrl => AllowLan && TlsActive
        ? $"http://localhost:{ValidPort};https://0.0.0.0:{ValidHttpsPort}"
        : $"http://{(AllowLan ? "0.0.0.0" : "localhost")}:{ValidPort}";

    /// <summary>The address the DM opens in their own browser.</summary>
    [JsonIgnore]
    public string LocalUrl => $"http://localhost:{ValidPort}";

    /// <summary>How players reach this computer at <paramref name="address"/>: HTTPS on its own port while the server
    /// serves it, plain HTTP otherwise.</summary>
    public string NetworkUrl(string address) => TlsActive ? $"https://{address}:{ValidHttpsPort}" : $"http://{address}:{ValidPort}";

    /// <summary>The addresses worth copying: this computer always, the network and Tailscale only while the
    /// server is open to the network.</summary>
    public IReadOnlyList<LauncherLink> Links(string? lanAddress, string? tailnetAddress, string? internetUrl = null)
    {
        var links = new List<LauncherLink> { new(LinkKind.This, LocalUrl) };
        if (AllowLan && lanAddress != null) links.Add(new(LinkKind.Network, NetworkUrl(lanAddress)));
        if (AllowLan && tailnetAddress != null) links.Add(new(LinkKind.Tailscale, NetworkUrl(tailnetAddress)));
        if (internetUrl != null) links.Add(new(LinkKind.Internet, internetUrl));
        return links;
    }

    /// <summary>Whether the server is open to the whole internet through Tailscale Funnel. Deliberately not saved:
    /// it is off every time the launcher starts, so a reboot never puts the table back on the internet by itself.</summary>
    [JsonIgnore]
    public bool OpenToInternet { get; set; }

    /// <summary>The address invite links should use: the network address, or Tailscale when there is no other,
    /// or <c>null</c> while only this computer can connect.</summary>
    public string? PlayerUrl(string? lanAddress, string? tailnetAddress) => Links(lanAddress, tailnetAddress).Skip(1).Select(l => l.Url).FirstOrDefault();

    /// <summary>The Tailscale address, only when <see cref="PlayerUrl"/> already leads with the network one, so
    /// the invite screen can offer both: a player away from the home network cannot use the first.</summary>
    public string? RemoteUrl(string? lanAddress, string? tailnetAddress) =>
        AllowLan && lanAddress != null && tailnetAddress != null ? NetworkUrl(tailnetAddress) : null;

    private int ValidPort => Port is > 0 and < 65536 ? Port : DefaultPort;

    // The same port as the plain one would make the server fail to start.
    private int ValidHttpsPort => HttpsPort is > 0 and < 65536 && HttpsPort != ValidPort ? HttpsPort : (ValidPort == DefaultHttpsPort ? DefaultHttpsPort + 1 : DefaultHttpsPort);

    /// <summary>The port the server really listens on (a bad saved value falls back to the default).</summary>
    [JsonIgnore]
    public int EffectivePort => ValidPort;

    public static LauncherSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path)) ?? new LauncherSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file must not stop the launcher: start from the defaults.
        }

        return new LauncherSettings();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a preference is not worth an error dialog.
        }
    }
}

/// <summary>Which address a link is. Logic keys on this; the label is only what the DM reads.</summary>
internal enum LinkKind { This, Network, Tailscale, Internet }

internal sealed record LauncherLink(LinkKind Kind, string Url)
{
    /// <summary>The label in the current language, resolved when it is shown.</summary>
    public string Label => Strings.LinkLabel(Kind);
}
