using Codex.Core;
using Codex.Core.Models;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Components.Shared;
using Codex.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;

namespace Codex.Tests;

/// <summary>
/// Which packs a campaign uses when it has never chosen: every pack that is on by default, so a pack with <c>activeByDefault: false</c> stays installed
/// but off. The choice is stored relative to the defaults, so a pack added later is on. Only pickers, lists and the AI tools' new picks are narrowed;
/// whatever a campaign already holds still resolves. A step or list with no entries says so, and says only what can help.
/// </summary>
public class PackDefaultsTests
{
    private static readonly SystemDefinition Demo = SystemDefinitionLoader.Parse("""
        id: demo
        name: Demo
        actorFields:
          - { key: class, label: Class, type: EntityReference, targetEntityType: class }
        entityTypes:
          - { type: class, label: Class, pluralLabel: Classes }
          - { type: faith, label: Faith, pluralLabel: Faiths }
        """);

    private static readonly CampaignAccess Dm = new("c1", "dm", CampaignRole.DM);
    private static readonly string[] None = Array.Empty<string>();

    private static PackManifest Pack(string id, bool activeByDefault = true, string system = "demo", string? locale = null, string? translationOf = null) =>
        new() { Id = id, SystemId = system, ActiveByDefault = activeByDefault, Locale = locale, TranslationOf = translationOf };

    private static PackChoice Choice(string[]? legacy = null, string[]? on = null, string[]? off = null) => new(legacy ?? None, on ?? None, off ?? None);

    private static CampaignLibrary Library(ContentRegistry registry)
    {
        var homebrew = Substitute.For<IHomebrewRepository>();
        homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(Enumerable.Empty<HomebrewEntryDocument>());
        var catalog = Substitute.For<ISystemCatalog>();
        catalog.GetDefinition("demo").Returns(Demo);
        return new CampaignLibrary(registry, homebrew, catalog);
    }

    /// <summary>A system with a pack that is on by default (the fighter) and one that is off (the bard).</summary>
    private static ContentRegistry RegistryWithAnOffPack()
    {
        var registry = new ContentRegistry();
        registry.RegisterPack(Pack("base"));
        registry.RegisterPack(Pack("extra", activeByDefault: false));
        registry.RegisterEntry(new ContentEntry { Id = "fighter", Type = "class", SystemId = "demo", PackId = "base", Name = "Fighter" }, 0);
        registry.RegisterEntry(new ContentEntry { Id = "bard", Type = "class", SystemId = "demo", PackId = "extra", Name = "Bard" }, 0);
        return registry;
    }

    private static CampaignDocument Fresh() => new() { Id = "c1", SystemId = "demo" };

    // ---- the rule ----

    [Fact]
    public void AnEmptyChoice_IsEveryPackThatIsOnByDefault()
    {
        var packs = new[] { Pack("base"), Pack("extra", activeByDefault: false) };

        Assert.Equal(new[] { "base" }, PackSelection.Resolve(Choice(), packs)!);
        Assert.True(PackSelection.IsActive(Choice(), packs, "base"));
        Assert.False(PackSelection.IsActive(Choice(), packs, "extra"));
    }

    [Fact]
    public void WhenNoPackOptsOut_AnEmptyChoiceMeansNoNarrowing()
    {
        Assert.Null(PackSelection.Resolve(Choice(), new[] { Pack("a"), Pack("b") }));
        Assert.Null(PackSelection.Resolve(Choice(), Array.Empty<PackManifest>()));
    }

    [Fact]
    public void AnOlderFullList_KeepsItsExactMeaning_WhateverTheDefaults()
    {
        var packs = new[] { Pack("base"), Pack("extra", activeByDefault: false) };
        var legacy = Choice(legacy: new[] { "extra" });

        Assert.Equal(new[] { "extra" }, PackSelection.Resolve(legacy, packs)!);
        Assert.True(PackSelection.IsActive(legacy, packs, "EXTRA"));
        Assert.False(PackSelection.IsActive(legacy, packs, "base"));
    }

