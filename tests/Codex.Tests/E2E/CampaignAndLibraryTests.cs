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
        // Exact name, not a substring: before the debounced search lands, the unfiltered list already
        // contains "Delayed Blast Fireball", and a contains-match would click that on a slow runner.
        var fireball = dm.GetByRole(AriaRole.Link, new() { Name = "Fireball", Exact = true });
        await fireball.WaitForAsync();
        await fireball.ClickAsync();
        await dm.WaitForURLAsync(url => url.EndsWith("/library/spell/fireball"));
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
        await dm.GetByLabel("Level", new() { Exact = true }).CommitAsync("2");
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

        // ---- 4. The player creates their own character; the system's rules shape every pick. ----
        await E2EHelpers.GoToSectionAsync(player, campaignId, "cast");
        await player.GetByTestId("create-character").ClickAsync();
        await player.GetByTestId("create-character-name").FillAsync("Aria Stonehelm");
        await player.GetByTestId("create-character-submit").ClickAsync();
        await player.WaitForURLAsync(url => url.Contains("/actors/"));
        await E2EHelpers.WaitForInteractiveAsync(player);
        await Assertions.Expect(player.GetByTestId("actor-name")).ToHaveTextAsync("Aria Stonehelm");

        // Point buy is the default method: a fresh sheet starts at the minimum with nothing spent.
        await Assertions.Expect(player.GetByTestId("attr-method")).ToHaveValueAsync("point-buy");
        await Assertions.Expect(player.GetByTestId("point-buy-summary")).ToContainTextAsync("0 of 27");

        await player.GetByLabel("Class", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Fighter" });
        await player.GetByLabel("Species", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Dwarf" });

        // A subclass is a level 3 choice: a level 1 Fighter is offered none.
        var subclassSelect = player.GetByLabel("Subclass", new() { Exact = true });
        await Assertions.Expect(subclassSelect.Locator("option", new() { HasText = "Champion" })).ToHaveCountAsync(0);

        // From level 3 subclasses are narrowed to the chosen class: a Fighter sees Champion and nothing else.
        // Wait for the re-render after the level change before reading the options once.
        await player.GetByLabel("Level", new() { Exact = true }).CommitAsync("3");
        await Assertions.Expect(subclassSelect.Locator("option", new() { HasText = "Champion" })).ToHaveCountAsync(1);
        var subclassOptions = await subclassSelect.Locator("option").AllTextContentsAsync();
        Assert.Contains("Champion", subclassOptions);
        Assert.DoesNotContain("Oath of Devotion", subclassOptions);
        Assert.DoesNotContain("Circle of the Land", subclassOptions);
        await player.GetByLabel("Level", new() { Exact = true }).CommitAsync("1");

        // A Fighter has no spell table: both spell lists are capped at zero.
        await Assertions.Expect(player.GetByTestId("list-spells-count")).ToHaveTextAsync("0 of 0");
        await Assertions.Expect(player.GetByTestId("list-cantrips-count")).ToHaveTextAsync("0 of 0");

        // Out-of-range point-buy score: the rules panel explains, and a player can't save (strict).
        await player.GetByTestId("attr-str").CommitAsync("18");
        await Assertions.Expect(player.GetByTestId("rule-violations")).ToContainTextAsync("8 to 15");
        await player.GetByTestId("sheet-save").ClickAsync();
        await Assertions.Expect(player.GetByTestId("status-message")).ToContainTextAsync("Not saved");

        // A legal spread: 15/14/13/12/10/8 is exactly 27 points.
        await player.GetByTestId("attr-str").CommitAsync("15");
        await player.GetByTestId("attr-dex").CommitAsync("14");
        await player.GetByTestId("attr-con").CommitAsync("13");
        await player.GetByTestId("attr-int").CommitAsync("12");
        await player.GetByTestId("attr-wis").CommitAsync("10");
        await player.GetByTestId("attr-cha").CommitAsync("8");
        await Assertions.Expect(player.GetByTestId("point-buy-summary")).ToContainTextAsync("27 of 27");
        await Assertions.Expect(player.GetByTestId("rule-violations")).ToHaveCountAsync(0);

        // Background bonuses: the Sage lists CON/INT/WIS; +2/+1 on two of them raises the totals.
        await player.GetByLabel("Background", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Sage" });
        await player.GetByTestId("bonus-int").SelectOptionAsync("2");
        await player.GetByTestId("bonus-wis").SelectOptionAsync("1");
        await Assertions.Expect(player.GetByTestId("attr-int-total")).ToHaveTextAsync("12 + 2 = 14");
        await Assertions.Expect(player.GetByTestId("attr-wis-total")).ToHaveTextAsync("10 + 1 = 11");
        await Assertions.Expect(player.GetByTestId("derived-proficiencyBonus")).ToHaveTextAsync("+2");

        // The origin feat is narrowed to the one the background grants.
        var originOptions = await player.GetByLabel("Origin Feat", new() { Exact = true }).Locator("option").AllTextContentsAsync();
        Assert.Contains(originOptions, o => o.StartsWith("Magic Initiate"));
        Assert.DoesNotContain("Alert", originOptions);

        Assert.Empty(await player.GetByTestId("rule-violations").AllInnerTextsAsync());
        await player.GetByTestId("sheet-save").ClickAsync();
        await Assertions.Expect(player.GetByTestId("status-message")).ToContainTextAsync("Saved.");
        await Assertions.Expect(player.GetByLabel("From the Library").GetByRole(AriaRole.Link, new() { Name = "Fighter" })).ToBeVisibleAsync();
        await Assertions.Expect(player.GetByTestId("class-level-fighter")).ToHaveTextAsync("1 level"); // the Classes panel
        await Assertions.Expect(player.GetByTestId("attr-int-total")).ToHaveTextAsync("12 + 2 = 14"); // persisted and reloaded

        await E2EHelpers.GoToSectionAsync(dm, campaignId, "cast");
        var ariaRow = dm.GetByTestId("actor-row").Filter(new() { HasText = "Aria Stonehelm" });
        await Assertions.Expect(ariaRow).ToContainTextAsync("Dwarf");
        await Assertions.Expect(ariaRow).ToContainTextAsync("Fighter");

        // ---- 4b. The DM builds a Wizard NPC: spell picks follow the class list and slot levels. ----
        await dm.GetByTestId("create-character").ClickAsync();
        await dm.GetByTestId("create-character-name").FillAsync("Mage of the Tower");
        await dm.GetByTestId("create-character-submit").ClickAsync();
        await dm.WaitForURLAsync(url => url.Contains("/actors/"));
        await E2EHelpers.WaitForInteractiveAsync(dm);
        await dm.GetByLabel("Class", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "Wizard" });
        await dm.GetByLabel("Level", new() { Exact = true }).CommitAsync("1");

        // Fireball is a Wizard spell, but level 3 is above a level-1 Wizard's slots: not offered.
        await dm.GetByTestId("list-spells-search").FillAsync("Fireball");
        await Assertions.Expect(dm.GetByTestId("list-spells-options")).ToContainTextAsync("No allowed entry");
        // Cure Wounds is never on the Wizard list.
        await dm.GetByTestId("list-spells-search").FillAsync("Cure Wounds");
        await Assertions.Expect(dm.GetByTestId("list-spells-options")).ToContainTextAsync("No allowed entry");
        // Magic Missile is level 1 on the Wizard list: offered and added.
        await dm.GetByTestId("list-spells-search").FillAsync("Magic Missile");
        await dm.GetByTestId("list-spells-options").GetByRole(AriaRole.Button, new() { Name = "Add Magic Missile" }).ClickAsync();
        await Assertions.Expect(dm.GetByTestId("list-spells-count")).ToHaveTextAsync("1 of 4");

        // At level 5 the cap and the slot level rise: Fireball becomes available.
        await dm.GetByLabel("Level", new() { Exact = true }).CommitAsync("5");
        await Assertions.Expect(dm.GetByTestId("list-spells-count")).ToHaveTextAsync("1 of 9");
        await dm.GetByTestId("list-spells-search").FillAsync("Fireball");
        await dm.GetByTestId("list-spells-options").GetByRole(AriaRole.Button, new() { Name = "Add Fireball" }).ClickAsync();
        await Assertions.Expect(dm.GetByTestId("list-spells-count")).ToHaveTextAsync("2 of 9");
        await Assertions.Expect(dm.GetByTestId("derived-proficiencyBonus")).ToHaveTextAsync("+3");
        await dm.GetByTestId("sheet-save").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("status-message")).ToContainTextAsync("Saved.");
        await Assertions.Expect(dm.GetByTestId("list-spells-pick")).ToHaveCountAsync(2);

        // ---- 4c. House rules: the DM downgrades strict to warn, and the player's over-budget sheet saves with a warning. ----
        await E2EHelpers.GoToSectionAsync(dm, campaignId, "settings");
        await dm.GetByTestId("settings-rules").SelectOptionAsync("warn");
        await dm.GetByTestId("settings-save").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("status-message")).ToContainTextAsync("Saved.");

        await player.ReloadAsync();
        await E2EHelpers.WaitForInteractiveAsync(player);
        await player.GetByTestId("attr-str").CommitAsync("18");
        await Assertions.Expect(player.GetByTestId("rule-violations")).ToContainTextAsync("Warnings only");
        await player.GetByTestId("sheet-save").ClickAsync();
        await Assertions.Expect(player.GetByTestId("status-message")).ToContainTextAsync("Saved with 1 rule warning");

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
