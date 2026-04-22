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
    public void ContentRegistry_Should_RegisterAndRetrieveActors()
    {
        var registry = new ContentRegistry(_evaluator);
        var actor = new ActorDefinition { Id = "bob", SystemId = "dnd5e", PackId = "test", Name = "Bob the Blacksmith" };

        registry.RegisterActor(actor, 0);
        var retrieved = registry.GetActor("dnd5e:bob");

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
        Directory.CreateDirectory(Path.Combine(_tempPath, "actors"));
        Directory.CreateDirectory(Path.Combine(_tempPath, "locations"));

        File.WriteAllText(Path.Combine(_tempPath, "abilities", "fireball.yaml"), "id: fireball\nname: Fireball");
        File.WriteAllText(Path.Combine(_tempPath, "actors", "bob.yaml"), "id: bob\nname: Bob");
        File.WriteAllText(Path.Combine(_tempPath, "locations", "inn.yaml"), "id: inn\nname: The Rusty Tankard");

        // 2. Load
        var registry = new ContentRegistry(_evaluator);
        var loader = new YamlContentPackLoader(registry);
        await loader.LoadPackAsync(_tempPath);

        // 3. Verify
        Assert.NotNull(registry.GetAbility("dnd5e:fireball"));
        Assert.NotNull(registry.GetActor("dnd5e:bob"));
        Assert.NotNull(registry.GetLocation("dnd5e:inn"));
    }

    [Fact]
    public async Task YamlLoader_Should_HandleActorInheritance()
    {
        // 1. Setup Manifest
        var manifestJson = @"{
            ""id"": ""test-pack"",
            ""systemId"": ""dnd5e""
        }";
        File.WriteAllText(Path.Combine(_tempPath, "manifest.json"), manifestJson);
        Directory.CreateDirectory(Path.Combine(_tempPath, "actors"));

        // 2. Setup Base Actor in Registry
        var registry = new ContentRegistry(_evaluator);
        var baseActor = new ActorDefinition
        {
            Id = "humanoid",
            SystemId = "dnd5e",
            PackId = "core",
            Name = "Base Humanoid",
            Tags = new List<string> { "Humanoid" },
            Properties = new Dictionary<string, object> { { "Speed", 30 } }
        };
        registry.RegisterActor(baseActor, 0);

        // 3. Setup YAML for derived actor
        var banditYaml = @"
id: bandit
name: Bandit
inherits: dnd5e:humanoid
tags:
  - Hostile
properties:
  ArmorClass: 12
";
        File.WriteAllText(Path.Combine(_tempPath, "actors", "bandit.yaml"), banditYaml);

        // 4. Load
        var loader = new YamlContentPackLoader(registry);
        await loader.LoadPackAsync(_tempPath);

        // 5. Verify
        var bandit = registry.GetActor("dnd5e:bandit");
        Assert.NotNull(bandit);
        Assert.Equal("Bandit", bandit.Name);
        Assert.Contains("Humanoid", bandit.Tags); // Inherited
        Assert.Contains("Hostile", bandit.Tags); // Added
        Assert.Equal(30, Convert.ToInt32(bandit.Properties["Speed"])); // Inherited
        Assert.Equal(12, Convert.ToInt32(bandit.Properties["ArmorClass"])); // Added
    }
}