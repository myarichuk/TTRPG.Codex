using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>H4a (docs/HAZARDS_DESIGN.md section 12): the pure half of a check, on srd5e. No dice, no runtime.</summary>
public class ActorCheckTests
{
    private static readonly SystemDefinition Srd5e = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

    [Theory]
    [InlineData("Perception", "perception")]
    [InlineData("perception", "perception")]
    [InlineData("PERCEPTION", "perception")]
    [InlineData("Sleight of Hand", "sleight-of-hand")]
    public void Resolve_FindsASkill_ByKeyOrLabel_IgnoringCase(string check, string key)
    {
        var spec = ActorChecks.Resolve(Srd5e, check);
        Assert.Equal(CheckKind.Skill, spec!.Kind);
        Assert.Equal(key, spec.Key, ignoreCase: true);
    }

    [Fact]
    public void Resolve_FindsAnAttribute_ByLabel_AndASaveOnlyWhenTheKindIsSave()
    {
        Assert.Equal(new CheckSpec(CheckKind.Attribute, "dex", "Dexterity"), ActorChecks.Resolve(Srd5e, "Dexterity"));
        Assert.Equal(new CheckSpec(CheckKind.Attribute, "dex", "Dexterity"), ActorChecks.Resolve(Srd5e, "dex"));
        Assert.Equal(new CheckSpec(CheckKind.Save, "dex", "Dexterity"), ActorChecks.Resolve(Srd5e, "Dexterity", CheckKind.Save));
        Assert.Null(ActorChecks.Resolve(Srd5e, "Perception", CheckKind.Save));
    }

    [Fact]
    public void Resolve_ReadsAPassive_ByKeyOrLabel_AndAskingForASkillNeverFindsOne()
    {
        var passive = Srd5e.SheetRules!.Skills!.Passives.First();
        var spec = ActorChecks.Resolve(Srd5e, passive.Label)!;
        Assert.Equal((CheckKind.Passive, passive.Key), (spec.Kind, spec.Key));
        Assert.Equal(CheckKind.Passive, ActorChecks.Resolve(Srd5e, passive.Key)!.Kind);
        Assert.Null(ActorChecks.Resolve(Srd5e, passive.Label, CheckKind.Skill));
    }

    [Theory]
    [InlineData("Thieves' Tools")]
    [InlineData("Spellcasting")]
    [InlineData("")]
    public void Resolve_KnowsNothingOfATool(string check) => Assert.Null(ActorChecks.Resolve(Srd5e, check));

    [Fact]
    public void HasPassive_IsTrueForASkillWithAPassive_AndFalseForAnythingElse()
    {
        Assert.True(ActorChecks.HasPassive(Srd5e, ActorChecks.Resolve(Srd5e, "Perception")!));
        Assert.False(ActorChecks.HasPassive(Srd5e, ActorChecks.Resolve(Srd5e, "Stealth")!));
        Assert.False(ActorChecks.HasPassive(Srd5e, ActorChecks.Resolve(Srd5e, "Dexterity")!));
        Assert.False(ActorChecks.HasPassive(Srd5e, new CheckSpec(CheckKind.Skill, string.Empty, "Thieves' Tools")));
        Assert.True(ActorChecks.HasPassive(Srd5e, ActorChecks.Resolve(Srd5e, Srd5e.SheetRules!.Skills!.Passives.First().Key)!));
    }

    [Fact]
    public void Grade_PassesAtTheDc_AndFailsOneBelow()
    {
        Assert.Equal(new CheckOutcome("success", true), ActorChecks.Grade(15, 10, 15));
        Assert.Equal(new CheckOutcome("failure", false), ActorChecks.Grade(14, 10, 15));
        Assert.False(ActorChecks.Grade(14, 20, 15).Passed); // the natural die is kept for a later grade, and does not change this one
    }

