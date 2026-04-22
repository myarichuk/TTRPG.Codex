using System;

namespace Codex.Plugin.Abstractions;

public record PackManifest(
    string Id,
    string Name,
    string Version,
    string SystemId,
    string? Description = null,
    string? ThumbnailPath = null,
    string MinAppVersion = "1.0.0",
    int Priority = 0,
    string[]? ContentPaths = null,
    string[]? Dependencies = null
)
{
    public bool IsAppVersionCompatible(string currentAppVersion)
    {
        if (string.IsNullOrWhiteSpace(currentAppVersion)) currentAppVersion = "1.0.0";
        if (string.IsNullOrWhiteSpace(MinAppVersion)) return true;

        var currentClean = currentAppVersion.Split('-')[0].Split('+')[0];
        var minClean = MinAppVersion.Split('-')[0].Split('+')[0];

        if (System.Version.TryParse(currentClean, out var current) &&
            System.Version.TryParse(minClean, out var min))
        {
            // Normalize versions to 4 parts (Major.Minor.Build.Revision)
            // Or just Ensure at least Build is set if missing, to make 1.0 == 1.0.0
            var normCurrent = NormalizeVersion(current);
            var normMin = NormalizeVersion(min);

            return normCurrent >= normMin;
        }

        return false;
    }

    private static System.Version NormalizeVersion(System.Version v)
    {
        return new System.Version(
            v.Major,
            v.Minor,
            v.Build >= 0 ? v.Build : 0,
            v.Revision >= 0 ? v.Revision : 0
        );
    }
}
