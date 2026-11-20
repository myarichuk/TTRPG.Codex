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
          - { key: rally, label: Rally, scope: actor, while: Hurt, note: Spends what it can }
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

    [Fact]
    public void TheButton_PointsAtALineOnlyWhenThereIsOne_AndTheNoteIsTheLine()
    {
        var hurt = Panel("Hurt");

        // The script moment has a note; the table moment's line is what it restores.
        var rally = hurt.Find(Id("moment-fire-rally"));
        var line = hurt.Find(Id("moment-rally") + " span.small-muted");
        Assert.Equal("Spends what it can", line.TextContent.Trim());
        Assert.Equal(line.Id, rally.GetAttribute("aria-describedby"));
        Assert.NotNull(hurt.Find(Id("moment-fire-downtime")).GetAttribute("aria-describedby"));

        var bare = Render<MomentsPanel>(p => p
            .Add(c => c.Definition, Bare)
            .Add(c => c.Sheet, new Dictionary<string, object> { ["kind"] = "crew" }));
        Assert.Null(bare.Find(Id("moment-fire-quiet")).GetAttribute("aria-describedby"));
    }

    private static readonly SystemDefinition Bare = SystemDefinitionLoader.Parse("""
        id: bare-moments
        name: Bare moments
        moments: [{ key: quiet, label: Quiet }]
        sheetKinds:
          - kind: crew
        events:
          quiet: { script: 'rules.js#quiet' }
        """);
}
