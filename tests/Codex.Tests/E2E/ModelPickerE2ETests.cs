using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// The model field on the AI settings page: a combobox that suggests models, narrows as you type, is driven from the keyboard, and
/// still takes any text. For OpenRouter the suggestions are its live list (here a fake one, so no test needs the internet) and a
/// picked model brings the prices it lists.
/// </summary>
[Collection("RavenDb")]
public class ModelPickerE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public ModelPickerE2ETests(AppFixture app) => _app = app;

    private async Task<(IBrowserContext Context, IPage Page)> OpenAsync()
    {
        const string password = "Correct-Horse-1";
        var id = "mp-" + Guid.NewGuid().ToString("N")[..12];
        await _app.SeedUserAsync(id, "u_" + id, password, "Player");
        var context = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var page = await context.NewPageAsync();
        await E2EHelpers.LoginAsync(page, "u_" + id, password);
        await E2EHelpers.GotoInteractiveAsync(page, "/settings/ai");
        return (context, page);
    }

    private static ILocator Input(IPage page) => page.GetByTestId("ai-model");

    private static ILocator List(IPage page) => page.GetByTestId("ai-model-list");

    private static ILocator Options(IPage page) => List(page).GetByRole(AriaRole.Option);

    private static async Task<string[]> OptionIdsAsync(IPage page) =>
        [.. await Options(page).EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-value'))")];

    [Fact]
    public async Task TheListOpensOnFocus_NarrowsAsYouType_AndTheKeysPickWithoutSubmittingTheForm()
    {
        var (context, page) = await OpenAsync();
        await using var _ = context;
        await E2EHelpers.PickAiServiceAsync(page, "anthropic", "Anthropic");

        // The field already holds the service's default model, and still offers all of them.
        await Input(page).ClickAsync();
        await Assertions.Expect(List(page)).ToBeVisibleAsync();
        await Assertions.Expect(Options(page)).ToHaveCountAsync(4);
        await Assertions.Expect(Input(page)).ToHaveAttributeAsync("role", "combobox");
        await Assertions.Expect(Input(page)).ToHaveAttributeAsync("aria-expanded", "true");

        await Input(page).FillAsync(string.Empty);
        await Input(page).PressSequentiallyAsync("son");
        await Assertions.Expect(Options(page)).ToHaveCountAsync(1);
        await Assertions.Expect(Options(page).First).ToContainTextAsync("Claude Sonnet 5.5");

        await Input(page).PressAsync("ArrowDown");
        var chosen = Options(page).First;
        await Assertions.Expect(chosen).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(Input(page)).ToHaveAttributeAsync("aria-activedescendant", await chosen.GetAttributeAsync("id") ?? "?");

        await Input(page).PressAsync("Enter");

        await Assertions.Expect(Input(page)).ToHaveValueAsync("claude-sonnet-5-5");
        await Assertions.Expect(List(page)).ToBeHiddenAsync();
        await Assertions.Expect(Input(page)).ToHaveAttributeAsync("aria-expanded", "false");
        // Enter that picks a model must not also submit the form (which, with no key yet, would have said so).
        await Assertions.Expect(page.GetByTestId("ai-form-error")).ToHaveCountAsync(0);
        Assert.Empty(await E2EHelpers.UnlabelledControlsAsync(page));
    }

    [Fact]
    public async Task FastTyping_NeverLosesACharacter()
    {
        // Blazor Server answers each keystroke from the server; if the answer put the field's old text back over what was typed in the
        // meantime, characters vanished ("acme" came out "ame"). The browser keeps what is typed; the server only follows.
        var (context, page) = await OpenAsync();
        await using var _ = context;

        foreach (var typed in new[] { "acme-chat-mini", "anthropic/claude-sonnet-5-5", "zzzz-0123456789-qwerty", "acme" })
        {
            await Input(page).FillAsync(string.Empty);
            await Input(page).PressSequentiallyAsync(typed);
            await Assertions.Expect(Input(page)).ToHaveValueAsync(typed);
            // Settled: the server has caught up and still agrees.
            await page.WaitForTimeoutAsync(300);
            await Assertions.Expect(Input(page)).ToHaveValueAsync(typed);
        }
    }

    [Fact]
    public async Task ChangesFromOutside_ReachTheField_EvenAfterSomethingWasTyped()
    {
        // The browser owns the text while it is typed, so a change the page makes itself (another service, the form starting over
        // after a save) has to be pushed in. Typed text must not be what blocks it.
        var (context, page) = await OpenAsync();
        await using var _ = context;
        const string openRouterDefault = "openai/gpt-4o-mini";
        await Assertions.Expect(Input(page)).ToHaveValueAsync(openRouterDefault);

        await Input(page).FillAsync("something-typed");
        await E2EHelpers.PickAiServiceAsync(page, "anthropic", "Anthropic");
        await Assertions.Expect(Input(page)).ToHaveValueAsync("claude-haiku-5-5");

        await E2EHelpers.PickAiServiceAsync(page, "openrouter", "OpenRouter");
        await Assertions.Expect(Input(page)).ToHaveValueAsync(openRouterDefault);

        // Saved with a model of its own, the form starts over on the default again, not on what was typed (the default is also what
        // the field was last given from outside, which is the case a careless "only push what changed" would miss).
        await Input(page).FillAsync("my-own/model");
        await page.GetByTestId("ai-key").FillAsync("sk-or-test");
        await page.GetByTestId("ai-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("ai-provider-list")).ToContainTextAsync("my-own/model");
        await Assertions.Expect(Input(page)).ToHaveValueAsync(openRouterDefault);
    }

    [Fact]
    public async Task EscapeClosesTheList_ArrowDownOpensIt_AndAnythingTypedIsKept()
    {
        var (context, page) = await OpenAsync();
        await using var _ = context;
        await E2EHelpers.PickAiServiceAsync(page, "anthropic", "Anthropic");

        await Input(page).ClickAsync();
        await Assertions.Expect(List(page)).ToBeVisibleAsync();
        await Input(page).PressAsync("Escape");
        await Assertions.Expect(List(page)).ToBeHiddenAsync();

        await Input(page).PressAsync("ArrowDown");
        await Assertions.Expect(List(page)).ToBeVisibleAsync();

        // Nothing listed matches: it says what will happen to the text, and the text is the model.
        await Input(page).FillAsync("my-own-local-model");
        await Assertions.Expect(Options(page)).ToHaveCountAsync(0);
        await Assertions.Expect(List(page)).ToContainTextAsync("typed");
        // Tab moves on, as it does in any field; it does not insert a suggestion.
        await Input(page).PressAsync("Tab");
        await Assertions.Expect(List(page)).ToBeHiddenAsync();
        await Assertions.Expect(Input(page)).ToHaveValueAsync("my-own-local-model");
    }

    [Fact]
    public async Task ClickingAnOptionPicksIt_AndClickingElsewhereClosesTheList()
    {
        var (context, page) = await OpenAsync();
        await using var _ = context;
        await E2EHelpers.PickAiServiceAsync(page, "anthropic", "Anthropic");

        await Input(page).ClickAsync();
        await Options(page).Filter(new() { HasText = "Claude Opus 5.5" }).ClickAsync();
        await Assertions.Expect(Input(page)).ToHaveValueAsync("claude-opus-5-5");
        await Assertions.Expect(List(page)).ToBeHiddenAsync();

        await Input(page).ClickAsync();
        await Assertions.Expect(List(page)).ToBeVisibleAsync();
        await page.GetByTestId("ai-name").ClickAsync();
        await Assertions.Expect(List(page)).ToBeHiddenAsync();
        await Assertions.Expect(Input(page)).ToHaveValueAsync("claude-opus-5-5");
    }

    [Fact]
    public async Task ACustomServerHasNoSuggestions_SoTheFieldIsJustAField()
    {
        var (context, page) = await OpenAsync();
        await using var _ = context;
        await E2EHelpers.PickAiServiceAsync(page, "custom", "Custom (OpenAI-compatible)");

        await Input(page).ClickAsync();
        await Input(page).FillAsync("whatever-the-server-calls-it");

        await Assertions.Expect(List(page)).ToBeHiddenAsync();
        await Assertions.Expect(Input(page)).ToHaveValueAsync("whatever-the-server-calls-it");
    }

    [Fact]
    public async Task OpenRouter_OffersItsLiveList_WithoutRouters_MarksToolModels_AndNarrowsByNameOrId()
    {
        var (context, page) = await OpenAsync();
        await using var _ = context;
        await Assertions.Expect(page.GetByTestId("ai-kind")).ToHaveValueAsync("openrouter");

        await Input(page).ClickAsync();
        await Assertions.Expect(Options(page)).ToHaveCountAsync(3);
        var ids = await OptionIdsAsync(page);
        Assert.Contains(FakeOpenRouter.ChatPro, ids);
        Assert.Contains(FakeOpenRouter.ChatMini, ids);
        Assert.Contains(FakeOpenRouter.ChatPlain, ids);
        Assert.DoesNotContain(FakeOpenRouter.Router, ids);

        // The tools badge says which models can run the assistants; a model with context and prices says so.
        await Assertions.Expect(Options(page).Filter(new() { HasText = "Acme: Chat Pro" }).Locator(".pill")).ToHaveCountAsync(1);
        await Assertions.Expect(Options(page).Filter(new() { HasText = "Acme: Plain" }).Locator(".pill")).ToHaveCountAsync(0);
        await Assertions.Expect(Options(page).Filter(new() { HasText = "Acme: Chat Mini" })).ToContainTextAsync("128K context");

        await Input(page).FillAsync(string.Empty);
        await Input(page).PressSequentiallyAsync("acme plain");
        await Assertions.Expect(Options(page)).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task PickingAnOpenRouterModel_FillsInItsPrices_UntilSomeoneTypesTheirOwn()
    {
        var (context, page) = await OpenAsync();
        await using var _ = context;

        await Input(page).ClickAsync();
        await Options(page).Filter(new() { HasText = "Acme: Chat Mini" }).ClickAsync();

        await Assertions.Expect(Input(page)).ToHaveValueAsync(FakeOpenRouter.ChatMini);
        await Assertions.Expect(page.GetByTestId("ai-price-in")).ToHaveValueAsync("0.15");
        await Assertions.Expect(page.GetByTestId("ai-price-out")).ToHaveValueAsync("0.6");
        await Assertions.Expect(page.GetByTestId("ai-price-cached")).ToHaveValueAsync("0.075");
        await Assertions.Expect(page.GetByTestId("ai-price-label")).ToHaveValueAsync("USD");
        await Assertions.Expect(page.GetByTestId("ai-prices-autofilled")).ToBeVisibleAsync();

        // Another model's prices replace those, as nobody has touched them.
        await Input(page).ClickAsync();
        await Options(page).Filter(new() { HasText = "Acme: Chat Pro" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("ai-price-in")).ToHaveValueAsync("3");
        await Assertions.Expect(page.GetByTestId("ai-price-out")).ToHaveValueAsync("15");
        await Assertions.Expect(page.GetByTestId("ai-price-cached")).ToHaveValueAsync("0.3");

        // Typed prices are the person's: they stay, and the note that the numbers came from OpenRouter goes.
        await page.GetByTestId("ai-price-in").CommitAsync("9");
        await Assertions.Expect(page.GetByTestId("ai-prices-autofilled")).ToHaveCountAsync(0);
        await Input(page).ClickAsync();
        await Options(page).Filter(new() { HasText = "Acme: Plain" }).ClickAsync();
        await Assertions.Expect(Input(page)).ToHaveValueAsync(FakeOpenRouter.ChatPlain);
        await Assertions.Expect(page.GetByTestId("ai-price-in")).ToHaveValueAsync("9");
        await Assertions.Expect(page.GetByTestId("ai-price-out")).ToHaveValueAsync("15");
    }

    [Fact]
    public async Task ATypedModelId_BringsNoPrices_AndAnyPriceCanBeEnteredByHand()
    {
        var (context, page) = await OpenAsync();
        await using var _ = context;

        await Input(page).FillAsync(FakeOpenRouter.ChatMini);
        await Assertions.Expect(page.GetByTestId("ai-price-in")).ToHaveValueAsync(string.Empty);
        await Assertions.Expect(page.GetByTestId("ai-prices-autofilled")).ToHaveCountAsync(0);

        await page.GetByTestId("ai-price-in").CommitAsync("1.25");
        await Assertions.Expect(page.GetByTestId("ai-price-in")).ToHaveValueAsync("1.25");
    }

    [Fact]
    public async Task AnOpenRouterImageModel_BringsAPerPicturePrice_OnlyWhenOpenRouterLists_OneNotAGuess()
    {
        var (context, page) = await OpenAsync();
        await using var _ = context;

        await page.GetByTestId("ai-purpose-image").ClickAsync();
        await E2EHelpers.PickAiServiceAsync(page, "openrouter", "OpenRouter");

        await Input(page).ClickAsync();
        await Assertions.Expect(Options(page)).ToHaveCountAsync(2);
        await Options(page).Filter(new() { HasText = "Acme: Draw" }).First.ClickAsync();
        await Assertions.Expect(Input(page)).ToHaveValueAsync(FakeOpenRouter.DrawFlat);
        await Assertions.Expect(page.GetByTestId("ai-price-image")).ToHaveValueAsync("0.04");

        // A model OpenRouter prices per token of picture has no price per picture: the field is emptied, not guessed.
        await Input(page).ClickAsync();
        await Options(page).Filter(new() { HasText = "Draw By Token" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("ai-price-image")).ToHaveValueAsync(string.Empty);
    }

    [Fact]
    public async Task ASavedProvidersPricesAreNeverReplacedByPickingAnotherModel_AndCanBeEditedAndSavedAgainLater()
    {
        var (context, page) = await OpenAsync();
        await using var _ = context;

        await Input(page).ClickAsync();
        await Options(page).Filter(new() { HasText = "Acme: Chat Mini" }).ClickAsync();
        await page.GetByTestId("ai-key").FillAsync("sk-or-test");
        await page.GetByTestId("ai-save").ClickAsync();
        var list = page.GetByTestId("ai-provider-list");
        await Assertions.Expect(list).ToContainTextAsync(FakeOpenRouter.ChatMini);

        await page.GetByRole(AriaRole.Button, new() { Name = "Edit OpenRouter" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("ai-price-in")).ToHaveValueAsync("0.15");
        await Assertions.Expect(page.GetByTestId("ai-prices-autofilled")).ToHaveCountAsync(0);

        // Another model on a saved provider changes the model and leaves the numbers alone.
        await Input(page).ClickAsync();
        await Options(page).Filter(new() { HasText = "Acme: Chat Pro" }).ClickAsync();
        await Assertions.Expect(Input(page)).ToHaveValueAsync(FakeOpenRouter.ChatPro);
        await Assertions.Expect(page.GetByTestId("ai-price-in")).ToHaveValueAsync("0.15");

        // Prices recorded later: edit, save, and they are there when it is opened again.
        await page.GetByTestId("ai-price-in").CommitAsync("1.5");
        await page.GetByTestId("ai-save").ClickAsync();
        await Assertions.Expect(list).ToContainTextAsync(FakeOpenRouter.ChatPro);
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit OpenRouter" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("ai-price-in")).ToHaveValueAsync("1.5");
    }
}
