using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// Pictures in the text (docs/BOOK_EXPORT.md 53): the chips under the entries' headings. Run against a fake model server: nothing here is a
/// real model, and the test counts what was sent to prove that a chip sends nothing until its second click.
/// </summary>
[Collection("RavenDb")]
public class BookPicturesE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public BookPicturesE2ETests(AppFixture app) => _app = app;

    private static async Task AddModelAsync(IPage dm, string url, bool pictures)
    {
        await dm.GotoAsync("/settings/ai");
        await dm.GetByTestId(pictures ? "ai-add-image-provider" : "ai-add-provider").ClickAsync();
        await dm.GetByTestId("ai-kind").SelectOptionAsync("custom");
        await dm.GetByTestId("ai-name").FillAsync(pictures ? "Fake pictures" : "Fake model");
        await dm.GetByTestId("ai-url").FillAsync(url);
        await dm.GetByTestId("ai-model").FillAsync(pictures ? "fake-pictures" : "fake-model");
        if (pictures)
        {
            await dm.GetByTestId("ai-price-image").FillAsync("0.05");
            await dm.GetByTestId("ai-price-image-label").FillAsync("USD");
        }

        await dm.GetByTestId("ai-save").ClickAsync();
        await Assertions.Expect(dm.GetByTestId(pictures ? "ai-image-provider-list" : "ai-provider-list")).ToContainTextAsync(pictures ? "Fake pictures" : "Fake model");
    }

    private static ILocator Chip(IPage dm, string label) => dm.Locator($".cm-book-chip[aria-label='Picture for {label}']");

    private static Task<string> TextAsync(IPage dm) =>
        // The chapter's lines without what the editor draws beside them (the name after a picture's key).
        dm.EvaluateAsync<string>("[...document.querySelectorAll('.cm-content .cm-line')].map(l => { const c = l.cloneNode(true); c.querySelectorAll('.cm-book-arthint').forEach(x => x.remove()); return c.textContent; }).join('\\n')");

    [Fact]
    public async Task AChipSuggestsAndDrawsOnlyOnItsSecondClick_PutsThePictureUnderItsHeading_AndNeverChangesTheTextByItself()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bpic-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await using var model = new BookAiPanelE2ETests.FakeModelServer();
        await using var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await context.NewPageAsync();
        var errors = new List<string>();
        dm.PageError += (_, e) => errors.Add(e);
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Lamplight");
        await _app.SeedActorsAsync(
        [
            new Codex.Web.TestSeedActor { Id = "bpic-orin-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Orin", Biography = "Lights the lamps.", Visibility = Codex.Persistence.ActorVisibility.Known },
            new Codex.Web.TestSeedActor { Id = "bpic-vera-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Vera", Biography = "Keeps the lighthouse.", Visibility = Codex.Persistence.ActorVisibility.Known }
        ]);
        await AddModelAsync(dm, model.Url, pictures: false);

        await E2EHelpers.OpenBookAsync(dm, campaignId, "party", "cast");
        var preview = dm.FrameLocator("[data-testid=book-draft-preview]");
        await Assertions.Expect(preview.Locator(".page").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        var vera = Chip(dm, "Vera");
        var orin = Chip(dm, "Orin");
        await Assertions.Expect(vera).ToHaveAttributeAsync("data-state", "empty", new() { Timeout = 15_000 });
        await Assertions.Expect(orin).ToHaveAttributeAsync("data-state", "empty");
        // A chip is beside the text, not in it: the chapter holds no picture and is as saved.
        var before = await TextAsync(dm);
        Assert.DoesNotContain("art:", before);
        Assert.DoesNotContain("Add picture", before);
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved");
        await Assertions.Expect(preview.Locator("[data-ghost]")).ToHaveCountAsync(0);

        // Alt-Enter in the text goes to the chip of the entry the cursor is in; Escape comes back.
        await dm.Locator(".cm-line", new() { HasText = "Keeps the lighthouse." }).ClickAsync();
        await dm.Keyboard.PressAsync("Alt+Enter");
        Assert.Equal("Picture for Vera", await dm.EvaluateAsync<string>("document.activeElement.closest('.cm-book-chip')?.getAttribute('aria-label') ?? ''"));
        await dm.Keyboard.PressAsync("Escape");
        Assert.True(await dm.EvaluateAsync<bool>("document.activeElement.classList.contains('cm-content')"));

        // Suggest: the cost first, and nothing sent until "Ask".
        var sent = model.Requests.Count;
        await vera.GetByRole(AriaRole.Button, new() { Name = "Add picture" }).ClickAsync();
        await vera.GetByRole(AriaRole.Menuitem, new() { Name = "Suggest a picture" }).ClickAsync();
        await Assertions.Expect(vera.Locator(".cm-book-chip-ask")).ToContainTextAsync(new Regex(@"Ask about 3 places in Appendix A: Cast, with Fake model"));
        Assert.Equal(sent, model.Requests.Count);
        await vera.GetByRole(AriaRole.Button, new() { Name = "Ask", Exact = true }).ClickAsync();
        await Assertions.Expect(vera).ToHaveAttributeAsync("data-state", "idea", new() { Timeout = 30_000 });
        await Assertions.Expect(vera).ToContainTextAsync("Picture: Vera");
        await Assertions.Expect(orin).ToHaveAttributeAsync("data-state", "idea");
        Assert.Equal(sent + 1, model.Requests.Count);
        await Assertions.Expect(dm.GetByTestId("book-chip-status")).ToContainTextAsync("Done");
        // Where a suggested picture would sit, in the preview only.
        await Assertions.Expect(preview.Locator("[data-ghost]")).ToHaveCountAsync(2);
        Assert.Equal(before, await TextAsync(dm));

        // No picture model: Draw is disabled and says why, with the way to set one; Upload still works.
        var draw = vera.GetByRole(AriaRole.Button, new() { Name = "Draw", Exact = true });
        await Assertions.Expect(draw).ToHaveAttributeAsync("aria-disabled", "true");
        await draw.FocusAsync();
        var tip = dm.Locator("#" + await draw.GetAttributeAsync("aria-describedby"));
        await Assertions.Expect(tip).ToHaveAttributeAsync("role", "tooltip");
        await Assertions.Expect(tip).ToBeVisibleAsync();
        await Assertions.Expect(tip).ToContainTextAsync("No picture model is set.");
        await Assertions.Expect(tip.Locator("a")).ToHaveAttributeAsync("href", "/settings/ai");
        // Shown on focus, gone when the focus leaves the chip.
        await dm.Locator(".cm-line", new() { HasText = "Lights the lamps." }).ClickAsync();
        await Assertions.Expect(tip).ToBeHiddenAsync();
        await draw.ClickAsync(new() { Force = true });
        await Assertions.Expect(vera.Locator(".cm-book-chip-ask")).ToHaveCountAsync(0);

        // Dismiss Orin's suggestion, then upload a picture for him: its block goes under his heading, and no model is asked.
        await orin.GetByRole(AriaRole.Button, new() { Name = "Dismiss" }).ClickAsync();
        await Assertions.Expect(orin).ToHaveAttributeAsync("data-state", "empty");
        await Assertions.Expect(preview.Locator("[data-ghost]")).ToHaveCountAsync(1);
        await orin.GetByRole(AriaRole.Button, new() { Name = "Add picture" }).ClickAsync();
        var chooser = await dm.RunAndWaitForFileChooserAsync(() => orin.GetByRole(AriaRole.Menuitem, new() { Name = "Upload" }).ClickAsync());
        await chooser.SetFilesAsync(new FilePayload { Name = "orin.png", MimeType = "image/png", Buffer = BookArtTests.Png(300) });
        await Assertions.Expect(dm.GetByTestId("book-chip-status")).ToContainTextAsync("The picture is in the text", new() { Timeout = 30_000 });
        await Assertions.Expect(orin).ToHaveCountAsync(0);
        var withOrin = await TextAsync(dm);
        Assert.Matches(new Regex(@"## Orin\n\n\{\{portrait\n!\[Orin\]\(art:s[0-9a-f]{24}\)\n\}\}\n\nLights the lamps\."), withOrin);
        Assert.Empty(model.ImagePrompts);
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved", new() { Timeout = 15_000 });
        // The name of the place after its key.
        await Assertions.Expect(dm.Locator(".cm-book-arthint")).ToHaveTextAsync("Orin");
        await Assertions.Expect(preview.Locator("img[data-art]")).ToHaveCountAsync(1, new() { Timeout = 15_000 });

        // With a picture model and a price: the tooltip holds the prompt and the price, Draw asks first, and only "Draw it" draws.
        await AddModelAsync(dm, model.Url, pictures: true);
        await E2EHelpers.OpenBookAsync(dm, campaignId, "party", "cast");
        vera = Chip(dm, "Vera");
        await Assertions.Expect(vera).ToHaveAttributeAsync("data-state", "idea", new() { Timeout = 15_000 });
        draw = vera.GetByRole(AriaRole.Button, new() { Name = "Draw", Exact = true });
        await Assertions.Expect(draw).Not.ToHaveAttributeAsync("aria-disabled", "true");
        await draw.FocusAsync();
        tip = dm.Locator("#" + await draw.GetAttributeAsync("aria-describedby"));
        await Assertions.Expect(tip).ToBeVisibleAsync();
        await Assertions.Expect(tip).ToContainTextAsync("a lamp burning in a high window above dark water");
        await Assertions.Expect(tip).ToContainTextAsync("About 0.050 USD a picture");
        var shown = await tip.Locator(".cm-book-chip-prompt").InnerTextAsync();
        await draw.ClickAsync();
        await Assertions.Expect(vera.Locator(".cm-book-chip-ask")).ToContainTextAsync("Draw this picture, about 0.050 USD?");
        Assert.Empty(model.ImagePrompts);
        await vera.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Assertions.Expect(vera.Locator(".cm-book-chip-ask")).ToHaveCountAsync(0);
        Assert.Empty(model.ImagePrompts);
        await draw.ClickAsync();
        await vera.GetByRole(AriaRole.Button, new() { Name = "Draw it" }).ClickAsync();
        await Assertions.Expect(dm.GetByTestId("book-chip-status")).ToContainTextAsync("Done: drew 1 of 1", new() { Timeout = 30_000 });
        await Assertions.Expect(vera).ToHaveCountAsync(0);
        // What was sent is what the chip showed, and it names nobody.
        var prompt = Assert.Single(model.ImagePrompts);
        Assert.Equal(shown.Trim(), prompt.Trim());
        Assert.DoesNotContain("Vera", prompt);
        Assert.Matches(new Regex(@"## Vera\n\n\{\{portrait\n!\[Vera\]\(art:s[0-9a-f]{24}\)\n\}\}\n\nKeeps the lighthouse\."), await TextAsync(dm));
        await Assertions.Expect(preview.Locator("[data-ghost]")).ToHaveCountAsync(0);
        await Assertions.Expect(preview.Locator("img[data-art]")).ToHaveCountAsync(2, new() { Timeout = 15_000 });
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved", new() { Timeout = 15_000 });
        // The chapter is still the generator's own: saved with the block, it shows no notice that the campaign moved on.
        await Assertions.Expect(dm.GetByTestId("book-draft-stale")).ToHaveCountAsync(0);

        // One edit: Ctrl-Z takes the block out again, and the chip then offers to put the picture (which is still the place's) back.
        await dm.Locator(".cm-line", new() { HasText = "Keeps the lighthouse." }).ClickAsync();
        await dm.Keyboard.PressAsync("ControlOrMeta+z");
        await Assertions.Expect(vera).ToHaveAttributeAsync("data-state", "ready");
        Assert.DoesNotMatch(new Regex(@"!\[Vera\]"), await TextAsync(dm));
        await vera.GetByRole(AriaRole.Button, new() { Name = "Put it in the text" }).ClickAsync();
        await Assertions.Expect(vera).ToHaveCountAsync(0);
        Assert.Matches(new Regex(@"!\[Vera\]\(art:"), await TextAsync(dm));
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved", new() { Timeout = 15_000 });

        // Opened again, the chapter is the generator's own: no notice that the campaign moved on.
        await E2EHelpers.OpenBookAsync(dm, campaignId, "party", "cast");
        await Assertions.Expect(dm.Locator(".cm-book-arthint")).ToHaveCountAsync(2, new() { Timeout = 15_000 });
        await Assertions.Expect(dm.GetByTestId("book-draft-stale")).ToHaveCountAsync(0);

        // Suggestions and help off: no chips and no names, the text as it is.
        await Assertions.Expect(dm.Locator(".cm-book-chip")).ToHaveCountAsync(0);
        await dm.GetByTestId("book-draft-more").ClickAsync();
        await dm.GetByTestId("book-draft-assist").ClickAsync();
        await Assertions.Expect(dm.Locator(".cm-book-arthint")).ToHaveCountAsync(0);
        await Assertions.Expect(dm.Locator(".cm-book-chip")).ToHaveCountAsync(0);
        Assert.Empty(errors);
    }
}
