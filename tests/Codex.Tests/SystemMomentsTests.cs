using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The one resolver for a system's moments (<see cref="SystemMoments"/>): rests, declared moments, initiative and dawn.</summary>
public class SystemMomentsTests
{
    private static SystemDefinition Fitd(string moments = "[{ key: downtime, label: Downtime }, { key: loadChosen, label: Load chosen, onSet: load }]", string extra = "") =>
        SystemDefinitionLoader.Parse($$"""
            id: moment-test
            name: Moment test
            moments: {{moments}}
            sheetKinds:
              - kind: hero
                values:
                  - { key: load, input: true, default: 1 }
                  - { key: notInput, formula: "2" }
            {{extra}}
            """);

    private static SystemDefinition WithRests(string moments = "[]") => SystemDefinitionLoader.Parse($$"""
        id: rest-test
        name: Rest test
        moments: {{moments}}
        vitals:
          rests: [{ name: Long Rest }, { name: Short Rest }]
        """);

    [Fact]
    public void TheResolver_ListsRests_ThenDeclared_ThenInitiativeAndDawn()
    {
        var system = WithRests("[{ key: downtime, label: Downtime }]");

        var moments = SystemMoments.Of(system);

        Assert.Equal(new[] { "Long Rest", "Short Rest", "downtime", "initiative", "dawn" }, moments.Select(m => m.Key));
        Assert.Equal(new[] { MomentKind.Rest, MomentKind.Rest, MomentKind.Declared, MomentKind.Initiative, MomentKind.Dawn }, moments.Select(m => m.Kind));
        Assert.Equal("Downtime", moments[2].Label);
    }

    [Fact]
    public void ASystemWithNoMoments_HasExactlyInitiativeAndDawnBesidesItsRests()
    {
        Assert.Equal(new[] { "initiative", "dawn" }, SystemMoments.Of(null).Select(m => m.Key));
        Assert.Equal(new[] { "initiative", "dawn", "manual" }, SystemMoments.RefillWords(WithRests()));
        Assert.Equal(new[] { "Long Rest", "Short Rest", "initiative", "dawn" }, SystemMoments.ChangeMoments(WithRests()));
    }

    [Fact]
    public void IsRest_IsFalseForMomentsAndManual_AndTrueForAnyOtherName()
    {
        var system = WithRests("[{ key: downtime }]");

        Assert.False(SystemMoments.IsRest(system, "downtime"));
        Assert.False(SystemMoments.IsRest(system, "DAWN"));
        Assert.False(SystemMoments.IsRest(system, "initiative"));
        Assert.False(SystemMoments.IsRest(system, "manual"));
        Assert.True(SystemMoments.IsRest(system, "Long Rest"));
        Assert.True(SystemMoments.IsRest(null, "downtime")); // without the system there is no way to know it is declared
    }

    [Fact]
    public void ADeclaredMomentWithoutALabel_IsShownByItsKey_AndOnSetIsFound()
    {
        var system = Fitd("[{ key: downtime }, { key: loadChosen, onSet: load }]");

        Assert.Equal("downtime", SystemMoments.Find(system, "downtime")!.Label);
        Assert.Equal("loadChosen", SystemMoments.FiredBySetting(system, "load")!.Key);
        Assert.Null(SystemMoments.FiredBySetting(system, "other"));
    }

    [Fact]
    public void ALocalizedSystem_ShowsTheTranslatedMomentLabel()
    {
        var system = Fitd();
        system.Translations["ru"] = new SystemTranslation { Moments = { ["downtime"] = "Передышка" } };

        var shown = system.Localize("ru");

        Assert.Equal("Передышка", SystemMoments.Find(shown, "downtime")!.Label);
        Assert.Equal("Load chosen", SystemMoments.Find(shown, "loadChosen")!.Label);
        Assert.Equal("Downtime", SystemMoments.Find(system, "downtime")!.Label);
    }

    [Theory]
    [InlineData("[{ key: dawn }]")]
    [InlineData("[{ key: Initiative }]")]
    [InlineData("[{ key: manual }]")]
    [InlineData("[{ key: long rest }]")]
    [InlineData("[{ key: Long Rest }]")]
    [InlineData("[{ key: a }, { key: A }]")]
    [InlineData("[{ label: No key }]")]
    [InlineData("[{ key: 'bad|key' }]")]
    public void TheLoader_RefusesAKeyThatClashesOrIsBad(string moments)
    {
        Assert.Throws<InvalidDataException>(() => WithRests(moments));
    }

