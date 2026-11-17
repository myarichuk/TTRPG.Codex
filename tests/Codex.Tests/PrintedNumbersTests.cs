using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>P4b (b) of docs/CONDITION_IMPLIES_DESIGN.md: a printed creature's numbers read the conditions that hold, as a pure method beside the evaluation.
/// Runs on srd5e with a valued condition and <c>sheetRules.printed</c> added.</summary>
public class PrintedNumbersTests
{
    private const string Frightened = "condition_frightened";
    private const string Spooked = "condition_spooked";

    private sealed class Feats(params ContentEntry[] entries) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => entries.FirstOrDefault(e => e.Id == id && e.Type == type);
        public IEnumerable<ContentEntry> Candidates(string type) => entries.Where(e => e.Type == type);
    }

    private static ModifierDefinition Mod(string target, string amount, string? type = "status") => new() { Target = target, Amount = amount, Type = type };

    private static SystemDefinition System(Action<SheetRules>? more = null)
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        system.ConditionEffects["Frightened"] = new ConditionEffectDefinition
        {
            Value = new ConditionValueDefinition { Max = "4" },
            Modifiers = { Mod("armorClass", "-value"), Mod("save", "-value"), Mod("initiative", "-value"), Mod("check.skill.stealth", "-value"), Mod("attack", "-value", "untyped") }
        };
        system.ConditionEffects["Spooked"] = new ConditionEffectDefinition
        {
            Value = new ConditionValueDefinition { Max = "4" },
            Modifiers = { Mod("attack.melee", "-3"), Mod("attack.ranged", "-1"), Mod("attack", "-1d4", "untyped") }
        };
        system.SheetRules!.Printed = new List<PrintedNumberDefinition>
        {
            new() { From = "armorClass", Target = "armorClass" },
            new() { From = "conSave", Target = "save.con" },
            new() { From = "alertness", Target = "initiative" },
            new() { From = "stealthBonus", Target = "check.skill.stealth" },
            new() { List = "attacks", Bonus = "bonus", Range = "reach", Name = "name" }
        };
        more?.Invoke(system.SheetRules);
        return system;
    }

    private static Dictionary<string, object> Creature(params (string Key, object Value)[] extra)
    {
        var sheet = new Dictionary<string, object>
        {
            [SheetRuleEngine.MethodKey] = SheetRuleEngine.PrintedMethod,
            ["armorClass"] = 18L, ["conSave"] = 7L, ["alertness"] = 3L, ["stealthBonus"] = 5L,
            ["attacks"] = new List<object>
            {
                new Dictionary<string, object> { ["name"] = "Longbow", ["bonus"] = 8L, ["reach"] = "ranged" },
                new Dictionary<string, object> { ["name"] = "Claw", ["bonus"] = 6L }
            }
        };
        foreach (var (key, value) in extra) sheet[key] = value;
        return sheet;
    }

    private static IReadOnlyList<PrintedNumber> Read(SystemDefinition system, Dictionary<string, object> sheet, IEntryResolver? resolver = null) =>
        new SheetRuleEngine(system, resolver ?? NullEntryResolver.Instance).Printed(sheet);

    [Fact]
    public void ArmorClass18_WithFrightened2_Is16_AndRecomputeLeavesTheStoredNumberAlone()
    {
        var system = System();
        var sheet = Creature((Frightened, 2L));

        var ac = Read(system, sheet).Single(n => n.Target == "armorClass");

        Assert.Equal((18, 16), (ac.Printed, ac.Total));
        Assert.Equal("Frightened", Assert.Single(ac.Bonuses).Source);
        new SheetRuleEngine(system, NullEntryResolver.Instance).Recompute(sheet);
        Assert.Equal(18L, sheet["armorClass"]);
    }

    [Fact]
    public void ASave7_WithFrightened1_Is6_AndASkillAndADerivedNumberRead_TheirOwnTargets()
    {
        var read = Read(System(), Creature((Frightened, 1L)));

        Assert.Equal(6, read.Single(n => n.Target == "save.con").Total);
        Assert.Equal(4, read.Single(n => n.Target == "check.skill.stealth").Total);
        Assert.Equal(2, read.Single(n => n.Target == "initiative").Total);
    }

    [Fact]
    public void AStrike_ReadsItsOwnRange_AndDiceGoToDiceNeverToTheTotal()
    {
        var read = Read(System(), Creature((Spooked, 1L)));

        var bow = read.Single(n => n.Label == "Longbow");
        var claw = read.Single(n => n.Label == "Claw");
        Assert.Equal((8, 7), (bow.Printed, bow.Total));
        Assert.Equal((6, 3), (claw.Printed, claw.Total));
        Assert.Equal("-1d4", Assert.Single(bow.Dice).Dice);
        Assert.Equal("Spooked", Assert.Single(bow.Dice).Source);
    }

    [Fact]
    public void AFeatureModifierOnTheCreature_DoesNotCount()
    {
        var feat = new ContentEntry
        {
            Id = "armored", Type = "feat", Name = "Armored", SystemId = "srd5e",
            Properties = new Dictionary<string, object> { ["modifiers"] = new List<object> { new Dictionary<string, object> { ["target"] = "armorClass", ["amount"] = "2" } } }
        };
        var system = System();
        var sheet = Creature(("feats", new List<object> { "armored" }));

        var ac = Read(system, sheet, new Feats(feat)).Single(n => n.Target == "armorClass");

        Assert.Equal(18, ac.Total);
    }

    [Fact]
    public void ASheetWhoseMethodIsNotPrinted_GivesNothing_AndSoDoesASystemWithoutPrinted()
    {
        var sheet = Creature((Frightened, 2L));
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        Assert.Empty(Read(System(), sheet));
        Assert.Empty(Read(System(rules => rules.Printed.Clear()), Creature((Frightened, 2L))));
    }

    [Fact]
    public void ANumberTheSheetDoesNotHold_GivesNoEntry()
    {
        var sheet = Creature((Frightened, 2L));
        sheet.Remove("conSave");

        Assert.DoesNotContain(Read(System(), sheet), n => n.Target == "save.con");
    }

    // ---- Load checks ----

    [Fact]
    public void TheLoader_RefusesFromAndListTogether_NeitherAndAListWithoutBonus()
    {
        var yaml = File.ReadAllText(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        IReadOnlyList<string> With(string printed) =>
            SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(yaml.Replace("\nsheetRules:\n", "\nsheetRules:\n  printed:\n" + printed + "\n"))));

        Assert.Contains(With("    - { from: armorClass, target: armorClass, list: attacks, bonus: bonus }"), e => e.Contains("sheetRules.printed[0]") && e.Contains("both"));
        Assert.Contains(With("    - { target: armorClass }"), e => e.Contains("sheetRules.printed[0]") && e.Contains("needs a 'from'"));
        Assert.Contains(With("    - { list: attacks }"), e => e.Contains("sheetRules.printed[0]") && e.Contains("'bonus'"));
        Assert.Contains(With("    - { from: x, target: nowhere }"), e => e.Contains("sheetRules.printed[0]") && e.Contains("'nowhere'"));
        Assert.Contains(With("    - { from: x, target: save.zzz }"), e => e.Contains("'save.zzz'"));
        // Every kind of printed number loads.
        SystemDefinitionLoader.Parse(yaml.Replace("\nsheetRules:\n", "\nsheetRules:\n  printed:\n    - { from: x, target: armorClass }\n    - { from: y, target: save.con }\n    - { list: attacks, bonus: bonus }\n    - { from: z, target: check.skill.stealth }\n"));
    }

    [Fact]
    public void AKindsTarget_IsNotAPrintedTarget_OnAHybrid()
    {
        var yaml = File.ReadAllText(Path.Combine(Codex.Tests.Reference.ReferenceFixtures.Root, "degrees", "system.yaml"));
        var errors = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() =>
            SystemDefinitionLoader.Parse(yaml.Replace("\nsheetRules:\n  enforcement: warn\n", "\nsheetRules:\n  enforcement: warn\n  printed:\n    - { from: x, target: senseDc }\n    - { from: y, target: classDc }\n"))));

        Assert.Contains(errors, e => e.Contains("sheetRules.printed[0]") && e.Contains("sheet kind's"));
        Assert.DoesNotContain(errors, e => e.Contains("sheetRules.printed[1]"));
    }
}
