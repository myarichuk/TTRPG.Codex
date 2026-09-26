using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// P0 overview remediation: the dashboard lists accessible campaigns as real links (no
/// dead Storyline card, no <c>demo</c> lore link), shows system + role + last session per
/// card with a recent-activity feed, locks the system after creation, and lets players
/// propose lore / write campaign notes for DM review.
/// </summary>
[Collection("RavenDb")]
public class OverviewHomeTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public OverviewHomeTests(AppFixture app) => _app = app;

    [Fact]
    public async Task Overview_LinksLockAndPropose_Async()
    {
        const string dmPassword = "Correct-Horse-1";
        const string playerPassword = "Correct-Horse-2";
        var dmId = "ov-dm-" + Guid.NewGuid();
        var playerId = "ov-p1-" + Guid.NewGuid();

        await _app.SeedUserAsync(dmId, "dm_" + dmId, dmPassword, "Player");
        await _app.SeedUserAsync(playerId, "pip_" + playerId, playerPassword, "Player");

        var browser = await _app.GetBrowserAsync();
        var contextOptions = new BrowserNewContextOptions { BaseURL = _app.BaseUrl };
        await using var dmContext = await browser.NewContextAsync(contextOptions);
        await using var playerContext = await browser.NewContextAsync(contextOptions);
        var dm = await dmContext.NewPageAsync();
        var player = await playerContext.NewPageAsync();

        await LoginHelper.LoginAsync(dm, "dm_" + dmId, dmPassword);
        await LoginHelper.LoginAsync(player, "pip_" + playerId, playerPassword);

        // DM creates a campaign, then the overview must link straight to it.
        await dm.GotoAsync("/campaigns");
        await dm.GetByRole(AriaRole.Button, new() { Name = "Create Your First Campaign" }).ClickAsync();
        await dm.GetByRole(AriaRole.Button, new() { Name = "Combat" }).WaitForAsync();
        var campaignId = new Uri(dm.Url).Segments[^1].TrimEnd('/');

        await dm.GotoAsync("/");
        await Assertions.Expect(dm.Locator("a.campaign-card-link[href='/campaigns/" + campaignId + "']")).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByText("New Storyline")).ToHaveCountAsync(0);
        var loreHref = await dm.Locator("a[aria-label='View all lore']").GetAttributeAsync("href");
        Assert.Equal("/grimoire", loreHref);

        await dm.Locator("a.campaign-card-link[href='/campaigns/" + campaignId + "']").ClickAsync();
        await dm.GetByRole(AriaRole.Button, new() { Name = "Combat" }).WaitForAsync();

        // System is locked: badge text, no dropdown in the Chronicle card.
        var chronicle = dm.Locator("div.card:has(h5:text('Chronicle'))");
        await Assertions.Expect(chronicle.Locator("span.badge.bg-secondary").First).ToBeVisibleAsync();
        await Assertions.Expect(chronicle.Locator("text=Locked after creation")).ToBeVisibleAsync();
        await Assertions.Expect(chronicle.Locator("select")).ToHaveCountAsync(0);

        // Player joins, proposes lore and writes a campaign note.
        var inviteUrl = await dm.Locator("div.card:has(h5:text('Invite')) input").InputValueAsync();
        await player.GotoAsync(inviteUrl);
        await player.GetByText("You're in!").WaitForAsync();

        const string proposal = "OV Pip heard the well whispers";
        await player.GotoAsync($"/campaigns/{campaignId}");
        await player.GetByRole(AriaRole.Button, new() { Name = "Lore" }).ClickAsync();
        await player.Locator("input[placeholder='Summary']").FillAsync(proposal);
        await player.GetByRole(AriaRole.Button, new() { Name = "Propose Lore" }).ClickAsync();
        await player.GetByText("Proposed - the DM will review it.").WaitForAsync();

        const string note = "OV pip campaign note";
        await player.Locator("textarea[placeholder='Add a campaign note...']").ClickAsync();
        await player.Keyboard.TypeAsync(note);
        await player.GetByRole(AriaRole.Button, new() { Name = "Add Note", Exact = true }).ClickAsync();
        await player.GetByText(note).WaitForAsync();

        // DM sees the proposal queue and the note, approves the proposal.
        // (Re-navigate: tab switches don't re-query, so a fresh load is required to
        // pick up another circuit's writes - same as every other list on this page.)
        await dm.GotoAsync($"/campaigns/{campaignId}");
        await dm.GetByRole(AriaRole.Button, new() { Name = "Lore" }).ClickAsync();
        await dm.GetByText("Proposed by Players (1)").WaitForAsync();
        await dm.GetByText(note).WaitForAsync();
        var proposalCard = dm.Locator("div.card.border-warning", new() { HasText = proposal });
        await proposalCard.GetByRole(AriaRole.Button, new() { Name = "Approve" }).ClickAsync();
        await dm.GetByText($"Approved \"{proposal}\".").WaitForAsync();
    }
}
