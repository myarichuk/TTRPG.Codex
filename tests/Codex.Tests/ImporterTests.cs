using Codex.ContentImport;
using Codex.Core;
using Codex.Core.Scripting;
using Codex.Core.Systems;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>Runs the SRD importer over a tiny synthetic 5e-bits data set and loads the result.</summary>
public class ImporterTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), "CodexImport_" + Guid.NewGuid());

    public ImporterTests() => Directory.CreateDirectory(_tempPath);

    public void Dispose()
    {
        if (Directory.Exists(_tempPath)) Directory.Delete(_tempPath, true);
    }

    [Fact]
    public async Task Import_ConvertsSpellsFeatsAndMonsters_WithLicence()
    {
        var source = Path.Combine(_tempPath, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "5e-SRD-Spells.json"), """
            [{"index":"fireball","name":"Fireball","level":3,"school":{"index":"evocation","name":"Evocation"},
              "classes":[{"name":"Wizard"}],"casting_time":"Action","ritual":false,"range":"150 feet","components":["V","S"],
              "duration":"Instantaneous","concentration":false,"description":"Boom.\n  Flammable objects ig- nite.","higher_level":"More."}]
            """);
        File.WriteAllText(Path.Combine(source, "5e-SRD-Feats.json"), """
            [{"index":"alert","name":"Alert","type":"origin","description":"You gain the following benefits."},
             {"index":"grappler","name":"Grappler","type":"general","prerequisites":{"minimum_level":4},"prerequisite_options":{"desc":"Strength or Dexterity 13+"},"description":"Grab."}]
            """);
        File.WriteAllText(Path.Combine(source, "5e-SRD-Monsters.json"), """
            [{"index":"goblin-warrior","name":"Goblin Warrior","size":"Small","type":"fey","alignment":"chaotic neutral",
              "armor_class":[{"value":15,"armor":[{"name":"Leather Armor"}]}],"hit_points":10,"hit_dice":"3d6","speed":{"walk":"30 ft."},
              "strength":8,"dexterity":15,"constitution":10,"intelligence":10,"wisdom":8,"charisma":8,
              "senses":{"darkvision":"60 ft.","passive_perception":9},"languages":"Common, Goblin","challenge_rating":0.25,"xp":50,"proficiency_bonus":2,
              "proficiencies":[{"value":6,"proficiency":{"index":"skill-stealth","name":"Skill: Stealth"}}],
              "actions":[{"name":"Scimitar","desc":"Melee Attack Roll: +4."}],"bonus_actions":[{"name":"Nimble Escape","desc":"Disengage or Hide."}]}]
            """);

        // The pack directory sits under a system folder so the importer finds the system.yaml.
        var systemDir = Path.Combine(_tempPath, "srd5e");
        Directory.CreateDirectory(systemDir);
        File.Copy(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"), Path.Combine(systemDir, "system.yaml"));
        var output = Path.Combine(systemDir, "packs", "test");

        var result = await new Srd5eImporter(source).ImportAsync(output, TextWriter.Null);

        Assert.Equal(1, result.Types["spell"]);
        Assert.Equal(2, result.Types["feat"]);
        Assert.Equal(1, result.Types["monster"]);
        Assert.True(File.Exists(Path.Combine(output, "manifest.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "LEGAL.md")));
        Assert.True(File.Exists(Path.Combine(output, "spells", "fireball.yaml")));

        var registry = new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
        var system = SystemDefinitionLoader.Load(Path.Combine(systemDir, "system.yaml"));
        await new YamlContentPackLoader(registry).LoadPackAsync(output, system);

        var pack = registry.GetPack("srd-5.2.1")!;
        Assert.StartsWith("This work includes material from the System Reference Document 5.2.1", pack.License!.Attribution);

        var fireball = registry.GetEntry("srd5e", "spell", "fireball")!;
        Assert.Equal(3L, fireball.Properties["level"]);
        Assert.Contains("Flammable objects ignite.", fireball.Description); // hyphenated line break repaired
        Assert.Contains("**Using a Higher-Level Spell Slot.** More.", fireball.Description);

        var grappler = registry.GetEntry("srd5e", "feat", "grappler")!;
        Assert.Equal("general", grappler.Properties["category"]);
        Assert.Equal("Level 4+; Strength or Dexterity 13+", grappler.Properties["prerequisites"]);

        var goblin = registry.GetActor("srd5e:goblin-warrior")!;
        Assert.Equal(10, goblin.Resources["HP"]);
        Assert.Equal(15L, goblin.Properties["dex"]);
        var entry = registry.GetEntry("srd5e", "monster", "goblin-warrior")!;
        Assert.Equal("1/4", entry.Properties["challengeRating"]);
        Assert.Contains("### Bonus Actions", entry.Description);
        Assert.Contains("| 8 (-1) | 15 (+2) |", entry.Description);
    }

    [Fact]
    public void Options_ParseTheDocumentedCommandLine()
    {
        var options = ImportOptions.Parse(new[] { "srd5e", "--source", "in", "--out", "out", "--download" });
        Assert.NotNull(options);
        Assert.Equal("in", options!.Source);
        Assert.Equal("out", options.Out);
        Assert.True(options.Download);
        Assert.Equal("2024", options.Edition);

        Assert.Null(ImportOptions.Parse(Array.Empty<string>()));
        Assert.Null(ImportOptions.Parse(new[] { "srd5e", "--source", "in" }));
        Assert.Null(ImportOptions.Parse(new[] { "srd5e", "--source", "in", "--out", "o", "--edition", "1999" }));
    }

    [Fact]
    public void MergeUnclosedBold_JoinsAStraySentenceBackOntoItsParagraph()
    {
        const string text = "While Unconscious:\n**Inert.** You drop whatever you're holding.\n**When this condition ends, you remain Prone.\n**Speed 0.** Your Speed is 0.";

        Assert.Equal("While Unconscious:\n**Inert.** You drop whatever you're holding. When this condition ends, you remain Prone.\n**Speed 0.** Your Speed is 0.",
            Srd5eImporter.MergeUnclosedBold(text));
        Assert.Equal("**Bold.** fine", Srd5eImporter.MergeUnclosedBold("**Bold.** fine"));
    }
}
