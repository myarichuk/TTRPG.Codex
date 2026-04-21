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

public class ContentSystemTests : IDisposable
{
    private readonly string _tempPath;
    private readonly ScriptEvaluator _evaluator = new();

    public ContentSystemTests()
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
    public void ContentRegistry_Should_RegisterAndRetrieveAbilities()
    {
        var registry = new ContentRegistry(_evaluator);
        var ability = new AbilityDefinition { Id = "fireball", SystemId = "dnd5e", PackId = "srd", Name = "Fireball" };

        registry.RegisterAbility(ability, 0);
        var retrieved = registry.GetAbility("dnd5e:fireball");

        Assert.NotNull(retrieved);
        Assert.Equal("Fireball", retrieved.Name);
    }

    [Fact]
    public void ContentRegistry_Should_RegisterAndRetrieveNpcs()
    {
        var registry = new ContentRegistry(_evaluator);
        var npc = new NpcDefinition { Id = "bob", SystemId = "dnd5e", PackId = "test", Name = "Bob the Blacksmith" };

        registry.RegisterNpc(npc, 0);
        var retrieved = registry.GetNpc("dnd5e:bob");

        Assert.NotNull(retrieved);
        Assert.Equal("Bob the Blacksmith", retrieved.Name);
    }

    [Fact]
    public async Task YamlLoader_Should_LoadFullPack()
    {
        // 1. Setup Manifest
        var manifestJson = @"{
            ""id"": ""test-pack"",
            ""systemId"": ""dnd5e"",
            ""name"": ""Test Pack"",
            ""version"": ""1.0.0""
        }";
        File.WriteAllText(Path.Combine(_tempPath, "manifest.json"), manifestJson);

        Directory.CreateDirectory(Path.Combine(_tempPath, "abilities"));
        Directory.CreateDirectory(Path.Combine(_tempPath, "npcs"));
        Directory.CreateDirectory(Path.Combine(_tempPath, "locations"));

        File.WriteAllText(Path.Combine(_tempPath, "abilities", "fireball.yaml"), "id: fireball\nname: Fireball");
        File.WriteAllText(Path.Combine(_tempPath, "npcs", "bob.yaml"), "id: bob\nname: Bob");
        File.WriteAllText(Path.Combine(_tempPath, "locations", "inn.yaml"), "id: inn\nname: The Rusty Tankard");

        // 2. Load
        var registry = new ContentRegistry(_evaluator);
        var loader = new YamlContentPackLoader(registry);
        await loader.LoadPackAsync(_tempPath);

        // 3. Verify
        Assert.NotNull(registry.GetAbility("dnd5e:fireball"));
        Assert.NotNull(registry.GetNpc("dnd5e:bob"));
        Assert.NotNull(registry.GetLocation("dnd5e:inn"));
    }

    [Fact]
    public async Task AbilityRegistry_Should_ExecuteScript()
    {
        // Arrange
        var registry = new ContentRegistry(_evaluator);
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
                    Script = "world.AddStatus(target, \"stunned\", \"core\", 5)"
                }
            }
        };
        registry.RegisterAbility(ability, 0);

        using var world = new CodexWorld();
        var caster = world.CreateEntity();
        var target = world.CreateEntity();
        var context = new AbilityContext(caster, target, world);

        // Act
        await registry.ExecuteAbilityAsync("core:test_script", context);

        // Assert
        Assert.True(target.Has<Codex.Core.Components.DurationComponent>());
        Assert.True(target.Has<Codex.Core.Components.StatusEffectComponent>());
    }
}