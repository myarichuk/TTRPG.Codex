using System.Text.Json;
using System.Text.Json.Serialization;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Systems;

/// <summary>One system a release offers as an archive, as <c>systems.json</c> lists it.</summary>
/// <param name="File">The archive's file name, beside <c>systems.json</c> (<c>system-&lt;id&gt;-&lt;version&gt;.zip</c>).</param>
/// <param name="Size">The archive's size in bytes.</param>
/// <param name="Sha256">The archive's SHA-256, lower-case hex.</param>
public sealed record SystemCatalogEntry(
    string Id,
    string Name,
    string Version,
    string? Description,
    string? License,
    string? Compatibility,
    bool Scripts,
    string File,
    long Size,
    string Sha256);

/// <summary>The list of separately released systems a release carries (<c>systems.json</c>): written by
/// <c>Codex.SystemCheck</c> when the release is packed, read by the app's catalog.</summary>
public sealed record SystemCatalogFile(int Format, IReadOnlyList<SystemCatalogEntry> Systems)
{
    /// <summary>The only format this build reads.</summary>
    public const int CurrentFormat = 1;

    public const string FileName = "systems.json";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly System.Text.RegularExpressions.Regex ArchiveNameRule =
        new(@"^system-[a-z0-9_-]+-[0-9A-Za-z.+-]+\.zip$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="file"/> is a name an archive may be released and fetched under: <c>system-&lt;id&gt;-&lt;version&gt;.zip</c>,
    /// with what a system id may hold (lower-case letters, digits, <c>-</c>, <c>_</c>) and no slash.</summary>
    public static bool IsArchiveName(string? file) => file != null && ArchiveNameRule.IsMatch(file);

    /// <summary>The name a system's archive is released under.</summary>
    public static string ArchiveName(SystemDefinition system) => $"system-{system.Id}-{system.Version}.zip";

    /// <summary>The entry for a system and the archive written from it (which is released as <see cref="ArchiveName"/>).</summary>
    public static SystemCatalogEntry EntryFor(SystemDefinition system, string archivePath, bool scripts)
    {
        var file = new FileInfo(archivePath);
        using var stream = file.OpenRead();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
        return new SystemCatalogEntry(
            system.Id, system.Name, system.Version,
            string.IsNullOrWhiteSpace(system.Description) ? null : system.Description.Trim(),
            string.IsNullOrWhiteSpace(system.License?.Name) ? null : system.License!.Name,
            string.IsNullOrWhiteSpace(system.Compatibility) ? null : system.Compatibility,
            scripts, ArchiveName(system), file.Length, hash);
    }

    /// <summary>The catalog's text.</summary>
    public static string Write(IEnumerable<SystemCatalogEntry> entries) =>
        JsonSerializer.Serialize(new SystemCatalogFile(CurrentFormat, entries.OrderBy(e => e.Id, StringComparer.Ordinal).ToList()), Json);

    /// <summary>Reads a catalog; null when it is not one this build understands (not json, another format).</summary>
    public static SystemCatalogFile? TryRead(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<SystemCatalogFile>(json, Json);
            return file is { Format: CurrentFormat, Systems: not null } ? file : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
