using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// The editor and reading polish, as a DM and a player meet it: the getting-started list ticks itself off and stays put away;
/// "[[" offers pages and Ctrl+S saves; the wiki list filters by kind; and the hover previews say no more than the link
/// would: a hidden NPC and a DM-only page are not found, a stranger is their mask, and a page comes without its DM blocks.
/// </summary>
[Collection("RavenDb")]
public class EditorPolishE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public EditorPolishE2ETests(AppFixture app) => _app = app;

    [Fact]
    public async Task TheEditorsHelp_AndPreviewsTellNoMoreThanTheLinks()
    {
        const string password = "Correct-Horse-1";
        var dmId = "ep-dm-" + Guid.NewGuid();
        var playerId = "ep-p1-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await _app.SeedUserAsync(playerId, "p_" + playerId, password, "Player");

        var options = new BrowserNewContextOptions { BaseURL = _app.BaseUrl };
        await using var dmContext = await _app.Browser.NewContextAsync(options);
        await using var playerContext = await _app.Browser.NewContextAsync(options);
        var dm = await dmContext.NewPageAsync();
        var player = await playerContext.NewPageAsync();
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        await E2EHelpers.LoginAsync(player, "p_" + playerId, password);

        // A new campaign: the DM is shown where to start.
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Polish campaign");
        await E2EHelpers.WaitForInteractiveAsync(dm);
        await Assertions.Expect(dm.GetByTestId("setup-checklist")).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("setup-Page")).ToHaveAttributeAsync("data-done", "false");
        await Assertions.Expect(dm.GetByTestId("setup-Invite")).ToHaveAttributeAsync("data-done", "false");
        // The invite step copies the link and shows it, selected, where it is on the page.
        await dm.GetByTestId("setup-invite-copy").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("invite-link")).ToBeFocusedAsync();
        await E2EHelpers.JoinAsync(player, await E2EHelpers.ReadInviteLinkAsync(dm, campaignId));
        await Assertions.Expect(player.GetByTestId("setup-checklist")).ToHaveCountAsync(0);
        // The DM's open campaign learns of the player without a reload: the overview reads who is in it as stored.
        await dm.GetByRole(AriaRole.Link, new() { Name = "Wiki", Exact = true }).First.ClickAsync();
        await dm.WaitForURLAsync(url => url.EndsWith("/wiki"));
        await dm.GetByRole(AriaRole.Link, new() { Name = "Overview", Exact = true }).First.ClickAsync();
        await Assertions.Expect(dm.GetByTestId("setup-Invite")).ToHaveAttributeAsync("data-done", "true");

        // An NPC kept a stranger, and one kept hidden.
        var morId = await CreateNpcAsync(dm, campaignId, "Vizier Mor", "Unknown", "Mor poisoned the old king.", "Hooded stranger");
        // The DM's history box takes the DM's templates; nothing else about it changed.
        await Assertions.Expect(dm.Locator("#actor-bio")).ToHaveAttributeAsync("data-templates", new Regex("\"a\":"));
        var hiddenId = await CreateNpcAsync(dm, campaignId, "Agent Vel", "Hidden", "Vel spies for the cult.");

        // A DM-only page, saved with Ctrl+S from the text.
        await E2EHelpers.GotoInteractiveAsync(dm, $"/campaigns/{campaignId}/wiki/new?title=Cult%20Lair");
        var body = dm.GetByTestId("wiki-body");
        await body.FillAsync("Deep below the lighthouse.");
        await Assertions.Expect(dm.GetByTestId("wiki-save-bar-dirty")).ToBeVisibleAsync();
        await body.PressAsync("Control+s");
        await dm.WaitForURLAsync(url => url.EndsWith("/wiki/cult-lair"));

        // "[[" offers the pages; picking one writes the link.
        await E2EHelpers.GotoInteractiveAsync(dm, $"/campaigns/{campaignId}/wiki/new?title=Saltmere");
        await body.PressSequentiallyAsync("Near [[cul");
        await Assertions.Expect(dm.Locator("#mention-list")).ToContainTextAsync("Cult Lair");
        await dm.Keyboard.PressAsync("Enter");
        await Assertions.Expect(body).ToHaveValueAsync("Near [[Cult Lair]]");
        await body.FillAsync("A quiet village near [[Cult Lair]]. Ask @Vizier Mor about it.\n\n:::dm\nThe keeper runs the smugglers.\n:::");
        await dm.GetByTestId("wiki-visibility").SelectOptionAsync("Party");
        await dm.GetByTestId("wiki-save").ClickAsync();
        await dm.WaitForURLAsync(url => url.EndsWith("/wiki/saltmere"));

        await E2EHelpers.GotoInteractiveAsync(dm, $"/campaigns/{campaignId}/wiki/new?title=Mara");
        await dm.GetByTestId("wiki-kind-select").SelectOptionAsync("Person");
        await body.FillAsync("A fisher.");
        await dm.GetByTestId("wiki-visibility").SelectOptionAsync("Party");
        await dm.GetByTestId("wiki-save").ClickAsync();
        await dm.WaitForURLAsync(url => url.EndsWith("/wiki/mara"));

        // The list by kind.
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "wiki");
        await dm.GetByTestId("wiki-kind-person").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("wiki-list")).ToContainTextAsync("Mara");
        await Assertions.Expect(dm.GetByTestId("wiki-list")).Not.ToContainTextAsync("Saltmere");

        // The list ticks itself off, and once put away stays away.
        await E2EHelpers.GotoInteractiveAsync(dm, $"/campaigns/{campaignId}");
        await Assertions.Expect(dm.GetByTestId("setup-Page")).ToHaveAttributeAsync("data-done", "true");
        await Assertions.Expect(dm.GetByTestId("setup-Invite")).ToHaveAttributeAsync("data-done", "true");
        await Assertions.Expect(dm.GetByTestId("setup-Cast")).ToHaveAttributeAsync("data-done", "true");
        await Assertions.Expect(dm.GetByTestId("overview-recent")).ToContainTextAsync("Mara");
        await dm.GetByTestId("setup-dismiss").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("setup-checklist")).ToHaveCountAsync(0);
        await E2EHelpers.GotoInteractiveAsync(dm, $"/campaigns/{campaignId}");
        await Assertions.Expect(dm.GetByTestId("overview-recent")).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("setup-checklist")).ToHaveCountAsync(0);

        // Previews, as the player asks for them.
        var preview = $"/campaigns/{campaignId}/preview";
        Assert.Equal(404, (await player.APIRequest.GetAsync($"{preview}/actor/{hiddenId}")).Status);
        Assert.Equal(404, (await player.APIRequest.GetAsync($"{preview}/page/cult-lair")).Status);
        Assert.Equal(404, (await player.APIRequest.GetAsync($"{preview}/wizard/{morId}")).Status);
        var stranger = await player.APIRequest.GetAsync($"{preview}/actor/{morId}");
        Assert.Equal(200, stranger.Status);
        var strangerJson = await stranger.TextAsync();
        Assert.Equal("Hooded stranger", JsonDocument.Parse(strangerJson).RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("Vizier", strangerJson);
        Assert.DoesNotContain("poisoned", strangerJson);
        var saltmere = await (await player.APIRequest.GetAsync($"{preview}/page/saltmere")).TextAsync();
        var text = JsonDocument.Parse(saltmere).RootElement.GetProperty("text").GetString();
        Assert.Contains("A quiet village", text);
        Assert.Contains("Hooded stranger", text);
        Assert.DoesNotContain("smugglers", saltmere);
        Assert.DoesNotContain("Vizier", saltmere);
        // The DM's preview is the whole of it.
        var dmView = await (await dm.APIRequest.GetAsync($"{preview}/actor/{morId}")).TextAsync();
        Assert.Equal("Vizier Mor", JsonDocument.Parse(dmView).RootElement.GetProperty("title").GetString());

        // And on the page: hovering the mention shows the card.
        await E2EHelpers.GotoInteractiveAsync(player, $"/campaigns/{campaignId}/wiki/saltmere");
        await player.GetByTestId("wiki-body").Locator("a.mention").HoverAsync();
        await Assertions.Expect(player.GetByTestId("hover-card")).ToBeVisibleAsync();
        await Assertions.Expect(player.GetByTestId("hover-card")).ToContainTextAsync("Hooded stranger");
        await player.Keyboard.PressAsync("Escape");
        await Assertions.Expect(player.GetByTestId("hover-card")).ToBeHiddenAsync();
    }

    private static async Task<string> CreateNpcAsync(IPage dm, string campaignId, string name, string visibility, string history, string? publicName = null)
    {
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "cast");
        await dm.GetByTestId("create-character").ClickAsync();
        await dm.GetByTestId("create-character-name").FillAsync(name);
        await dm.GetByTestId("create-character-submit").ClickAsync();
        await dm.WaitForURLAsync(url => url.Contains("/actors/"));
        var id = new Uri(dm.Url).AbsolutePath.Split('/').Last();
        await E2EHelpers.WaitForInteractiveAsync(dm);
        await dm.Locator("#actor-visibility").SelectOptionAsync(visibility);
        if (publicName != null) await dm.GetByTestId("actor-public-name").FillAsync(publicName);
        await dm.GetByTestId("tab-history").ClickAsync();
        await dm.Locator("#actor-bio").FillAsync(history);
        await E2EHelpers.SaveSheetAsync(dm);
        return id;
    }
}