    [Fact]
    public void TheSettingsRoundTrip_StoresOnlyTheDifferenceFromTheDefaults_AndClearsTheOlderList()
    {
        var packs = new[] { Pack("base"), Pack("extra", activeByDefault: false), Pack("base-ru", locale: "ru", translationOf: "base") };

        // What Settings shows for a campaign that never chose: the packs on by default, and no toggle for the translation.
        var shown = PackSelection.Selected(Choice(), packs);
        Assert.Equal(new[] { "base" }, shown.OrderBy(x => x));
        var saved = PackSelection.FromSelection(shown, packs);
        Assert.Empty(saved.On);
        Assert.Empty(saved.Off);
        Assert.Empty(saved.Legacy);

        // Turning the off pack on stores that one pack and nothing else.
        shown.Add("extra");
        saved = PackSelection.FromSelection(shown, packs);
        Assert.Equal(new[] { "extra" }, saved.On);
        Assert.Empty(saved.Off);

        // Unticking a pack that is on by default stores that.
        shown.Remove("base");
        saved = PackSelection.FromSelection(shown, packs);
        Assert.Equal(new[] { "extra" }, saved.On);
        Assert.Equal(new[] { "base" }, saved.Off);

        // Turning it all back to the defaults is the empty choice again.
        saved = PackSelection.FromSelection(new HashSet<string> { "base" }, packs);
        Assert.Empty(saved.On);
        Assert.Empty(saved.Off);
    }

    [Fact]
    public void AnOlderListConvertsOnTheNextSave_AndSwitchingEveryPackOffIsPossible()
    {
        var packs = new[] { Pack("base"), Pack("extra", activeByDefault: false) };

        // The older list ["base", "extra"] is read as it was, and what Settings saves from it is relative to the defaults.
        var shown = PackSelection.Selected(Choice(legacy: new[] { "base", "extra" }), packs);
        var saved = PackSelection.FromSelection(shown, packs);
        Assert.Equal(new[] { "extra" }, saved.On);
        Assert.Empty(saved.Legacy);

        // Nothing ticked is a choice of its own now: no pack is on, and nothing is offered.
        var nothing = PackSelection.FromSelection(new HashSet<string>(), packs);
        Assert.Equal(new[] { "base" }, nothing.Off);
        Assert.Empty(PackSelection.Resolve(nothing, packs)!);
    }

    [Fact]
    public void APackAddedLater_IsOnForACampaignThatTurnedAnOptionalPackOn()
    {
        var before = new[] { Pack("rules"), Pack("homebrew", activeByDefault: false) };
        var choice = PackSelection.FromSelection(new HashSet<string> { "rules", "homebrew" }, before);

        var after = before.Append(Pack("creatures")).ToArray();

        Assert.Equal(new[] { "creatures", "homebrew", "rules" }, PackSelection.Selected(choice, after).Order());
        Assert.Null(PackSelection.Resolve(choice, after.Where(p => p.Id != "homebrew").Append(Pack("homebrew")).ToArray())); // everything on: nothing narrowed
        // And a campaign that switched a default pack off keeps it off while the new one is on.
        var withoutRules = PackSelection.FromSelection(new HashSet<string> { "homebrew" }, before);
        Assert.Equal(new[] { "creatures", "homebrew" }, PackSelection.Selected(withoutRules, after).Order());
    }

    [Fact]
    public async Task ThePackedContent_ShipsTheRightFlags()
    {
        var registry = new ContentRegistry();
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(registry));
        await loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

        var degrees = registry.GetPacks("four-degrees").ToList();
        Assert.False(degrees.Single(p => p.Id == "four-degrees-core").ActiveByDefault);
        Assert.True(degrees.Single(p => p.Id == "four-degrees-rules").ActiveByDefault);

