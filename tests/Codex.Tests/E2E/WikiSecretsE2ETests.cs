using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// Secrets on a shared wiki page, at the table: the DM reveals a block from the block itself, the player finds it on the page
/// marked new; the DM reveals another to the player's character alone, which the party's view does not show; and hides the
/// first again.
/// </summary>
[Collection("RavenDb")]
public class WikiSecretsE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public WikiSecretsE2ETests(AppFixture app) => _app = app;

    [Fact]
    public async Task TheDmRevealsSecrets_ToThePartyAndToOneCharacter_AndHidesThemAgain()
    {
        const string password = "Correct-Horse-1";
        var dmId = "ws-dm-" + Guid.NewGuid();
        var playerId = "ws-p1-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await _app.SeedUserAsync(playerId, "p_" + playerId, password, "Player");

        var options = new BrowserNewContextOptions { BaseURL = _app.BaseUrl };
        await using var dmContext = await _app.Browser.NewContextAsync(options);
        await using var playerContext = await _app.Browser.NewContextAsync(options);
        var dm = await dmContext.NewPageAsync();
        var player = await playerContext.NewPageAsync();
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        await E2EHelpers.LoginAsync(player, "p_" + playerId, password);

        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Secrets campaign");
        await E2EHelpers.JoinAsync(player, await E2EHelpers.ReadInviteLinkAsync(dm, campaignId));
        await E2EHelpers.GoToSectionAsync(player, campaignId, "cast");
        await player.GetByTestId("create-character").ClickAsync();
        await player.GetByTestId("create-character-name").FillAsync("Aria");
        await player.GetByTestId("create-character-submit").ClickAsync();
        await player.WaitForURLAsync(url => url.Contains("/actors/"));

        // A shared page with two secrets; the second names Aria by mention, as the editor stores it.
        await E2EHelpers.GotoInteractiveAsync(dm, $"/campaigns/{campaignId}/wiki/new?title=Saltmere");
        await dm.GetByTestId("wiki-body").FillAsync("A quiet village.\n\n:::dm\nThe keeper is a cultist.\n:::\n\n:::dm\nThe keeper taught Aria.\n:::");
        await dm.GetByTestId("wiki-visibility").SelectOptionAsync("Party");
        await dm.GetByTestId("wiki-save").ClickAsync();
        await dm.WaitForURLAsync(url => url.EndsWith("/wiki/saltmere"));
        await E2EHelpers.WaitForInteractiveAsync(dm);
        await Assertions.Expect(dm.GetByTestId("wiki-secrets")).ToBeVisibleAsync();

        // The player reads the page before anything is revealed, so the reveal is new to them afterwards.
        await E2EHelpers.GotoInteractiveAsync(player, $"/campaigns/{campaignId}/wiki/saltmere");
        await Assertions.Expect(player.GetByTestId("wiki-body")).ToHaveTextAsync("A quiet village.");

        // The block's own Reveal asks in a dialog that shows the whole secret, the party picked to begin with.
        await dm.GetByTestId("secret-party-0").ClickAsync();
        var reveal = dm.GetByTestId("reveal-dialog");
        await Assertions.Expect(reveal.GetByTestId("reveal-secret-text")).ToContainTextAsync("The keeper is a cultist.");
        await Assertions.Expect(reveal.GetByTestId("reveal-to-party")).ToBeCheckedAsync();
        await reveal.GetByTestId("reveal-accept").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("secret-dm-0")).ToBeVisibleAsync();

        // The side panel's Reveal… asks the same; picking "some characters" offers the ones players play, and needs one ticked.
        await dm.GetByTestId("wiki-secret-reveal-1").ClickAsync();
        await Assertions.Expect(reveal.GetByTestId("reveal-secret-text")).ToContainTextAsync("The keeper taught Aria.");
        await reveal.GetByTestId("reveal-to-some").CheckAsync();
        await Assertions.Expect(reveal.GetByTestId("reveal-accept")).ToBeDisabledAsync();
        await reveal.GetByTestId("reveal-picks").GetByText("Aria", new() { Exact = true }).ClickAsync();
        await Assertions.Expect(reveal.GetByTestId("reveal-accept")).ToHaveTextAsync("Reveal to them");
        await reveal.GetByTestId("reveal-accept").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("wiki-body")).ToContainTextAsync("Revealed to Aria");

        await E2EHelpers.GotoInteractiveAsync(player, $"/campaigns/{campaignId}/wiki/saltmere");
        await Assertions.Expect(player.GetByTestId("wiki-body")).ToContainTextAsync("The keeper is a cultist.");
        await Assertions.Expect(player.GetByTestId("wiki-body")).ToContainTextAsync("The keeper taught Aria.");
        await Assertions.Expect(player.Locator(".secret-new")).ToHaveCountAsync(2);

        // The party at large (a player who plays no one) reads the first, not Aria's.
        await dm.GetByTestId("wiki-view-as-party").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("wiki-body")).ToContainTextAsync("The keeper is a cultist.");
        await Assertions.Expect(dm.GetByTestId("wiki-body")).Not.ToContainTextAsync("taught");

        await dm.GetByTestId("wiki-view-as").GetByRole(AriaRole.Button).First.ClickAsync();
        await dm.GetByTestId("wiki-secret-hide-0").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("secret-party-0")).ToBeVisibleAsync();
        await E2EHelpers.GotoInteractiveAsync(player, $"/campaigns/{campaignId}/wiki/saltmere");
        await Assertions.Expect(player.GetByTestId("wiki-body")).Not.ToContainTextAsync("cultist");
        await Assertions.Expect(player.GetByTestId("wiki-body")).ToContainTextAsync("The keeper taught Aria.");
    }
}
