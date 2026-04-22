using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

    public async Task<PackManifest> ReadManifestAsync(string packPath)
    {
        if (File.Exists(packPath) && (packPath.EndsWith(".zip") || packPath.EndsWith(".cdx")))
        {
            using var archive = ZipFile.OpenRead(packPath);
            var entry = archive.GetEntry("manifest.json");
            if (entry == null) throw new FileNotFoundException("Manifest not found in archive.");
            using var stream = entry.Open();
            return await JsonSerializer.DeserializeAsync<PackManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException("Failed to deserialize manifest from archive.");
        }

        var manifestPath = Path.Combine(packPath, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Manifest not found at {manifestPath}");

        var json = await File.ReadAllTextAsync(manifestPath);
        return JsonSerializer.Deserialize<PackManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Failed to deserialize manifest.");
    }

    public async Task LoadPackAsync(string packPath)
    {
        if (File.Exists(packPath) && (packPath.EndsWith(".zip") || packPath.EndsWith(".cdx")))
        {
            await LoadFromZipAsync(packPath);
            return;
        }

        var manifest = await ReadManifestAsync(packPath);

        // Load Abilities
        await LoadContentSubfolder<AbilityDefinition>(packPath, manifest, "abilities", (item, prio) => RegisterAbility(item, prio));
        // Load Actors
        await LoadContentSubfolder<ActorDefinition>(packPath, manifest, "actors", (item, prio) => RegisterActor(item, prio));
        // Load Locations
        await LoadContentSubfolder<LocationDefinition>(packPath, manifest, "locations", (item, prio) => _registry.RegisterLocation(item, prio));
    }

    private void RegisterAbility(AbilityDefinition item, int prio)
    {
        if (!string.IsNullOrEmpty(item.Inherits))
        {
            var baseAbility = _registry.GetAbility(item.Inherits);
            if (baseAbility != null) item.MergeFrom(baseAbility);
        }
        _registry.RegisterAbility(item, prio);
    }

    private void RegisterActor(ActorDefinition item, int prio)
    {
        if (!string.IsNullOrEmpty(item.Inherits))
        {
            var baseActor = _registry.GetActor(item.Inherits);
            if (baseActor != null) item.MergeFrom(baseActor);
        }
        _registry.RegisterActor(item, prio);
    }

    private async Task LoadFromZipAsync(string zipPath)
    {
        var manifest = await ReadManifestAsync(zipPath);
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) continue;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var yaml = await reader.ReadToEndAsync();

            if (entry.FullName.Contains("/abilities/"))
                DeserializeAndRegister<AbilityDefinition>(yaml, manifest, RegisterAbility);
            else if (entry.FullName.Contains("/actors/"))
                DeserializeAndRegister<ActorDefinition>(yaml, manifest, RegisterActor);
            else if (entry.FullName.Contains("/locations/"))
                DeserializeAndRegister<LocationDefinition>(yaml, manifest, (item, prio) => _registry.RegisterLocation(item, prio));
        }
    }

    private void DeserializeAndRegister<T>(string yaml, PackManifest manifest, Action<T, int> registerAction) where T : class
    {
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
                return;
            }
        }
        catch { }

        var loaded = _yamlDeserializer.Deserialize<T>(yaml);
        if (loaded != null)
        {
            ApplyMetadata(loaded, manifest);
            registerAction(loaded, manifest.Priority);
        }
    }

    private async Task LoadContentSubfolder<T>(string packDirectoryPath, PackManifest manifest, string defaultSubfolder, Action<T, int> registerAction) where T : class
    {
        var contentPath = Path.Combine(packDirectoryPath, defaultSubfolder);
        if (!Directory.Exists(contentPath)) return;

        foreach (var file in Directory.GetFiles(contentPath, "*.yaml", SearchOption.AllDirectories))
        {
            var yaml = await File.ReadAllTextAsync(file);
            DeserializeAndRegister(yaml, manifest, registerAction);
        }
    }

    private void ApplyMetadata(object item, PackManifest manifest)
    {
        var type = item.GetType();
        type.GetProperty("SystemId")?.SetValue(item, manifest.SystemId);
        type.GetProperty("PackId")?.SetValue(item, manifest.Id);
    }
}