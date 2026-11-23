using Bunit;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests.Components;

/// <summary>The table's list of encounters ready to start (ENCOUNTER_BUILDER_DESIGN.md 5.2): Ready and Ran plans, never a Draft, each a button with its own name.</summary>
public class PreparedEncounterPickerTests : ComponentTestBase
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly EncounterWorld _w = new();

    private IRenderedComponent<PreparedEncounterPicker> Draw(string? placeId = null)
    {
        Services.AddSingleton(_w.Service);
        Services.AddSingleton(Localizer.For("en"));
        Services.AddSingleton<ILogger<PreparedEncounterPicker>>(NullLogger<PreparedEncounterPicker>.Instance);
        return Render<PreparedEncounterPicker>(p => p
            .Add(x => x.Campaign, _w.Campaign)
            .Add(x => x.Access, _w.Gm)
            .Add(x => x.PlaceId, placeId));
    }

    private async Task StoredAsync(string name, string place, PreparedStatus status)
    {
        var plan = _w.Plan(name, place, EncounterWorld.Bandits(2));
        plan.Status = status;
        Assert.True((await _w.Service.SaveAsync(_w.Campaign, _w.Gm, plan)).Saved);
    }

    [Fact]
    public async Task ItListsReadyPlansAsButtons_LeavesDraftsOut_AndPutsThePlaceInUseFirst()
    {
        await StoredAsync("Alpha", "hall", PreparedStatus.Ready);
        await StoredAsync("Beta", "cellar", PreparedStatus.Ready);
        await StoredAsync("Gamma", "cellar", PreparedStatus.Draft);

        var cut = Draw("cellar");

        cut.WaitForState(() => cut.FindAll(Id("start-prepared-plan")).Count == 2, Patience);
        var buttons = cut.FindAll(Id("start-prepared-plan"));
        Assert.Equal(new[] { "Beta", "Alpha" }, buttons.Select(b => b.TextContent.Trim()));
        Assert.Equal("Start Beta", buttons[0].GetAttribute("aria-label"));
        Assert.Equal("UL", buttons[0].ParentElement!.ParentElement!.TagName);
        Assert.DoesNotContain("Gamma", cut.Markup);
    }

    [Fact]
    public void WithNoPlansReady_ItSaysSo_AndLinksToTheEncounters()
    {
        var cut = Draw();

        cut.WaitForAssertion(() => Assert.Contains("No encounters are ready to start.", cut.Markup), Patience);
        Assert.Equal("/campaigns/c1/encounters", cut.Find("a").GetAttribute("href"));
        Assert.Empty(cut.FindAll(Id("start-prepared-plan")));
    }
}
