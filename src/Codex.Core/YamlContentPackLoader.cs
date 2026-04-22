using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Codex.Core.Models;
using Codex.Plugin.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Codex.Core;

public class YamlContentPackLoader : IContentPackLoader
{
    private readonly IContentRegistry _registry;
    private readonly IDeserializer _yamlDeserializer;

    public YamlContentPackLoader(IContentRegistry registry)
    {
        _registry = registry;
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public async Task<PackManifest> ReadManifestAsync(string packDirectoryPath)
    {
        var manifestPath = Path.Combine(packDirectoryPath, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Manifest not found at {manifestPath}");

        var json = await File.ReadAllTextAsync(manifestPath);
        return JsonSerializer.Deserialize<PackManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Failed to deserialize manifest.");
    }

    public async Task LoadPackAsync(string packDirectoryPath)
    {
        var manifest = await ReadManifestAsync(packDirectoryPath);

        // Load Abilities
        await LoadContentSubfolder<AbilityDefinition>(packDirectoryPath, manifest, "abilities", (item, prio) =>
        {
            if (!string.IsNullOrEmpty(item.Inherits))
            {
                var baseAbility = _registry.GetAbility(item.Inherits);
                if (baseAbility != null) item.MergeFrom(baseAbility);
            }
            _registry.RegisterAbility(item, prio);
        });

        // Load Actors
        await LoadContentSubfolder<ActorDefinition>(packDirectoryPath, manifest, "actors", (item, prio) =>
        {
            if (!string.IsNullOrEmpty(item.Inherits))
            {
                var baseActor = _registry.GetActor(item.Inherits);
                if (baseActor != null) item.MergeFrom(baseActor);
            }
            _registry.RegisterActor(item, prio);
        });

        // Load Locations
        await LoadContentSubfolder<LocationDefinition>(packDirectoryPath, manifest, "locations", (item, prio) =>
        {
            _registry.RegisterLocation(item, prio);
        });
    }

    private async Task LoadContentSubfolder<T>(string packDirectoryPath, PackManifest manifest, string defaultSubfolder, Action<T, int> registerAction) where T : class
    {
        var contentPath = Path.Combine(packDirectoryPath, defaultSubfolder);
        if (!Directory.Exists(contentPath)) return;

        foreach (var file in Directory.GetFiles(contentPath, "*.yaml", SearchOption.AllDirectories))
        {
            var yaml = await File.ReadAllTextAsync(file);

            // Try as list first
            try
            {
                var loadedList = _yamlDeserializer.Deserialize<List<T>>(yaml);
                if (loadedList != null)
                {
                    foreach (var item in loadedList)
                    {
                        ApplyMetadata(item, manifest);
                        registerAction(item, manifest.Priority);
                    }
                    continue;
                }
            }
            catch { /* Not a list */ }

            // Try as single item
            var loaded = _yamlDeserializer.Deserialize<T>(yaml);
            if (loaded != null)
            {
                ApplyMetadata(loaded, manifest);
                registerAction(loaded, manifest.Priority);
            }
        }
    }

    private void ApplyMetadata(object item, PackManifest manifest)
    {
        // Use reflection to set SystemId and PackId if properties exist
        var type = item.GetType();
        type.GetProperty("SystemId")?.SetValue(item, manifest.SystemId);
        type.GetProperty("PackId")?.SetValue(item, manifest.Id);
    }
}