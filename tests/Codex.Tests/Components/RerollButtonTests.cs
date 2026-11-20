using Bunit;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;

namespace Codex.Tests.Components;

/// <summary>The reroll button, on the table's roll log and on the sheet's roll card (both render <see cref="RerollButton"/>): who is offered it, what it is
/// called, why it is off, and what it sends.</summary>
public sealed class RerollButtonTests : ComponentTestBase
{
    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: reroll-button-test
        name: Reroll button test
        rerolls: [{ key: lucky, label: Lucky reroll, spend: { track: luck } }]
        sheetKinds:
          - kind: hero
            tracks: [{ key: luck, max: 3 }]
        """);

    private static RollLogEntry Roll(bool secret = false, string? rerolledBy = null) => new()
    {
        ActorId = "vex", Expression = "1d20+5", Label = "Strike", Total = 24, CheckDieIndex = 0, IsSecret = secret, RerolledBy = rerolledBy,
        Replay = new RollReplay { Kind = RollReplay.OfExpression }
    };

    private static ActorDocument Hero(long luck) => new() { Id = "vex", Name = "Vex", OwnerUserId = "player-1", Sheet = new Dictionary<string, object> { ["kind"] = "hero", ["luck"] = luck } };

    private IRenderedComponent<TableDicePanel> Panel(RollLogEntry entry, Func<RollLogEntry, IReadOnlyList<RerollChoice>> choices, Action<(RollLogEntry, string)>? pressed = null, Func<RollLogEntry, bool>? counts = null) =>
        Render<TableDicePanel>(p => p
            .Add(c => c.Counts, counts)
            .Add(c => c.Rolls, new[] { entry })
            .Add(c => c.RollerName, _ => "Vex")
            .Add(c => c.ActorHref, _ => null)
            .Add(c => c.RerollsOf, choices)
            .Add(c => c.OnReroll, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<(RollLogEntry Roll, string Key)>(this, r => pressed?.Invoke(r))));

    [Fact]
    public void TheTableLog_OffersTheButton_NamedForTheRoll_AndSendsTheRollAndTheKey()
    {
        var entry = Roll();
        (RollLogEntry, string)? sent = null;
        var cut = Panel(entry, e => TableVisibility.RerollsFor(System, e, isDm: false, isOwner: true, null, Hero(2)), r => sent = r);

        var button = cut.Find(Id("reroll-lucky-button"));
        Assert.Equal("Lucky reroll (luck: 1)", button.TextContent.Trim());
        Assert.Equal("Lucky reroll (luck: 1) for Vex, Strike, 24", button.GetAttribute("aria-label"));
        Assert.Null(button.GetAttribute("aria-disabled"));
        Assert.Empty(cut.FindAll(Id("reroll-lucky-why")));

        button.Click();

        Assert.Equal((entry, "lucky"), sent);
    }

    [Fact]
    public void ShortOfPoints_TheButtonIsOff_StaysFocusable_DoesNothing_AndSaysWhatIsShort()
    {
        (RollLogEntry, string)? sent = null;
        var cut = Panel(Roll(), e => TableVisibility.RerollsFor(System, e, isDm: false, isOwner: true, null, Hero(0)), r => sent = r);

        var button = cut.Find(Id("reroll-lucky-button"));
        var why = cut.Find(Id("reroll-lucky-why"));
        Assert.Equal("true", button.GetAttribute("aria-disabled"));
        Assert.False(button.HasAttribute("disabled"));
        Assert.Equal("Not enough luck left.", why.TextContent.Trim());
        Assert.Equal(why.Id, button.GetAttribute("aria-describedby"));

        button.Click();

        Assert.Null(sent);
    }

    [Fact]
    public void WithKeepHigher_TheRollThatCountsSaysSo()
    {
        var cut = Panel(Roll(rerolledBy: "next"), _ => Array.Empty<RerollChoice>(), counts: _ => true);

        Assert.Equal("this one counts", cut.Find(Id("roll-counts")).TextContent.Trim());
    }

    [Fact]
    public void TheTablesPoolsDecideWhatCanBePaid_NotTheStoredSheet()
    {
        var state = new ActorStateSnapshot(new Dictionary<string, int> { ["luck"] = 0 }, Array.Empty<EffectSnapshot>());

        var choice = Assert.Single(TableVisibility.RerollsFor(System, Roll(), isDm: false, isOwner: true, state, Hero(3)));

        Assert.False(choice.CanPay);
    }

    [Fact]
    public void ARollThatWasRerolled_ShowsNoButton_ButSaysSo()
    {
        var cut = Panel(Roll(rerolledBy: "next"), e => TableVisibility.RerollsFor(System, e, isDm: true, isOwner: true, null, Hero(2)));

        Assert.Empty(cut.FindAll(Id("reroll-lucky-button")));
        Assert.Equal("Rolled again", cut.Find(Id("roll-rerolled")).TextContent.Trim());
    }

    [Fact]
    public void OnlyTheGmAndTheOwner_AreOffered_AndASecretRollIsTheGmsAlone()
    {
        var hero = Hero(2);

        Assert.Empty(TableVisibility.RerollsFor(System, Roll(), isDm: false, isOwner: false, null, hero));
        Assert.Single(TableVisibility.RerollsFor(System, Roll(), isDm: true, isOwner: false, null, hero));
        Assert.Single(TableVisibility.RerollsFor(System, Roll(), isDm: false, isOwner: true, null, hero));
        Assert.Empty(TableVisibility.RerollsFor(System, Roll(secret: true), isDm: false, isOwner: true, null, hero));
        Assert.Single(TableVisibility.RerollsFor(System, Roll(secret: true), isDm: true, isOwner: true, null, hero));
    }

    [Fact]
    public void TheSheetsRollCard_IsOfferedOnlyWhatTheTablesLogStillHolds_ToSomeoneWhoCanEdit()
    {
        // The sheet page is a large page of many services, so what it asks (RerollChoicesOf) is this helper, tested here with the shared button above.
        var hero = Hero(2);
        var mine = Roll();
        var snapshot = new RuntimeSnapshot(null, new[] { mine }, Array.Empty<SessionNote>(), new Dictionary<string, ActorStateSnapshot>());

        Assert.Single(TableVisibility.RerollsOnSheet(System, snapshot, mine, hero, isDm: false, canEdit: true));
        Assert.Empty(TableVisibility.RerollsOnSheet(System, snapshot, mine, hero, isDm: false, canEdit: false));
        // A roll that is not in the shared log (made privately on the page) is not offered, and neither is one the table has since rerolled.
        Assert.Empty(TableVisibility.RerollsOnSheet(System, snapshot, Roll(), hero, isDm: false, canEdit: true));
        mine.RerolledBy = "next";
        Assert.Empty(TableVisibility.RerollsOnSheet(System, snapshot, mine, hero, isDm: false, canEdit: true));
    }

    [Fact]
    public void TheSheetsRollCard_UsesTheSameButton()
    {
        var pressed = 0;
        var cut = Render<RerollButton>(p => p
            .Add(c => c.Label, "Lucky reroll").Add(c => c.Roll, "Strike, 24").Add(c => c.TestId, "sheet-reroll-lucky")
            .Add(c => c.OnReroll, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => pressed++)));

        cut.Find(Id("sheet-reroll-lucky-button")).Click();

        Assert.Equal(1, pressed);
        Assert.Equal("Lucky reroll for Strike, 24", cut.Find("button").GetAttribute("aria-label"));
    }
}
