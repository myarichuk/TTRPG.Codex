using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// The book editor's preview in each browser engine a GM may use (docs/BOOK_EXPORT.md 41.20): Chromium, WebKit (Safari's) and Firefox. The PDF is
/// always printed by the server's Chromium; the preview is drawn by the GM's own browser, so it must draw, measure and update there too.
/// An engine this machine does not have is passed over (only Chromium is required; <c>playwright install webkit firefox</c> adds the others).
/// </summary>
[Collection("RavenDb")]
public class BookDraftEnginesE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public BookDraftEnginesE2ETests(AppFixture app) => _app = app;

    // Each chapter page with text in its second column, and how far above the foot of the content box its first column ends, in the page's
    // own pixels. The columns fill top to bottom, so a page that reached its second column has a full first one.
    private const string FirstColumnGaps = @"() => {
        const d = document.querySelector('[data-testid=book-draft-preview]').contentDocument, w = d.defaultView;
        return [...d.querySelectorAll('.page')].filter(p => p.dataset.section && p.dataset.section !== 'front').map(page => {
            const box = page.getBoundingClientRect(), scale = box.width / page.offsetWidth, cs = w.getComputedStyle(page);
            const foot = box.bottom - parseFloat(cs.paddingBottom) * scale, middle = box.left + box.width / 2;
            // Text only, as Fit pages counts it: no page number or footer (placed absolutely), no empty filler, not the chapter's title.
            const text = el => !['absolute', 'fixed'].includes(w.getComputedStyle(el).position) && el.textContent.trim() !== '' && el.tagName !== 'H1';
            const rects = [...page.querySelectorAll('.columnWrapper > *')].filter(text).flatMap(el => [...el.getClientRects()]).filter(r => r.height > 0);
            const left = rects.filter(r => r.right <= middle), right = rects.filter(r => r.left >= middle);
            return right.length && left.length ? Math.round((foot - Math.max(...left.map(r => r.bottom))) / scale) : null;
        }).filter(g => g !== null);
    }";

    // Each page's first words.
    private const string PageStarts = @"() => [...document.querySelector('[data-testid=book-draft-preview]').contentDocument.querySelectorAll('.page')]
        .map(p => p.textContent.replace(/\s+/g, ' ').trim().slice(0, 40)).join(' | ')";

    [Theory]
    [InlineData("chromium")]
    [InlineData("webkit")]
    [InlineData("firefox")]
    public async Task ThePreview_Draws_Updates_ChangesTheme_AndSaysPagesRunOn_InEveryEngine(string engine)
    {
        if (await _app.EngineAsync(engine) is not { } browser) return;
        const string password = "Correct-Horse-1";
        var dmId = "bde-dm-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        var errors = new List<string>();
        async Task<IPage> SignInAsync(IBrowserContext context)
        {
            var page = await context.NewPageAsync();
            page.PageError += (_, e) => errors.Add(e);
            // WebKit says it blocked a script in the preview frame when the test reads the frame while it is still loading: that is the test
            // driver's own script (with a pause before the first read, six runs out of six were clean, and the app draws in that pause). The
            // frame runs no script by design; nothing of the app's tries to.
            page.Console += (_, m) =>
            {
                if (m.Type == "error" && !(engine == "webkit" && m.Text.StartsWith("Blocked script execution in 'about:srcdoc'", StringComparison.Ordinal)))
                    errors.Add(m.Text);
            };
            await E2EHelpers.LoginAsync(page, "dm_" + dmId, password);
            return page;
        }

        async Task OpenAsync(IPage page, string campaign)
        {
            await E2EHelpers.GoToSectionAsync(page, campaign, "settings");
            // From the book on: Firefox logs a connection the sign-in's own reload cut short, which is the helpers', not the book's.
            errors.Clear();
            await page.GetByTestId("book-edition").SelectOptionAsync("party");
            await page.GetByTestId("book-tab-draft").ClickAsync();
            await page.GetByTestId("book-draft-open").ClickAsync();
            await Assertions.Expect(page.GetByTestId("book-draft-dialog")).ToBeVisibleAsync();
        }

        static string Story(int lines) => string.Join("\n\n", Enumerable.Range(0, lines).Select(i => $"Line {i} of the keeper's long story, which goes on past one page and then another."));

        // A chapter that fills its first page and a half: the page under the chapter's title, with text in both columns, has its first column
        // full, as the PDF's has (the columns fill top to bottom in every engine; a balanced pair would show two half columns).
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await SignInAsync(context);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");
        await _app.SeedActorsAsync([
            new Codex.Web.TestSeedActor { Id = "bde-vera-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Vera", Biography = "Keeps the lighthouse.", Visibility = Codex.Persistence.ActorVisibility.Known },
            new Codex.Web.TestSeedActor { Id = "bde-short-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Short", Biography = Story(30), Visibility = Codex.Persistence.ActorVisibility.Known }]);
        await OpenAsync(dm, campaignId);
        var preview = dm.FrameLocator("[data-testid=book-draft-preview]");
        await Assertions.Expect(preview.Locator(".page h1").First).ToHaveTextAsync("Saltmere Chronicle");
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "Keeps the lighthouse." })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.GetByTestId("book-draft-run-on")).ToBeHiddenAsync();
        var gaps = await dm.EvaluateAsync<int[]>(FirstColumnGaps);
        Assert.True(gaps.Length == 1 && gaps[0] is >= 0 and < 60, "first columns end above the foot by: " + string.Join(", ", gaps));

        // The campaign grows past its pages, and the book is opened in a fresh browser (nothing of the theme's faces loaded yet). The long
        // story runs past its page: the preview measures that in this engine and says so.
        string[] names = ["Ash Morrow", "Brother Oswin", "Captain Ilse Varn", "Mistress Ondine", "Old Tam", "Pell the Ferryman", "The Widow Crane"];
        await _app.SeedActorsAsync(names.Select((name, i) => new Codex.Web.TestSeedActor
        {
            Id = $"bde-{i}-" + Guid.NewGuid(), CampaignId = campaignId, Name = name, Visibility = Codex.Persistence.ActorVisibility.Known,
            Biography = string.Join("\n\n", Enumerable.Range(0, 3 + i % 4).Select(k => $"{name} paragraph {k}: keeps watch over the harbour lights, trades rumours for salt fish, and remembers every storm since the bell tower fell into the sea. Nobody asks twice."))
        }));
        await _app.SeedActorsAsync([
            new Codex.Web.TestSeedActor { Id = "bde-long-" + Guid.NewGuid(), CampaignId = campaignId, Name = "Long", Biography = Story(130), Visibility = Codex.Persistence.ActorVisibility.Known }]);
        await context.CloseAsync();
        await using var fresh = await browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        dm = await SignInAsync(fresh);
        await OpenAsync(dm, campaignId);
        preview = dm.FrameLocator("[data-testid=book-draft-preview]");
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "Line 129 of" })).ToHaveCountAsync(1);
        await Assertions.Expect(dm.GetByTestId("book-draft-run-on")).ToBeVisibleAsync();
        gaps = await dm.EvaluateAsync<int[]>(FirstColumnGaps);
        Assert.True(gaps.Length >= 2 && gaps.All(g => g is >= 0 and < 60), "first columns end above the foot by: " + string.Join(", ", gaps));

        // The first drawing is measured in the theme's own faces: drawn again, nothing moves (Safari's engine measured the first in fallback
        // faces, and broke pages early).
        var first = await dm.EvaluateAsync<string>(PageStarts);
        await dm.GetByTestId("book-draft-theme").SelectOptionAsync("ashen-vellum");
        await Assertions.Expect(preview.Locator("body.theme-ashen-vellum")).ToHaveCountAsync(1);
        await dm.GetByTestId("book-draft-theme").SelectOptionAsync("plain");
        await Assertions.Expect(preview.Locator("body.theme-plain")).ToHaveCountAsync(1);
        var again = "";
        for (var tries = 0; tries < 50 && (again = await dm.EvaluateAsync<string>(PageStarts)) != first; tries++) await dm.WaitForTimeoutAsync(100);
        Assert.Equal(first, again);
        // Themes picked in quick succession leave one editor (an opening began while the last was still drawing; Firefox kept both).
        foreach (var theme in new[] { "ashen-vellum", "plain", "ashen-vellum", "plain" }) await dm.GetByTestId("book-draft-theme").SelectOptionAsync(theme);
        await Assertions.Expect(preview.Locator("body.theme-plain")).ToHaveCountAsync(1);
        await dm.WaitForTimeoutAsync(1500);
        await Assertions.Expect(dm.Locator(".cm-content")).ToHaveCountAsync(1);

        await dm.Locator("[data-testid=book-draft-chapter][data-section=cast]").ClickAsync();
        await dm.Locator(".cm-content").ClickAsync();
        await dm.Keyboard.PressAsync("ControlOrMeta+End");
        await dm.Keyboard.TypeAsync("\nPUBLIC_GM_LINE\n");
        await Assertions.Expect(dm.Locator("[data-testid=book-draft-chapter][data-section=cast] .book-draft-badge-edited")).ToBeVisibleAsync();
        await Assertions.Expect(dm.GetByTestId("book-draft-save-state")).ToHaveTextAsync("Saved");
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "PUBLIC_GM_LINE" })).ToHaveCountAsync(1);

        await dm.GetByTestId("book-draft-theme").SelectOptionAsync("ashen-vellum");
        await Assertions.Expect(preview.Locator("body.theme-ashen-vellum")).ToHaveCountAsync(1);
        await Assertions.Expect(preview.Locator(".page", new() { HasText = "PUBLIC_GM_LINE" })).ToHaveCountAsync(1);

        Assert.True(errors.Count == 0, string.Join("\n----\n", errors));
    }
}