        // A campaign that never chose gets the imported pack and not the homebrew one; srd5e is unchanged: nothing narrowed.
        Assert.Equal(new[] { "four-degrees-rules" }, PackSelection.Resolve(Choice(), degrees)!.Order());
        Assert.All(registry.GetPacks("srd5e"), p => Assert.True(p.ActiveByDefault));
        Assert.Null(PackSelection.Resolve(Choice(), registry.GetPacks("srd5e").ToList()));
    }

    [Fact]
    public void TheRegistry_TreatsAnEmptyPackSetAsNothing_NotAsNoFilter()
    {
        var registry = RegistryWithAnOffPack();
        var nothing = new HashSet<string>();

        Assert.Empty(registry.GetEntries("demo", "class", nothing));
        Assert.Empty(registry.Search("demo", string.Empty, "class", nothing));
        Assert.Equal(2, registry.GetEntries("demo", "class").Count());
    }

    // ---- the library, the resolver and what is held ----

    [Fact]
    public async Task PickersAndLists_LeaveOutAnOffPack_AndTheCampaignsChoiceBringsItBack()
    {
        var library = Library(RegistryWithAnOffPack());
        var turnedOn = new CampaignDocument { Id = "c1", SystemId = "demo", PacksOn = { "extra" } };
        var older = new CampaignDocument { Id = "c1", SystemId = "demo", ActivePacks = { "base", "extra" } };

        Assert.Equal(new[] { "fighter" }, (await library.OptionsAsync(Fresh(), Dm, "class")).Select(i => i.Entry.Id));
        Assert.Equal(new[] { "fighter" }, (await library.SearchAsync(Fresh(), Dm, string.Empty, "class")).Select(i => i.Entry.Id));
        Assert.Equal(new[] { "bard", "fighter" }, (await library.OptionsAsync(turnedOn, Dm, "class")).Select(i => i.Entry.Id).Order());
        Assert.Equal(new[] { "bard", "fighter" }, (await library.OptionsAsync(older, Dm, "class")).Select(i => i.Entry.Id).Order());
    }

    [Fact]
    public async Task TheVault_UsesEveryPack_BecauseItHasNoGmAndNoSettings()
    {
        var library = Library(RegistryWithAnOffPack());
        var vault = VaultService.CampaignFor("demo");

        Assert.Null(library.ActivePacks(vault));
        Assert.Equal(new[] { "bard", "fighter" }, (await library.OptionsAsync(vault, CampaignAccess.Vault("u"), "class")).Select(i => i.Entry.Id).Order());
    }

    [Fact]
    public async Task AReferenceAlreadyHeld_StillResolvesWithItsPackOff()
    {
        var library = Library(RegistryWithAnOffPack());

        // The detail page, the initiative row's lookups and the sheet's linked entries all go through GetAsync.
        Assert.Equal("Bard", (await library.GetAsync(Fresh(), Dm, "class", "bard"))?.Entry.Name);
        Assert.Contains("bard", (await library.OptionsAsync(Fresh(), Dm, "class", onlyActivePacks: false)).Select(i => i.Entry.Id));
    }

    [Fact]
    public async Task TheSheetsReferencePicker_KeepsAHeldEntryUnderItsOwnName_WhenItsPackIsOff()
    {
        var library = Library(RegistryWithAnOffPack());
        var offered = (await library.OptionsAsync(Fresh(), Dm, "class")).Select(i => (i.Entry.Id, i.Entry.Name)).ToList();

        var withHeld = await library.WithHeldAsync(Fresh(), Dm, "class", offered, new string?[] { "bard", "bard", null, "gone" });

        Assert.Equal(new[] { ("fighter", "Fighter"), ("bard", "Bard") }, withHeld); // listed once; an id nothing knows is left to the form's "missing"
    }

    [Fact]
    public async Task TheSheetResolver_ReadsAnOffPackEntry_ButDoesNotOfferIt()
    {
        var library = Library(RegistryWithAnOffPack());

        var resolver = await CachedEntryResolver.LoadAsync(library, Fresh(), Dm, Demo);

        Assert.NotNull(resolver.Resolve("class", "bard"));
        Assert.NotNull(resolver.Resolve("class", "fighter"));
        Assert.Equal(new[] { "fighter" }, resolver.Candidates("class").Select(e => e.Id));
    }

    [Fact]
    public async Task ACharacterOnAPackThatIsOff_JoiningACampaign_IsNotListedAsMissing_BecauseHeldReferencesResolve()
    {
        // Decided: a reference resolves whatever the campaign's packs say, so the bring-in review does not list it. (The GM sees the class on the sheet
        // under its own name, and can switch the pack off again or ask for the entry to be swapped.)
        var library = Library(RegistryWithAnOffPack());
        var inCampaign = await CachedEntryResolver.LoadAsync(library, Fresh(), Dm, Demo);
        var sheet = new Dictionary<string, object> { ["class"] = "bard" };

        Assert.Empty(SheetReferences.Missing(Demo, sheet, inCampaign));
    }

    [Fact]
    public async Task ANewPickFromTheAi_HasToComeFromAPackThatIsOn_ButAHeldEntryStillResolves()
    {
        var library = Library(RegistryWithAnOffPack());
        var resolver = await CachedEntryResolver.LoadAsync(library, Fresh(), Dm, Demo);

        // The tools check a proposal with GetOfferedAsync and IsOffered; the resolver alone would accept the id.
        Assert.Null(await library.GetOfferedAsync(Fresh(), Dm, "class", "bard"));
        Assert.NotNull(await library.GetOfferedAsync(Fresh(), Dm, "class", "fighter"));
        Assert.False(library.IsOffered(Fresh(), resolver.Resolve("class", "bard")!));
        Assert.True(library.IsOffered(Fresh(), resolver.Resolve("class", "fighter")!));
        Assert.True(library.IsOffered(new CampaignDocument { Id = "c1", SystemId = "demo", PacksOn = { "extra" } }, resolver.Resolve("class", "bard")!));
        Assert.NotNull(await library.GetAsync(Fresh(), Dm, "class", "bard"));
    }

    [Fact]
    public async Task ForASystemWhereNoPackOptsOut_NothingChanges()
    {
        var registry = new ContentRegistry();
        registry.RegisterPack(Pack("base"));
        registry.RegisterPack(Pack("more"));
        registry.RegisterEntry(new ContentEntry { Id = "fighter", Type = "class", SystemId = "demo", PackId = "base", Name = "Fighter" }, 0);
        registry.RegisterEntry(new ContentEntry { Id = "bard", Type = "class", SystemId = "demo", PackId = "more", Name = "Bard" }, 0);
        var library = Library(registry);

        Assert.Null(library.ActivePacks(Fresh()));
        Assert.Equal(new[] { "bard", "fighter" }, (await library.OptionsAsync(Fresh(), Dm, "class")).Select(i => i.Entry.Id).Order());
    }

    // ---- the empty state, rendered ----

    private static async Task<string> RenderAsync<T>(IReadOnlyDictionary<string, object?> parameters, ContentRegistry? registry = null) where T : IComponent
    {
        var catalog = Substitute.For<ISystemCatalog>();
        catalog.GetDefinition("demo").Returns(Demo);
        var services = new ServiceCollection()
            .AddSingleton(new Localizer()).AddSingleton(catalog).AddSingleton<IContentRegistry>(registry ?? RegistryWithAnOffPack())
            .AddSingleton(Substitute.For<IJSRuntime>())
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters.ToDictionary(p => p.Key, p => p.Value)))).ToHtmlString());
    }

    private static Task<string> EmptyAsync(string type, bool canManage, string? basePath = "/campaigns/c1", CampaignDocument? campaign = null, bool callback = false) =>
        RenderAsync<EmptyEntries>(new Dictionary<string, object?>
        {
            ["SystemId"] = "demo", ["EntryType"] = type, ["Campaign"] = campaign ?? Fresh(), ["BasePath"] = basePath, ["CanManage"] = canManage,
            ["OnAddOwn"] = callback ? EventCallback.Factory.Create(new object(), () => { }) : default(EventCallback)
        });

    [Fact]
    public async Task AnEmptyType_WithAnOffPackThatHasEntries_PointsToSettingsAndToTheNewHomebrewEditor()
    {
        // The fresh campaign has the bard's pack off, and the registry has no hazards... so use the type the off pack does hold, with nothing on.
        var registry = new ContentRegistry();
        registry.RegisterPack(Pack("base"));
        registry.RegisterPack(Pack("extra", activeByDefault: false));
        registry.RegisterEntry(new ContentEntry { Id = "bard", Type = "class", SystemId = "demo", PackId = "extra", Name = "Bard" }, 0);
        var html = await RenderAsync<EmptyEntries>(new Dictionary<string, object?>
        {
            ["SystemId"] = "demo", ["EntryType"] = "class", ["Campaign"] = Fresh(), ["BasePath"] = "/campaigns/c1", ["CanManage"] = true
        }, registry);

        Assert.Contains("role=\"status\"", html);
        Assert.Contains("No classes in this world&#x27;s packs yet.", html.Replace("&#39;", "&#x27;"));
        Assert.Contains("href=\"/campaigns/c1/settings#packs-heading\"", html);
        Assert.Contains("Turn on a pack in Settings", html);
        Assert.Contains("href=\"/campaigns/c1/library/class?new=1\"", html);
        Assert.Contains("Add your own", html);
        Assert.Contains("d-block", html); // the links are on their own line, not run into the sentence
    }

    [Fact]
    public async Task AnEmptyType_NoPackHasEntriesOf_OffersOnlyAddYourOwn_AndSaysTheSystemHasNone()
    {
        var html = await EmptyAsync("faith", canManage: true);

        Assert.Contains("in this system&#x27;s packs yet.", html.Replace("&#39;", "&#x27;"));
        Assert.DoesNotContain("Turn on a pack in Settings", html);
        Assert.DoesNotContain("/settings", html);
        Assert.Contains("href=\"/campaigns/c1/library/faith?new=1\"", html);
    }

    [Fact]
    public async Task InsideTheLibrary_AddYourOwn_IsAButtonThatOpensTheEditorOnTheSamePage()
    {
        var html = await EmptyAsync("faith", canManage: true, callback: true);

        Assert.Contains("<button", html);
        Assert.DoesNotContain("?new=1", html);
        Assert.Contains("Add your own", html);
    }

    [Fact]
    public async Task AnEmptyType_ForAPlayer_TellsThemWhomToAsk_OnlyWhenAPackCouldHelp_AndLinksNothing()
    {
        var withOffPack = await EmptyAsync("class", canManage: false, campaign: new CampaignDocument { Id = "c1", SystemId = "demo", PacksOff = { "base" } });
        var nothingToTurnOn = await EmptyAsync("faith", canManage: false);

        Assert.Contains("Ask your GM", withOffPack);
        Assert.DoesNotContain("<a ", withOffPack);
        Assert.DoesNotContain("Ask your GM", nothingToTurnOn);
    }

    [Fact]
    public async Task AnEmptyType_InTheVault_IsJustTheSentence_NoGmNoSettingsNoBrokenLink()
    {
        var html = await EmptyAsync("faith", canManage: false, basePath: null, campaign: VaultService.CampaignFor("demo"));

        Assert.Contains("No faiths in this system", html);
        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("Ask your GM", html);
        Assert.DoesNotContain("/campaigns/", html);
    }

    // ---- the slots hosts fill ----

    [Fact]
    public async Task AReferencePickerWithNoOptions_ShowsTheEmptySlot_AndDescribesTheSelectByIt()
    {
        var schema = new UISchema("actor", PreferredEditor.Form, new List<FieldDefinition> { new("class", "Class", FieldType.EntityReference, targetEntityType: "class") });
        RenderFragment<string> empty = type => b => b.AddMarkupContent(0, $"<span class=\"x\">none-of-{type}</span>");

        var html = await RenderAsync<SchemaForm>(new Dictionary<string, object?>
        {
            ["Schema"] = schema, ["Values"] = new Dictionary<string, object>(), ["EmptyReference"] = empty,
            ["ReferenceOptions"] = new Dictionary<string, IReadOnlyList<(string Id, string Name)>> { ["class"] = new List<(string, string)>() }
        });

        Assert.Contains("none-of-class", html);
        Assert.Contains("-empty\"", html);
        Assert.Matches("aria-describedby=\"[^\"]*-empty\"", html);
    }

    [Fact]
    public async Task AReferencePicker_ThatHasOptions_ARulesNarrowedOne_OrAReadOnlyForm_ShowsNoEmptySlot()
    {
        var schema = new UISchema("actor", PreferredEditor.Form, new List<FieldDefinition> { new("class", "Class", FieldType.EntityReference, targetEntityType: "class") });
        RenderFragment<string> empty = type => b => b.AddMarkupContent(0, "<span>none-here</span>");
        var none = new Dictionary<string, IReadOnlyList<(string Id, string Name)>> { ["class"] = new List<(string, string)>() };
        var some = new Dictionary<string, IReadOnlyList<(string Id, string Name)>> { ["class"] = new List<(string, string)> { ("fighter", "Fighter") } };

        Task<string> Render(IReadOnlyDictionary<string, IReadOnlyList<(string Id, string Name)>> options, bool readOnly = false, IReadOnlyDictionary<string, IReadOnlyList<(string Id, string Name)>>? narrowed = null) =>
            RenderAsync<SchemaForm>(new Dictionary<string, object?>
            {
                ["Schema"] = schema, ["Values"] = new Dictionary<string, object>(), ["EmptyReference"] = empty, ["ReferenceOptions"] = options, ["ReadOnly"] = readOnly, ["FieldOptions"] = narrowed
            });

        Assert.DoesNotContain("none-here", await Render(some));
        Assert.DoesNotContain("none-here", await Render(none, readOnly: true));
        Assert.DoesNotContain("none-here", await Render(none, narrowed: none)); // the rules narrowed it to nothing: not a missing pack
    }

    [Fact]
    public async Task ABrowserWithNothingToChooseFrom_ShowsTheEmptySlot_InsteadOfItsPlainText()
    {
        RenderFragment content = b => b.AddMarkupContent(0, "<span>slot-text</span>");

        var empty = await RenderAsync<EntryBrowser>(new Dictionary<string, object?> { ["Options"] = new List<ContentEntry>(), ["EmptyText"] = "plain-text", ["EmptyContent"] = content });
        var plain = await RenderAsync<EntryBrowser>(new Dictionary<string, object?> { ["Options"] = new List<ContentEntry>(), ["EmptyText"] = "plain-text" });
        var some = await RenderAsync<EntryBrowser>(new Dictionary<string, object?>
        {
            ["Options"] = new List<ContentEntry> { new() { Id = "a", Type = "class", SystemId = "demo", PackId = "base", Name = "Axe" } }, ["EmptyContent"] = content
        });

        Assert.Contains("slot-text", empty);
        Assert.DoesNotContain("plain-text", empty);
        Assert.Contains("plain-text", plain);
        Assert.DoesNotContain("slot-text", some);
    }

    // ---- the combat quick-add ----

    private static ContentRegistry RegistryWithACreatureInAnOffPack()
    {
        var registry = RegistryWithAnOffPack();
        registry.RegisterActor(new ActorDefinition { Id = "wisp", SystemId = "demo", PackId = "extra", Name = "Wisp" }, 0);
        return registry;
    }

    [Fact]
    public async Task TheCombatQuickAdd_OffersNoCreatureFromAPackThatIsOff_AndSaysSoInsteadOfTheSearchHint()
    {
        var html = await RenderAsync<QuickAddPanel>(new Dictionary<string, object?>
        {
            ["SystemId"] = "demo", ["Campaign"] = Fresh(), ["CreatureType"] = "class", ["OnAdd"] = (Func<IActorDefinition, Task<bool>>)(_ => Task.FromResult(true))
        }, RegistryWithACreatureInAnOffPack());

        Assert.Contains("empty-entries", html);
        Assert.Contains("Turn on a pack in Settings", html);
    }

    [Fact]
    public async Task TheCombatQuickAdd_WithTheCreaturesPackOn_ShowsTheSearchHint()
    {
        var html = await RenderAsync<QuickAddPanel>(new Dictionary<string, object?>
        {
            ["SystemId"] = "demo", ["Campaign"] = new CampaignDocument { Id = "c1", SystemId = "demo", PacksOn = { "extra" } },
            ["OnAdd"] = (Func<IActorDefinition, Task<bool>>)(_ => Task.FromResult(true))
        }, RegistryWithACreatureInAnOffPack());

        Assert.DoesNotContain("empty-entries", html);
    }
}
