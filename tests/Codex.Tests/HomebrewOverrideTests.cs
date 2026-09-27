using Codex.Core;
using Codex.Core.Abilities;
using Codex.Core.Components;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// Homebrew layering over the real full-SRD pack: a higher-priority pack's entry wins for
/// its own id, and `inherits` pulls the base entry's fields in. The override pack is built
/// inline in a temp dir (no fixture data to rot) - this is the contract the old sample
/// pack's priority-0-vs-10 setup used to pin from checked-in YAML.
/// </summary>
public class HomebrewOverrideTests : IDisposable
{
    private readonly CodexWorld _world = new();
    private readonly ScriptEvaluator _evaluator = new(NullLogger<ScriptEvaluator>.Instance);
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        _world.Dispose();
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of a throwaway temp directory.
            }
        }
    }

    private static string FindPackDir(string packId)
    {
        var start = Path.GetDirectoryName(typeof(HomebrewOverrideTests).Assembly.Location) ?? AppContext.BaseDirectory;
        var dir = new DirectoryInfo(start);
        for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "packs", packId);
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"packs/{packId} not found above {start}. " +
            "Generate it with scripts/import_srd51.py.");
    }

    private string WriteOverridePack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "HomebrewOverride_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "abilities"));
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """
            {
              "id": "homebrew-override-test",
              "name": "Homebrew Override (test-only)",
              "version": "1.0.0",
              "systemId": "DnD5e",
              "minAppVersion": "1.0.0",
              "priority": 20
            }
            """);
        File.WriteAllText(Path.Combine(dir, "abilities", "override.yaml"), """
            - id: fireball-homebrew
              name: Fireball (Empowered Homebrew)
              inherits: DnD5e:fireball
              effects:
                - type: Damage
                  params:
                    pool: HP
                    amount: 35
            """);
        _tempDirs.Add(dir);
        return dir;
    }

    private async Task<ContentRegistry> LoadPackAsync(string packId)
    {
        var registry = new ContentRegistry(_evaluator);
        await new YamlContentPackLoader(registry).LoadPackAsync(FindPackDir(packId));
        return registry;
    }

    [Fact]
    public async Task HigherPriorityPack_OverridesBase_WithoutTouchingIt()
    {
        var registry = new ContentRegistry(_evaluator);
        var loader = new YamlContentPackLoader(registry);
        await loader.LoadPackAsync(FindPackDir("srd51-full"));
        await loader.LoadPackAsync(WriteOverridePack());

        // The base entry is untouched: still the SRD 8d6 dice version.
        var fireball = registry.GetAbility("DnD5e:fireball");
        Assert.NotNull(fireball);
        var baseDamage = Assert.Single(fireball.Effects!, e => e.Type == "Damage");
        Assert.Equal("8d6", baseDamage.Params!["dice"].ToString());

        // The override wins for its own id, with inherited fields filled in.
        var homebrew = registry.GetAbility("DnD5e:fireball-homebrew");
        Assert.NotNull(homebrew);
        Assert.NotNull(homebrew.Requires);
        Assert.NotNull(homebrew.Costs);
        var damage = Assert.Single(homebrew.Effects!, e => e.Type == "Damage");
        Assert.Equal("Damage", damage.Type);
        Assert.Equal(35, Convert.ToInt32(damage.Params!["amount"]));
    }
}
