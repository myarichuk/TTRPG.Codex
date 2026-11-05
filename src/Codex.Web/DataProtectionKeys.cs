namespace Codex.Web;

/// <summary>
/// The Data Protection key ring: in <see cref="DataFolders.Keys"/>, beside the database by default, so sign-in cookies and
/// stored AI keys stay readable (D12). Moving or restoring the database means moving its <c>Keys</c> sibling too: without
/// it, stored AI keys need entering again and everyone signs in once more. The ring is plain XML at rest.
/// </summary>
public static class DataProtectionKeys
{
    public const string ApplicationName = "TTRPG.Codex";

    /// <summary>The key folder, created if missing.</summary>
    public static DirectoryInfo Directory(string keysDir)
    {
        var keys = new DirectoryInfo(Path.GetFullPath(keysDir));
        keys.Create();
        return keys;
    }
}
