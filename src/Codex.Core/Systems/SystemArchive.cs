using System.IO.Compression;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Systems;

/// <summary>What checking a system archive found: the system it holds, what stops it installing, and what it would load
/// with.</summary>
/// <param name="Folder">Where the archive was unpacked (the staging folder), ready to move into place.</param>
/// <param name="Errors">Why it cannot be installed; empty when it can.</param>
/// <param name="Warnings">Load problems it would have once installed, as the About page would list them.</param>
public sealed record SystemArchiveCheck(
    string Folder,
    string? Id,
    string? Name,
    string? Version,
    SystemFingerprint? Fingerprint,
    bool NamesScripts,
    int Packs,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool CanInstall => Errors.Count == 0 && Id != null;
}

/// <summary>
/// A system as an archive (overhaul step 10): a zip holding one system's folder, system.yaml at its top or under one folder,
/// with its translations, scripts and packs. This is what the app's install takes and what <c>Codex.SystemCheck pack</c> writes.
/// <list type="bullet">
/// <item><b>Safe to unpack.</b> Every entry stays inside the folder (no rooted path, no <c>..</c>, no drive), none is a link,
/// and there are at most <see cref="MaxEntries"/> of at most <see cref="MaxEntryBytes"/>, <see cref="MaxBytes"/> in all,
/// counted as they are written, so a zip that lies about its sizes stops there.</item>
/// <item><b>No compiled code</b> (section 4, rule 7): a DLL or any other executable refuses the archive. A system with one is
/// installed by copying its folder onto the server, by whoever runs it.</item>
/// <item><b>Loads as the server would load it.</b> <see cref="SystemDefinitionLoader.Load(string, ICollection{string})"/> on
/// the unpacked folder: an error refuses it, a load problem is a warning. Scripts, when the server has them turned off,
/// refuse it. Each pack must be for this system and is loaded to find its problems.</item>
/// </list>
/// </summary>
public static class SystemArchive
{
    public const int MaxEntries = 2000;
    public const long MaxEntryBytes = 16L * 1024 * 1024;
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>Files that are code a server would run natively or through .NET: never in an uploaded system.</summary>
    private static readonly HashSet<string> CompiledExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".so", ".dylib", ".pdb", ".com", ".bat", ".cmd", ".ps1", ".sh", ".msi"
    };

    /// <summary>Unpacks <paramref name="zip"/> into <paramref name="folder"/> (which must not exist yet) and checks the system it
    /// holds. Whatever the outcome, <paramref name="folder"/> is the caller's to move into place or delete.</summary>
    public static async Task<SystemArchiveCheck> CheckAsync(Stream zip, string folder, bool scriptsAllowed)
    {
        var errors = new List<string>();
        Directory.CreateDirectory(folder);
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            Unpack(archive, folder, errors);
        }
        catch (InvalidDataException ex)
        {
            errors.Add($"It is not a zip archive that can be read: {ex.Message}");
        }

        if (errors.Count > 0) return Failed(folder, errors);

        var yaml = Path.Combine(folder, SystemDefinitionLoader.FileName);
        if (!File.Exists(yaml)) return Failed(folder, new[] { $"There is no {SystemDefinitionLoader.FileName} at its top, or in a single folder at its top." });
        var nested = Directory.GetFiles(folder, SystemDefinitionLoader.FileName, SearchOption.AllDirectories).Where(f => !string.Equals(f, yaml, StringComparison.Ordinal)).ToList();
        if (nested.Count > 0)
        {
            return Failed(folder, new[] { $"It holds more than one system: there is another {SystemDefinitionLoader.FileName} in {Relative(folder, Path.GetDirectoryName(nested[0])!)}. Install each on its own." });
        }

        SystemDefinition definition;
        var unknownKeys = new List<string>();
        try
        {
            definition = SystemDefinitionLoader.Load(yaml, unknownKeys);
        }
        catch (Exception ex)
        {
            return Failed(folder, SystemDefinitionLoader.ErrorsOf(ex).Select(e => e.Replace(folder + Path.DirectorySeparatorChar, string.Empty, StringComparison.Ordinal)).ToList());
        }

        var scripts = Rules.SystemScripts.FilesNamed(definition).ToList();
        if (!scriptsAllowed && scripts.Count > 0)
        {
            errors.Add($"It names scripts ({string.Join(", ", scripts)}), and this server has scripts turned off (Codex:Systems:Scripts).");
        }

        var warnings = new List<string>();
        if (unknownKeys.Count > 0) warnings.Add(PluginLoader.UnknownKeysMessage(unknownKeys));
        warnings.AddRange(PluginLoader.ProblemsOf(definition));

        var packs = 0;
        var registry = new ContentRegistry();
        var packLoader = new YamlContentPackLoader(registry);
        foreach (var manifest in Directory.GetFiles(folder, YamlContentPackLoader.ManifestYaml, SearchOption.AllDirectories)
                     .Concat(Directory.GetFiles(folder, YamlContentPackLoader.ManifestJson, SearchOption.AllDirectories)).Order(StringComparer.Ordinal))
        {
            var packFolder = Path.GetDirectoryName(manifest)!;
            var where = Relative(folder, packFolder);
            try
            {
                var read = await packLoader.ReadManifestAsync(packFolder);
                if (!string.Equals(read.SystemId, definition.Id, StringComparison.Ordinal))
                {
                    errors.Add($"The pack in {where} is for system '{read.SystemId}', not '{definition.Id}'.");
                    continue;
                }

                await packLoader.LoadPackAsync(packFolder, definition);
                packs++;
                warnings.AddRange(packLoader.TakeProblems().Select(p => $"Pack {read.Id}: {p}"));
            }
            catch (Exception ex)
            {
                errors.Add($"The pack in {where} does not load: {ex.Message}");
            }
        }

        return new SystemArchiveCheck(folder, definition.Id, definition.Name, definition.Version, definition.Fingerprint, scripts.Count > 0, packs, errors, warnings);
    }

    private static SystemArchiveCheck Failed(string folder, IReadOnlyList<string> errors) =>
        new(folder, null, null, null, null, false, 0, errors, Array.Empty<string>());

    private static string Relative(string folder, string path)
    {
        var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
        return relative == "." ? "its top folder" : relative + "/";
    }

    /// <summary>Writes the archive's files under <paramref name="folder"/>, without the one folder everything sits in when it
    /// does (a zip of the system's folder rather than of its contents).</summary>
    private static void Unpack(ZipArchive archive, string folder, List<string> errors)
    {
        var files = archive.Entries.Where(e => !IsFolder(e)).ToList();
        if (files.Count == 0)
        {
            errors.Add("It is empty.");
            return;
        }

        if (archive.Entries.Count > MaxEntries)
        {
            errors.Add($"It has {archive.Entries.Count} entries; a system may have at most {MaxEntries}.");
            return;
        }

        var names = new List<(ZipArchiveEntry Entry, string Name)>();
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part == ".."))
            {
                errors.Add($"{entry.FullName} would be written outside the system's folder.");
                continue;
            }

            if (IsLink(entry))
            {
                errors.Add($"{entry.FullName} is a link, which a system may not hold.");
                continue;
            }

            if (!IsFolder(entry) && CompiledExtensions.Contains(Path.GetExtension(name)))
            {
                errors.Add($"{entry.FullName} is compiled code. A system with compiled code is installed by copying its folder onto the server, not by upload.");
                continue;
            }

            names.Add((entry, name));
        }

        if (errors.Count > 0) return;

        // One folder holding everything ("street/system.yaml", "street/rules.js") is the system's own folder: drop it.
        var tops = names.Select(n => n.Name.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
        var prefix = tops.Count == 1 && names.All(n => n.Name.Contains('/')) ? tops[0] + "/" : "";

        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var (entry, name) in names)
        {
            var relative = name[prefix.Length..];
            if (relative.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(folder, relative));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                errors.Add($"{entry.FullName} would be written outside the system's folder.");
                return;
            }

            if (IsFolder(entry))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            if (entry.Length > MaxEntryBytes)
            {
                errors.Add($"{entry.FullName} is {entry.Length / (1024 * 1024)} MB; a file may be at most {MaxEntryBytes / (1024 * 1024)} MB.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Create(target);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                total += read;
                if (written > MaxEntryBytes || total > MaxBytes)
                {
                    errors.Add(written > MaxEntryBytes
                        ? $"{entry.FullName} unpacks to more than {MaxEntryBytes / (1024 * 1024)} MB."
                        : $"It unpacks to more than {MaxBytes / (1024 * 1024)} MB.");
                    return;
                }

                output.Write(buffer, 0, read);
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="folder"/> as a system archive, its files at the archive's top: what the app's install takes. Hidden
    /// folders and files, <c>bin</c>, <c>obj</c> and <c>node_modules</c> are left out. A folder with compiled code cannot be one
    /// (it is installed by copying), so that is an error, as is a folder without a system.yaml.
    /// </summary>
    /// <returns>Why it was not written; empty when it was.</returns>
    public static IReadOnlyList<string> Write(string folder, Stream output)
    {
        folder = Path.GetFullPath(folder);
        if (!File.Exists(Path.Combine(folder, SystemDefinitionLoader.FileName))) return new[] { $"{folder} has no {SystemDefinitionLoader.FileName}." };
        var files = Files(folder).ToList();
        var compiled = files.Where(f => CompiledExtensions.Contains(Path.GetExtension(f))).ToList();
        if (compiled.Count > 0)
        {
            return new[] { $"It holds compiled code ({string.Join(", ", compiled.Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')))}), which is installed by copying the folder onto the server, not as an archive." };
        }

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            archive.CreateEntryFromFile(file, Path.GetRelativePath(folder, file).Replace('\\', '/'), CompressionLevel.Optimal);
        }

        return Array.Empty<string>();

        static IEnumerable<string> Files(string directory)
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                if (!Path.GetFileName(file).StartsWith('.')) yield return file;
            }

            foreach (var sub in Directory.GetDirectories(directory))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules") continue;
                foreach (var file in Files(sub)) yield return file;
            }
        }
    }

    private static bool IsFolder(ZipArchiveEntry entry) => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    /// <summary>A Unix symbolic link: the zip's external attributes carry the file's mode in their top 16 bits.</summary>
    private static bool IsLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
}