    [Fact]
    public void TheLoader_RefusesAnOnSetThatIsNotAnInputValue()
    {
        var notInput = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => Fitd("[{ key: a, onSet: notInput }]")));
        var unknown = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => Fitd("[{ key: a, onSet: nothing }]")));

        Assert.Contains(notInput, e => e.Contains("onSet 'notInput'"));
        Assert.Contains(unknown, e => e.Contains("onSet 'nothing'"));
        Assert.Empty(SystemDefinitionLoader.Validate(Fitd()));
    }

    [Fact]
    public void ChangesOn_AcceptsADeclaredMomentOnASystemThatDeclaresIt_AndRefusesItOnOneThatDoesNot()
    {
        var choose = new RowChoose { Id = "x", ChangesOn = new() { "downtime" } };

        Assert.Empty(SystemDefinitionLoader.CheckRowChoose(Fitd(), choose, "here"));
        Assert.Contains(SystemDefinitionLoader.CheckRowChoose(WithRests(), choose, "here"), e => e.Contains("changesOn 'downtime'"));
        Assert.Empty(SystemDefinitionLoader.CheckRowChoose(WithRests(), new RowChoose { Id = "x", ChangesOn = new() { "Long Rest", "dawn", "initiative" } }, "here"));
    }

    [Fact]
    public void TheSrd5eSystem_GetsTodaysMoments()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

        Assert.Empty(system.Moments);
        Assert.Equal(new[] { "initiative", "dawn", "manual" }, FeatureGrantVocabulary.For(system)!.Moments);
        Assert.Equal(new[] { "Short Rest", "Long Rest" }, SystemMoments.Of(system).Where(m => m.IsRest).Select(m => m.Key));
    }

    private static SystemDefinition Kinds(string events) => SystemDefinitionLoader.Parse($$"""
        id: matters-test
        name: Matters test
        moments: [{ key: downtime }, { key: loadChosen }, { key: scripted }]
        sheetKinds:
          - kind: scoundrel
            tracks: [{ key: armor, max: 3 }, { key: stress, max: 9 }]
          - kind: crew
            tracks: [{ key: rep, max: 12 }]
        events: {{events}}
        """);

    private static Dictionary<string, object> Sheet(string kind, params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object> { ["kind"] = kind };
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    [Fact]
    public void AHandler_TouchesTheKindsWhoseTracksItNamesByLiteral_AndNoOther()
    {
        var system = Kinds("""
            { loadChosen: [{ intents: [{ set: { track: armor, to: 0 } }] }], downtime: [{ intents: [{ adjust: { track: rep, by: 1 } }, { log: { key: x } }] }] }
            """);

        Assert.True(SystemMoments.Matters(system, Sheet("scoundrel"), "loadChosen"));
        Assert.False(SystemMoments.Matters(system, Sheet("crew"), "loadChosen"));
        Assert.True(SystemMoments.Matters(system, Sheet("crew"), "downtime"));
        Assert.False(SystemMoments.Matters(system, Sheet("scoundrel"), "downtime"));
        Assert.Equal(new[] { "armor" }, SystemMoments.HandlerTracks(system, "loadChosen"));
    }

    [Fact]
    public void ACampaignLine_AndAMoment_WithNoHandler_TouchNoSheet()
    {
        var system = Kinds("{ downtime: [{ target: campaign.clocks, intents: [{ adjust: { track: armor, by: 1 } }] }] }");

        Assert.False(SystemMoments.Matters(system, Sheet("scoundrel"), "downtime"));
        Assert.False(SystemMoments.Matters(system, Sheet("scoundrel"), "loadChosen"));
        Assert.Empty(SystemMoments.HandlerTracks(system, "loadChosen")!);
    }

    [Fact]
    public void AScriptHandler_CannotBeRead_SoItTouchesEveryKind()
    {
        var system = Kinds("{ scripted: { script: 'rules.js#go' } }");

        Assert.Null(SystemMoments.HandlerTracks(system, "scripted"));
        Assert.True(SystemMoments.Matters(system, Sheet("crew"), "scripted"));
    }

    private static SystemDefinition Actors(string moments = "[{ key: downtime }, { key: rally, scope: actor, while: Hurt }]", string extra = "") => SystemDefinitionLoader.Parse($$"""
        id: actor-moment-test
        name: Actor moment test
        conditions: [Hurt, Spent]
        moments: {{moments}}
        sheetKinds:
          - kind: crew
            tracks: [{ key: rep, max: 12 }]
        events: { downtime: [{ intents: [{ adjust: { track: rep, by: 1 } }] }], rally: { script: 'rules.js#rally' } }
        {{extra}}
        """);

    [Fact]
    public void AnActorMoment_IsNoRefillOrDecreaseWord_AndNoChangeMoment()
    {
        var system = Actors();

        Assert.True(SystemMoments.IsMoment(system, "rally"));
        Assert.True(SystemMoments.Find(system, "rally")!.ActorOnly);
        Assert.False(SystemMoments.IsTableMoment(system, "rally"));
        Assert.True(SystemMoments.IsTableMoment(system, "downtime"));
        Assert.Equal(new[] { "downtime", "initiative", "dawn", "manual" }, SystemMoments.RefillWords(system));
        Assert.Equal(new[] { "downtime", "initiative", "dawn" }, SystemMoments.ChangeMoments(system));
        // Not a rest either: a name that is a moment of any kind is never taken for one.
        Assert.False(SystemMoments.IsRest(system, "rally"));
        Assert.False(SheetConditions.IsDropMoment(system, "rally"));
        Assert.True(SheetConditions.IsDropMoment(system, "downtime"));
    }

    [Fact]
    public void AMomentWithWhile_IsOfferedOnlyWhileTheConditionHolds()
    {
        var system = Actors();

        Assert.False(SystemMoments.Offered(system, Sheet("crew"), "rally"));
        Assert.False(SystemMoments.Offered(system, Sheet("crew", ("conditions", new List<object> { "Spent" })), "rally"));
        Assert.True(SystemMoments.Offered(system, Sheet("crew", ("conditions", new List<object> { "Hurt" })), "rally"));
        // `while` is the only thing a moment without it lacks: it is offered wherever it matters.
        Assert.True(SystemMoments.Offered(system, Sheet("crew"), "downtime"));
    }

    [Fact]
    public void TheLoader_RefusesABadScope_AWhileThatIsNoCondition_AndAnActorMomentWithOnSet()
    {
        var scope = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => Actors("[{ key: a, scope: table }]")));
        var unknown = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => Actors("[{ key: a, while: Nothing }]")));
        var onSet = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => Fitd("[{ key: a, scope: actor, onSet: load }]")));

        Assert.Contains(scope, e => e.Contains("scope 'table'"));
        Assert.Contains(unknown, e => e.Contains("while 'Nothing'"));
        Assert.Contains(onSet, e => e.Contains("cannot also fire on setting"));
        Assert.Empty(SystemDefinitionLoader.Validate(Actors()));
    }

    [Fact]
    public void ThePartyBar_LeavesOutActorMoments_AndTheLoaderRefusesWhileOnAPartyMoment()
    {
        Assert.Equal(new[] { "downtime", "dawn" }, SystemMoments.PartyMoments(Actors()).Select(m => m.Key));
        var errors = SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => Actors("[{ key: a, while: Hurt }]")));
        Assert.Contains(errors, e => e.Contains("scope: actor"));
    }

    [Fact]
    public void ADecreaseOnAnActorMoment_IsRefused()
    {
        var system = Assert.Throws<InvalidDataException>(() => Actors(extra: """
            conditionEffects:
              Spent: { value: { decrease: { on: rally, by: 1 } } }
            """));

        Assert.Contains(SystemDefinitionLoader.ErrorsOf(system), e => e.Contains("decrease.on 'rally'"));
    }

    [Fact]
    public void ALocalizedSystem_KeepsAMomentsScopeAndWhile()
    {
        var system = Actors();
        system.Translations["ru"] = new SystemTranslation { Moments = { ["rally"] = "Сбор" } };

        var shown = SystemMoments.Find(system.Localize("ru"), "rally")!;

        Assert.Equal(("Сбор", true, "Hurt"), (shown.Label, shown.ActorOnly, shown.While));
    }

    [Fact]
    public void ACounterThatNamesTheMoment_OrAChoiceChangedAtIt_MakesItMatter()
    {
        var system = Kinds("{}");

        Assert.False(SystemMoments.Matters(system, Sheet("crew"), "downtime"));
        Assert.True(SystemMoments.Matters(system, Sheet("crew", ("uses_a", 1L), ("uses_a_Max", 2L), ("uses_a_Rest", "Long Rest|downtime=1")), "downtime"));
        Assert.True(SystemMoments.Matters(system, Sheet("crew", ("changes_on__choice__x__y", "downtime")), "downtime"));
        Assert.False(SystemMoments.Matters(system, Sheet("crew", ("uses_a", 1L), ("uses_a_Max", 2L), ("uses_a_Rest", "Long Rest")), "downtime"));
    }
}
