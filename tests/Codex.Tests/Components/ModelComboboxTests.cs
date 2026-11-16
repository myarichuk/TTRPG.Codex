using Bunit;
using Codex.Web.Components.Shared;

namespace Codex.Tests.Components;

/// <summary>The model picker's markup and its narrowing. What the browser does with it (keys, opening and closing) is
/// <c>@github/combobox-nav</c>, and is exercised in the end-to-end tests.</summary>
public sealed class ModelComboboxTests : ComponentTestBase
{
    private static readonly IReadOnlyList<ModelOption> Few =
    [
        new("claude-sonnet-5-5", "Claude Sonnet 5.5"),
        new("claude-haiku-5-5", "Claude Haiku 5.5"),
        new("anthropic/claude-opus-5-5", "Anthropic: Claude Opus 5.5", "1M context · in 4 / out 20 per 1M tokens", Tools: true)
    ];

    private IRenderedComponent<ModelCombobox> Render(IReadOnlyList<ModelOption> options, string? value = null, bool loading = false, bool failed = false,
        Action<string?>? changed = null, Action<ModelOption>? picked = null) =>
        Render<ModelCombobox>(p => p
            .Add(c => c.Id, "m")
            .Add(c => c.TestId, "m")
            .Add(c => c.Options, options)
            .Add(c => c.Value, value)
            .Add(c => c.Loading, loading)
            .Add(c => c.Failed, failed)
            .Add(c => c.ValueChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<string?>(this, v => changed?.Invoke(v)))
            .Add(c => c.OnPick, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<ModelOption>(this, o => picked?.Invoke(o))));

