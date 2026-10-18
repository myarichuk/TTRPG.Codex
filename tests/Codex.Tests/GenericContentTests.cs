using Codex.Core;
using Codex.Core.Models;
using Codex.Core.Scripting;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The generic content model: typed entries in packs, search, overrides, blueprints.</summary>
public class GenericContentTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), "CodexGeneric_" + Guid.NewGuid());
    private readonly ContentRegistry _registry = new(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));

    public GenericContentTests() => Directory.CreateDirectory(_tempPath);

    public void Dispose()
    {
        if (Directory.Exists(_tempPath)) Directory.Delete(_tempPath, true);
    }

    private static ContentEntry Entry(string type, string id, string name, string pack = "core", params string[] tags) => new()
    {
        Type = type, Id = id, Name = name, SystemId = "demo", PackId = pack, Tags = tags.ToList()
    };

    [Fact]
    public void Search_RanksNameBeforeTagBeforeDescription_AndFiltersByType()
    {
        _registry.RegisterEntry(Entry("spell", "fire-bolt", "Fire Bolt"), 0);
        _registry.RegisterEntry(Entry("spell", "fireball", "Fireball"), 0);
        _registry.RegisterEntry(Entry("spell", "scorching-ray", "Scorching Ray", "core", "fire"), 0);
        var described = Entry("feat", "elemental-adept", "Elemental Adept");
        described.Description = "Choose fire, cold or lightning...";
        _registry.RegisterEntry(described, 0);
        _registry.RegisterEntry(Entry("spell", "cure-wounds", "Cure Wounds"), 0);

        var all = _registry.Search("demo", "fire").Select(e => e.Id).ToList();
        Assert.Equal(new[] { "fire-bolt", "fireball", "scorching-ray", "elemental-adept" }, all);

        var spellsOnly = _registry.Search("demo", "fire", "spell").Select(e => e.Id).ToList();
        Assert.DoesNotContain("elemental-adept", spellsOnly);

        Assert.Equal(4, _registry.Search("demo", "", "spell").Count());
        Assert.Equal(new[] { "feat", "spell" }, _registry.GetEntryTypes("demo").OrderBy(t => t));
    }

    [Fact]
    public void RegisterEntry_HigherPriorityPackOverridesSameId()
    {
        _registry.RegisterEntry(Entry("spell", "fireball", "Fireball (SRD)", "srd"), 0);
        _registry.RegisterEntry(Entry("spell", "fireball", "Fireball (House rules)", "house"), 10);
        _registry.RegisterEntry(Entry("spell", "fireball", "Fireball (ignored)", "late-low-priority"), 0);

        Assert.Equal("Fireball (House rules)", _registry.GetEntry("demo", "spell", "fireball")!.Name);
    }

    [Fact]
    public void GetEntries_CanBeNarrowedToActivePacks()
    {
        _registry.RegisterEntry(Entry("monster", "goblin", "Goblin", "srd"), 0);
        _registry.RegisterEntry(Entry("monster", "gloomstalker", "Gloomstalker", "homebrew"), 0);

        var srdOnly = _registry.GetEntries("demo", "monster", new HashSet<string> { "srd" }).Select(e => e.Id);
        Assert.Equal(new[] { "goblin" }, srdOnly);
        Assert.Equal(2, _registry.GetEntries("demo", "monster").Count());
    }

    [Fact]
    public async Task Loader_ReadsManifestYaml_TypedFolders_AndActorBlueprints()
    {
        var system = SystemDefinitionLoader.Parse("""
            id: demo
            name: Demo
            attributes:
              - { key: str, label: Strength }
            resources:
              - { key: HP, label: HP, maxKey: HP_Max, isPrimary: true }
            entityTypes:
              - type: spell
                label: Spell
                folder: spells
              - type: monster
                label: Monster
                folder: monsters
                actorBlueprint: true
                hitPointsProperty: hitPoints
                attributeProperties: { strength: str }
            """);

        File.WriteAllText(Path.Combine(_tempPath, "manifest.yaml"), """
            id: demo-pack
            name: Demo Pack
            version: 1.2.3
            systemId: demo
            kind: rules
            license:
              name: CC-BY-4.0
              url: https://creativecommons.org/licenses/by/4.0/legalcode
              attribution: Attribution line.
              modified: true
            """);
        Directory.CreateDirectory(Path.Combine(_tempPath, "spells"));
        Directory.CreateDirectory(Path.Combine(_tempPath, "monsters"));
        Directory.CreateDirectory(Path.Combine(_tempPath, "oddities"));
        File.WriteAllText(Path.Combine(_tempPath, "spells", "fireball.yaml"), """
            id: fireball
            name: Fireball
            tags: [evocation]
            properties:
              level: 3
              concentration: false
              classes: [Wizard]
            """);
        File.WriteAllText(Path.Combine(_tempPath, "monsters", "goblin.yaml"), """
            - id: goblin
              name: Goblin
              properties:
                hitPoints: 7
                strength: 8
                actions:
                  - name: Scimitar
                    description: Melee attack.
            """);
        File.WriteAllText(Path.Combine(_tempPath, "oddities", "thing.yaml"), "id: thing\nname: Thing\n");

        var loader = new YamlContentPackLoader(_registry);
        await loader.LoadPackAsync(_tempPath, system);

        var pack = _registry.GetPack("demo-pack");
        Assert.NotNull(pack);
        Assert.Equal("1.2.3", pack!.Version);
        Assert.Equal("CC-BY-4.0", pack.License!.Name);
        Assert.True(pack.License.Modified);

        var fireball = _registry.GetEntry("demo", "spell", "fireball");
        Assert.NotNull(fireball);
        Assert.Equal(3L, fireball!.Properties["level"]);          // scalars are typed, not strings
        Assert.Equal(false, fireball.Properties["concentration"]);
        Assert.Equal(new List<object> { "Wizard" }, fireball.Properties["classes"]);
        Assert.Equal("demo-pack", fireball.PackId);

        var goblinEntry = _registry.GetEntry("demo", "monster", "goblin");
        Assert.NotNull(goblinEntry);
        var rows = Assert.IsType<List<object>>(goblinEntry!.Properties["actions"]);
        Assert.Equal("Scimitar", ((Dictionary<string, object>)rows[0])["name"]);

        var goblin = _registry.GetActor("demo:goblin");
        Assert.NotNull(goblin);
        Assert.Equal(7, goblin!.Resources["HP"]);
        Assert.Equal(7, goblin.Resources["HP_Max"]);
        Assert.Equal(8L, goblin.Properties["str"]);
        Assert.Contains("monster", goblin.Tags);

        // An undeclared folder still loads, typed by its folder name.
        Assert.NotNull(_registry.GetEntry("demo", "oddities", "thing"));
    }

    [Fact]
    public async Task Loader_EntryInherits_FillsMissingFieldsFromBase()
    {
        File.WriteAllText(Path.Combine(_tempPath, "manifest.yaml"), "id: p\nname: P\nsystemId: demo\n");
        Directory.CreateDirectory(Path.Combine(_tempPath, "spells"));
        File.WriteAllText(Path.Combine(_tempPath, "spells", "a.yaml"), """
            - id: fireball
              name: Fireball
              description: Boom.
              tags: [evocation]
              properties: { level: 3, damage: 8d6 }
            - id: fireball-plus
              name: Fireball Plus
              inherits: fireball
              properties: { damage: 10d6 }
            """);

        await new YamlContentPackLoader(_registry).LoadPackAsync(_tempPath);

        var plus = _registry.GetEntry("demo", "spells", "fireball-plus")!;
        Assert.Equal("Boom.", plus.Description);
        Assert.Equal(3L, plus.Properties["level"]);
        Assert.Equal("10d6", plus.Properties["damage"]);
        Assert.Contains("evocation", plus.Tags);
        Assert.Equal("8d6", _registry.GetEntry("demo", "spells", "fireball")!.Properties["damage"]); // base untouched
    }

    [Fact]
    public async Task Exporter_WritesPortableEntries_ThatLoadBackIdentically()
    {
        var system = SystemDefinitionLoader.Parse("id: demo\nname: Demo\nentityTypes:\n  - { type: spell, label: Spell, folder: spells }\n");
        var manifest = new PackManifest { Id = "hb", Name = "Homebrew", Version = "1.0.0", SystemId = "demo", Kind = "homebrew" };
        var entry = new ContentEntry
        {
            Id = "thunderous-yawn", Type = "spell", Name = "Thunderous Yawn", SystemId = "demo", PackId = "ignored",
            Description = "A **very** loud yawn.", Tags = { "evocation" },
            Properties = { ["level"] = 2L, ["classes"] = new List<object> { "Bard" }, ["rows"] = new List<object> { new Dictionary<string, object> { ["name"] = "Yawn", ["level"] = 1L } } }
        };

        var dir = Path.Combine(_tempPath, "export");
        await new ContentPackExporter().ExportToDirectoryAsync(manifest, Array.Empty<AbilityDefinition>(), Array.Empty<ActorDefinition>(), Array.Empty<LocationDefinition>(), dir, new[] { entry }, system);

        var yaml = File.ReadAllText(Path.Combine(dir, "spells", "thunderous-yawn.yaml"));
        Assert.DoesNotContain("fullId", yaml);
        Assert.DoesNotContain("systemId", yaml);
        Assert.False(yaml.StartsWith('﻿'), "no BOM");

        var fresh = new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
        await new YamlContentPackLoader(fresh).LoadPackAsync(dir, system);
        var loaded = fresh.GetEntry("demo", "spell", "thunderous-yawn")!;
        Assert.Equal("Thunderous Yawn", loaded.Name);
        Assert.Equal("hb", loaded.PackId);
        Assert.Equal(2L, loaded.Properties["level"]);
        Assert.Equal("A **very** loud yawn.", loaded.Description);
        var rows = Assert.IsType<List<object>>(loaded.Properties["rows"]);
        Assert.Equal(1L, ((Dictionary<string, object>)rows[0])["level"]);
    }

    [Fact]
    public void Blueprint_FromHomebrewMonsterEntry_UsesDeclaredHitPointsAndAttributes()
    {
        var type = new ContentTypeDefinition { Type = "monster", ActorBlueprint = true, HitPointsProperty = "hitPoints", AttributeProperties = { ["dexterity"] = "dex" } };
        var entry = Entry("monster", "owlbear", "Owlbear");
        entry.Properties["hitPoints"] = 59L;
        entry.Properties["dexterity"] = 12L;

        var actor = YamlContentPackLoader.ToActorBlueprint(entry, type);

        Assert.Equal(59, actor.Resources["HP"]);
        Assert.Equal(12L, actor.Properties["dex"]);
        Assert.Equal("monster", actor.Metadata["entryType"]);
    }
}
