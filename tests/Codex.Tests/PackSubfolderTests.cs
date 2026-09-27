using System.IO.Compression;
using Codex.Core;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>Spells and feats load as abilities from their own subfolders (6.1), both from
/// loose directories and from .zip/.cdx archives (the B5 first-segment dispatch).</summary>
public class PackSubfolderTests
{
    private static async Task<ContentRegistry> LoadPackFromDirAsync(string packDir)
    {
        var registry = new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
        await new YamlContentPackLoader(registry).LoadPackAsync(packDir);
        return registry;
    }

    private static string WriteTempPack()
    {
        var packDir = Path.Combine(Path.GetTempPath(), "SubfolderTest_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(packDir, "spells"));
        Directory.CreateDirectory(Path.Combine(packDir, "feats"));
        Directory.CreateDirectory(Path.Combine(packDir, "classes"));
        Directory.CreateDirectory(Path.Combine(packDir, "equipment"));
        File.WriteAllText(Path.Combine(packDir, "manifest.json"),
            """{"id":"sub-test","name":"Sub","version":"1.0.0","systemId":"Test","priority":0}""");
        File.WriteAllText(Path.Combine(packDir, "spells", "zap.yaml"),
            "- id: zap\n  name: Zap\n  metadata:\n    kind: spell\n    level: 0\n");
        File.WriteAllText(Path.Combine(packDir, "feats", "tough.yaml"),
            "- id: tough\n  name: Tough\n  metadata:\n    kind: feat\n    level: 1\n");
        File.WriteAllText(Path.Combine(packDir, "classes", "fighter.yaml"),
            "- id: fighter\n  name: Fighter\n  properties:\n    hitDie: 10\n");
        File.WriteAllText(Path.Combine(packDir, "equipment", "sword.yaml"),
            "- id: sword\n  name: Sword\n  properties:\n    category: weapon\n");
        return packDir;
    }

    [Fact]
    public async Task LoadPackAsync_LoadsSpellsAndFeatsAsAbilities()
    {
        var packDir = WriteTempPack();
        try
        {
            var registry = await LoadPackFromDirAsync(packDir);

            var zap = registry.GetAbility("Test:zap");
            Assert.NotNull(zap);
            Assert.Equal("spell", zap.Metadata!["kind"].ToString());

            var tough = registry.GetAbility("Test:tough");
            Assert.NotNull(tough);
            Assert.Equal("feat", tough.Metadata!["kind"].ToString());

            var fighter = registry.GetRulesEntry("Test:fighter");
            Assert.NotNull(fighter);
            Assert.Equal("class", fighter.Kind);
            Assert.Equal(10, Convert.ToInt32(fighter.Properties["hitDie"]));

            var sword = registry.GetRulesEntry("Test:sword");
            Assert.NotNull(sword);
            Assert.Equal("equipment", sword.Kind);
        }
        finally
        {
            Directory.Delete(packDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadPackAsync_LoadsSpellsAndFeatsFromZip()
    {
        var packDir = WriteTempPack();
        var zipPath = packDir + ".zip";
        try
        {
            ZipFile.CreateFromDirectory(packDir, zipPath);
            var registry = await LoadPackFromDirAsync(zipPath);

            Assert.NotNull(registry.GetAbility("Test:zap"));
            Assert.NotNull(registry.GetAbility("Test:tough"));
        }
        finally
        {
            Directory.Delete(packDir, recursive: true);
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
    }
}
