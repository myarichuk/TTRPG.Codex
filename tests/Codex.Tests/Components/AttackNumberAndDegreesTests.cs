using Bunit;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Tests.Reference;
using Codex.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests.Components;

/// <summary>The degrees fixture, loaded for the tests of P6: the multiple attack penalty as a roll input on the Actions panel, and the degree of a roll
/// made against a DC (docs/ROLL_DEGREES_DESIGN.md).</summary>
[CollectionDefinition(nameof(DegreesContent))]
public sealed class DegreesCollection : ICollectionFixture<DegreesContent>;

public sealed class DegreesContent : IAsyncLifetime
{
    public SystemDefinition System { get; private set; } = null!;
    public IEntryResolver Resolver { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var registry = new ContentRegistry();
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(registry));
        await loader.LoadAndInitializeAsync(Path.Combine(ReferenceFixtures.Root, "degrees"));
        if (loader.LoadedSystemIds.Count == 0) throw new InvalidOperationException(string.Join(" ", loader.FailedSystems.SelectMany(f => f.Errors)));
        System = loader.GetDefinition(loader.LoadedSystemIds.Single())!;
        Resolver = new PackResolver(registry, System.Id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The level 5 Vanguard of the degrees spec (Str +4, Dex +2) holding a plain staff and a knife with the featherlight trait.</summary>
    public static Dictionary<string, object> Hero(int frightened = 0)
    {
        var sheet = new Dictionary<string, object>
        {
            ["level"] = 5L, ["str"] = 18L, ["dex"] = 14L, ["con"] = 14L, ["int"] = 10L, ["wis"] = 12L, ["cha"] = 10L,
            ["ancestry"] = "hillfolk", ["class"] = "vanguard", ["acMode"] = "auto",
            ["inventory"] = new List<object> { "scale-coat|equipped:1", "oak-staff|equipped:1", "swift-knife|equipped:1" }
        };
        if (frightened > 0) sheet["condition_frightened"] = (long)frightened;
        return sheet;
    }

    public SheetEvaluation Evaluate(Dictionary<string, object> sheet) => new SheetRuleEngine(System, Resolver).Recompute(sheet);

    private sealed class PackResolver(ContentRegistry registry, string systemId) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries(systemId, type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries(systemId, type);
    }
}

[Collection(nameof(DegreesContent))]
public class MultipleAttackTests(DegreesContent content)
{
    private static MultipleAttackRules Rules => new()
    {
        Penalties = { 0, -5, -10 },
        Variants = { new MultipleAttackVariant { Property = "traits", Has = "featherlight", Penalties = { 0, -4, -8 } } }
    };

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, -5)]
    [InlineData(3, -10)]
    [InlineData(7, -10)]
    [InlineData(0, 0)]
    public void TheTableGivesTheRowOfTheAttackNumber_AndTheLastRowHoldsForLaterOnes(int attack, int expected) =>
        Assert.Equal(expected, MultipleAttack.Penalty(Rules, attack));

    [Theory]
    [InlineData(2, -4)]
    [InlineData(5, -8)]
    public void AWeaponTraitPicksTheVariantRow(int attack, int expected)
    {
        Assert.Equal(expected, MultipleAttack.Penalty(Rules, attack, name => name == "traits" ? new List<object> { "featherlight" } : null));
        // One word, or a comma list, counts the same; case does not matter.
        Assert.Equal(expected, MultipleAttack.Penalty(Rules, attack, name => name == "traits" ? "Reach, FeatherLight" : null));
    }

    [Fact]
    public void AWeaponWithoutTheTrait_OrNoRule_KeepsTheTable()
    {
        Assert.Equal(-5, MultipleAttack.Penalty(Rules, 2, name => name == "traits" ? new List<object> { "reach" } : null));
        Assert.Equal(0, MultipleAttack.Penalty(null, 3));
    }

    [Fact]
    public void ARuleWithNothingToAdd_IsAProblemTheLoaderSays()
    {
        Assert.NotEmpty(MultipleAttack.Problems(new MultipleAttackRules()));
        Assert.NotEmpty(MultipleAttack.Problems(new MultipleAttackRules { Penalties = { 0, 5 } }));
        Assert.Empty(MultipleAttack.Problems(Rules));
    }

    [Fact]
    public void TheFixtureSaysItInYaml()
    {
        var rules = content.System.SheetRules!.MultipleAttack!;
        Assert.Equal(new[] { 0, -5, -10 }, rules.Penalties);
        Assert.Equal(new[] { 0, -4, -8 }, Assert.Single(rules.Variants).Penalties);
        Assert.Equal("degreeCheck", content.System.Dice.CheckRoll);
        Assert.Empty(SheetRolls.Problems(content.System));
    }

    [Fact]
    public void ACheckRollThatIsNotThere_OrHasNoDcInput_IsAProblem()
    {
        var gone = new SystemDefinition { Id = "x", Dice = { CheckRoll = "nothing" } };
        Assert.Contains(SheetRolls.Problems(gone), p => p.Contains("nothing"));
        var bare = new SystemDefinition { Id = "y", Dice = { CheckRoll = "c" }, Rolls = { new RollDefinition { Key = "c", Dice = "1d20" } } };
        Assert.Contains(SheetRolls.Problems(bare), p => p.Contains("bonus and target"));
    }

    [Fact]
    public void TheSheetsAttackBonusCountsAConditionOnce_AndTheAttackPenaltyIsAddedByThePanelNotTheSheet()
    {
        var strike = (SheetAction a) => a.Entry.Id;
        var calm = DegreesContent.Hero();
        var scared = DegreesContent.Hero(frightened: 2);
        var calmStaff = content.Evaluate(calm).Actions.Single(a => strike(a) == "oak-staff").AttackBonus;
        var scaredStaff = content.Evaluate(scared).Actions.Single(a => strike(a) == "oak-staff").AttackBonus;
        Assert.Equal(11, calmStaff);
        Assert.Equal(9, scaredStaff); // Frightened 2, once: not 7.
        Assert.Equal(9, content.Evaluate(calm).Actions.Single(a => strike(a) == "swift-knife").AttackBonus);
    }
}

