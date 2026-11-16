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
        Assert.Equal(new[] { "Attack 1 (no penalty)", "Attack 2 (−5)", "Attack 3 or later (−10)" }, select.QuerySelectorAll("option").Select(o => o.TextContent));
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
}
