using Codex.Persistence;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// Guided character creation (6.4): one martial, one wizard, one cleric, and one warlock
/// walk the real wizard UI. The warlock case proves patron data flows end to end -
/// Burning Hands is NOT on the base warlock list, so its presence in the level-1 spell
/// choices means the Fiend patron's expanded list merged correctly.
///
/// Shares the "RavenDb" collection with every other embedded-RavenDB test (see
/// CampaignRuntimeTests.cs/PersistenceTest.cs) - this scenario's RavenDB instance lives
/// inside a child process, and the collection keeps the machine from running several
/// embedded RavenDB servers at once.
/// </summary>
[Collection("RavenDb")]
public class CharacterCreationTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public CharacterCreationTests(AppFixture app) => _app = app;

    [Fact]
    public async Task FourPlayers_CreateMartialWizardClericWarlock()
    {
        const string password = "Secret-1";
        var dmId = "cc-dm-" + Guid.NewGuid();
        var fighterId = "cc-fighter-" + Guid.NewGuid();
        var wizardId = "cc-wizard-" + Guid.NewGuid();
        var clericId = "cc-cleric-" + Guid.NewGuid();
        var warlockId = "cc-warlock-" + Guid.NewGuid();

        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await _app.SeedUserAsync(fighterId, "borin_" + fighterId, password, "Player");
        await _app.SeedUserAsync(wizardId, "elara_" + wizardId, password, "Player");
        await _app.SeedUserAsync(clericId, "durn_" + clericId, password, "Player");
        await _app.SeedUserAsync(warlockId, "mira_" + warlockId, password, "Player");

        var browser = await _app.GetBrowserAsync();
        var contextOptions = new BrowserNewContextOptions { BaseURL = _app.BaseUrl };
        await using var dmContext = await browser.NewContextAsync(contextOptions);
        await using var fighterContext = await browser.NewContextAsync(contextOptions);
        await using var wizardContext = await browser.NewContextAsync(contextOptions);
        await using var clericContext = await browser.NewContextAsync(contextOptions);
        await using var warlockContext = await browser.NewContextAsync(contextOptions);

        var dm = await dmContext.NewPageAsync();
        var fighter = await fighterContext.NewPageAsync();
        var wizard = await wizardContext.NewPageAsync();
        var cleric = await clericContext.NewPageAsync();
        var warlock = await warlockContext.NewPageAsync();

        await LoginAsync(dm, "dm_" + dmId, password);
        await LoginAsync(fighter, "borin_" + fighterId, password);
        await LoginAsync(wizard, "elara_" + wizardId, password);
        await LoginAsync(cleric, "durn_" + clericId, password);
        await LoginAsync(warlock, "mira_" + warlockId, password);

        await dm.GotoAsync("/campaigns");
        await dm.GetByRole(AriaRole.Button, new() { Name = "Create Your First Campaign" }).ClickAsync();
        await dm.GetByRole(AriaRole.Button, new() { Name = "Combat" }).WaitForAsync();
        var campaignId = new Uri(dm.Url).Segments[^1].TrimEnd('/');

        var inviteUrl = await dm.Locator("div.card:has(h5:text('Invite')) input").InputValueAsync();
        foreach (var player in new[] { fighter, wizard, cleric, warlock })
        {
            await player.GotoAsync(inviteUrl);
            await player.GetByText("You're in!").WaitForAsync();
            await player.GetByRole(AriaRole.Link, new() { Name = "Go to Campaign" }).ClickAsync();
        }

        // Martial: fighter with the standard array (CON 14 + human +1 = +2 -> HP 12).
        // Uses the in-page entry link to prove the wizard is reachable from the campaign page.
        await fighter.GetByRole(AriaRole.Button, new() { Name = "Cast" }).ClickAsync();
        await fighter.GetByTestId("create-character-link").ClickAsync();
        await CreateFighterAsync(fighter, "Borin-" + Guid.NewGuid().ToString("N")[..6]);

        // Wizard: elf + high-elf subrace, 3 cantrips + 6 level-1 (spellbook fallback).
        await wizard.GotoAsync($"/campaigns/{campaignId}/create");
        await CreateWizardAsync(wizard, "Elara-" + Guid.NewGuid().ToString("N")[..6]);

        // Cleric: dwarf + hill-dwarf (CON 10 + 2 = +1 -> HP 9), 3 cantrips, prepared L1.
        await cleric.GotoAsync($"/campaigns/{campaignId}/create");
        await CreateClericAsync(cleric, "Durn-" + Guid.NewGuid().ToString("N")[..6]);

        // Warlock: halfling + lightfoot, Fiend patron, 2 cantrips + 2 L1 including
        // Burning Hands - only available via the patron's expanded list.
        await warlock.GotoAsync($"/campaigns/{campaignId}/create");
        await CreateWarlockAsync(warlock, "Mira-" + Guid.NewGuid().ToString("N")[..6]);
    }

    private static Task LoginAsync(IPage page, string username, string password) =>
        LoginHelper.LoginAsync(page, username, password);

    private static async Task NameAndNextAsync(IPage page, string name)
    {
        // The Name step is the first interaction after a full-page load, so the fill can land
        // before the Blazor circuit connects - the input event fires into the void, the
        // re-render resets the box, and Next stays disabled forever. Refill until the circuit
        // proves it's listening (Next enables); every later step rides the proven circuit.
        var input = page.GetByTestId("wizard-name");
        var next = page.GetByTestId("wizard-next");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await input.FillAsync(name);
            try
            {
                await Assertions.Expect(next).ToBeEnabledAsync(new() { Timeout = 3000 });
                break;
            }
            catch (PlaywrightException) when (attempt < 4)
            {
                // Circuit wasn't listening yet - loop around and refill.
            }
        }

        await next.ClickAsync();
    }

    private static async Task PickCardAndNextAsync(IPage page, string testId)
    {
        await page.GetByTestId(testId).ClickAsync();
        await page.GetByTestId("wizard-next").ClickAsync();
    }

    private static async Task AssertSheetShowsAsync(IPage page, string name, string className)
    {
        await page.WaitForURLAsync(url => url.Contains("/actors/"));
        await page.GetByText(name).First.WaitForAsync();
        await page.GetByText(className).First.WaitForAsync();
    }

    private static async Task CreateFighterAsync(IPage page, string name)
    {
        await page.GetByTestId("wizard-step-Name").WaitForAsync();
        await NameAndNextAsync(page, name);

        await PickCardAndNextAsync(page, "class-card-fighter");
        await PickCardAndNextAsync(page, "subclass-card-champion");
        await PickCardAndNextAsync(page, "race-card-human");

        await page.GetByTestId("standard-array").ClickAsync();
        await page.GetByTestId("wizard-next").ClickAsync();

        await page.GetByTestId("wizard-step-Equipment").WaitForAsync();
        await page.GetByTestId("wizard-next").ClickAsync();

        // No spells step for fighters - straight to review with HP 12.
        await page.GetByTestId("wizard-step-Review").WaitForAsync();
        await Assertions.Expect(page.GetByTestId("review-hp")).ToHaveTextAsync("12");
        await page.GetByTestId("wizard-create").ClickAsync();

        await AssertSheetShowsAsync(page, name, "Fighter");
    }

    private static async Task CreateWizardAsync(IPage page, string name)
    {
        await page.GetByTestId("wizard-step-Name").WaitForAsync();
        await NameAndNextAsync(page, name);

        await page.GetByTestId("class-card-wizard").ClickAsync();
        await page.GetByTestId("wizard-next").ClickAsync();
        await PickCardAndNextAsync(page, "subclass-card-evocation");

        await page.GetByTestId("race-card-elf").ClickAsync();
        await page.Locator("[data-testid=subrace-select]").SelectOptionAsync("high-elf");
        await page.GetByTestId("wizard-next").ClickAsync();

        await page.GetByTestId("wizard-step-Scores").WaitForAsync();
        await page.GetByTestId("wizard-next").ClickAsync();

        await page.GetByTestId("wizard-step-Equipment").WaitForAsync();
        await page.GetByTestId("wizard-next").ClickAsync();

        await page.GetByTestId("wizard-step-Spells").WaitForAsync();
        var cantrips = page.GetByTestId("spells-cantrips").Locator("input[type=checkbox]");
        for (var i = 0; i < 3; i++)
        {
            await cantrips.Nth(i).CheckAsync();
        }

        var level1 = page.GetByTestId("spells-level1").Locator("input[type=checkbox]");
        for (var i = 0; i < 6; i++)
        {
            await level1.Nth(i).CheckAsync();
        }

        await page.GetByTestId("wizard-next").ClickAsync();

        await page.GetByTestId("wizard-step-Review").WaitForAsync();
        await Assertions.Expect(page.GetByTestId("review-hp")).ToHaveTextAsync("6");
        await page.GetByTestId("wizard-create").ClickAsync();

        await AssertSheetShowsAsync(page, name, "Wizard");
    }

    private static async Task CreateClericAsync(IPage page, string name)
    {
        await page.GetByTestId("wizard-step-Name").WaitForAsync();
        await NameAndNextAsync(page, name);

        await page.GetByTestId("class-card-cleric").ClickAsync();
        await page.GetByTestId("wizard-next").ClickAsync();
        await PickCardAndNextAsync(page, "subclass-card-life");

        await page.GetByTestId("race-card-dwarf").ClickAsync();
        await page.Locator("[data-testid=subrace-select]").SelectOptionAsync("hill-dwarf");
        await page.GetByTestId("wizard-next").ClickAsync();

        await page.GetByTestId("wizard-step-Scores").WaitForAsync();
        await page.GetByTestId("wizard-next").ClickAsync();

        await page.GetByTestId("wizard-step-Equipment").WaitForAsync();
        await page.GetByTestId("wizard-next").ClickAsync();

        // Clerics prepare level-1 spells from the full list - only cantrips to pick.
        await page.GetByTestId("wizard-step-Spells").WaitForAsync();
        var cantrips = page.GetByTestId("spells-cantrips").Locator("input[type=checkbox]");
        for (var i = 0; i < 3; i++)
        {
            await cantrips.Nth(i).CheckAsync();
        }

        await page.GetByTestId("wizard-next").ClickAsync();

        await page.GetByTestId("wizard-step-Review").WaitForAsync();
        await Assertions.Expect(page.GetByTestId("review-hp")).ToHaveTextAsync("9");
        await page.GetByTestId("wizard-create").ClickAsync();

        await AssertSheetShowsAsync(page, name, "Cleric");
    }

    private static async Task CreateWarlockAsync(IPage page, string name)
    {
        await page.GetByTestId("wizard-step-Name").WaitForAsync();
        await NameAndNextAsync(page, name);

        await page.GetByTestId("class-card-warlock").ClickAsync();
        await page.GetByTestId("wizard-next").ClickAsync();
        await PickCardAndNextAsync(page, "subclass-card-fiend");

        await page.GetByTestId("race-card-halfling").ClickAsync();
        await page.Locator("[data-testid=subrace-select]").SelectOptionAsync("lightfoot-halfling");
        await page.GetByTestId("wizard-next").ClickAsync();

        await page.GetByTestId("wizard-step-Scores").WaitForAsync();
        await page.GetByTestId("wizard-next").ClickAsync();

        await page.GetByTestId("wizard-step-Equipment").WaitForAsync();
        await page.GetByTestId("wizard-next").ClickAsync();

        await page.GetByTestId("wizard-step-Spells").WaitForAsync();
        var cantrips = page.GetByTestId("spells-cantrips").Locator("input[type=checkbox]");
        await cantrips.Nth(0).CheckAsync();
        await cantrips.Nth(1).CheckAsync();

        // Burning Hands is Fiend-only (not on the base warlock list) - its presence
        // proves the patron's expanded spells merged into the choices.
        var burningHands = page.GetByTestId("spell-burning-hands");
        await burningHands.WaitForAsync();
        await burningHands.CheckAsync();
        var level1 = page.GetByTestId("spells-level1").Locator("input[type=checkbox]");
        var first = level1.Nth(0);
        if (!await first.IsCheckedAsync())
        {
            await first.CheckAsync();
        }
        else
        {
            await level1.Nth(1).CheckAsync();
        }

        await page.GetByTestId("wizard-next").ClickAsync();

        await page.GetByTestId("wizard-step-Review").WaitForAsync();
        await Assertions.Expect(page.GetByTestId("review-hp")).ToHaveTextAsync("8");
        await page.GetByTestId("wizard-create").ClickAsync();

        await AssertSheetShowsAsync(page, name, "Warlock");
    }
}