[Collection(nameof(DegreesContent))]
public class CheckGradeTests(DegreesContent content)
{
    private CheckGrade? Grade(double total, int natural, double dc) =>
        SheetRolls.Grade(content.System, content.Resolver, DegreesContent.Hero(), total, natural, dc);

    [Theory]
    [InlineData(20, 13, 20, "success", true)]
    [InlineData(19, 12, 20, "failure", false)]
    [InlineData(25, 18, 15, "criticalSuccess", true)]
    [InlineData(10, 3, 20, "criticalFailure", false)]
    [InlineData(27, 20, 30, "success", true)]   // A natural 20 moves a failure up one step.
    [InlineData(8, 1, 5, "failure", false)]      // A natural 1 moves a success down one step.
    public void TheSystemsCheckRollGradesTheTotal(double total, int natural, double dc, string outcome, bool passes)
    {
        var grade = Grade(total, natural, dc)!;
        Assert.Equal(outcome, grade.Outcome);
        Assert.Equal(passes, grade.Passed);
    }

    [Fact]
    public void ANaturalTwentyOnACriticalFailure_GoesUpToAFailure_AndWithoutACheckDieThereIsNoShift()
    {
        Assert.Equal("failure", Grade(10, 20, 30)!.Outcome); // 10 misses 30 by 20, a critical failure; the 20 lifts it one step.
        var entry = new Codex.Persistence.RollLogEntry { Total = 10, Kept = new List<int> { 20 }, CheckDieIndex = null };
        Codex.Persistence.Runtime.CheckGrading.Apply(entry, content.System, content.Resolver, DegreesContent.Hero(), 30);
        Assert.Equal("criticalFailure", entry.Outcome);
        Assert.Equal(30, entry.Dc);
    }

    [Fact]
    public void ACreatureWithAPickThatCarriesTheTrait_GetsTheLowerPenalty_FromThePickOnTheSheet()
    {
        var sheet = DegreesContent.Hero();
        sheet["trinkets"] = new List<object> { new Dictionary<string, object> { ["id"] = "rune-knife" } };
        double Total(string weapon, int attack) => SheetRolls.Roll(content.System, content.Resolver, sheet, new SheetRollRequest
        {
            Definition = "strike",
            Inputs = new Dictionary<string, object?> { ["bonus"] = 9d, ["target"] = 18d, ["attackNumber"] = (double)attack, ["weapon"] = weapon }
        }, new FixedDice(10)).Total;
        Assert.Equal(10 + 9 - 4, Total("rune-knife", 2)); // The knife is a pick on the sheet with the trait.
        Assert.Equal(10 + 9 - 5, Total("oak-staff", 2));
    }

