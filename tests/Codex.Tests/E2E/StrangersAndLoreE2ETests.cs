using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// What the DM keeps back, as a player meets it: an NPC kept unidentified reaches the player as a stranger, by the name and
/// description the DM gave, and a fact about them shows on their card under that name; a secret on a shared page has a
/// players' version beside it, which the player reads instead; and a block of Aria's history revealed to Aria is read by her
/// player apart from the history they edit.
/// </summary>
[Collection("RavenDb")]
public class StrangersAndLoreE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public StrangersAndLoreE2ETests(AppFixture app) => _app = app;

    [Fact]
    public async Task APlayerMeetsAStranger_ReadsThePlayersVersion_AndWhatWasRevealedToTheirCharacter()
    {
        const string password = "Correct-Horse-1";
        var dmId = "sl-dm-" + Guid.NewGuid();
        var playerId = "sl-p1-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await _app.SeedUserAsync(playerId, "p_" + playerId, password, "Player");

        var options = new BrowserNewContextOptions { BaseURL = _app.BaseUrl };
        await using var dmContext = await _app.Browser.NewContextAsync(options);
        await using var playerContext = await _app.Browser.NewContextAsync(options);
        var dm = await dmContext.NewPageAsync();
        var player = await playerContext.NewPageAsync();
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        await E2EHelpers.LoginAsync(player, "p_" + playerId, password);

        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Strangers campaign");
        await E2EHelpers.JoinAsync(player, await E2EHelpers.ReadInviteLinkAsync(dm, campaignId));
        await E2EHelpers.GoToSectionAsync(player, campaignId, "cast");
        await player.GetByTestId("create-character").ClickAsync();
        await player.GetByTestId("create-character-name").FillAsync("Aria");
        await player.GetByTestId("create-character-submit").ClickAsync();
        await player.WaitForURLAsync(url => url.Contains("/actors/"));
        var ariaUrl = new Uri(player.Url).AbsolutePath;

        // The DM makes an NPC and keeps them unidentified, with a name and a look for the players.
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "cast");
        await dm.GetByTestId("create-character").ClickAsync();
        await dm.GetByTestId("create-character-name").FillAsync("Vizier Mor");
        await dm.GetByTestId("create-character-submit").ClickAsync();
        await dm.WaitForURLAsync(url => url.Contains("/actors/"));
        var morUrl = new Uri(dm.Url).AbsolutePath;
        var morId = morUrl.Split('/').Last();
        await E2EHelpers.WaitForInteractiveAsync(dm);
        await dm.Locator("#actor-visibility").SelectOptionAsync("Unknown");
        await dm.GetByTestId("actor-public-name").FillAsync("Hooded stranger");
        await dm.GetByTestId("actor-public-description").FillAsync("Pays in old silver.");
        await dm.GetByTestId("tab-history").ClickAsync();
        await dm.Locator("#actor-bio").FillAsync("Mor poisoned the old king.");
        await E2EHelpers.SaveSheetAsync(dm);

        // A fact about them, written from their sheet, for the party.
        await dm.GetByTestId("actor-lore-add").ClickAsync();
        await dm.WaitForURLAsync(url => url.Contains("/lore?about="));
        await E2EHelpers.WaitForInteractiveAsync(dm);
        await Assertions.Expect(dm.GetByTestId("new-fact-details")).ToHaveValueAsync(new Regex("^@Vizier Mor"));
        await dm.GetByTestId("new-fact-summary").FillAsync("The stranger owes the guild");
        await dm.GetByTestId("new-fact-details").PressSequentiallyAsync("owes a debt.");
        await dm.GetByTestId("new-fact-visibility").SelectOptionAsync("Public");
        await dm.GetByTestId("new-fact-add").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("fact-card")).ToContainTextAsync("Vizier Mor owes a debt.");

        // The player meets a stranger: the name and look the DM gave, and the fact, never who they are.
        await E2EHelpers.GoToSectionAsync(player, campaignId, "cast");
        await Assertions.Expect(player.GetByTestId("npc-list")).ToContainTextAsync("Hooded stranger");
        await Assertions.Expect(player.GetByTestId("npc-list")).ToContainTextAsync("Pays in old silver.");
        await E2EHelpers.GotoInteractiveAsync(player, morUrl);
        await Assertions.Expect(player.GetByTestId("stranger-card")).ToBeVisibleAsync();
        await Assertions.Expect(player.GetByTestId("actor-name")).ToHaveTextAsync("Hooded stranger");
        await Assertions.Expect(player.GetByTestId("stranger-description")).ToHaveTextAsync("Pays in old silver.");
        await Assertions.Expect(player.GetByTestId("actor-lore")).ToContainTextAsync("The stranger owes the guild");
        var content = await player.ContentAsync();
        Assert.DoesNotContain("Vizier", content);
        Assert.DoesNotContain("poisoned", content);
        var portrait = await player.APIRequest.GetAsync($"/media/actors/{morId}/portrait");
        Assert.Equal(404, portrait.Status);
        await E2EHelpers.GoToSectionAsync(player, campaignId, "lore");
        await Assertions.Expect(player.GetByTestId("fact-details")).ToHaveTextAsync("Hooded stranger owes a debt.");

        // A shared page's secret, with what the players are told beside it.
        await E2EHelpers.GotoInteractiveAsync(dm, $"/campaigns/{campaignId}/wiki/new?title=Saltmere");
        await dm.GetByTestId("wiki-body").FillAsync("A quiet village.\n\n:::dm\nThe keeper runs the smugglers.\n:::");
        await dm.GetByTestId("wiki-visibility").SelectOptionAsync("Party");
        await dm.GetByTestId("wiki-save").ClickAsync();
        await dm.WaitForURLAsync(url => url.EndsWith("/wiki/saltmere"));
        await E2EHelpers.WaitForInteractiveAsync(dm);
        // Written in a dialog with the secret quoted above the box.
        await dm.GetByTestId("wiki-secret-players-0").ClickAsync();
        var told = dm.GetByTestId("players-version-dialog");
        await Assertions.Expect(told).ToContainTextAsync("The keeper runs the smugglers.");
        await told.GetByTestId("wiki-secret-draft-text").FillAsync("Lights move on the water at night.");
        await told.GetByTestId("wiki-secret-draft-save").ClickAsync();
        await Assertions.Expect(told).ToBeHiddenAsync();
        await Assertions.Expect(dm.GetByTestId("wiki-secret-told-0")).ToContainTextAsync("Lights move on the water at night.");
        await Assertions.Expect(dm.GetByTestId("wiki-body")).ToContainTextAsync("The keeper runs the smugglers.");

        await E2EHelpers.GotoInteractiveAsync(player, $"/campaigns/{campaignId}/wiki/saltmere");
        await Assertions.Expect(player.GetByTestId("wiki-body")).ToHaveTextAsync(new Regex(@"^A quiet village\.\s*Lights move on the water at night\.$"));
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "lore");
        await Assertions.Expect(dm.GetByTestId("lore-wiki")).ToContainTextAsync("The keeper runs the smugglers.");

        // In Aria's history, a block for Aria: her player reads it apart, and the box they edit never holds it.
        await E2EHelpers.GotoInteractiveAsync(dm, ariaUrl);
        await dm.GetByTestId("tab-history").ClickAsync();
        await dm.Locator("#actor-bio").FillAsync("Born at sea.\n\n:::for @Aria\nYou were adopted.\n:::");
        await E2EHelpers.SaveSheetAsync(dm);

        await E2EHelpers.GotoInteractiveAsync(player, ariaUrl);
        await player.GetByTestId("tab-history").ClickAsync();
        await Assertions.Expect(player.GetByTestId("actor-revealed-to-you")).ToContainTextAsync("You were adopted.");
        await Assertions.Expect(player.Locator("#actor-bio")).ToHaveValueAsync("Born at sea.");
    }
}
