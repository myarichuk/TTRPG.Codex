using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// Templates at the keyboard: a new page starts from the Person outline and Tab steps through its fields; an abbreviation and
/// Tab puts a snippet in; the DM writes a template of their own whose field is used twice, and finds it after "/" in a page.
/// </summary>
[Collection("RavenDb")]
public class TemplatesE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public TemplatesE2ETests(AppFixture app) => _app = app;

    [Fact]
    public async Task TheDmFillsTemplatesWithTab_AndWritesTheirOwn()
    {
        const string password = "Correct-Horse-1";
        var dmId = "tp-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Templates campaign");
        var body = dm.Locator("#wiki-body");
        var hint = dm.GetByTestId("template-hint");

        // A new page from the Person outline: the page becomes a person's, and Tab goes from field to field.
        await E2EHelpers.GotoInteractiveAsync(dm, $"/campaigns/{campaignId}/wiki/new?title=Keeper%20Olm");
        await dm.GetByTestId("wiki-start-builtin:person").ClickAsync();
        await Assertions.Expect(hint).ToContainTextAsync("summary · 1 of 8");
        await Assertions.Expect(dm.GetByTestId("wiki-kind-select")).ToHaveValueAsync("Person");
        // As in an IDE, a field with no default holds its name, drawn greyed over the box, until it is typed over.
        await Assertions.Expect(body).ToHaveValueAsync(new System.Text.RegularExpressions.Regex(@"^summary\n\n## Look and voice\nlook\n"));
        await Assertions.Expect(dm.Locator(".template-marks .tf-ph.tf-current")).ToHaveTextAsync("summary");
        await dm.Keyboard.TypeAsync("Keeper of the lighthouse.");
        await dm.Keyboard.PressAsync("Tab");
        await Assertions.Expect(hint).ToContainTextAsync("look · 2 of 8");
        await dm.Keyboard.TypeAsync("Grey-eyed.");
        // The hint's buttons step too (a phone's keyboard has no Tab), and leave the caret in the box, so the template goes on.
        await dm.GetByTestId("template-prev").ClickAsync();
        await Assertions.Expect(hint).ToContainTextAsync("summary · 1 of 8");
        await dm.GetByTestId("template-next").ClickAsync();
        await Assertions.Expect(hint).ToContainTextAsync("look · 2 of 8");
        await Assertions.Expect(body).ToBeFocusedAsync();
        await dm.Keyboard.PressAsync("Escape");
        await Assertions.Expect(hint).ToBeHiddenAsync();
        // The names still standing in the fields left alone are taken out again.
        await Assertions.Expect(body).ToHaveValueAsync(new System.Text.RegularExpressions.Regex(@"^Keeper of the lighthouse\.\n\n## Look and voice\nGrey-eyed\.\n\n## History\n\n\n## Wants and fears\n\n\n"));
        await Assertions.Expect(dm.Locator(".template-marks")).ToBeHiddenAsync();

        // A snippet by its abbreviation: its default is selected, so typing replaces it; Tab after the last field ends it.
        await body.FillAsync("");
        await body.PressSequentiallyAsync("Road north.\n\nenc");
        await dm.Keyboard.PressAsync("Tab");
        await dm.Keyboard.TypeAsync("Bandits");
        for (var i = 0; i < 5; i++) await dm.Keyboard.PressAsync("Tab");
        await Assertions.Expect(hint).ToBeHiddenAsync();
        await dm.Keyboard.TypeAsync("Then home.");
        await Assertions.Expect(body).ToHaveValueAsync("Road north.\n\n### Bandits\n**When:** \n**Who:** \n**How it goes:** \n**Outcome:** \nThen home.");

        // Their own template, with a field used twice and a default.
        await E2EHelpers.GotoInteractiveAsync(dm, $"/campaigns/{campaignId}/templates");
        await dm.GetByTestId("template-new").ClickAsync();
        await dm.GetByTestId("template-name").FillAsync("Gift");
        await dm.GetByTestId("template-abbreviation").FillAsync("gv");
        await dm.Locator("#template-body").FillAsync("$who$ gives $item:a coin$; $who$ smiles.");
        await dm.GetByTestId("template-save").ClickAsync();
        await Assertions.Expect(dm.GetByTestId("template-fields")).ToContainTextAsync("who, item");
        await Assertions.Expect(dm.Locator(".template-row", new() { HasText = "Gift" })).ToBeVisibleAsync();

        // In a page, found by name after "/"; what is typed in the field fills its other use too.
        await E2EHelpers.GotoInteractiveAsync(dm, $"/campaigns/{campaignId}/wiki/new?title=Mara");
        await body.PressSequentiallyAsync("/gif");
        await Assertions.Expect(dm.Locator("#mention-list")).ToContainTextAsync("gv");
        await dm.Keyboard.PressAsync("Enter");
        await dm.Keyboard.TypeAsync("Mara");
        await Assertions.Expect(body).ToHaveValueAsync("Mara gives a coin; Mara smiles.");

        // What Blazor holds is the mirrored text: it is what the page saves.
        await dm.GetByTestId("wiki-save").ClickAsync();
        await dm.WaitForURLAsync(url => url.EndsWith("/wiki/mara"));
        await Assertions.Expect(dm.GetByTestId("wiki-body")).ToHaveTextAsync("Mara gives a coin; Mara smiles.");

        // Saved with Ctrl+S while a field still holds its name: the name never reaches the page.
        await E2EHelpers.GotoInteractiveAsync(dm, $"/campaigns/{campaignId}/wiki/new?title=Olm");
        await body.PressSequentiallyAsync("/gif");
        await dm.Keyboard.PressAsync("Enter");
        await Assertions.Expect(body).ToHaveValueAsync("who gives a coin; who smiles.");
        await body.PressAsync("Control+s");
        await dm.WaitForURLAsync(url => url.EndsWith("/wiki/olm"));
        await Assertions.Expect(dm.GetByTestId("wiki-body")).ToContainTextAsync("gives a coin;");
        await Assertions.Expect(dm.GetByTestId("wiki-body")).Not.ToContainTextAsync("who");
    }
}
