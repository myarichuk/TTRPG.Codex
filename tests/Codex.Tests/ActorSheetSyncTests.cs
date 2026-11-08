using Codex.Core;
using Codex.Core.Components;
using Codex.Core.Models;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

public class ActorSheetSyncTests
{
    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: demo
        name: Demo
        attributes:
          - { key: str, label: Strength, default: 10 }
          - { key: dex, label: Dexterity, default: 10 }
        resources:
          - { key: HP, label: Hit Points, maxKey: HP_Max, default: 8, isPrimary: true }
          - { key: TempHP, label: Temporary }
        """);

    private static ComponentRegistry Registry()
    {
        var registry = new ComponentRegistry();
        registry.Register<ResourcePoolComponent>();
        registry.Register<AttributesComponent>();
        registry.Register<TagsComponent>();
        return registry;
    }

    [Fact]
    public void ApplySheetToState_BuildsPoolAndAttributes_FromSheetKeys()
    {
        var registry = Registry();
        var actor = new ActorDocument { Sheet = { ["str"] = 16L, ["dex"] = "12", ["HP"] = 7L, ["HP_Max"] = 10L, ["class"] = "fighter" } };

        ActorSheetSync.ApplySheetToState(actor, System, registry);

        var components = registry.Hydrate(actor.State).ToList();
        var pool = components.OfType<ResourcePoolComponent>().Single();
        var attributes = components.OfType<AttributesComponent>().Single();
        Assert.Equal(7, pool.Get("HP"));
        Assert.Equal(10, pool.Get("HP_Max"));
        Assert.Equal(16, attributes.Get("str"));
        Assert.Equal(12, attributes.Get("dex"));
        Assert.Equal("fighter", actor.Sheet["class"]); // non-component fields stay on the sheet only
    }

    [Fact]
    public void ApplySheetToState_PreservesOtherComponents()
    {
        var registry = Registry();
        var actor = new ActorDocument { Sheet = { ["HP"] = 5L } };
        var tags = new TagsComponent { Tags = { "undead" } };
        actor.State = registry.Snapshot(new object[] { tags });

        ActorSheetSync.ApplySheetToState(actor, System, registry);

        Assert.Contains(registry.Hydrate(actor.State), c => c is TagsComponent t && t.Tags.Contains("undead"));
        Assert.Contains(registry.Hydrate(actor.State), c => c is ResourcePoolComponent);
    }

    [Fact]
    public void ApplyStateToSheet_CopiesLiveValuesBack()
    {
        var registry = Registry();
        var pool = new ResourcePoolComponent();
        pool.Set("HP", 3);
        pool.Set("HP_Max", 10);
        var actor = new ActorDocument { Sheet = { ["HP"] = 10L }, State = registry.Snapshot(new object[] { pool }) };

        ActorSheetSync.ApplyStateToSheet(actor, System, registry);

        Assert.Equal(3L, actor.Sheet["HP"]);
        Assert.Equal((3, 10, "Hit Points"), ActorSheetSync.PrimaryResource(actor, System));
    }

    [Fact]
    public void SeedFromBlueprint_CopiesStatsAndResources()
    {
        var registry = Registry();
        var blueprint = new ActorDefinition
        {
            Id = "goblin", SystemId = "demo", Name = "Goblin",
            Resources = { ["HP"] = 7, ["HP_Max"] = 7 },
            Properties = { ["str"] = 8L, ["armorClass"] = 15L }
        };
        var actor = new ActorDocument();

        ActorSheetSync.SeedFromBlueprint(actor, blueprint, System, registry);

        Assert.Equal(7L, actor.Sheet["HP"]);
        Assert.Equal(15L, actor.Sheet["armorClass"]);
        // Scores as the stat block prints them: the sheet's rules must not hold them to point buy.
        Assert.Equal(SheetRuleEngine.PrintedMethod, actor.Sheet[SheetRuleEngine.MethodKey]);
        var pool = registry.Hydrate(actor.State).OfType<ResourcePoolComponent>().Single();
        Assert.Equal(7, pool.Get("HP"));
        Assert.Equal(8, registry.Hydrate(actor.State).OfType<AttributesComponent>().Single().Get("str"));
    }

    private static readonly SystemDefinition Capped = SystemDefinitionLoader.Parse("""
        id: capped
        name: Capped
        attributes:
          - { key: str, label: Strength, default: 10, modifier: half-minus-five }
          - { key: dex, label: Dexterity, default: 10, modifier: half-minus-five }
        actorFields:
          - { key: background, label: Background, type: EntityReference, targetEntityType: background }
        entityTypes:
          - { type: background, label: Background }
          - { type: feat, label: Feat }
        sheetRules:
          attributes:
            methods: [point-buy, manual]
            default: point-buy
            max: 20
            pointBuy: { budget: 27, min: 8, max: 15 }
            bonuses: { label: Background bonuses, fromField: background, property: abilityScores, allocations: [[2, 1]] }
            increases: { label: Score increases, fromList: feats }
          lists:
            - { key: feats, label: Feats, targetEntityType: feat }
        """);

    /// <summary>An Ability Score Improvement (2 points, any score) and a background that may raise Strength or Dexterity.</summary>
    private sealed class CappedEntries : IEntryResolver
    {
        public static readonly CappedEntries Instance = new();

        private readonly List<ContentEntry> _entries = new()
        {
            new ContentEntry { Id = "asi", Name = "Ability Score Improvement", Type = "feat", Properties = { ["abilityIncrease"] = 2L } },
            new ContentEntry { Id = "soldier", Name = "Soldier", Type = "background", Properties = { ["abilityScores"] = new List<object> { "str", "dex" } } }
        };

        public ContentEntry? Resolve(string type, string id) => _entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => _entries.Where(e => e.Type == type);
    }

    private static ActorDefinition PitFiend() => new()
    {
        Id = "pit-fiend", SystemId = "capped", Name = "Pit Fiend",
        Properties = { ["str"] = 26L, ["dex"] = 14L }
    };

    private static (Dictionary<string, object> Sheet, SheetEvaluation Evaluation) Recomputed(ActorDocument actor)
    {
        var sheet = new Dictionary<string, object>(actor.Sheet);
        return (sheet, new SheetRuleEngine(Capped, CappedEntries.Instance).Recompute(sheet));
    }

    [Fact]
    public void AStatBlocksScores_AreAsPrinted_AboveTheCapCharactersAreHeldTo()
    {
        var creature = new ActorDocument();
        ActorSheetSync.SeedFromBlueprint(creature, PitFiend(), Capped, Registry());

        var (sheet, evaluation) = Recomputed(creature);

        Assert.Equal(SheetRuleEngine.PrintedMethod, creature.Sheet[SheetRuleEngine.MethodKey]);
        Assert.Equal(26L, sheet["str"]);
        Assert.Empty(evaluation.Violations);
    }

    [Fact]
    public void ScoresTypedInByHand_StillStopAtTheCap()
    {
        var typed = new ActorDocument { Sheet = { [SheetRuleEngine.MethodKey] = "manual", [SheetRuleEngine.BasePrefix + "str"] = 26L } };

        var (_, evaluation) = Recomputed(typed);

        Assert.Equal("str", Assert.Single(evaluation.Violations, v => v.Key == "Actor.Rule.ScoreMax").FieldKey);
    }

    /// <summary>A point-buy fighter: Strength 15, +2 from their background, +2 from an Ability Score Improvement.</summary>
    private static ActorDocument Fighter() => new()
    {
        Sheet =
        {
            [SheetRuleEngine.MethodKey] = "point-buy",
            [SheetRuleEngine.BasePrefix + "str"] = 15L,
            [SheetRuleEngine.BasePrefix + "dex"] = 14L,
            ["background"] = "soldier",
            [SheetRuleEngine.BonusPrefix + "str"] = 2L,
            [SheetRuleEngine.BonusPrefix + "dex"] = 1L,
            ["feats"] = new List<object> { "asi|str:2" },
            ["str"] = 19L,
            ["notes"] = "kept"
        }
    };

    [Fact]
    public void TheFighter_IsStrength19_BeforeAnyRestat()
    {
        var (sheet, evaluation) = Recomputed(Fighter());

        Assert.Equal(19L, sheet["str"]);
        Assert.Equal(15L, sheet["dex"]);
        Assert.Empty(evaluation.Violations);
    }

    [Fact]
    public void RestattingACharacter_GivesTheBlocksScores_WithoutTheOldBonusesOrIncreasesOnTop()
    {
        var character = Fighter();

        ActorSheetSync.SeedFromBlueprint(character, PitFiend(), Capped, Registry());
        var (sheet, evaluation) = Recomputed(character);

        // 26 and 14, as printed: not the old 15 + 2 + 2, nor 26 + 2 + 2.
        Assert.Equal(26L, sheet["str"]);
        Assert.Equal(14L, sheet["dex"]);
        Assert.Equal(SheetRuleEngine.PrintedMethod, character.Sheet[SheetRuleEngine.MethodKey]);
        Assert.Empty(evaluation.Violations);
        Assert.Empty(evaluation.Increases);
        // The rest of the sheet stays, the picks that made the old scores included.
        Assert.Equal("kept", character.Sheet["notes"]);
        Assert.Equal(new List<object> { "asi|str:2" }, character.Sheet["feats"]);
        Assert.Equal(2L, character.Sheet[SheetRuleEngine.BonusPrefix + "str"]);
    }

    [Fact]
    public void LeavingPrinted_CountsTheBonusesAndIncreasesAgain()
    {
        var character = Fighter();
        ActorSheetSync.SeedFromBlueprint(character, PitFiend(), Capped, Registry());

        character.Sheet[SheetRuleEngine.MethodKey] = "manual";
        var (sheet, evaluation) = Recomputed(character);

        Assert.Equal(30L, sheet["str"]);
        Assert.Contains(evaluation.Violations, v => v.FieldKey == "str" && v.Key == "Actor.Rule.ScoreMax");
    }

    [Fact]
    public void WithoutASystem_ConventionalHpKeysStillDriveThePool()
    {
        var registry = Registry();
        var actor = new ActorDocument { Sheet = { ["HP"] = 4L, ["HP_Max"] = 9L } };

        ActorSheetSync.ApplySheetToState(actor, null, registry);

        var pool = registry.Hydrate(actor.State).OfType<ResourcePoolComponent>().Single();
        Assert.Equal(4, pool.Get("HP"));
        Assert.Equal(9, pool.Get("HP_Max"));
    }
}
