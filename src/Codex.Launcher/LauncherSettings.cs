using System.Text.Json;

namespace Codex.Launcher;

/// <summary>What the DM chooses in the launcher window; saved beside the campaign data.</summary>
internal sealed class LauncherSettings
{
    public const int DefaultPort = 5080;

    /// <summary>Listen on every network interface so players on the same network can join.</summary>
    public bool AllowLan { get; set; }

    public int Port { get; set; } = DefaultPort;

    public bool OpenBrowserOnStart { get; set; } = true;

    /// <summary>What Kestrel binds to: loopback only, unless players on the network are allowed in.</summary>
    public string BindUrl => $"http://{(AllowLan ? "0.0.0.0" : "localhost")}:{ValidPort}";

    /// <summary>The address the DM opens in their own browser.</summary>
    public string LocalUrl => $"http://localhost:{ValidPort}";

    /// <summary>The address to give players, or <c>null</c> while only this computer can connect.</summary>
    public string? JoinUrl(string? lanAddress) => AllowLan && lanAddress != null ? $"http://{lanAddress}:{ValidPort}" : null;

    private int ValidPort => Port is > 0 and < 65536 ? Port : DefaultPort;

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
