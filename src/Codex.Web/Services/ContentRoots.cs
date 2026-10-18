namespace Codex.Web.Services;

/// <summary>
/// Resolves where systems and content packs are loaded from. Two kinds of location are combined:
/// <list type="bullet">
/// <item><c>plugins/</c> and <c>content/</c> next to the published executable (what a release ships);</item>
/// <item>the configured <c>Codex:ContentDirectories</c> (dev-time defaults point at the repo's
/// <c>content/</c> folder, resolved against the content root).</item>
/// </list>
/// Missing directories are skipped quietly; the loader logs what it actually found.
/// </summary>
public static class ContentRoots
{
    public static IReadOnlyList<string> Resolve(IConfiguration configuration, string contentRootPath)
    {
        var roots = new List<string>();

        foreach (var folder in new[] { "plugins", "content" })
        {
            var beside = Path.Combine(AppContext.BaseDirectory, folder);
            if (Directory.Exists(beside))
            {
                roots.Add(Path.GetFullPath(beside));
            }
        }

        var configured = configuration.GetSection("Codex:ContentDirectories").Get<string[]>() ?? Array.Empty<string>();
        var legacy = configuration["Codex:PluginsDirectory"];
        foreach (var relative in configured.Concat(legacy != null ? new[] { legacy } : Array.Empty<string>()))
        {
            if (string.IsNullOrWhiteSpace(relative)) continue;
            var full = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(contentRootPath, relative));
            if (Directory.Exists(full) && !roots.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(full);
            }
        }

        return roots;
    }
}
