using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Codex.Core.Models;
using Codex.Plugin.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Codex.Core;

/// <summary>
/// Writes a content pack (manifest + YAML entries) as a directory or a <c>.zip</c> that
/// <see cref="YamlContentPackLoader"/> loads back unchanged. Used to turn a campaign's homebrew into
/// a shareable pack and by tests to prove the round trip.
/// </summary>
public class ContentPackExporter
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ISerializer _yamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    public async Task ExportToZipAsync(PackManifest manifest,
                                     IEnumerable<AbilityDefinition> abilities,
                                     IEnumerable<ActorDefinition> actors,
                                     IEnumerable<LocationDefinition> locations,
                                     string outputPath,
                                     IEnumerable<ContentEntry>? entries = null,
                                     SystemDefinition? system = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "codex-export-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            await ExportToDirectoryAsync(manifest, abilities, actors, locations, tempDir, entries, system);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            ZipFile.CreateFromDirectory(tempDir, outputPath);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    /// <summary>Zips a pack in memory - what the web UI streams to the browser.</summary>
    public async Task<byte[]> ExportToZipBytesAsync(PackManifest manifest, IEnumerable<ContentEntry> entries, SystemDefinition? system = null)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), "codex-export-" + Guid.NewGuid() + ".zip");
        try
        {
            await ExportToZipAsync(manifest, Array.Empty<AbilityDefinition>(), Array.Empty<ActorDefinition>(), Array.Empty<LocationDefinition>(), tempZip, entries, system);
            return await File.ReadAllBytesAsync(tempZip);
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    public async Task ExportToDirectoryAsync(PackManifest manifest,
                                           IEnumerable<AbilityDefinition> abilities,
                                           IEnumerable<ActorDefinition> actors,
                                           IEnumerable<LocationDefinition> locations,
                                           string directory,
                                           IEnumerable<ContentEntry>? entries = null,
                                           SystemDefinition? system = null)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, YamlContentPackLoader.ManifestYaml), _yamlSerializer.Serialize(manifest), Utf8NoBom);

        await WriteAllAsync(directory, "abilities", abilities, a => a.Id);
        await WriteAllAsync(directory, "actors", actors, a => a.Id);
        await WriteAllAsync(directory, "locations", locations, l => l.Id);

        foreach (var group in (entries ?? Array.Empty<ContentEntry>()).GroupBy(e => e.Type))
        {
            var folder = system?.FindType(group.Key)?.FolderOrType ?? group.Key;
            await WriteAllAsync(directory, folder, group.Select(ToPortable), e => e.Id);
        }
    }

    /// <summary>The on-disk shape of an entry: only the fields the loader reads, without the
    /// system/pack ids it fills in from the manifest or computed properties like FullId.</summary>
    private sealed class PortableEntry
    {
        public string Id { get; set; } = string.Empty;
        public string? Type { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Inherits { get; set; }
        public string? Source { get; set; }
        public List<string>? Tags { get; set; }
        public Dictionary<string, object>? Properties { get; set; }
    }

    private static PortableEntry ToPortable(ContentEntry entry) => new()
    {
        Id = entry.Id,
        Type = entry.Type,
        Name = entry.Name,
        Description = entry.Description,
        Inherits = entry.Inherits,
        Source = entry.Source,
        Tags = entry.Tags.Count == 0 ? null : entry.Tags,
        Properties = entry.Properties.Count == 0 ? null : entry.Properties
    };

    private async Task WriteAllAsync<T>(string directory, string folder, IEnumerable<T> items, Func<T, string> idOf)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        var dir = Path.Combine(directory, folder);
        Directory.CreateDirectory(dir);
        foreach (var item in list)
        {
            var fileName = SafeFileName(idOf(item)) + ".yaml";
            await File.WriteAllTextAsync(Path.Combine(dir, fileName), _yamlSerializer.Serialize(item), Utf8NoBom);
        }
    }

    public static string SafeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(id.Select(c => invalid.Contains(c) || c == ':' ? '-' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "entry" : cleaned;
    }
}
