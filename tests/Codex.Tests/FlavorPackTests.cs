using Codex.Core;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>A pack's <c>flavor/</c> folder fills in lore on entries other packs define, and nothing else.</summary>
public class FlavorPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flavor-pack-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static readonly SystemDefinition Demo = Codex.Core.Systems.SystemDefinitionLoader.Parse("""
        id: demo
        name: Demo
        entityTypes:
          - type: spell
            label: Spell
            folder: spells
            fields:
              - { key: level, label: Level, type: Number }
        """);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<ContentRegistry> LoadAsync()
    {
        var registry = new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
        var loader = new YamlContentPackLoader(registry);
        await loader.LoadPackAsync(Path.Combine(_root, "base"), Demo);
        await loader.LoadPackAsync(Path.Combine(_root, "flavor-pack"), Demo);
        return registry;
    }

    [Fact]
    public async Task Flavor_IsMergedIntoTheEntryItNames_WithoutTouchingRules()
    {
        Write("base/manifest.yaml", "id: base\nname: Base\nversion: 1.0.0\nsystemId: demo\n");
        Write("base/spells/bolt.yaml", "id: bolt\ntype: spell\nname: Bolt\ndescription: Rules text.\nproperties:\n  level: 1\n");
        Write("flavor-pack/manifest.yaml", "id: flavor-pack\nname: Flavor\nversion: 1.0.0\nsystemId: demo\ndependencies: [base]\n");
        Write("flavor-pack/flavor/spell/spell-001.yaml", """
            - id: bolt
              flavor: >-
                A crack of light.
              imagePrompt: A bright streak over a dark field.
              level: 9
              damage: 99
              description: Replaced rules.
            - id: ghost
              flavor: Nobody home.
            """);

        var registry = await LoadAsync();
        var bolt = registry.GetEntry("demo", "spell", "bolt")!;

        Assert.Equal("A crack of light.", bolt.Properties["flavor"]);
        Assert.Equal("A bright streak over a dark field.", bolt.Properties["imagePrompt"]);
        Assert.Equal("Rules text.", bolt.Description);
        Assert.False(bolt.Properties.ContainsKey("damage")); // not a flavor key and not a declared field
        Assert.Null(registry.GetEntry("demo", "spell", "ghost")); // flavor never creates entries
        Assert.Empty(registry.GetEntries("demo", "flavor"));
    }

    [Fact]
    public async Task Flavor_CreditsItsPack_AndIsKeptForHomebrewEntriesWithThatId()
    {
        Write("base/manifest.yaml", "id: base\nname: Base\nversion: 1.0.0\nsystemId: demo\n");
        Write("base/spells/bolt.yaml", "id: bolt\ntype: spell\nname: Bolt\nproperties:\n  level: 1\n");
        Write("flavor-pack/manifest.yaml", "id: flavor-pack\nname: Flavor\nversion: 1.0.0\nsystemId: demo\ndependencies: [base]\n");
        Write("flavor-pack/flavor/spell/spell-001.yaml", "- id: bolt\n  flavor: Pack lore.\n  imagePrompt: Pack art.\n- id: house-bolt\n  flavor: For a homebrew id.\n  imagePrompt: House art.\n");

        var registry = await LoadAsync();

        Assert.Equal("flavor-pack", registry.GetEntry("demo", "spell", "bolt")!.Properties[ContentEntry.FlavorSourceKey]);

        // A table's own entry with that id picks the lore up where it has none of its own...
        var plain = new ContentEntry { Id = "house-bolt", Type = "spell", SystemId = "demo", Name = "House Bolt" };
        plain.AddFlavor(registry.GetFlavor("demo", "spell", "house-bolt"), overwrite: false);
        Assert.Equal("For a homebrew id.", plain.Properties["flavor"]);
        Assert.Equal("flavor-pack", plain.Properties[ContentEntry.FlavorSourceKey]);

        // ...and keeps what it wrote itself, without crediting the pack for text that is not the pack's.
        var own = new ContentEntry { Id = "house-bolt", Type = "spell", SystemId = "demo", Name = "House Bolt" };
        own.Properties["flavor"] = "Our own words.";
        own.AddFlavor(registry.GetFlavor("demo", "spell", "house-bolt"), overwrite: false);
        Assert.Equal("Our own words.", own.Properties["flavor"]);
        Assert.Equal("House art.", own.Properties["imagePrompt"]);
        Assert.False(own.Properties.ContainsKey(ContentEntry.FlavorSourceKey));

        Assert.Empty(registry.GetFlavor("demo", "spell", "unknown"));
    }

    [Fact]
    public void TheEditorOffersLoreFields_ToEveryType_UnlessTheSystemDeclaresThem()
    {
        var definition = Codex.Core.Systems.SystemDefinitionLoader.Parse("""
            id: lore
            name: Lore
            entityTypes:
              - type: item
                label: Item
                fields:
                  - { key: flavor, label: Legend, type: Text }
              - { type: beast, label: Beast, actorBlueprint: true }
            """);
        var schemas = new Codex.Core.Systems.DataDrivenSystemPlugin(definition).GetUISchemas().ToDictionary(s => s.EntityType);
        Assert.Equal(new[] { "flavor", "habitat", "behavior", "imagePrompt" }, definition.FindType("beast")!.EditorFields().Select(f => f.Key)); // what the homebrew editor renders

        Assert.Equal(new[] { "flavor", "imagePrompt" }, schemas["item"].Fields.Select(f => f.Key));
        Assert.Equal("Legend", schemas["item"].Fields.Single(f => f.Key == "flavor").Label); // the system's own wins
        Assert.Equal(new[] { "flavor", "habitat", "behavior", "imagePrompt" }, schemas["beast"].Fields.Select(f => f.Key));
        Assert.All(schemas["beast"].Fields, f => Assert.Equal("Lore", f.Category));
    }

    [Fact]
    public async Task Flavor_AlsoWorksFromAZipArchive()
    {
        Write("base/manifest.yaml", "id: base\nname: Base\nversion: 1.0.0\nsystemId: demo\n");
        Write("base/spells/bolt.yaml", "id: bolt\ntype: spell\nname: Bolt\nproperties:\n  level: 1\n");
        Write("src/manifest.yaml", "id: flavor-pack\nname: Flavor\nversion: 1.0.0\nsystemId: demo\ndependencies: [base]\n");
        Write("src/flavor/spell/one.yaml", "- id: bolt\n  flavor: From a zip.\n");
        System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(_root, "src"), Path.Combine(_root, "flavor-pack-zip.zip"));

        var registry = new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
        var loader = new YamlContentPackLoader(registry);
        await loader.LoadPackAsync(Path.Combine(_root, "base"), Demo);
        await loader.LoadPackAsync(Path.Combine(_root, "flavor-pack-zip.zip"), Demo);

        Assert.Equal("From a zip.", registry.GetEntry("demo", "spell", "bolt")!.Properties["flavor"]);
    }
}