    [Fact]
    public void ACreatureFrightenedByTwo_LowersTheNamedStrikeOnce_AndThePanelsBonusOnce()
    {
        var sheet = DegreesContent.Hero(frightened: 2);
        var request = new SheetRollRequest
        {
            Definition = "strike",
            Inputs = new Dictionary<string, object?> { ["bonus"] = 9d, ["target"] = 18d }
        };
        Assert.Equal(10 + 9 - 2, SheetRolls.Roll(content.System, content.Resolver, sheet, request, new FixedDice(10)).Total);
        // The panel's Strike reads `attack`, not the roll's own key: Frightened 2 on it is the 11 - 2 the evaluation already showed.
        Assert.Equal(9, content.Evaluate(sheet).Actions.Single(a => a.Entry.Id == "oak-staff").AttackBonus);
    }

    [Fact]
    public void ACriticalIsMarkedFromItsOutcome()
    {
        Assert.Equal("critical", Grade(25, 18, 15)!.Mark);
        Assert.Equal("fumble", Grade(10, 3, 20)!.Mark);
        Assert.Null(Grade(20, 13, 20)!.Mark);
    }

    [Fact]
    public void ASystemWithoutACheckRoll_HasNoDegrees()
    {
        var plain = new SystemDefinition { Id = "plain" };
        Assert.Null(SheetRolls.Grade(plain, NullEntryResolver.Instance, new Dictionary<string, object>(), 20, 10, 15));
    }

    [Fact]
    public void ARollMadeThroughTheDefinitionAndAGradedTotal_AgreeOnTheDegree()
    {
        var request = new SheetRollRequest
        {
            Definition = "degreeCheck",
            Inputs = new Dictionary<string, object?> { ["bonus"] = 7d, ["target"] = 30d }
        };
        var rolled = SheetRolls.Roll(content.System, content.Resolver, DegreesContent.Hero(), request, new FixedDice(20));
        var graded = Grade(rolled.Total, rolled.Natural!.Value, 30)!;
        Assert.Equal(rolled.Outcome, graded.Outcome);
    }

    [Fact]
    public void ARollLogEntryAgainstADc_GetsItsDegree_AndOneWithoutIsLeftAlone()
    {
        var entry = new Codex.Persistence.RollLogEntry { Total = 27, Kept = new List<int> { 20 }, CheckDieIndex = 0 };
        Codex.Persistence.Runtime.CheckGrading.Apply(entry, content.System, content.Resolver, DegreesContent.Hero(), 30);
        Assert.Equal("success", entry.Outcome);

        var none = new Codex.Persistence.RollLogEntry { Total = 27, Kept = new List<int> { 20 }, CheckDieIndex = 0 };
        Codex.Persistence.Runtime.CheckGrading.Apply(none, content.System, content.Resolver, DegreesContent.Hero(), null);
        Assert.Null(none.Outcome);

        var plain = new Codex.Persistence.RollLogEntry { Total = 27, Kept = new List<int> { 20 }, CheckDieIndex = 0 };
        Codex.Persistence.Runtime.CheckGrading.Apply(plain, new SystemDefinition { Id = "plain" }, NullEntryResolver.Instance, null, 30);
        Assert.Null(plain.Outcome);
    }

    [Theory]
    [InlineData("en", "criticalSuccess", "Critical success")]
    [InlineData("ru", "failure", "Провал")]
    [InlineData("he", "criticalFailure", "כישלון קריטי")]
    public void ADegreeIsSaidInWords_InTheReadersLanguage(string culture, string outcome, string words)
    {
        Assert.Equal(words, DegreeWords.Of(Localizer.For(culture), content.System, outcome));
        Assert.Null(DegreeWords.Of(Localizer.For(culture), new SystemDefinition { Id = "plain" }, outcome));
        Assert.Null(DegreeWords.Of(Localizer.For(culture), content.System, "partial"));
    }

    private sealed class FixedDice(params int[] faces) : Codex.Plugin.Abstractions.Dice.IDiceSource
    {
        private int _next;
        public int Roll(int sides) => faces[_next++ % faces.Length];
    }
}

[Collection(nameof(DegreesContent))]
public class ActionsPanelAttackNumberTests : ComponentTestBase
{
    private readonly DegreesContent content;

    public ActionsPanelAttackNumberTests(DegreesContent content) => this.content = content;

    private IRenderedComponent<ActionsPanel> Draw(string culture, List<ActionRoll> rolled, bool withSystem = true)
    {
        Services.AddSingleton(Localizer.For(culture));
        var evaluation = content.Evaluate(DegreesContent.Hero());
        return Render<ActionsPanel>(p => p
            .Add(x => x.Evaluation, evaluation)
            .Add(x => x.Value, _ => (int?)null)
            .Add(x => x.Definition, withSystem ? content.System : null)
            .Add(x => x.OnRoll, EventCallback.Factory.Create<ActionRoll>(this, r => rolled.Add(r))));
    }

