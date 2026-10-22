using System;
using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

/// <summary>
/// Metadata for one content pack (<c>manifest.yaml</c> or the older <c>manifest.json</c>). A pack
/// belongs to exactly one system, can depend on other packs (loaded first so <c>inherits</c>
/// resolves), overrides lower-priority packs entry-by-entry, and carries the licence its material
/// is under so the app can show the required attribution.
/// </summary>
public class PackManifest
{
    public PackManifest()
    {
    }

    public PackManifest(
        string id,
        string name,
        string version,
        string systemId,
        string minAppVersion = "0.0.0",
        int priority = 0,
        string[]? contentPaths = null,
        string[]? dependencies = null)
    {
        Id = id;
        Name = name;
        Version = version;
        SystemId = systemId;
        MinAppVersion = minAppVersion;
        Priority = priority;
        ContentPaths = contentPaths;
        Dependencies = dependencies;
    }

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string SystemId { get; set; } = string.Empty;
    public string MinAppVersion { get; set; } = "0.0.0";
    public int Priority { get; set; }
    public string[]? ContentPaths { get; set; }
    public string[]? Dependencies { get; set; }

    public string? Description { get; set; }
    public List<string> Authors { get; set; } = new();
    public LicenseInfo? License { get; set; }

    /// <summary>"rules" for a system's reference content, "world" for a setting (places, people, lore),
    /// "homebrew" for table-made material. Purely informational; used for grouping in the UI.</summary>
    public string Kind { get; set; } = "rules";

    public bool IsAppVersionCompatible(string currentAppVersion)
    {
        if (string.IsNullOrWhiteSpace(currentAppVersion)) currentAppVersion = "1.0.0";
        if (string.IsNullOrWhiteSpace(MinAppVersion)) return true;

        var currentClean = currentAppVersion.Split('-')[0].Split('+')[0];
        var minClean = MinAppVersion.Split('-')[0].Split('+')[0];

        if (System.Version.TryParse(currentClean, out var current) &&
            System.Version.TryParse(minClean, out var min))
        {
            return NormalizeVersion(current) >= NormalizeVersion(min);
        }

        return false;
    }

    private static System.Version NormalizeVersion(System.Version v) =>
        new(v.Major, v.Minor, v.Build >= 0 ? v.Build : 0, v.Revision >= 0 ? v.Revision : 0);
}
