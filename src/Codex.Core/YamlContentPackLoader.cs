using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Codex.Core.Models;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Codex.Core;

/// <summary>
/// Loads a content pack - a directory or a <c>.zip</c>/<c>.cdx</c> archive - into the
/// <see cref="IContentRegistry"/>. Layout:
/// <code>
/// manifest.yaml (or manifest.json)
/// abilities/*.yaml      TRCE abilities (executable)
/// actors/*.yaml         NPC/monster blueprints in the legacy ActorDefinition shape
/// locations/*.yaml      LocationDefinition
/// &lt;type-folder&gt;/*.yaml  generic ContentEntry, typed by the system's entity type for that folder
///                       (or by the folder name itself when the system declares no schema for it)
/// flavor/&lt;type&gt;/*.yaml  lore and art prompts for entries other packs define: each item is an id plus
///                       properties (<see cref="ContentEntry.FlavorKeys"/> and the type's own fields) merged
///                       into that entry; the pack must depend on the one that defines it
/// </code>
/// Each YAML file holds either one item or a list of items.
/// </summary>
public class YamlContentPackLoader : IContentPackLoader
{
    public const string ManifestYaml = "manifest.yaml";
    public const string ManifestJson = "manifest.json";

    private const string FlavorFolder = "flavor";
    private static readonly string[] ReservedFolders = { "abilities", "actors", "locations", FlavorFolder };

    private readonly IContentRegistry _registry;
    private readonly IDeserializer _yamlDeserializer;
    private readonly ILogger _logger;