    [Fact]
    public void TheControlIsLabelled_AndItsChoicesSayTheirPenalty()
    {
        var cut = Draw("en", new List<ActionRoll>());
        var select = cut.Find(Id("attack-number"));
        Assert.Equal("Attack this turn", cut.Find($"label[for='{select.Id}']").TextContent);
        Assert.Equal(new[] { "Attack 1 (no penalty)", "Attack 2 (⁦−5⁩)", "Attack 3 or later (⁦−10⁩)" }, select.QuerySelectorAll("option").Select(o => o.TextContent));
        Assert.False(string.IsNullOrEmpty(select.GetAttribute("aria-describedby")));
        Assert.NotNull(cut.Find($"#{select.GetAttribute("aria-describedby")}"));
    }

    [Fact]
    public void TheFirstAttackCarriesNoPenalty()
    {
        var rolled = new List<ActionRoll>();
        var cut = Draw("en", rolled);
        cut.Find(Id("action-use-weapon-oak-staff")).Click();
        var roll = Assert.Single(rolled);
        Assert.Equal(11, roll.CheckBonus);
        Assert.Equal(1, roll.AttackNumber);
        Assert.Equal("Oak staff: attack", roll.Label);
    }

    [Fact]
    public void TheAttackNumberReachesTheRoll_AndAWeaponTraitLowersThePenalty()
    {
        var rolled = new List<ActionRoll>();
        var cut = Draw("en", rolled);
        cut.Find(Id("attack-number")).Change("2");
        cut.Find(Id("action-use-weapon-oak-staff")).Click();
        cut.Find(Id("action-use-weapon-swift-knife")).Click();
        Assert.Equal(new int?[] { 11 - 5, 9 - 4 }, rolled.Select(r => r.CheckBonus));
        Assert.All(rolled, r => Assert.Equal(2, r.AttackNumber));
        Assert.Equal("Oak staff: attack 2", rolled[0].Label);

        cut.Find(Id("attack-number")).Change("3");
        cut.Find(Id("action-use-weapon-oak-staff")).Click();
        cut.Find(Id("action-use-weapon-swift-knife")).Click();
        Assert.Equal(new int?[] { 11 - 10, 9 - 8 }, rolled.Skip(2).Select(r => r.CheckBonus));
    }

    [Fact]
    public void TheNumberAnAttackIsAgainstReachesTheRoll_WhenGiven()
    {
        var rolled = new List<ActionRoll>();
        var cut = Draw("en", rolled);
        cut.Find(Id("action-use-weapon-oak-staff")).Click();
        Assert.Null(rolled[0].Dc);

        var against = cut.Find(Id("attack-against"));
        Assert.Equal("Against (armor class or DC, optional)", cut.Find($"label[for='{against.Id}']").TextContent);
        against.Change("18");
        cut.Find(Id("action-use-weapon-oak-staff")).Click();
        Assert.Equal(18, rolled[1].Dc);
    }

    [Fact]
    public void ASystemWithoutTheRule_ShowsNoControl_AndRollsAsBefore()
    {
        var rolled = new List<ActionRoll>();
        var cut = Draw("en", rolled, withSystem: false);
        Assert.Empty(cut.FindAll(Id("attack-controls")));
        cut.Find(Id("action-use-weapon-oak-staff")).Click();
        Assert.Equal(11, Assert.Single(rolled).CheckBonus);
    }

