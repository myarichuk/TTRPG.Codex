using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The layout check for the <c>features</c> and <c>moments</c> widgets on a sheet kind, and the generic reading of a sheet's moments.</summary>
public class SheetKindWidgetsTests
{
    private static SystemDefinition Parse(string kindFeatures, string moments, string widgets) => SystemDefinitionLoader.Parse($$"""
        id: kind-widgets
        name: Kind widgets
        entityTypes:
          - { type: ability, label: Ability }
        {{moments}}
        sheetKinds:
          - kind: hero
            {{kindFeatures}}
            lists:
              - { key: abilities, label: Abilities, type: ability }
            tracks:
              - { key: armor, max: 1, display: boxes }
            layout:
              - { section: Main, widgets: [{{widgets}}] }
        events:
          downtime:
            - { intents: [{ set: { track: armor, to: 0 } }] }
        """);

    [Fact]
    public void Features_IsAllowedOnAKindWithFeatureSources_AndRefusedOnOneWithout()
    {
        Assert.Empty(SheetLayout.Problems(Parse("features: [{ list: abilities }]", "moments: [{ key: downtime }]", "{ widget: features }")));

        var problems = SheetLayout.Problems(Parse("", "moments: [{ key: downtime }]", "{ widget: features }")).ToList();
        Assert.Contains(problems, p => p.Contains("features") && p.Contains("none"));
    }

    [Fact]
    public void Moments_IsAllowedWhereTheSystemDeclaresSome_AndSaysSoWhenItDeclaresNone()
    {
        Assert.Empty(SheetLayout.Problems(Parse("", "moments: [{ key: downtime }]", "{ widget: moments }")));
        Assert.Contains(SheetLayout.Problems(Parse("", "", "{ widget: moments }")), p => p.Contains("no moments"));
    }

    [Fact]
    public void ADeclaredMoment_MattersToASheetOfAKindItsHandlerTouches()
    {
        var system = Parse("", "moments: [{ key: downtime }]", "{ widget: moments }");

        Assert.True(SystemMoments.Matters(system, new Dictionary<string, object> { ["kind"] = "hero" }, "downtime"));
        Assert.Empty(SystemMoments.Effects(system, new Dictionary<string, object> { ["kind"] = "hero" }, "downtime").Counters);
        Assert.Equal(new[] { "armor" }, SystemMoments.Effects(system, new Dictionary<string, object> { ["kind"] = "hero" }, "downtime").Tracks);
    }
}
