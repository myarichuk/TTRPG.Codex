using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using Codex.Core.Models;
using Codex.Plugin.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Codex.Authoring.Services;

public class ContentPackExporter
{
    private readonly ISerializer _yamlSerializer;

    public ContentPackExporter()
    {
        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
    }

    public async Task ExportToZipAsync(PackManifest manifest,
                                     IEnumerable<AbilityDefinition> abilities,
                                     IEnumerable<ActorDefinition> actors,
                                     IEnumerable<LocationDefinition> locations,
                                     string outputPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Write Manifest
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(tempDir, "manifest.json"), manifestJson);

            // 2. Write Abilities
            if (abilities != null)
            {
                var dir = Path.Combine(tempDir, "abilities");
                Directory.CreateDirectory(dir);
                foreach (var ability in abilities)
                {
                    var yaml = _yamlSerializer.Serialize(ability);
                    await File.WriteAllTextAsync(Path.Combine(dir, $"{ability.Id}.yaml"), yaml);
                }
            }

            // 3. Write Actors
            if (actors != null)
            {
                var dir = Path.Combine(tempDir, "actors");
                Directory.CreateDirectory(dir);
                foreach (var actor in actors)
                {
                    var yaml = _yamlSerializer.Serialize(actor);
                    await File.WriteAllTextAsync(Path.Combine(dir, $"{actor.Id}.yaml"), yaml);
                }
            }

            // 4. Write Locations
            if (locations != null)
            {
                var dir = Path.Combine(tempDir, "locations");
                Directory.CreateDirectory(dir);
                foreach (var loc in locations)
                {
                    var yaml = _yamlSerializer.Serialize(loc);
                    await File.WriteAllTextAsync(Path.Combine(dir, $"{loc.Id}.yaml"), yaml);
                }
            }

            // 5. ZIP it up
            if (File.Exists(outputPath)) File.Delete(outputPath);
            ZipFile.CreateFromDirectory(tempDir, outputPath);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}