    [Theory]
    [InlineData("ru", "Атака в этот ход")]
    [InlineData("he", "התקפה בתור הזה")]
    public void TheControlIsInTheReadersLanguage(string culture, string label)
    {
        var cut = Draw(culture, new List<ActionRoll>());
        var select = cut.Find(Id("attack-number"));
        Assert.Equal(label, cut.Find($"label[for='{select.Id}']").TextContent);
        Assert.DoesNotContain(select.QuerySelectorAll("option"), o => o.TextContent.Contains("Attack"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("he")]
    public void ThePenaltyInAChoice_IsOneLeftToRightRun_SoItReadsRightInHebrew(string culture)
    {
        var cut = Draw(culture, new List<ActionRoll>());
        var options = cut.Find(Id("attack-number")).QuerySelectorAll("option").Select(o => o.TextContent).ToList();
        Assert.Contains("⁦−5⁩", options[1]);
        Assert.Contains("⁦−10⁩", options[2]);
        // The isolate opens and closes inside the choice, so the sign cannot drift to the other side of the digits.
        Assert.All(options, o => Assert.Equal(o.Count(c => c == '⁦'), o.Count(c => c == '⁩')));
    }

    [Fact]
    public void TheAttackNumberStartsOver_WhenTheCreaturesTurnComesRound_AndNotBefore()
    {
        var rolled = new List<ActionRoll>();
        Services.AddSingleton(Localizer.For("en"));
        var evaluation = content.Evaluate(DegreesContent.Hero());
        var cut = Render<ActionsPanel>(p => p.Add(x => x.Evaluation, evaluation).Add(x => x.Value, _ => (int?)null).Add(x => x.Definition, content.System)
            .Add(x => x.TurnKey, "1:0").Add(x => x.OnRoll, EventCallback.Factory.Create<ActionRoll>(this, r => rolled.Add(r))));
        cut.Find(Id("attack-number")).Change("3");
        cut.Find(Id("attack-against")).Change("18");

        // The same turn again (a refresh of the table): nothing changes.
        cut.Render(p => p.Add(x => x.TurnKey, "1:0"));
        cut.Find(Id("action-use-weapon-oak-staff")).Click();
        Assert.Equal((3, 1, (int?)18), (rolled[0].AttackNumber, rolled[0].CheckBonus, rolled[0].Dc));

        // Not their turn (null), then their next turn: back to the first attack. The "against" number was emptied by the roll above, so set it again to see the reset clear it.
        cut.Find(Id("attack-against")).Change("20");
        cut.Render(p => p.Add(x => x.TurnKey, null));
        cut.Render(p => p.Add(x => x.TurnKey, "2:0"));
        cut.Find(Id("action-use-weapon-oak-staff")).Click();
        Assert.Equal((1, 11, (int?)null), (rolled[1].AttackNumber, rolled[1].CheckBonus, rolled[1].Dc));
    }

    [Fact]
    public void TheNumberAnAttackWasAgainst_IsEmptiedOnceUsed()
    {
        var rolled = new List<ActionRoll>();
        var cut = Draw("en", rolled);
        cut.Find(Id("attack-against")).Change("18");
        cut.Find(Id("action-use-weapon-oak-staff")).Click();
        cut.Find(Id("action-use-weapon-oak-staff")).Click();
        Assert.Equal(new int?[] { 18, null }, rolled.Select(r => r.Dc));
    }

    [Fact]
    public async Task ASystemThatHasNeitherKey_ShowsNoControlAndRollsAsBefore()
    {
        var registry = new ContentRegistry();
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(registry));
        await loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        var srd5e = loader.GetDefinition("srd5e")!;
        Assert.Null(srd5e.Dice.CheckRoll);
        Assert.Null(srd5e.SheetRules!.MultipleAttack);

        var rolled = new List<ActionRoll>();
        Services.AddSingleton(Localizer.For("en"));
        var cut = Render<ActionsPanel>(p => p.Add(x => x.Evaluation, content.Evaluate(DegreesContent.Hero())).Add(x => x.Value, _ => (int?)null)
            .Add(x => x.Definition, srd5e).Add(x => x.OnRoll, EventCallback.Factory.Create<ActionRoll>(this, r => rolled.Add(r))));
        Assert.Empty(cut.FindAll(Id("attack-controls")));
        cut.Find(Id("action-use-weapon-oak-staff")).Click();
        var roll = Assert.Single(rolled);
        Assert.Equal((11, 1, (int?)null), (roll.CheckBonus, roll.AttackNumber, roll.Dc));
    }

    [Fact]
    public void ADegreeIsShownInWords_WithTheNumberItWasAgainst()
    {
        Services.AddSingleton(Localizer.For("he"));
        var cut = Render<DiceResult>(p => p.Add(x => x.Total, 19).Add(x => x.Outcome, "הצלחה").Add(x => x.Mark, (string?)null).Add(x => x.Dc, 18)
            .Add(x => x.Kept, new List<int> { 12 }));
        var pill = cut.Find(Id("dice-outcome"));
        Assert.Equal("הצלחה", pill.TextContent);
        Assert.False(string.IsNullOrEmpty(pill.GetAttribute("aria-label")));
        Assert.Contains("18", cut.Find(Id("dice-against")).TextContent);
    }

    [Fact]
    public void TheEventFeedSaysADegreeInTheReadersLanguage()
    {
        var term = Codex.Persistence.EventTerm.Degree("criticalFailure");
        Assert.Equal("critical failure", term.Text);
        foreach (var (culture, words) in new[] { ("en", "Critical failure"), ("ru", "Критический провал"), ("he", "כישלון קריטי") })
        {
            var said = Codex.Web.Services.EventTerms.For(content.System, Localizer.For(culture), new Dictionary<string, string>())(term.Kind, term.Key);
            Assert.Equal(words, said);
        }
    }
}
