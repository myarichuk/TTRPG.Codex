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

public class YamlAbilityPackLoader : IAbilityPackLoader
{
    private readonly IAbilityRegistry _registry;
    private readonly IDeserializer _yamlDeserializer;

    public YamlAbilityPackLoader(IAbilityRegistry registry)
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

    public async Task<IEnumerable<IAbilityDefinition>> LoadPackAsync(string packDirectoryPath)
    {
        var manifest = await ReadManifestAsync(packDirectoryPath);
        var abilities = new List<AbilityDefinition>();

        var contentPaths = manifest.ContentPaths ?? new[] { "abilities" };

        foreach (var relativePath in contentPaths)
        {
            var fullPath = Path.Combine(packDirectoryPath, relativePath);
            if (!Directory.Exists(fullPath)) continue;

            foreach (var file in Directory.GetFiles(fullPath, "*.yaml", SearchOption.AllDirectories))
            {
                var yaml = await File.ReadAllTextAsync(file);
                try
                {
                    var loaded = _yamlDeserializer.Deserialize<List<AbilityDefinition>>(yaml);
                    if (loaded != null) abilities.AddRange(loaded);
                }
                catch
                {
                    var loaded = _yamlDeserializer.Deserialize<AbilityDefinition>(yaml);
                    if (loaded != null) abilities.Add(loaded);
                }
            }
        }

        foreach (var ability in abilities)
        {
            ability.SystemId = manifest.SystemId;
            ability.PackId = manifest.Id;

            if (!string.IsNullOrEmpty(ability.Inherits))
            {
                var baseAbility = _registry.GetAbility(ability.Inherits);
                if (baseAbility != null)
                {
                    ability.MergeFrom(baseAbility);
                }
            }

            _registry.Register(ability, manifest.Priority);
        }

        return abilities;
    }
}