    [Fact]
    public void WhenTheSystemNamesACheckRoll_TheGradeIsItsOutcome()
    {
        var degrees = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "four-degrees", "system.yaml"));
        var sheet = new Dictionary<string, object>();

        // 27 against DC 15 is ten or more over: a critical success. Beating the DC by less is a success, missing it a failure.
        Assert.Equal(new CheckOutcome("criticalSuccess", true, "critical"), ActorChecks.Grade(degrees, NullEntryResolver.Instance, sheet, 27, 12, 15));
        Assert.Equal(("success", true), ActorChecks.Grade(degrees, NullEntryResolver.Instance, sheet, 17, 12, 15) is { } ok ? (ok.Key, ok.Passed) : default);
        // A natural 1 moves the degree down one step: 15 against DC 15 is a failure here, and a success on a system that grades pass or fail.
        Assert.Equal(("failure", false), ActorChecks.Grade(degrees, NullEntryResolver.Instance, sheet, 15, 1, 15) is { } low ? (low.Key, low.Passed) : default);
        Assert.Equal(("criticalFailure", false), ActorChecks.Grade(degrees, NullEntryResolver.Instance, sheet, 4, 2, 15) is { } worst ? (worst.Key, worst.Passed) : default);
        Assert.Equal(new CheckOutcome("success", true), ActorChecks.Grade(Srd5e, NullEntryResolver.Instance, sheet, 15, 1, 15));
        // A passive score has no die: the degree is read without a natural face.
        Assert.Equal("success", ActorChecks.Grade(degrees, NullEntryResolver.Instance, sheet, 16, null, 15).Key);
    }

    [Fact]
    public void Suggest_ReadsThePrintedText_OnlyWhenTheTypeNamesTheField()
    {
        var goblin = new ActorDocument
        {
            Id = "g", Name = "Goblin", BlueprintId = "srd5e:goblin",
            Sheet = new Dictionary<string, object> { ["skills"] = "History +12, Perception +10", ["savingThrows"] = "DEX +3, Wisdom -1" }
        };
        var perception = ActorChecks.Resolve(Srd5e, "Perception")!;
        Assert.Equal(10, ActorChecks.Suggest(Srd5e, goblin, perception));
        Assert.Equal(12, ActorChecks.Suggest(Srd5e, goblin, ActorChecks.Resolve(Srd5e, "History")!));
        Assert.Null(ActorChecks.Suggest(Srd5e, goblin, ActorChecks.Resolve(Srd5e, "Stealth")!));
        Assert.Equal(3, ActorChecks.Suggest(Srd5e, goblin, ActorChecks.Resolve(Srd5e, "dex", CheckKind.Save)!));
        Assert.Equal(-1, ActorChecks.Suggest(Srd5e, goblin, ActorChecks.Resolve(Srd5e, "Wisdom", CheckKind.Save)!));

        var unnamed = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        foreach (var type in unnamed.EntityTypes) (type.PrintedSkills, type.PrintedSaves) = (null, null);
        Assert.Null(ActorChecks.Suggest(unnamed, goblin, perception));
    }

    private static ActorDocument Stamped(string? blueprint) => new()
    {
        Id = "a", BlueprintId = blueprint, Sheet = new Dictionary<string, object> { [SheetRuleEngine.MethodKey] = SheetRuleEngine.PrintedMethod }
    };

    [Fact]
    public void IsPrinted_ReadsThePrintedMethodStamp_HomebrewCreaturesIncluded_AndNeedsNoClassLevels()
    {
        var evaluation = new SheetEvaluation();
        Assert.False(ActorChecks.IsPrinted(Srd5e, new ActorDocument { Id = "a" }, evaluation));
        // A BlueprintId alone is not enough: a restatted creature (manual method) is evaluated.
        Assert.False(ActorChecks.IsPrinted(Srd5e, new ActorDocument { Id = "a", BlueprintId = "srd5e:goblin" }, evaluation));
        Assert.True(ActorChecks.IsPrinted(Srd5e, Stamped("srd5e:goblin"), evaluation));
        Assert.True(ActorChecks.IsPrinted(Srd5e, Stamped(null), evaluation)); // homebrew: CastService leaves BlueprintId null
        evaluation.Classes.Add(new ClassLevel("fighter", null, 3, true));
        Assert.False(ActorChecks.IsPrinted(Srd5e, Stamped("srd5e:goblin"), evaluation));
    }

    [Fact]
    public void Resolve_PassiveKindWithASkillName_IsThatSkillsPassive_AndASkillWithNoneIsNotFound()
    {
        var spec = ActorChecks.Resolve(Srd5e, "Perception", CheckKind.Passive)!;
        Assert.Equal((CheckKind.Passive, "passivePerception"), (spec.Kind, spec.Key));
        Assert.Null(ActorChecks.Resolve(Srd5e, "Stealth", CheckKind.Passive));
    }
}