    [Fact]
    public void Renders_AComboboxInputAndAListboxOfOptions()
    {
        var cut = Render(Few, value: "claude-haiku-5-5");

        var input = cut.Find(Id("m"));
        Assert.Equal("claude-haiku-5-5", input.GetAttribute("value"));
        Assert.Equal("off", input.GetAttribute("autocomplete"));
        var list = cut.Find("[role=listbox]");
        Assert.True(list.HasAttribute("hidden"));
        Assert.NotNull(list.GetAttribute("aria-label"));
        Assert.Equal(3, cut.FindAll("[role=option]").Count);
        // The list is wired to the field for assistive technology by the browser half; the ids it points at must exist and be unique.
        var ids = cut.FindAll("[role=option]").Select(o => o.Id).ToList();
        Assert.All(ids, id => Assert.False(string.IsNullOrEmpty(id)));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void AnOption_ShowsItsNameIdDetailAndToolsBadge()
    {
        var cut = Render(Few);

        var option = cut.FindAll("[role=option]")[2];
        Assert.Equal("anthropic/claude-opus-5-5", option.GetAttribute("data-value"));
        Assert.Contains("Anthropic: Claude Opus 5.5", option.QuerySelector(".model-combobox-name")!.TextContent);
        Assert.Equal("anthropic/claude-opus-5-5", option.QuerySelector(".model-combobox-id")!.TextContent);
        Assert.Equal("ltr", option.QuerySelector(".model-combobox-id")!.GetAttribute("dir"));
        Assert.Contains("1M context", option.QuerySelector(".model-combobox-detail")!.TextContent);
        Assert.NotNull(option.QuerySelector(".pill"));
        Assert.Null(cut.FindAll("[role=option]")[0].QuerySelector(".pill"));
    }

    [Fact]
    public void AnOptionWithNoName_ShowsTheIdOnce()
    {
        var cut = Render([new ModelOption("llama3.1")]);

        var option = cut.Find("[role=option]");
        Assert.Equal("llama3.1", option.QuerySelector(".model-combobox-name")!.TextContent);
        Assert.Null(option.QuerySelector(".model-combobox-id"));
    }

    [Fact]
    public void Typing_NarrowsTheList_AndTellsTheOwner()
    {
        string? latest = null;
        var cut = Render(Few, changed: v => latest = v);

        cut.Find(Id("m")).Input("haiku");

        Assert.Equal("haiku", latest);
        Assert.Equal(["claude-haiku-5-5"], cut.FindAll("[role=option]").Select(o => o.GetAttribute("data-value")).ToArray());
    }

    [Fact]
    public void AFieldThatAlreadyHoldsAModel_StillOffersEveryModel()
    {
        // The reason the list narrows by what was typed and not by the field's value: opening it must not show one lonely match.
        var cut = Render(Few, value: "claude-sonnet-5-5");

        Assert.Equal(3, cut.FindAll("[role=option]").Count);
    }

    [Fact]
    public void WhenNothingMatches_ItSaysTheTextWillBeUsedAsTyped()
    {
        var cut = Render(Few);

        cut.Find(Id("m")).Input("my-own-model");

        Assert.Empty(cut.FindAll("[role=option]"));
        var note = cut.Find("[data-note]");
        Assert.Contains("typed", note.TextContent);
        Assert.Equal("presentation", note.GetAttribute("role"));
    }

    [Fact]
    public void AListLongerThanTheLimit_IsCapped_AndSaysSo()
    {
        var many = Enumerable.Range(0, 200).Select(i => new ModelOption($"vendor/model-{i:000}")).ToList();
        var cut = Render(many);

        Assert.Equal(60, cut.FindAll("[role=option]").Count);
        Assert.Contains("60", cut.Find("[data-note]").TextContent);
        Assert.Contains("200", cut.Find("[data-note]").TextContent);

        cut.Find(Id("m")).Input("model-19");
        Assert.Equal(10, cut.FindAll("[role=option]").Count);
        Assert.Empty(cut.FindAll("[data-note]"));
    }

    [Fact]
    public void WhileTheListLoads_OrWhenItFailed_TheListSaysSo_AndTheFieldStillWorks()
    {
        var loading = Render([], loading: true);
        Assert.NotNull(loading.Find("[data-testid='m-loading']"));
        Assert.Empty(loading.FindAll("[role=option]"));

        var failed = Render([], failed: true);
        Assert.Contains("type", failed.Find("[data-testid='m-failed']").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.False(failed.Find(Id("m")).HasAttribute("disabled"));

        // A service with no suggestions at all (a custom server) has nothing to show: no notes either.
        var none = Render([]);
        Assert.Empty(none.FindAll("[data-note]"));
    }

    [Fact]
    public async Task APick_SetsTheValue_AndReportsTheOptionPicked()
    {
        string? value = null;
        ModelOption? picked = null;
        var cut = Render(Few, changed: v => value = v, picked: o => picked = o);

        await cut.InvokeAsync(() => cut.Instance.PickAsync("claude-haiku-5-5"));

        Assert.Equal("claude-haiku-5-5", value);
        Assert.Equal("Claude Haiku 5.5", picked!.Label);
    }

    [Fact]
    public async Task APickOfSomethingNotInTheList_SetsTheValueButReportsNoOption()
    {
        string? value = null;
        var picked = false;
        var cut = Render(Few, changed: v => value = v, picked: _ => picked = true);

        await cut.InvokeAsync(() => cut.Instance.PickAsync("gone-since-it-was-offered"));

        Assert.Equal("gone-since-it-was-offered", value);
        Assert.False(picked);
    }

    [Fact]
    public void Invalid_IsExposedToAssistiveTechnology()
    {
        var cut = Render<ModelCombobox>(p => p.Add(c => c.Id, "m").Add(c => c.TestId, "m").Add(c => c.Invalid, "true"));

        Assert.Equal("true", cut.Find(Id("m")).GetAttribute("aria-invalid"));
    }
}

/// <summary>How the picker narrows its options.</summary>
public sealed class ModelFilterTests
{
    private static readonly IReadOnlyList<ModelOption> Models =
    [
        new("anthropic/claude-sonnet-5-5", "Anthropic: Claude Sonnet 5.5"),
        new("openai/gpt-6-luna", "OpenAI: GPT-6 Luna"),
        new("google/gemini-4-hydrogen", "Google: Gemini 4 Hydrogen"),
        new("anthropic/claude-haiku-5-5", "Anthropic: Claude Haiku 5.5"),
        new("meta/llama-sonnet-mix")
    ];

    private static string[] Ids(string? query, int limit = 50) => [.. ModelFilter.Apply(Models, query, limit, out _).Select(o => o.Id)];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingTyped_EverythingMatches_InTheOrderGiven(string? query)
    {
        var shown = ModelFilter.Apply(Models, query, 50, out var total);

        Assert.Equal(5, total);
        Assert.Equal(Models.Select(m => m.Id), shown.Select(m => m.Id));
    }

    [Fact]
    public void EveryWordTyped_MustAppear_InTheIdOrTheName_InAnyCase()
    {
        Assert.Equal(["anthropic/claude-sonnet-5-5"], Ids("CLAUDE son"));
        Assert.Equal(["openai/gpt-6-luna"], Ids("openai luna"));
        Assert.Equal(["google/gemini-4-hydrogen"], Ids("Hydrogen"));
        Assert.Empty(Ids("claude luna"));
    }

    [Fact]
    public void ASlashIsASeparator_SoTheVendorAndTheModelCanBeTypedAsInTheId()
    {
        Assert.Equal(["anthropic/claude-sonnet-5-5", "anthropic/claude-haiku-5-5"], Ids("anthropic/claude"));
    }

    [Fact]
    public void TheNameIsSearchedToo_NotOnlyTheId()
    {
        Assert.Equal(["google/gemini-4-hydrogen"], Ids("Google:"));
    }

    [Fact]
    public void AModelThatStartsWithWhatWasTyped_ComesBeforeOneThatOnlyContainsIt()
    {
        // Listed second, but its id starts with what was typed; the other only contains it.
        IReadOnlyList<ModelOption> listed = [new("vendor/zeta-claude"), new("claude/alpha")];

        var shown = ModelFilter.Apply(listed, "claude", 50, out _);

        Assert.Equal(["claude/alpha", "vendor/zeta-claude"], shown.Select(o => o.Id).ToArray());
        // The name counts as well as the id, and models that tie keep the order they were given.
        IReadOnlyList<ModelOption> named = [new("a/one", "Other Thing"), new("b/two", "Claude Two"), new("c/three", "Claude Three")];

        Assert.Equal(["b/two", "c/three"], ModelFilter.Apply(named, "claude", 50, out _).Select(o => o.Id).ToArray());
    }

    [Fact]
    public void TheLimitCapsWhatIsDrawn_ButTheTotalIsEverythingThatMatched()
    {
        var shown = ModelFilter.Apply(Models, "a", 2, out var total);

        Assert.Equal(2, shown.Count);
        Assert.True(total > 2);
    }
}
