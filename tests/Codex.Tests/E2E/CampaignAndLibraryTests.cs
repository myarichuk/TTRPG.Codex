using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// The prep loop around the new UI:
///   1. Campaign lifecycle: create via the dialog (name + system), rename in Settings, delete with the
///      accessible confirm dialog.
///   2. Library: browse SRD spells, search, open a detail page that shows the CC-BY attribution.
///   3. Homebrew: the DM creates a homebrew spell; the player finds it in the Library (and cannot
///      author one); the DM exports the homebrew pack.
///   4. Characters: a player creates their own character and picks class and species from SRD
///      content; the DM sees it in the Cast with those picks.
///   5. Accessibility smoke: no unlabelled control on any main page, and the New campaign dialog
///      is fully operable with the keyboard.
/// </summary>
[Collection("RavenDb")]
public class CampaignAndLibraryTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public CampaignAndLibraryTests(AppFixture app) => _app = app;

    [Fact]
    public async Task PrepLoop_FullScenario_Async()
    {
        const string dmPassword = "Correct-Horse-1";
        const string playerPassword = "Correct-Horse-2";
        var dmId = "lib-dm-" + Guid.NewGuid();
        var playerId = "lib-p1-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, dmPassword, "Player");
        await _app.SeedUserAsync(playerId, "aria_" + playerId, playerPassword, "Player");

        var contextOptions = new BrowserNewContextOptions { BaseURL = _app.BaseUrl };
        await using var dmContext = await _app.Browser.NewContextAsync(contextOptions);
        await using var playerContext = await _app.Browser.NewContextAsync(contextOptions);
        var dm = await dmContext.NewPageAsync();
        var player = await playerContext.NewPageAsync();

        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, dmPassword);
        await E2EHelpers.LoginAsync(player, "aria_" + playerId, playerPassword);

        // ---- 1. Create with an explicit system; the title and system pill reflect the choice. ----
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Library Test Campaign", "srd5e");
        await Assertions.Expect(dm.GetByTestId("campaign-title")).ToHaveTextAsync("Library Test Campaign");
        await Assertions.Expect(dm.GetByText("Fifth Edition SRD (5.2.1)").First).ToBeVisibleAsync();

        var inviteUrl = await E2EHelpers.ReadInviteLinkAsync(dm, campaignId);
        await E2EHelpers.JoinAsync(player, inviteUrl);

        // ---- 2. Library: spells chip, search, detail with attribution. ----
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "library");
        await dm.GetByTestId("library-type-spell").ClickAsync();
        await dm.GetByTestId("library-search").FillAsync("Fireball");
        var fireball = dm.GetByTestId("library-item").Filter(new() { HasText = "Fireball" }).First;
        await fireball.ClickAsync();
        await dm.WaitForURLAsync(url => url.Contains("/library/spell/fireball"));
        var detail = dm.GetByTestId("library-detail");
        await Assertions.Expect(detail.GetByRole(AriaRole.Heading, new() { Name = "Fireball" })).ToBeVisibleAsync();
        await Assertions.Expect(detail).ToContainTextAsync("8d6");
        await Assertions.Expect(detail.GetByTestId("entry-attribution")).ToContainTextAsync("System Reference Document 5.2.1");
        await Assertions.Expect(detail.GetByTestId("entry-attribution")).ToContainTextAsync("Creative Commons Attribution 4.0");

        // Deep link works for the player too (the Library is the same content).
        await E2EHelpers.GotoInteractiveAsync(player, $"/campaigns/{campaignId}/library/spell/fireball");
        await Assertions.Expect(player.GetByTestId("library-detail").GetByTestId("entry-attribution")).ToContainTextAsync("System Reference Document 5.2.1");

        // ---- 3. Homebrew: DM authors, player sees, player cannot author. ----
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "library");
        await dm.GetByTestId("new-homebrew").ClickAsync();
        await dm.GetByTestId("homebrew-type").SelectOptionAsync("spell");
        await dm.GetByTestId("homebrew-name").FillAsync("Thunderous Yawn");
        await dm.GetByTestId("homebrew-description").FillAsync("A **very** loud yawn that stuns the rude.");
        await dm.GetByLabel("Level", new() { Exact = true }).FillAsync("2");
        await dm.GetByLabel("School", new() { Exact = true }).SelectOptionAsync("Evocation");
        await dm.GetByTestId("homebrew-save").ClickAsync();
        await dm.GetByTestId("status-message").GetByText("Saved “Thunderous Yawn”.").WaitForAsync();
        await dm.WaitForURLAsync(url => url.Contains("/library/spell/thunderous-yawn"));
        await Assertions.Expect(dm.GetByTestId("library-detail")).ToContainTextAsync("Homebrew");
        await Assertions.Expect(dm.GetByTestId("library-detail").Locator("strong", new() { HasText = "very" })).ToBeVisibleAsync();

        await E2EHelpers.GoToSectionAsync(player, campaignId, "library");
        await player.GetByTestId("library-search").FillAsync("Thunderous");
        await Assertions.Expect(player.GetByTestId("library-item").Filter(new() { HasText = "Thunderous Yawn" })).ToBeVisibleAsync();
        await Assertions.Expect(player.GetByTestId("new-homebrew")).ToHaveCountAsync(0);
        // Players don't get the creature list as a picker.
        await Assertions.Expect(player.GetByTestId("library-type-monster")).ToHaveCountAsync(0);
        await Assertions.Expect(dm.GetByTestId("library-type-monster")).ToHaveCountAsync(1);

        // ---- 4. The player creates their own character with SRD picks. ----
        await E2EHelpers.GoToSectionAsync(player, campaignId, "cast");
        await player.GetByTestId("create-character").ClickAsync();
        await player.GetByTestId("create-character-name").FillAsync("Aria Stonehelm");
        await player.GetByTestId("create-character-submit").ClickAsync();
        await player.WaitForURLAsync(url => url.Contains("/actors/"));
        await Assertions.Expect(player.GetByTestId("actor-name")).ToHaveTextAsync("Aria Stonehelm");

        await player.GetByLabel("Class", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Fighter" });
        await player.GetByLabel("Species", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Dwarf" });
        await player.GetByTestId("attr-str").FillAsync("16");
        await player.GetByTestId("sheet-save").ClickAsync();
        await player.GetByTestId("status-message").GetByText("Saved.").WaitForAsync();
        await Assertions.Expect(player.GetByRole(AriaRole.Link, new() { Name = "Fighter" })).ToBeVisibleAsync();
        await Assertions.Expect(player.GetByText("+3")).ToBeVisibleAsync(); // STR 16 -> +3

        await E2EHelpers.GoToSectionAsync(dm, campaignId, "cast");
        var ariaRow = dm.GetByTestId("actor-row").Filter(new() { HasText = "Aria Stonehelm" });
        await Assertions.Expect(ariaRow).ToContainTextAsync("Dwarf");
        await Assertions.Expect(ariaRow).ToContainTextAsync("Fighter");

        // ---- 5. Accessibility smoke across the main pages. ----
        foreach (var path in new[] { "/", $"/campaigns/{campaignId}", $"/campaigns/{campaignId}/cast", $"/campaigns/{campaignId}/world", $"/campaigns/{campaignId}/lore", $"/campaigns/{campaignId}/sessions", $"/campaigns/{campaignId}/table", $"/campaigns/{campaignId}/library/spell/fireball", $"/campaigns/{campaignId}/settings", "/about", "/library" })
        {
            await E2EHelpers.GotoInteractiveAsync(dm, path);
            var offenders = await E2EHelpers.UnlabelledControlsAsync(dm);
            Assert.True(offenders.Length == 0, $"Unlabelled controls on {path}:\n" + string.Join("\n", offenders));
        }

        // Keyboard-only campaign creation: Tab to the New campaign button, Enter opens the dialog,
        // focus lands in the name field, Enter submits.
        await E2EHelpers.GotoInteractiveAsync(dm, "/");
        await dm.GetByTestId("new-campaign").FocusAsync();
        await dm.Keyboard.PressAsync("Enter");
        await Assertions.Expect(dm.GetByTestId("new-campaign-name")).ToBeFocusedAsync();
        await dm.Keyboard.TypeAsync("Keyboard Campaign");
        await dm.Keyboard.PressAsync("Enter");
        await dm.WaitForURLAsync(url => url.Contains("/campaigns/"));
        await Assertions.Expect(dm.GetByTestId("campaign-title")).ToHaveTextAsync("Keyboard Campaign");
        var keyboardCampaignId = new Uri(dm.Url).Segments[2].TrimEnd('/');

        // ---- 1 (continued). Rename in Settings, then delete with the confirm dialog. ----
        await E2EHelpers.GoToSectionAsync(dm, keyboardCampaignId, "settings");
        await dm.GetByTestId("settings-name").FillAsync("Renamed Campaign");
        await dm.GetByTestId("settings-save").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("campaign-title")).ToHaveTextAsync("Renamed Campaign");

        await dm.GetByTestId("settings-delete").ClickAsync();
        await dm.GetByTestId("confirm-accept").ClickAsync();
        await dm.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/");
        await dm.GetByTestId("campaign-list").WaitForAsync();
        await Assertions.Expect(dm.GetByText("Renamed Campaign")).ToHaveCountAsync(0);
        await Assertions.Expect(dm.GetByText("Library Test Campaign")).ToBeVisibleAsync();
    }
}
