using Codex.ContentImport;
using Codex.Core;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
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

        var result = await new Srd5eImporter(source, grants: GrantTable.None).ImportAsync(output, TextWriter.Null);

        Assert.Equal(1, result.Types["spell"]);
        Assert.Equal(2, result.Types["feat"]);
        Assert.Equal(1, result.Types["monster"]);
        Assert.True(File.Exists(Path.Combine(output, "manifest.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "LEGAL.md")));
        Assert.True(File.Exists(Path.Combine(output, "spells", "fireball.yaml")));

        var registry = new ContentRegistry();
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

    private static ClassColumns Columns(string json) => ClassColumns.Read(System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToList());

    /// <summary>Works out a step formula the importer wrote: <c>classLevel</c>, a number, or <c>classLevel &gt;= N ? V : rest</c>.</summary>
    private static int Worked(string formula, int level)
    {
        formula = formula.Trim();
        if (formula == "classLevel") return level;
        var match = System.Text.RegularExpressions.Regex.Match(formula, @"^classLevel >= (\d+) \? (\d+) : (.+)$");
        if (!match.Success) return int.Parse(formula);
        return level >= int.Parse(match.Groups[1].Value) ? int.Parse(match.Groups[2].Value) : Worked(match.Groups[3].Value, level);
    }

    [Fact]
    public void ClassColumns_BuildAStepFormulaThatGivesTheColumnAtEveryLevel()
    {
        var rage = new[] { 2, 2, 3, 3, 3, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 6, 6, 6, 6 };
        var json = "[" + string.Join(",", Enumerable.Range(1, 20).Select(l =>
            $"{{\"level\":{l},\"class\":{{\"index\":\"barbarian\"}},\"class_specific\":{{\"rage_count\":{rage[l - 1]},\"points\":{l},\"flat\":3}}}}")) +
            ",{\"level\":3,\"class\":{\"index\":\"barbarian\"},\"subclass\":{\"index\":\"berserker\"},\"class_specific\":{\"rage_count\":99}}]";
        var columns = Columns(json);

        var formula = columns.Formula("barbarian", "rage_count");
        Assert.Equal("classLevel >= 17 ? 6 : classLevel >= 12 ? 5 : classLevel >= 6 ? 4 : classLevel >= 3 ? 3 : 2", formula);
        for (var level = 1; level <= 20; level++) Assert.Equal(rage[level - 1], Worked(formula, level));

        Assert.Equal("classLevel", columns.Formula("barbarian", "points")); // equal to the class level
        Assert.Equal("3", columns.Formula("barbarian", "flat"));
        Assert.Throws<InvalidDataException>(() => columns.Formula("barbarian", "nothing"));
    }

    private static ContentEntry ClassWithRows(string id, params (string Name, int Level)[] rows)
    {
        var entry = new ContentEntry { Type = "class", Id = id, Name = id, SystemId = "srd5e" };
        entry.Properties["features"] = rows.Select(r => (object)new Dictionary<string, object> { ["name"] = r.Name, ["level"] = (long)r.Level, ["description"] = r.Name + "." }).ToList();
        return entry;
    }

    private static ContentEntry Spell(string id) => new() { Type = "spell", Id = id, Name = id, SystemId = "srd5e" };

    private static ClassColumns NoColumns => ClassColumns.Read(Array.Empty<System.Text.Json.JsonElement>());

    [Fact]
    public void GrantTable_RefusesAFileThatDoesNotFitTheSource()
    {
        var entries = new List<ContentEntry> { ClassWithRows("barbarian", ("Rage", 1), ("Danger Sense", 2)), Spell("fireball") };

        string Problem(string yaml) => Assert.Throws<GrantTableException>(() => GrantTable.Parse(yaml).Apply(entries, NoColumns, null)).Message;

        Assert.Contains("matches no row", Problem("class:\n  barbarian:\n    - { row: Rage, text: state }\n    - { row: Danger Sense, text: advantage }\n    - { row: Nothing, text: other }"));
        Assert.Contains("classified twice", Problem("class:\n  barbarian:\n    - { row: Rage, text: state }\n    - { row: Rage, text: other }\n    - { row: Danger Sense, text: advantage }"));
        Assert.Contains("is not in the grants file", Problem("class:\n  barbarian:\n    - { row: Rage, text: state }"));
        Assert.Contains("not a spell id", Problem("class:\n  barbarian:\n    - { row: Rage, spells: [no-such-spell] }\n    - { row: Danger Sense, text: advantage }"));
        Assert.Contains("needs a key", Problem("class:\n  barbarian:\n    - { row: Rage, uses: { max: 2 } }\n    - { row: Danger Sense, text: advantage }"));
        Assert.Contains("no counter", Problem("class:\n  barbarian:\n    - { row: Rage, spends: { key: nothing } }\n    - { row: Danger Sense, text: advantage }"));
        Assert.Contains("in the grants file but not in the source", Problem("class:\n  barbarian:\n    - { row: Rage, text: state }\n    - { row: Danger Sense, text: advantage }\n  monk:\n    - { row: Martial Arts, text: die }"));
    }

    [Fact]
    public void GrantTable_ExpandsColumnsPoolsAndSpellShorthands()
    {
        var barbarian = ClassWithRows("barbarian", ("Rage", 1), ("Danger Sense", 2));
        var other = ClassWithRows("fighter", ("Second Wind", 1));
        var json = "[" + string.Join(",", Enumerable.Range(1, 20).Select(l => $"{{\"level\":{l},\"class\":{{\"index\":\"barbarian\"}},\"class_specific\":{{\"rage_count\":{(l < 3 ? 2 : 4)}}}}}")) + "]";
        var yaml = """
            pools:
              recharge: { key: recharge, max: 1, recoversOn: [Long Rest] }
            class:
              barbarian:
                - { row: Rage, uses: { key: rage, max: { column: rage_count }, recoversOn: [Long Rest] } }
                - { row: Danger Sense, uses: { $ref: recharge, label: Sense }, spells: { 0: [fireball], 3: [fireball] } }
              fighter:
                - { row: Second Wind, uses: { $ref: recharge } }
            """;
        var entries = new List<ContentEntry> { barbarian, other, Spell("fireball") };

        // A shared pool may be named by two rows; its fields are copied and the row's own win.
        GrantTable.Parse(yaml).Apply(entries, Columns(json), null);

        var rows = ((List<object>)barbarian.Properties["features"]).Cast<Dictionary<string, object>>().ToList();
        var rage = (Dictionary<string, object>)rows[0]["uses"];
        Assert.Equal("classLevel >= 3 ? 4 : 2", rage["max"]);
        var sense = (Dictionary<string, object>)rows[1]["uses"];
        Assert.Equal("recharge", sense["key"]);
        Assert.Equal("Sense", sense["label"]);
        Assert.Equal(1L, sense["max"]);
        var adds = ((List<object>)rows[1]["adds"]).Cast<Dictionary<string, object>>().ToList();
        Assert.Equal(2, adds.Count);
        Assert.False(adds[0].ContainsKey("level")); // level 0: always
        Assert.Equal(3L, adds[1]["level"]);
        Assert.Equal("grantedSpells", adds[1]["to"]);
        Assert.Equal("Rage.", rows[0]["description"]); // the text is never touched
    }
}
