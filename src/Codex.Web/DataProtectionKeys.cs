namespace Codex.Web;

/// <summary>
/// Where the Data Protection key ring lives: under the server's data directory, so moving or restoring that folder
/// keeps sign-in cookies and stored AI keys readable (D12). Before this the ring went to the OS user's profile, and
/// the application name was the install path; the switch signs everyone out once and drops stored AI keys, which
/// AI settings then asks for again.
/// </summary>
public static class DataProtectionKeys
{
    public const string ApplicationName = "TTRPG.Codex";

    /// <summary>The key folder for <paramref name="dataDir"/>, created if missing.</summary>
    public static DirectoryInfo Directory(string dataDir)
    {
        var keys = new DirectoryInfo(Path.Combine(Path.GetFullPath(dataDir), "Keys"));
        keys.Create();
        return keys;
    }
}
