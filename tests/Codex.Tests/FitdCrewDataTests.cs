using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>RB3 and CL3 on the real Forged in the Dark pack: the crew's upgrades and abilities reach a linked scoundrel (docs/CREW_LINKS_DESIGN.md),
/// and the story-decided bonus dice are tick boxes on the roll (docs/ROLL_BONUSES_DESIGN.md). The entries come from the importer's reviewed table.</summary>
public class FitdCrewDataTests : IAsyncLifetime
{
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "srd-fitd-2017" };

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private SystemDefinition _system = null!;
    private IEntryResolver _resolver = null!;

    public FitdCrewDataTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        _system = _loader.GetDefinition("fitd-srd")!;
        _resolver = new PackResolver(_registry);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class PackResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("fitd-srd", type, Packs).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("fitd-srd", type, Packs);
    }

    private sealed class Actors(params (string Id, Dictionary<string, object> Sheet)[] sheets) : ILinkedSheets
    {
        public IReadOnlyDictionary<string, object>? Sheet(string actorId) => sheets.FirstOrDefault(s => s.Id == actorId).Sheet;
    }

    private static List<object> Picks(params object[] picks) => picks.ToList();

    private static Dictionary<string, object> Crew(string[] upgrades, params string[] abilities) =>
        new() { ["kind"] = "crew", ["upgrades"] = Picks(upgrades.Cast<object>().ToArray()), ["abilities"] = Picks(abilities.Cast<object>().ToArray()) };

    private static Dictionary<string, object> Scoundrel(params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object>
        {
            ["kind"] = "scoundrel",
            ["link__crew"] = "crew-1",
            ["actions"] = new Dictionary<string, object> { ["prowl"] = new Dictionary<string, object> { ["rating"] = 1L }, ["hunt"] = new Dictionary<string, object> { ["rating"] = 1L } }
        };
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    private static Actors WithCrew(Dictionary<string, object> crew) => new(("crew-1", crew));

    private SheetEvaluation Evaluate(Dictionary<string, object> sheet, ILinkedSheets? linked) => new SheetRuleEngine(_system, _resolver).Evaluate(sheet, linked);

    private static double Value(SheetEvaluation evaluation, string key) => Convert.ToDouble(evaluation.Model!.Values[key], System.Globalization.CultureInfo.InvariantCulture);

    private SheetRollResult Action(Dictionary<string, object> sheet, ILinkedSheets? linked, string action, params string[] ticked) =>
        SheetRolls.Roll(_system, _resolver, sheet, new SheetRollRequest { Definition = "action", Inputs = new Dictionary<string, object?> { ["action"] = action }, OptIns = ticked },
            new SequenceDiceSource(new[] { 3, 3, 3, 3, 3, 3 }), linked);

    [Fact]
    public void Composed_GivesALinkedScoundrelTenStressBoxes_AndAnUnlinkedOneNine()
    {
        var linked = WithCrew(Crew(new[] { "composed" }));
        Assert.Equal(10, Value(Evaluate(Scoundrel(), linked), "stress_Max"));
        Assert.Equal(9, Value(Evaluate(Scoundrel(), WithCrew(Crew(Array.Empty<string>()))), "stress_Max"));
        // A crew that is gone is unknown, not empty: the size is not worked out (nothing is clamped), until the link changes.
        Assert.True(Evaluate(Scoundrel(), new Actors()).LinksUnknown);
        Assert.Equal(9, Value(Evaluate(new Dictionary<string, object> { ["kind"] = "scoundrel" }, null), "stress_Max"));
    }

    [Fact]
    public void Hardened_MovesTheTraumaSizeAndTheRetiredThreshold()
    {
        var linked = WithCrew(Crew(new[] { "hardened" }));
        Assert.Equal(5, Value(Evaluate(Scoundrel(), linked), "trauma_Max"));

        var sheet = Scoundrel(("trauma", 4L));
        Assert.DoesNotContain("Retired", Evaluate(sheet, linked).Model!.Conditions);
        Assert.Contains("Retired", Evaluate(Scoundrel(("trauma", 4L)), new Actors()).Model!.Conditions);
    }

    [Fact]
    public void Mastery_LiftsTheActionCap_SoARatingOfFourIsAllowed()
    {
        var four = new Dictionary<string, object> { ["prowl"] = new Dictionary<string, object> { ["rating"] = 4L } };
        var without = Evaluate(Scoundrel(("actions", four)), WithCrew(Crew(Array.Empty<string>())));
        Assert.Contains(without.Violations, v => v.Key == "Actor.Rule.ActionCap");
        var with = Evaluate(Scoundrel(("actions", four)), WithCrew(Crew(new[] { "mastery" })));
        Assert.DoesNotContain(with.Violations, v => v.Key == "Actor.Rule.ActionCap");
        Assert.Equal(4, Value(with, "actionCap"));
    }

    [Fact]
    public void Deadly_IsEachMembersOwnPick_AndRaisesThatActionsRatingByOne()
    {
        var linked = WithCrew(Crew(Array.Empty<string>(), "deadly"));

        var open = Evaluate(Scoundrel(), linked);
        var pending = Assert.Single(open.PendingChoices, c => c.Key == "choice__deadly__deadly");
        Assert.Equal(new[] { "Hunt", "Prowl", "Skirmish" }, pending.Options.Select(o => o.Label));

        var prowl = Scoundrel(("choice__deadly__deadly", Picks("Prowl")));
        var hunt = Scoundrel(("choice__deadly__deadly", Picks("Hunt")));
        Assert.Equal(2, Value(Evaluate(prowl, linked), "actions.prowl"));
        Assert.Equal(1, Value(Evaluate(prowl, linked), "actions.hunt"));
        Assert.Equal(2, Value(Evaluate(hunt, linked), "actions.hunt"));
        Assert.Equal(1, Value(Evaluate(hunt, linked), "actions.prowl"));

        // The roll reads the raised rating: a die per dot.
        Assert.Equal(2, Action(prowl, linked, "prowl").Dice.Count);
        Assert.Equal(1, Action(hunt, linked, "prowl").Dice.Count);
    }

    [Fact]
    public void Deadly_APickThatPassesThree_BreaksTheCapRule()
    {
        var three = new Dictionary<string, object> { ["prowl"] = new Dictionary<string, object> { ["rating"] = 3L } };
        var sheet = Scoundrel(("actions", three), ("choice__deadly__deadly", Picks("Prowl")));
        Assert.Contains(Evaluate(sheet, WithCrew(Crew(Array.Empty<string>(), "deadly"))).Violations, v => v.Key == "Actor.Rule.ActionCap");
    }

    [Fact]
    public void ForgedInTheFire_AddsADieToResistance_AndAnointedIsOnlyATickBox()
    {
        var forged = WithCrew(Crew(Array.Empty<string>(), "forged-in-the-fire"));
        var sheet = Scoundrel();
        Dictionary<string, object?> Inputs() => new() { ["attribute"] = "resolve" };

        Assert.Empty(SheetRolls.OptIns(_system, _resolver, sheet, "resistance", Inputs(), forged));
        var rolled = SheetRolls.Roll(_system, _resolver, sheet, new SheetRollRequest { Definition = "resistance", Inputs = Inputs() }, new SequenceDiceSource(new[] { 3, 3, 3, 3 }), forged);
        Assert.Single(rolled.Bonuses);
        Assert.Single(rolled.Dice); // no resolve action has a dot: the crew's die is the whole pool

        var anointed = WithCrew(Crew(Array.Empty<string>(), "anointed"));
        var offered = Assert.Single(SheetRolls.OptIns(_system, _resolver, sheet, "resistance", Inputs(), anointed));
        Assert.Equal("Against a supernatural threat", offered.Label);
        Assert.Empty(SheetRolls.OptIns(_system, _resolver, sheet, "action", new Dictionary<string, object?> { ["action"] = "hunt" }, anointed));
    }

    [Fact]
    public void AmbushIsOfferedOnAnyActionRoll_SavageOnlyOnCommand_WeavingTheWebOnlyOnConsort()
    {
        List<string> Offered(string ability, string action) =>
            SheetRolls.OptIns(_system, _resolver, Scoundrel(("abilities", Picks(ability))), "action", new Dictionary<string, object?> { ["action"] = action }).Select(o => o.Label).ToList();

        Assert.Single(Offered("ambush", "skirmish"));
        Assert.Single(Offered("ambush", "prowl"));
        Assert.Single(Offered("savage", "command"));
        Assert.Empty(Offered("savage", "skirmish"));
        Assert.Single(Offered("weaving-the-web", "consort"));
        Assert.Empty(Offered("weaving-the-web", "hunt"));
        Assert.Single(Offered("trust-in-me", "sway"));
        Assert.Single(Offered("scout", "prowl"));
        Assert.Equal("Protecting a teammate", Assert.Single(SheetRolls.OptIns(_system, _resolver, Scoundrel(("abilities", Picks("bodyguard"))), "resistance", new Dictionary<string, object?> { ["attribute"] = "prowess" })).Label);
    }

    [Fact]
    public void ATickedAmbush_AddsADieToASkirmishRoll()
    {
        var sheet = Scoundrel(("abilities", Picks("ambush")), ("actions", new Dictionary<string, object> { ["skirmish"] = new Dictionary<string, object> { ["rating"] = 2L } }));
        var offered = Assert.Single(SheetRolls.OptIns(_system, _resolver, sheet, "action", new Dictionary<string, object?> { ["action"] = "skirmish" }));
        Assert.Equal(2, Action(sheet, null, "skirmish").Dice.Count);
        Assert.Equal(3, Action(sheet, null, "skirmish", offered.Id).Dice.Count);
    }
}
