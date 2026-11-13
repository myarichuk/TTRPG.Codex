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
}