    public YamlContentPackLoader(IContentRegistry registry, ILogger<YamlContentPackLoader>? logger = null)
    {
        _registry = registry;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public static bool IsArchive(string path) =>
        File.Exists(path) && (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cdx", StringComparison.OrdinalIgnoreCase));

    public async Task<PackManifest> ReadManifestAsync(string packPath)
    {
        if (IsArchive(packPath))
        {
            using var archive = ZipFile.OpenRead(packPath);
            var yamlEntry = archive.GetEntry(ManifestYaml);
            if (yamlEntry != null)
            {
                using var reader = new StreamReader(yamlEntry.Open());
                return ParseManifestYaml(await reader.ReadToEndAsync(), packPath);
            }

            var jsonEntry = archive.GetEntry(ManifestJson) ?? throw new FileNotFoundException("Manifest not found in archive.");
            using var stream = jsonEntry.Open();
            return await JsonSerializer.DeserializeAsync<PackManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException("Failed to deserialize manifest from archive.");
        }

        var yamlPath = Path.Combine(packPath, ManifestYaml);
        if (File.Exists(yamlPath))
        {
            return ParseManifestYaml(await File.ReadAllTextAsync(yamlPath), yamlPath);
        }

        var manifestPath = Path.Combine(packPath, ManifestJson);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Manifest not found at {yamlPath} or {manifestPath}");

        var json = await File.ReadAllTextAsync(manifestPath);
        return JsonSerializer.Deserialize<PackManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidOperationException("Failed to deserialize manifest.");
    }

    private PackManifest ParseManifestYaml(string yaml, string source)
    {
        var manifest = _yamlDeserializer.Deserialize<PackManifest>(yaml)
                       ?? throw new InvalidOperationException($"Failed to deserialize manifest at {source}.");
        if (string.IsNullOrWhiteSpace(manifest.Id)) throw new InvalidDataException($"Manifest at {source} has no id.");
        if (string.IsNullOrWhiteSpace(manifest.SystemId)) throw new InvalidDataException($"Manifest at {source} has no systemId.");
        return manifest;
    }

    public async Task LoadPackAsync(string packPath, SystemDefinition? system = null)
    {
        var manifest = await ReadManifestAsync(packPath);
        _registry.RegisterPack(manifest);

        if (IsArchive(packPath))
        {
            await LoadFromZipAsync(packPath, manifest, system);
            return;
        }

        await LoadContentSubfolder<AbilityDefinition>(packPath, manifest, "abilities", RegisterAbility);
        await LoadContentSubfolder<ActorDefinition>(packPath, manifest, "actors", RegisterActor);
        await LoadContentSubfolder<LocationDefinition>(packPath, manifest, "locations", (item, prio) => _registry.RegisterLocation(item, prio));

        foreach (var folder in Directory.GetDirectories(packPath))
        {
            var folderName = Path.GetFileName(folder);
            if (ReservedFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase) || folderName.StartsWith('.'))
            {
                continue;
            }

            var type = system?.FindType(folderName);
            var typeKey = type?.Type ?? folderName.ToLowerInvariant();
            foreach (var file in Directory.GetFiles(folder, "*.yaml", SearchOption.AllDirectories))
            {
                var yaml = await File.ReadAllTextAsync(file);
                DeserializeAndRegister<ContentEntry>(yaml, manifest, (entry, prio) => RegisterEntry(entry, typeKey, type, prio), file);
            }
        }

        var flavorPath = Path.Combine(packPath, FlavorFolder);
        if (Directory.Exists(flavorPath))
        {
            foreach (var typeFolder in Directory.GetDirectories(flavorPath))
            {
                foreach (var file in Directory.GetFiles(typeFolder, "*.yaml", SearchOption.AllDirectories))
                {
                    ApplyFlavor(Path.GetFileName(typeFolder), await File.ReadAllTextAsync(file), manifest, system, file);
                }
            }
        }
    }

    /// <summary>
    /// Merges the items of one <c>flavor/&lt;type&gt;/*.yaml</c> file into the entries they name. Only
    /// <see cref="ContentEntry.FlavorKeys"/> and fields the type declares are accepted, so a flavor file
    /// can add lore but never change a rule. An id no loaded pack defines is logged, and its flavor is kept for homebrew entries with that id.
    /// </summary>
    private void ApplyFlavor(string typeFolder, string yaml, PackManifest manifest, SystemDefinition? system, string source)
    {
        List<Dictionary<string, object>>? items;
        try
        {
            items = _yamlDeserializer.Deserialize<List<Dictionary<string, object>>>(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            _logger.LogError(ex, "Could not parse flavor file {Source} in pack {PackId}: {Message}", source, manifest.Id, ex.Message);
            return;
        }

        if (items == null) return;
        var type = system?.FindType(typeFolder);
        var typeKey = type?.Type ?? typeFolder.ToLowerInvariant();
        var allowed = new HashSet<string>(ContentEntry.FlavorKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var field in type?.Fields ?? new List<FieldDefinition>()) allowed.Add(field.Key);

        var missing = new List<string>();
        foreach (var item in items)
        {
            var id = item.TryGetValue("id", out var rawId) ? rawId?.ToString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var flavor = new Dictionary<string, object>();
            foreach (var (key, value) in item)
            {
                if (key.Equals("id", StringComparison.OrdinalIgnoreCase) || !allowed.Contains(key)) continue;
                if (YamlValues.Normalize(value) is { } normalized) flavor[key] = normalized;
            }

            // Remembered even when no loaded pack defines the entry: a campaign's homebrew may.
            _registry.RegisterFlavor(manifest.SystemId, typeKey, id, manifest.Id, flavor);

            var target = _registry.GetEntry(manifest.SystemId, typeKey, id);
            if (target == null) missing.Add(id);
            else target.AddFlavor(_registry.GetFlavor(manifest.SystemId, typeKey, id), overwrite: true);
        }

        if (missing.Count > 0)
        {
            _logger.LogWarning("Flavor in {Source} (pack {PackId}) names {Count} {Type} entries that no loaded pack defines, such as {Ids}. They only reach homebrew entries; if they should decorate another pack, does this pack depend on it?",
                source, manifest.Id, missing.Count, typeKey, string.Join(", ", missing.Take(5)));
        }
    }

    private void RegisterAbility(AbilityDefinition item, int prio)
    {
        if (!string.IsNullOrEmpty(item.Inherits))
        {
            var baseAbility = _registry.GetAbility(item.Inherits);
            if (baseAbility != null)
            {
                item.MergeFrom(baseAbility);
            }
            else
            {
                _logger.LogError("Ability {AbilityId} in pack {PackId} inherits from {BaseId}, which was not found. It will be registered without the inherited fields.",
                    item.Id, item.PackId, item.Inherits);
            }
        }

        _registry.RegisterAbility(item, prio);
    }

    private void RegisterActor(ActorDefinition item, int prio)
    {
        if (!string.IsNullOrEmpty(item.Inherits))
        {
            var baseActor = _registry.GetActor(item.Inherits);
            if (baseActor != null)
            {
                item.MergeFrom(baseActor);
            }
            else
            {
                _logger.LogError("Actor {ActorId} in pack {PackId} inherits from {BaseId}, which was not found. It will be registered without the inherited fields.",
                    item.Id, item.PackId, item.Inherits);
            }
        }

        item.Properties = YamlValues.NormalizeMap(item.Properties);
        _registry.RegisterActor(item, prio);
    }

    private void RegisterEntry(ContentEntry entry, string typeKey, ContentTypeDefinition? type, int prio)
    {
        if (string.IsNullOrWhiteSpace(entry.Type))
        {
            entry.Type = typeKey;
        }

        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            _logger.LogError("An entry named '{Name}' of type {Type} in pack {PackId} has no id and was skipped.", entry.Name, entry.Type, entry.PackId);
            return;
        }

        entry.Properties = YamlValues.NormalizeMap(entry.Properties);

        if (!string.IsNullOrEmpty(entry.Inherits))
        {
            var parts = entry.Inherits.Split(':');
            var baseEntry = parts.Length switch
            {
                3 => _registry.GetEntry(parts[0], parts[1], parts[2]),
                2 => _registry.GetEntry(entry.SystemId, parts[0], parts[1]),
                1 => _registry.GetEntry(entry.SystemId, entry.Type, parts[0]),
                _ => null
            };

            if (baseEntry != null)
            {
                entry.MergeFrom(baseEntry);
            }
            else
            {
                _logger.LogError("Entry {Type}/{Id} in pack {PackId} inherits from {BaseId}, which was not found.", entry.Type, entry.Id, entry.PackId, entry.Inherits);
            }
        }

        _registry.RegisterEntry(entry, prio);

        if (type is { ActorBlueprint: true })
        {
            _registry.RegisterActor(ToActorBlueprint(entry, type), prio);
        }
    }

    /// <summary>
    /// Projects a creature entry (a monster stat block) onto the <see cref="IActorDefinition"/> shape
    /// the encounter tooling instantiates: hit points become the primary resource pool, mapped
    /// attributes land in <c>Properties</c> under the system's attribute keys, and the rest of the
    /// stat block rides along in <c>Properties</c> untouched.
    /// </summary>
    public static ActorDefinition ToActorBlueprint(ContentEntry entry, ContentTypeDefinition type)
    {
        var actor = new ActorDefinition
        {
            Id = entry.Id,
            SystemId = entry.SystemId,
            PackId = entry.PackId,
            Name = entry.Name,
            Description = entry.Description,
            Tags = new List<string>(entry.Tags),
            Properties = new Dictionary<string, object>(entry.Properties)
        };

        actor.Tags.Insert(0, type.Type);

        var hp = YamlValues.GetInt(entry.Properties, type.HitPointsProperty ?? "hitPoints");
        if (hp.HasValue)
        {
            actor.Resources["HP"] = hp.Value;
            actor.Resources["HP_Max"] = hp.Value;
        }

        foreach (var (property, attributeKey) in type.AttributeProperties)
        {
            var value = YamlValues.GetInt(entry.Properties, property);
            if (value.HasValue)
            {
                actor.Properties[attributeKey] = (long)value.Value;
            }
        }

        actor.Metadata["entryType"] = type.Type;
        return actor;
    }

    private async Task LoadFromZipAsync(string zipPath, PackManifest manifest, SystemDefinition? system)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)) continue;

            var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
            var segments = normalized.Split('/');
            if (segments.Length < 2) continue;
            var firstSegment = segments[0];

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var yaml = await reader.ReadToEndAsync();

            switch (firstSegment.ToLowerInvariant())
            {
                case FlavorFolder:
                    if (segments.Length >= 3) ApplyFlavor(segments[1], yaml, manifest, system, entry.FullName);
                    break;
                case "abilities":
                    DeserializeAndRegister<AbilityDefinition>(yaml, manifest, RegisterAbility, entry.FullName);
                    break;
                case "actors":
                    DeserializeAndRegister<ActorDefinition>(yaml, manifest, RegisterActor, entry.FullName);
                    break;
                case "locations":
                    DeserializeAndRegister<LocationDefinition>(yaml, manifest, (item, prio) => _registry.RegisterLocation(item, prio), entry.FullName);
                    break;
                default:
                {
                    var type = system?.FindType(firstSegment);
                    var typeKey = type?.Type ?? firstSegment.ToLowerInvariant();
                    DeserializeAndRegister<ContentEntry>(yaml, manifest, (item, prio) => RegisterEntry(item, typeKey, type, prio), entry.FullName);
                    break;
                }
            }
        }
    }

    private void DeserializeAndRegister<T>(string yaml, PackManifest manifest, Action<T, int> registerAction, string source) where T : class
    {
        List<T>? loadedList = null;
        try
        {
            loadedList = _yamlDeserializer.Deserialize<List<T>>(yaml);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // Not a list - fall through to the single-item shape.
        }

        if (loadedList != null)
        {
            foreach (var item in loadedList)
            {
                ApplyMetadata(item, manifest);
                registerAction(item, manifest.Priority);
            }

            return;
        }

        try
        {
            var loaded = _yamlDeserializer.Deserialize<T>(yaml);
            if (loaded != null)
            {
                ApplyMetadata(loaded, manifest);
                registerAction(loaded, manifest.Priority);
            }
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            _logger.LogError(ex, "Could not parse {Source} in pack {PackId}: {Message}", source, manifest.Id, ex.Message);
        }
    }

    private async Task LoadContentSubfolder<T>(string packDirectoryPath, PackManifest manifest, string defaultSubfolder, Action<T, int> registerAction) where T : class
    {
        var contentPath = Path.Combine(packDirectoryPath, defaultSubfolder);
        if (!Directory.Exists(contentPath)) return;

        foreach (var file in Directory.GetFiles(contentPath, "*.yaml", SearchOption.AllDirectories))
        {
            var yaml = await File.ReadAllTextAsync(file);
            DeserializeAndRegister(yaml, manifest, registerAction, file);
        }
    }

    private static void ApplyMetadata(object item, PackManifest manifest)
    {
        var type = item.GetType();
        type.GetProperty("SystemId")?.SetValue(item, manifest.SystemId);
        type.GetProperty("PackId")?.SetValue(item, manifest.Id);
    }
}
