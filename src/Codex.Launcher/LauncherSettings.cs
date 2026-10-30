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

    /// <summary>What Kestrel binds to: loopback only, unless players on the network are allowed in.</summary>
    [JsonIgnore]
    public string BindUrl => $"http://{(AllowLan ? "0.0.0.0" : "localhost")}:{ValidPort}";

    /// <summary>The address the DM opens in their own browser.</summary>
    [JsonIgnore]
    public string LocalUrl => $"http://localhost:{ValidPort}";

    /// <summary>The addresses worth copying: this computer always, the network and Tailscale only while the
    /// server is open to the network.</summary>
    public IReadOnlyList<LauncherLink> Links(string? lanAddress, string? tailnetAddress, string? internetUrl = null)
    {
        var links = new List<LauncherLink> { new(LinkKind.This, LocalUrl) };
        if (AllowLan && lanAddress != null) links.Add(new(LinkKind.Network, $"http://{lanAddress}:{ValidPort}"));
        if (AllowLan && tailnetAddress != null) links.Add(new(LinkKind.Tailscale, $"http://{tailnetAddress}:{ValidPort}"));
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
        AllowLan && lanAddress != null && tailnetAddress != null ? $"http://{tailnetAddress}:{ValidPort}" : null;

    private int ValidPort => Port is > 0 and < 65536 ? Port : DefaultPort;

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