/// <summary>The shipped flavor overlay covers the SRD pack.</summary>
public class SrdFlavorTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
    private readonly PluginLoader _loader;

    public SrdFlavorTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void LoadsWithoutWarnings() => Assert.Null(_loader.LoadException);

    [Theory]
    [InlineData("class")]
    [InlineData("subclass")]
    [InlineData("species")]
    [InlineData("background")]
    [InlineData("feat")]
    [InlineData("condition")]
    [InlineData("spell")]
    [InlineData("monster")]
    [InlineData("magic-item")]
    public void EveryEntryOfTheseTypes_HasFlavor(string type)
    {
        var entries = _registry.GetEntries("srd5e", type, new HashSet<string> { "srd-5.2.1" }).ToList();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Properties.GetValueOrDefault("flavor")?.ToString()), $"{type}/{e.Id} has no flavor"));
    }

    [Fact]
    public void EveryCreature_HasAnImagePrompt()
    {
        var creatures = _registry.GetEntries("srd5e", "monster", new HashSet<string> { "srd-5.2.1" }).ToList();
        Assert.All(creatures, e => Assert.False(string.IsNullOrWhiteSpace(e.Properties.GetValueOrDefault("imagePrompt")?.ToString()), $"monster/{e.Id} has no imagePrompt"));
    }

    [Fact]
    public void FlavorLeavesTheRulesTextAlone()
    {
        var fireball = _registry.GetEntry("srd5e", "spell", "fireball")!;
        Assert.Contains("8d6", fireball.Description);
        Assert.NotNull(fireball.Properties["flavor"]);
        Assert.Equal(3, YamlValues.GetInt(fireball.Properties, "level"));
    }
}
