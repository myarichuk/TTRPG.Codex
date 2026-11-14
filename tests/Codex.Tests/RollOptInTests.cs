using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Tests;

/// <summary>Opt-in modifiers on the named rolls (docs/ROLL_BONUSES_DESIGN.md, RB1a), on the real Forged in the Dark system: tick boxes that count
/// in a roll only when ticked, what a roll offers, what ticking spends and ends, and the checks that refuse the shapes that cannot work.</summary>
public class RollOptInTests
{
    private sealed class Entries(params ContentEntry[] entries) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => entries.Where(e => e.Type == type);
    }

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    private static ContentEntry Ability(string id, params (string Key, object Value)[] properties)
    {
        var entry = new ContentEntry { Type = "special-ability", Id = id, Name = id, SystemId = "fitd-srd", PackId = "test", Description = id };
        foreach (var (key, value) in properties) entry.Properties[key] = value;
        return entry;
    }

    private static Dictionary<string, object> OptIn(string target, string label, string? when = null, Dictionary<string, object>? spends = null, bool ends = false)
    {
        var optIn = Map(("label", label));
        if (spends != null) optIn["spends"] = spends;
        if (ends) optIn["ends"] = true;
        var modifier = Map(("target", target), ("amount", 1L), ("optIn", optIn));
        if (when != null) modifier["when"] = when;
        return modifier;
    }

    private static readonly ContentEntry[] All =
    {
        Ability("ambush", ("modifiers", Items(OptIn("@pools", "Attacking from hiding", "input.action == 'prowl'")))),
        Ability("anointed", ("modifiers", Items(OptIn("@resistance", "Against a supernatural threat")))),
        Ability("lucky", ("modifiers", Items(OptIn("@pools", "Lucky charm", spends: Map(("key", "lucky"), ("amount", 1L)))))),
        Ability("iron-will", ("modifiers", Items(Map(("target", "@resistance"), ("amount", 1L), ("when", "input.attribute == 'resolve'"))))),
    };

    private static SystemDefinition Load() => SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "fitd-srd", "system.yaml"));

    private static readonly SystemDefinition Fitd = Load();

    private static Dictionary<string, object> Scoundrel(params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object>
        {
            ["kind"] = "scoundrel",
            ["actions"] = Map(("prowl", Map(("rating", 1L))), ("hunt", Map(("rating", 1L))), ("attune", Map(("rating", 1L))), ("command", Map(("rating", 1L))))
        };
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    private static SheetRollResult Action(SystemDefinition system, Dictionary<string, object> sheet, string action, params string[] ticked) =>
        SheetRolls.Roll(system, new Entries(All), sheet, new SheetRollRequest { Definition = "action", Inputs = new Dictionary<string, object?> { ["action"] = action }, OptIns = ticked },
            new SequenceDiceSource(new[] { 3, 3, 3, 3, 3 }));

    private static IReadOnlyList<RollOptIn> Offered(string roll, Dictionary<string, object> sheet, params (string, object?)[] inputs) =>
        SheetRolls.OptIns(Fitd, new Entries(All), sheet, roll, inputs.ToDictionary(i => i.Item1, i => i.Item2));

    [Fact]
    public void ARollOffersOnlyTheOptIns_ItsTargetsReach_AndWhoseWhenHolds()
    {
        var sheet = Scoundrel(("abilities", Items("ambush", "anointed", "iron-will")));

        var prowl = Offered("action", sheet, ("action", "prowl"));
        var ambush = Assert.Single(prowl);
        Assert.Equal("ambush#0", ambush.Id);
        Assert.Equal("Attacking from hiding", ambush.Label);
        Assert.Equal("+1", ambush.Amount);
        Assert.Equal("ambush", ambush.Source);
        Assert.Null(ambush.Spends);
        Assert.True(ambush.CanPay);
        Assert.Null(ambush.Ends);

        Assert.Empty(Offered("action", sheet, ("action", "hunt"))); // its when names another action
        Assert.Equal("anointed#0", Assert.Single(Offered("resistance", sheet, ("attribute", "insight"))).Id); // a resistance roll reads @resistance only
        Assert.Empty(Offered("group", sheet));
    }

    [Fact]
    public void ATickedOptIn_AddsItsDie_AndAnUntickedOneDoesNot()
    {
        var sheet = Scoundrel(("abilities", Items("ambush")));

        var plain = Action(Fitd, sheet, "prowl");
        Assert.Single(plain.Dice);
        Assert.Empty(plain.Bonuses);

        var ticked = Action(Fitd, sheet, "prowl", "ambush#0");
        Assert.Equal(2, ticked.Dice.Count);
        var bonus = Assert.Single(ticked.Bonuses);
        Assert.Equal(new RollBonus("ambush#0", "ambush", "Attacking from hiding", 1, true), bonus);
    }

    [Fact]
    public void AnOptInOnAnotherTarget_IsNeitherOffered_NorCounted()
    {
        var sheet = Scoundrel(("abilities", Items("anointed")));
        Assert.Empty(Offered("action", sheet, ("action", "prowl")));
        Assert.Single(Action(Fitd, sheet, "prowl", "anointed#0").Dice); // ticking an id the roll does not offer does nothing
        Assert.Empty(Action(Fitd, sheet, "prowl", "anointed#0").Bonuses);
    }

    [Fact]
    public void AnOptInWhoseWhenFails_IsNotCounted_EvenTicked()
    {
        var sheet = Scoundrel(("abilities", Items("ambush")));
        Assert.Single(Action(Fitd, sheet, "hunt", "ambush#0").Dice);
    }

    [Fact]
    public void IronWill_StillCountsWithNoTick_AndIsNotATickBox()
    {
        var sheet = Scoundrel(("abilities", Items("iron-will")));
        var rolled = SheetRolls.Roll(Fitd, new Entries(All), sheet, new SheetRollRequest { Definition = "resistance", Inputs = new Dictionary<string, object?> { ["attribute"] = "resolve" } },
            new SequenceDiceSource(new[] { 3, 3, 3, 3 }));

        Assert.Equal(3, rolled.Dice.Count);
        Assert.Equal(new RollBonus(null, "iron-will", "iron-will", 1, false), Assert.Single(rolled.Bonuses));
        Assert.Empty(Offered("resistance", sheet, ("attribute", "resolve")));
    }

    [Fact]
    public void ATickedSpend_IsAnAdjustIntent_ThatTheSheetApplies()
    {
        var sheet = Scoundrel(("abilities", Items("lucky")), ("uses_lucky", 2L));

        var offer = Assert.Single(Offered("action", sheet, ("action", "prowl")));
        Assert.Equal(new RollOptInSpend("uses_lucky", 1), offer.Spends);
        Assert.True(offer.CanPay);

        var ticked = Action(Fitd, sheet, "prowl", "lucky#0");
        var intent = Assert.Single(ticked.AllIntents());
        Assert.Equal("adjust", intent.Kind);
        Assert.Equal("uses_lucky", intent.Fields["track"]);
        Assert.Equal(-1d, intent.Fields["by"]);
        Assert.Empty(Action(Fitd, sheet, "prowl").Intents); // an unticked one costs nothing

        var applied = SheetIntents.Apply(Fitd, new Entries(All), sheet, ticked.AllIntents());
        Assert.Empty(applied.Left);
        Assert.Equal(1L, sheet["uses_lucky"]);
    }

    [Fact]
    public void AnEmptyPool_IsNotPayable_AndAPaidRollIsRefused()
    {
        var sheet = Scoundrel(("abilities", Items("lucky")), ("uses_lucky", 0L));
        Assert.False(Assert.Single(Offered("action", sheet, ("action", "prowl"))).CanPay);
        Assert.Throws<RollException>(() => Action(Fitd, sheet, "prowl", "lucky#0"));
        Assert.Single(Action(Fitd, sheet, "prowl").Dice); // unticked it still rolls
    }

    [Fact]
    public void ATickedEnds_RemovesTheCondition_ThroughAnIntent()
    {
        var system = Load();
        system.ConditionEffects["Edge"] = new ConditionEffectDefinition
        {
            Modifiers = { new ModifierDefinition { Target = "@pools", Amount = "1", Source = "Edge", OptIn = new OptInDefinition { Label = "Spend the edge", Ends = true } } }
        };
        Assert.Empty(SystemDefinitionLoader.Validate(system));
        var sheet = Scoundrel(("conditions", Items("Edge")));

        var offer = Assert.Single(SheetRolls.OptIns(system, new Entries(All), sheet, "action", new Dictionary<string, object?> { ["action"] = "prowl" }));
        Assert.Equal("condition:Edge#0", offer.Id);
        Assert.Equal("Edge", offer.Ends);

        Assert.Empty(Action(system, sheet, "prowl").Intents);
        var ticked = Action(system, sheet, "prowl", "condition:Edge#0");
        Assert.Equal(2, ticked.Dice.Count);
        var intent = Assert.Single(ticked.AllIntents());
        Assert.Equal("condition", intent.Kind);
        Assert.Equal("Edge", intent.Fields["remove"]);

        SheetIntents.Apply(system, new Entries(All), sheet, ticked.AllIntents());
        Assert.Empty(SheetRuleEngine.RawList(sheet, SheetModel.ConditionsKey));
    }

    [Fact]
    public void AnOptIn_NeverCountsOnTheSheet()
    {
        var sheet = Scoundrel(("abilities", Items("ambush")));
        var model = new SheetModel(Fitd, new Entries(All), sheet).Evaluate();
        Assert.False(model.Modifiers.TryGetValue("modifiers:@pools", out var applied) && applied.Count > 0);
    }

    [Fact]
    public void TheIdOfALinkedFeature_NamesItsLink()
    {
        Assert.Equal("ambush#2", OptInIds.Feature("ambush", 2));
        Assert.Equal("condition:Harm#0", OptInIds.Condition("Harm", 0));
        Assert.Equal("link:crew:anointed#0", OptInIds.Link("crew", "anointed", 0));
    }

    [Fact]
    public void OptIn_RoundTripsThroughTheGrantEditor()
    {
        var feature = Map(("modifiers", Items(OptIn("@pools", "Lucky charm", "input.action == 'prowl'", Map(("key", "lucky"), ("amount", 2L)), ends: false))));
        var data = FeatureGrantData.Read(feature);
        var optIn = data.Modifiers.Single().OptIn;
        Assert.NotNull(optIn);
        Assert.Equal("Lucky charm", optIn.Label);
        Assert.Equal("lucky", optIn.SpendKey);
        Assert.Equal("2", optIn.SpendAmount);
        Assert.False(optIn.Ends);

        var written = new Dictionary<string, object>();
        data.Apply(written);
        var again = FeatureGrantData.Read(written).Modifiers.Single();
        Assert.Equal("input.action == 'prowl'", again.When);
        Assert.Equal("lucky", again.OptIn!.SpendKey);

        var parsed = SheetRuleEngine.ParseOptIn(Map(("label", "x"), ("ends", true), ("spends", Map(("key", "k")))));
        Assert.True(parsed!.Ends);
        Assert.Equal("1", parsed.Spends!.Amount);
    }

    [Fact]
    public void TheChecks_RefuseAnEndsOutsideACondition_AndADiceAmountInAPoolRoll()
    {
        var kind = Fitd.SheetKinds.First();
        var system = Load();
        system.SheetKinds.First().Modifiers.Add(new ModifierDefinition { Target = "@pools", Amount = "1d4" });
        system.SheetKinds.First().Modifiers.Add(new ModifierDefinition { Target = "@pools", Amount = "1", OptIn = new OptInDefinition { Ends = true } });
        system.SheetKinds.First().Modifiers.Add(new ModifierDefinition { Target = "specialArmorBoxes", Amount = "1d4" }); // no pool roll reads it
        system.SheetKinds.First().Modifiers.Add(new ModifierDefinition { Target = "@pools", Amount = "1", OptIn = new OptInDefinition { Spends = new OptInSpendDefinition { Key = "x", Amount = "ask" } } });
        var problems = SystemDefinitionLoader.Validate(system).ToList();
        Assert.Contains(problems, p => p.Contains("modifiers[0]") && p.Contains("number of dice"));
        Assert.Contains(problems, p => p.Contains("modifiers[1]") && p.Contains("\"ends\" only works on a condition's effect"));
        Assert.DoesNotContain(problems, p => p.Contains("modifiers[2]"));
        Assert.Contains(problems, p => p.Contains("modifiers[3]") && p.Contains("whole number"));
        Assert.NotNull(kind);

        // A feature an srd5e subclass carries: its rows are checked as the draft check does, and an ends has no condition to end there.
        var srd = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        var row = Map(("name", "Edge"), ("level", 3L), ("modifiers", Items(Map(("target", "attack"), ("amount", 1L), ("optIn", Map(("ends", true)))))));
        var entry = new ContentEntry { Type = "subclass", Id = "edge", Name = "Edge", SystemId = "srd5e", PackId = "homebrew" };
        entry.Properties["class"] = "sorcerer";
        entry.Properties["features"] = Items(row);
        Assert.Contains(FeatureGrantCheck.Problems(srd, entry), p => p.Contains("\"ends\" only works on a condition's effect"));
        row["modifiers"] = Items(OptIn("attack", "Fine", spends: Map(("key", "lucky"), ("amount", 1L))));
        Assert.Empty(FeatureGrantCheck.Problems(srd, entry));
    }
}
