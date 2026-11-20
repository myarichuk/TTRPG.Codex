using Bunit;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;

namespace Codex.Tests.Components;

/// <summary>The sheet's moments panel: a moment that waits for a condition (<c>while</c>) shows its button only while the condition holds.</summary>
public sealed class MomentsPanelTests : ComponentTestBase
{
    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: moments-panel-test
        name: Moments panel test
        conditions: [Hurt]
        moments:
          - { key: downtime, label: Downtime }
          - { key: rally, label: Rally, scope: actor, while: Hurt }
        sheetKinds:
          - kind: crew
            tracks: [{ key: rep, max: 12 }]
        events:
          downtime: [{ intents: [{ adjust: { track: rep, by: 1 } }] }]
          rally: { script: 'rules.js#rally' }
        """);

    private IRenderedComponent<MomentsPanel> Panel(params string[] conditions) =>
        Render<MomentsPanel>(p => p
            .Add(c => c.Definition, System)
            .Add(c => c.Sheet, new Dictionary<string, object> { ["kind"] = "crew", ["conditions"] = conditions.Cast<object>().ToList() }));

    [Fact]
    public void AMomentWithWhile_IsHiddenWithoutTheCondition_AndShownWithIt()
    {
        var well = Panel();
        var hurt = Panel("Hurt");

        Assert.Empty(well.FindAll(Id("moment-fire-rally")));
        Assert.Single(well.FindAll(Id("moment-fire-downtime")));
        Assert.Equal("Rally", hurt.Find(Id("moment-fire-rally")).TextContent.Trim());
    }
}
