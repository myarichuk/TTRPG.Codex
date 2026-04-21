using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Codex.Core;
using Codex.Core.Models;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using NSubstitute;
using Xunit;

namespace Codex.Tests;

public class AbilitySystemTests : IDisposable
{
    private readonly string _tempPath;
    private readonly ScriptEvaluator _evaluator = new();

    public AbilitySystemTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "CodexTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, true);
        }
    }

    [Fact]
    public void AbilityRegistry_Should_RegisterAndRetrieve()
    {
        var registry = new AbilityRegistry(_evaluator);
        var ability = new AbilityDefinition { Id = "fireball", SystemId = "dnd5e", PackId = "srd", Name = "Fireball" };

        registry.Register(ability);
        var retrieved = registry.GetAbility("dnd5e:fireball");

        Assert.NotNull(retrieved);
        Assert.Equal("Fireball", retrieved.Name);
    }

    [Fact]
    public void AbilityRegistry_Should_HandlePriority()
    {
        var registry = new AbilityRegistry(_evaluator);
        var baseAbility = new AbilityDefinition { Id = "strike", SystemId = "core", PackId = "core", Name = "Basic Strike" };
        var overrideAbility = new AbilityDefinition { Id = "strike", SystemId = "core", PackId = "mod", Name = "Better Strike" };

        registry.Register(baseAbility, 0);
        registry.Register(overrideAbility, 10);
        var retrieved = registry.GetAbility("core:strike");

        Assert.NotNull(retrieved);
        Assert.Equal("Better Strike", retrieved.Name);
    }

    [Fact]
    public async Task YamlLoader_Should_LoadManifest()
    {
        var manifestJson = @"{
            ""id"": ""test-pack"",
            ""name"": ""Test Pack"",
            ""version"": ""1.0.0"",
            ""systemId"": ""dnd5e"",
            ""priority"": 5
        }";
        File.WriteAllText(Path.Combine(_tempPath, "manifest.json"), manifestJson);

        var registry = Substitute.For<IAbilityRegistry>();
        var loader = new YamlAbilityPackLoader(registry);
        var manifest = await loader.ReadManifestAsync(_tempPath);

        Assert.NotNull(manifest);
        Assert.Equal("test-pack", manifest.Id);
        Assert.Equal("dnd5e", manifest.SystemId);
        Assert.Equal(5, manifest.Priority);
    }

    [Fact]
    public async Task YamlLoader_Should_LoadAbilitiesAndMergeInheritance()
    {
        // 1. Setup Manifest
        var manifestJson = @"{
            ""id"": ""test-pack"",
            ""systemId"": ""dnd5e"",
            ""contentPaths"": [""abilities""]
        }";
        File.WriteAllText(Path.Combine(_tempPath, "manifest.json"), manifestJson);
        Directory.CreateDirectory(Path.Combine(_tempPath, "abilities"));

        // 2. Setup Base Ability in Registry
        var registry = new AbilityRegistry(_evaluator);
        var baseAbility = new AbilityDefinition
        {
            Id = "base_spell",
            SystemId = "dnd5e",
            PackId = "srd",
            Name = "Base Spell",
            Description = "Base Description",
            Costs = new Dictionary<string, int> { { "Mana", 5 } }
        };
        registry.Register(baseAbility);

        // 3. Setup YAML file with inheriting ability
        var abilityYaml = @"
id: fireball
name: Fireball
inherits: dnd5e:base_spell
costs:
  Mana: 10
effects:
  - type: damage
    params:
      amount: 8d6
";
        File.WriteAllText(Path.Combine(_tempPath, "abilities", "spells.yaml"), abilityYaml);

        // 4. Load
        var loader = new YamlAbilityPackLoader(registry);
        var loaded = await loader.LoadPackAsync(_tempPath);

        // 5. Verify
        var fireball = registry.GetAbility("dnd5e:fireball");
        Assert.NotNull(fireball);
        Assert.Equal("Fireball", fireball.Name);
        Assert.Equal("Base Description", fireball.Description); // Inherited
        Assert.NotNull(fireball.Costs);
        Assert.Equal(10, fireball.Costs["Mana"]); // Overridden
        Assert.NotNull(fireball.Effects);
        Assert.Single(fireball.Effects);
    }

    [Fact]
    public async Task AbilityRegistry_Should_ExecuteScript()
    {
        // Arrange
        var registry = new AbilityRegistry(_evaluator);
        var ability = new AbilityDefinition
        {
            Id = "test_script",
            SystemId = "core",
            PackId = "test",
            Name = "Test Script",
            Effects = new List<AbilityEffect>
            {
                new AbilityEffect
                {
                    Type = "script",
                    Script = "target.Set(new Duration(5))"
                }
            }
        };
        registry.Register(ability);

        using var world = new CodexWorld();
        var caster = world.CreateEntity();
        var target = world.CreateEntity();
        var context = new AbilityContext(caster, target, world);

        // Act
        await registry.ExecuteAbilityAsync("core:test_script", context);

        // Assert
        Assert.True(target.Has<Codex.Core.Components.DurationComponent>());
        Assert.Equal(5.0f, target.Get<Codex.Core.Components.DurationComponent>().RoundsRemaining);
    }
}