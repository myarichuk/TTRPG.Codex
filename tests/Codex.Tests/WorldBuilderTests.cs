using Codex.Core.AI;
using Codex.Core.Scripting;
using Codex.Core.Systems;
using Codex.Core.WorldBuilder;
using Codex.Core;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>The world builder's pure parts: recipes, the outline and expand replies, link rewriting, and what saving stores.</summary>
public class WorldBuilderTests
{
    private static readonly BundleRules Rules = new(
        new[] { DraftKinds.Homebrew, DraftKinds.Wiki },
        new Dictionary<string, string> { ["magic-item"] = "magic-item", ["Magic Item"] = "magic-item", ["spell"] = "spell" },
        new[] { "port-varr" });

    // ---- Recipes ----

    [Fact]
    public void ShippedRecipes_LoadAndValidate()
    {
        var homebrew = WorldRecipes.Find("homebrew");

        Assert.NotNull(homebrew);
        Assert.Equal(new[] { DraftKinds.Homebrew, DraftKinds.Wiki }, homebrew!.Drafts);
        Assert.Equal(QuestionTypes.ContentType, homebrew.Question("type")!.Type);
        Assert.True(homebrew.Question("type")!.Required);
    }

    [Fact]
    public void EveryRecipeIcon_IsOneTheAppDraws()
    {
        // LucideIcon draws a circle for a name it does not know, so an unknown icon would pass unnoticed.
        var icons = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "Components", "Layout", "LucideIcon.razor"));

        Assert.All(WorldRecipes.All, r => Assert.Contains($"[\"{r.Icon}\"]", icons));
    }

    [Fact]
    public void EveryRecipeLabel_IsTranslated()
    {
        // Recipe labels are looked up by key at run time, so LocalizationTests' scan of L["..."] literals cannot see them.
        var l = new Localizer();
        var missing = new List<string>();
        foreach (var recipe in WorldRecipes.All)
        {
            var keys = new List<string> { $"WorldBuilder.Recipe.{recipe.Key}.Title", $"WorldBuilder.Recipe.{recipe.Key}.Intro" };
            foreach (var q in recipe.Questions)
            {
                keys.Add($"WorldBuilder.Recipe.{recipe.Key}.{q.Key}");
                keys.AddRange(q.Options.Select(o => $"WorldBuilder.Recipe.{recipe.Key}.{q.Key}.{o}"));
            }

            missing.AddRange(keys.Where(k => !l.Has(k)));
        }

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("key: x\ndrafts: [castle]\nprompt: p\nquestions: [{ key: a, ask: A? }]", "unknown draft kind")]
    [InlineData("key: x\ndrafts: [wiki]\nprompt: p\nquestions: [{ key: a, ask: A? }]\nwrite: { npc: Be brief. }", "write: \"npc\" is not one of the recipe's drafts")]
    [InlineData("key: x\ndrafts: [wiki]\nprompt: p\nquestions: [{ key: a, type: choice, ask: A?, options: [one] }]", "at least two options")]
    [InlineData("key: x\ndrafts: [wiki]\nprompt: p\nquestions: [{ key: a, ask: A? }, { key: a, ask: B? }]", "duplicate key")]
    [InlineData("key: x\ndrafts: [wiki]\nprompt: p\nquestions: [{ key: a, type: choice, ask: A?, options: [one, two], default: three }]", "is not an option")]
    [InlineData("key: x\ndrafts: [wiki]\nprompt: p\nquestions: [{ key: a, type: text, ask: A?, options: [one, two] }]", "only a choice has options")]
    [InlineData("key: x\ndrafts: [wiki]\nquestions: [{ key: a, ask: A? }]", "prompt is empty")]
    public void Parse_RejectsABrokenRecipe_SayingWhy(string yaml, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => WorldRecipes.Parse(yaml, "test.yaml"));

        Assert.Contains(expected, ex.Message);
        Assert.StartsWith("test.yaml", ex.Message);
    }

    // ---- Outline ----

    [Fact]
    public void ParseOutline_KeepsAllowedDrafts_ResolvesLinks_AndListsWhatItDropped()
    {
        const string reply = """
            Here you go:
            ```json
            {"drafts": [
              {"ref": "a", "kind": "homebrew", "type": "Magic Item", "name": "Tide-glass", "pitch": "A lens that shows the sea's memory.", "links": ["b", "a", "existing:port-varr", "existing:nowhere", "zz"]},
              {"ref": "b", "kind": "wiki", "name": "The Glassblowers of Varr", "pitch": "Who made it.", "links": ["a"]},
              {"ref": "c", "kind": "faction", "name": "Salt Wardens"},
              {"ref": "d", "kind": "homebrew", "type": "vehicle", "name": "Sky Barge"},
              {"ref": "e", "kind": "wiki", "name": ""}
            ]}
            ```
            """;

        var bundle = WorldBundleParser.ParseOutline(reply, Rules);

        Assert.Equal(2, bundle.Drafts.Count);
        var item = bundle.Drafts[0];
        Assert.Equal(("d1", "homebrew", "magic-item", "Tide-glass"), (item.Ref, item.Kind, item.Type, item.Name));
        Assert.Equal("Tide-glass", item.OutlineName);
        Assert.Equal(new[] { "d2", "existing:port-varr" }, item.Links); // no self-link, unknown targets gone
        Assert.Equal(new[] { "d1" }, bundle.Drafts[1].Links);
        Assert.All(bundle.Drafts, d => Assert.True(d.Selected));
        Assert.Equal(new[] { DropReason.Kind, DropReason.Type, DropReason.Unnamed }, bundle.Dropped.Select(d => d.Reason));
    }

    [Theory]
    [InlineData("I could not think of anything.")]
    [InlineData("""{"ideas": []}""")]
    [InlineData("""{"drafts": [ {"name": "x",, } ]}""")]
    public void ParseOutline_WithoutADraftsList_IsAFormatError(string reply)
    {
        Assert.Throws<FormatException>(() => WorldBundleParser.ParseOutline(reply, Rules));
    }

    // ---- Expand ----

    private static readonly FieldSpec[] SpellFields =
    {
        new("level", "Level", "Number", null, 0, 9),
        new("school", "School", "Dropdown", new[] { "Evocation (Воплощение)", "Illusion (Иллюзия)" }, null, null),
        new("components", "Components", "TagList", null, null, null),
        new("damageType", "Damage Type", "Text", null, null, null),
        new("concentration", "Concentration", "Boolean", null, null, null),
        new("actions", "Actions", "Collection", null, null, null)
    };

    [Fact]
    public void ApplyHomebrew_CoercesFields_AndStoresTranslatedValuesAsTheRulesCompareThem()
    {
        var draft = new WorldDraft { Ref = "d1", Kind = DraftKinds.Homebrew, Type = "spell", Name = "Огненная дуга" };
        const string reply = """
            {"description": "Дуга пламени бьёт по цели.", "tags": ["fire", "Fire", "arc"],
             "properties": {"level": 12, "school": "Воплощение", "components": "V, S", "damageType": "огонь", "concentration": "false",
                            "actions": [{"name": "Arc", "description": "2d6 fire", "uses": 2}, {"description": "no name"}], "price": 50}}
            """;
        Func<string, string, string> canonical = (_, value) => value == "огонь" ? "fire" : value;

        WorldBundleParser.ApplyHomebrew(draft, reply, SpellFields, canonical);

        Assert.True(draft.Expanded);
        Assert.Equal("Дуга пламени бьёт по цели.", draft.Body);
        Assert.Equal(new[] { "fire", "arc" }, draft.Tags);
        Assert.Equal(9L, draft.Properties["level"]); // clamped
        Assert.Equal("Evocation", draft.Properties["school"]); // matched through the option's translated label
        Assert.Equal(new object[] { "V", "S" }, (List<object>)draft.Properties["components"]);
        Assert.Equal("fire", draft.Properties["damageType"]);
        Assert.Equal(false, draft.Properties["concentration"]);
        var row = Assert.Single((List<object>)draft.Properties["actions"]);
        Assert.Equal(new Dictionary<string, object> { ["name"] = "Arc", ["description"] = "2d6 fire", ["uses"] = 2L }, (Dictionary<string, object>)row);
        Assert.False(draft.Properties.ContainsKey("price")); // not a field of the type
    }

    [Fact]
    public void ApplyHomebrew_DropsAnOptionThatIsNotListed()
    {
        var draft = new WorldDraft { Kind = DraftKinds.Homebrew };

        WorldBundleParser.ApplyHomebrew(draft, """{"properties": {"school": "Chronomancy"}}""", SpellFields);

        Assert.False(draft.Properties.ContainsKey("school"));
    }

    [Fact]
    public void ApplyPage_RejectsAnEmptyReply()
    {
        Assert.Throws<FormatException>(() => WorldBundleParser.ApplyPage(new WorldDraft(), "  "));
    }

    // ---- Links ----

    private static WorldBundle Linked()
    {
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Homebrew, Type = "magic-item", Name = "Tide-glass", OutlineName = "Tide-glass" });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Wiki, Name = "The Varr Glassworks", OutlineName = "The Glassblowers of Varr" });
        bundle.Drafts.Add(new WorldDraft { Ref = "d3", Kind = DraftKinds.Wiki, Name = "A Rumour", OutlineName = "A Rumour", Selected = false });
        return bundle;
    }

    [Fact]
    public void ResolveLinks_FollowsRenames_AndUnlinksWhatWillHaveNoPage()
    {
        const string text = "See [[The Glassblowers of Varr|the makers]], [[d2]], [[Tide-glass]], [[A Rumour]] and [[Port Varr]].";

        Assert.Equal("See [[The Varr Glassworks|the makers]], [[The Varr Glassworks]], Tide-glass, A Rumour and [[Port Varr]].",
            WorldBundleParser.ResolveLinks(text, Linked()));
        Assert.Equal("See the makers, The Varr Glassworks, Tide-glass, A Rumour and Port Varr.",
            WorldBundleParser.ResolveLinks(text, Linked(), keepLinks: false));
    }

    [Fact]
    public void ResolveLinks_PrefersTheRef_ThenTheOutlineName_ThenTheRename()
    {
        var bundle = Linked();
        bundle.Drafts.Add(new WorldDraft { Ref = "d4", Kind = DraftKinds.Wiki, Name = "Old Varr", OutlineName = "Old Varr" });
        bundle.Drafts[1].Name = "Old Varr"; // the DM renamed the glassworks to the other page's outline name

        // A link meant for the old town still reaches it, not the page that took its name.
        Assert.Equal("[[Old Varr]] and [[Old Varr]]", WorldBundleParser.ResolveLinks("[[Old Varr]] and [[d4]]", bundle));
        Assert.Equal("[[Old Varr]]", WorldBundleParser.ResolveLinks("[[The Glassblowers of Varr]]", bundle));
    }

    [Fact]
    public void ResolveLinks_ToADraftWhoseNameWasCleared_IsPlainText()
    {
        var bundle = Linked();
        bundle.Drafts[1].Name = "  ";

        Assert.Equal("See The Glassblowers of Varr and the makers.",
            WorldBundleParser.ResolveLinks("See [[d2]] and [[The Glassblowers of Varr|the makers]].", bundle));
    }

    [Fact]
    public void ParseOutline_KeepsAtMostTwentyDrafts_AndCutsLongText()
    {
        var drafts = Enumerable.Range(1, 25).Select(i => $$"""{"kind": "wiki", "name": "Page {{i}}{{(i == 1 ? new string('x', 500) : "")}}", "pitch": "{{new string('p', 900)}}"}""");
        var bundle = WorldBundleParser.ParseOutline($$"""{"drafts": [{{string.Join(',', drafts)}}]}""", Rules);

        Assert.Equal(WorldBundleParser.MaxDrafts, bundle.Drafts.Count);
        Assert.Equal(5, bundle.Dropped.Count(d => d.Reason == DropReason.TooMany));
        Assert.Equal(200, bundle.Drafts[0].Name.Length);
        Assert.All(bundle.Drafts, d => Assert.Equal(500, d.Pitch.Length));
    }

    // ---- Translated values ----

    [Fact]
    public void CanonicalOption_IsTheReverseOfDisplayOption()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        var shown = system.DisplayOption("ru", "school", "Evocation");

        Assert.NotEqual("Evocation", shown);
        Assert.Equal("Evocation", system.CanonicalOption("ru", "school", shown));
        Assert.Equal("Evocation", system.CanonicalOption("ru", "school", shown.ToUpperInvariant()));
        Assert.Equal("fire", system.CanonicalOption("he", "damageType", system.DisplayOption("he", string.Empty, "fire")));
        Assert.Equal("Something else", system.CanonicalOption("ru", "school", "Something else"));
        Assert.Equal("Воплощение", system.CanonicalOption("en", "school", "Воплощение"));
    }

    // ---- Answers ----

    [Fact]
    public void Answers_ReadAsTheModelNeedsThem()
    {
        var recipe = WorldRecipes.Find("homebrew")!;
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        var answers = new Dictionary<string, string?> { ["type"] = "magic-item", ["concept"] = "  ", ["power"] = "major", ["tone"] = null, ["history"] = "true" };

        var read = WorldBuilderService.Answers(recipe, answers, system).ToDictionary(a => a.Question, a => a.Answer);

        Assert.Equal("Magic Item (type id: magic-item)", read[recipe.Question("type")!.Ask]);
        Assert.Equal("surprise me", read[recipe.Question("concept")!.Ask]);
        Assert.Equal("major", read[recipe.Question("power")!.Ask]);
        Assert.Equal("surprise me", read[recipe.Question("tone")!.Ask]);
        Assert.Equal("yes", read[recipe.Question("history")!.Ask]);
    }

    [Fact]
    public void Answers_WithoutARequiredOne_Throw()
    {
        var recipe = WorldRecipes.Find("homebrew")!;

        var ex = Assert.Throws<LocalizedException>(() => WorldBuilderService.Answers(recipe, new Dictionary<string, string?>(), null));
        Assert.Equal("WorldBuilder.Error.Required", ex.Key);
    }

    // ---- Prompts ----

    [Fact]
    public void OutlinePrompt_ListsTheAnswersAndChoices_AndKeepsKeysEnglishInAnotherLanguage()
    {
        var (system, user) = AiPrompts.WorldOutline(new WorldOutlineRequest(
            "Design one item.", "SRD", new[] { new WorldAnswer("How powerful?", "major") },
            new[] { "homebrew: a Library entry" }, new[] { "magic-item (Magic Item)" }, new[] { "existing:port-varr (Port Varr)" },
            new[] { "Old Name" }, "Salt and Ash", "he"));

        Assert.Contains("Design one item.", system);
        Assert.Contains("never use the names of published settings", system);
        Assert.Contains("in Hebrew", system);
        Assert.Contains("keep every JSON key, kind, type id, ref and existing key exactly as listed", system);
        Assert.Contains("- How powerful? major", user);
        Assert.Contains("- magic-item (Magic Item)", user);
        Assert.Contains("- existing:port-varr (Port Varr)", user);
        Assert.Contains("- Old Name", user);
        Assert.Contains("Campaign: Salt and Ash", user);
    }

    [Fact]
    public void ExpandHomebrewPrompt_DescribesEachField()
    {
        var (_, user) = AiPrompts.WorldExpandHomebrew(new WorldExpandRequest("Tide-glass", "A lens.", "Magic Item", "SRD",
            new[] { new FieldSpec("rarity", "Rarity", "Dropdown", new[] { "Rare", "Legendary" }, null, null), new FieldSpec("charges", "Charges", "Number", null, 0, 10, "How many uses.") },
            Array.Empty<WorldAnswer>(), new[] { "The Varr Glassworks (wiki page): who made it" }, Array.Empty<string>()));

        Assert.Contains("- rarity: Rarity, Dropdown, options: Rare | Legendary", user);
        Assert.Contains("- charges: Charges, Number, 0-10; How many uses.", user);
        Assert.Contains("- The Varr Glassworks (wiki page): who made it", user);
    }

    // ---- The assistant's retry ----

    private sealed class ScriptedChat(params string[] replies) : IChatClient
    {
        private readonly Queue<string> _replies = new(replies);
        public List<int> MessageCounts { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            MessageCounts.Add(messages.Count());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _replies.Dequeue())));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static readonly WorldOutlineRequest AnyOutline = new("Design one item.", "SRD", Array.Empty<WorldAnswer>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public async Task Outline_GivesAnUnusableReplyOneMoreChance_ShowingItBack()
    {
        var chat = new ScriptedChat("Sorry, here are some ideas: a lens.", """{"drafts": [{"kind": "wiki", "name": "The Lens"}]}""");

        var bundle = await new AiAssistant(chat).OutlineWorldAsync(AnyOutline, Rules);

        Assert.Equal("The Lens", Assert.Single(bundle.Drafts).Name);
        Assert.Equal(new[] { 2, 4 }, chat.MessageCounts); // system + user, then the bad reply and the nudge
    }

    [Fact]
    public async Task Outline_StopsAfterTheSecondUnusableReply()
    {
        var chat = new ScriptedChat("no", "still no");

        await Assert.ThrowsAsync<FormatException>(() => new AiAssistant(chat).OutlineWorldAsync(AnyOutline, Rules));
    }

    // ---- Saving ----

    private sealed record Saving(WorldBuilderService Service, List<WikiPageDocument> Pages, List<HomebrewEntryDocument> Entries, List<ActorDocument> Actors, List<FactDocument> Facts)
    {
        public List<RegionDocument> NewRegions { get; } = new();
        public List<(string RegionId, LocationDocument Place)> AddedPlaces { get; } = new();

        /// <summary>While true, saving a homebrew entry fails, as a save that breaks half-way would.</summary>
        public bool FailHomebrew { get; set; }

        /// <summary>The campaign as stored; its Bestiary shortlist is what saving added to it.</summary>
        public CampaignDocument Stored { get; } = new() { Id = "c1", SystemId = "srd5e", Name = "Salt and Ash" };
    }

    /// <summary>A system whose creatures ("monster") can stand on the table, and whose spells cannot.</summary>
    private static readonly SystemDefinition CreatureSystem = new()
    {
        Id = "srd5e",
        EntityTypes = new List<ContentTypeDefinition> { new() { Type = "monster", ActorBlueprint = true }, new() { Type = "spell" } }
    };

    private static Saving Service(IEnumerable<WikiPageDocument>? existingPages = null, params HomebrewEntryDocument[] existing) => Service(existingPages, Array.Empty<RegionDocument>(), existing);

    private static Saving Service(IEnumerable<WikiPageDocument>? existingPages, IEnumerable<RegionDocument> existingRegions, params HomebrewEntryDocument[] existing) =>
        Service(existingPages, existingRegions, null, existing);

    /// <param name="registry">The packs' content, which the Library and the cast both read; empty when null.</param>
    private static Saving Service(IEnumerable<WikiPageDocument>? existingPages, IEnumerable<RegionDocument> existingRegions, ContentRegistry? registry, params HomebrewEntryDocument[] existing)
    {
        registry ??= new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
        var pages = new List<WikiPageDocument>();
        var entries = new List<HomebrewEntryDocument>();
        var actors = new List<ActorDocument>();
        var facts = new List<FactDocument>();
        var wiki = Substitute.For<IWikiRepository>();
        wiki.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns((existingPages ?? Array.Empty<WikiPageDocument>()).ToList().AsEnumerable());
        wiki.TrySaveAsync(Arg.Any<WikiPageDocument>(), Arg.Any<CampaignAccess>()).Returns(ci =>
        {
            var page = ci.Arg<WikiPageDocument>();
            page.Id = $"page-{pages.Count + 1}";
            pages.Add(page);
            return ci.Arg<CampaignAccess>().IsDm;
        });
        var homebrew = Substitute.For<IHomebrewRepository>();
        homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(existing.AsEnumerable());
        var factRepo = Substitute.For<IFactRepository>();
        factRepo.SaveAsync(Arg.Do<FactDocument>(facts.Add), Arg.Any<CampaignAccess>()).Returns(true);
        var actorRepo = Substitute.For<IActorRepository>();
        actorRepo.TrySaveAsync(Arg.Do<ActorDocument>(actors.Add), Arg.Any<CampaignAccess>()).Returns(true);
        var catalog = Substitute.For<ISystemCatalog>();
        catalog.GetUISchemas(Arg.Any<string>()).Returns(Enumerable.Empty<UISchema>());
        catalog.GetDefinition("srd5e").Returns(CreatureSystem);
        var campaigns = Substitute.For<ICampaignRepository>();
        var library = new CampaignLibrary(registry, homebrew, catalog);
        var providers = new AiProviderService(new EphemeralDataProtectionProvider(), Substitute.For<IUserRepository>(), new AIConfiguration(), new AIImageConfiguration(),
            Substitute.For<IHttpClientFactory>(), NullLogger<AiProviderService>.Instance);
        var components = new ComponentRegistry();
        var runtimes = new Codex.Persistence.Runtime.CampaignRuntimeManager(actorRepo, Substitute.For<ISessionRepository>(), Substitute.For<ICampaignRepository>(),
            Substitute.For<IEncounterRepository>(), components, new PluginLoader(NullLogger<PluginLoader>.Instance, components, Substitute.For<IContentPackLoader>()), NullLoggerFactory.Instance);
        var cast = new CastService(runtimes, registry, components, catalog);
        var regions = Substitute.For<IRegionRepository>();
        regions.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(existingRegions.ToList().AsEnumerable());
        var saving = new Saving(new WorldBuilderService(providers, catalog, library, homebrew, wiki, factRepo, cast, regions, campaigns), pages, entries, actors, facts);
        campaigns.UpdateAsync("c1", Arg.Any<Action<CampaignDocument>>()).Returns(ci =>
        {
            ci.Arg<Action<CampaignDocument>>()(saving.Stored);
            return saving.Stored;
        });
        homebrew.TrySaveAsync(Arg.Any<HomebrewEntryDocument>(), Arg.Any<CampaignAccess>()).Returns(ci =>
        {
            if (saving.FailHomebrew) return false;
            var entry = ci.Arg<HomebrewEntryDocument>();
            entry.Id = $"entry-{entries.Count + 1}"; // as the repository does
            entries.Add(entry);
            return true;
        });
        regions.SaveAsync(Arg.Do<RegionDocument>(saving.NewRegions.Add), Arg.Any<CampaignAccess>()).Returns(true);
        regions.UpsertLocationAsync(Arg.Any<string>(), Arg.Any<LocationDocument>(), Arg.Any<CampaignAccess>()).Returns(ci =>
        {
            saving.AddedPlaces.Add((ci.ArgAt<string>(0), ci.ArgAt<LocationDocument>(1)));
            return true;
        });
        return saving;
    }

    private static readonly CampaignDocument Campaign = new() { Id = "c1", SystemId = "srd5e", Name = "Salt and Ash" };
    private static readonly CampaignAccess Dm = new("c1", "dm", CampaignRole.DM);

    [Fact]
    public async Task Save_StoresTheTickedDraftsHiddenFromPlayers_WithLinksRewritten()
    {
        var (service, pages, entries, _, _) = Service(null, new HomebrewEntryDocument { Type = "magic-item", EntryId = "tide-glass", Name = "Tide-glass" });
        var bundle = Linked();
        bundle.Drafts[0].Body = "Made by [[The Glassblowers of Varr]].";
        bundle.Drafts[0].Properties["rarity"] = "Rare";
        bundle.Drafts[0].Tags = new List<string> { "glass" };
        bundle.Drafts[1].Body = "They made [[Tide-glass]]. Ask at [[Port Varr]].";

        var result = await service.SaveAsync(Campaign, Dm, WorldRecipes.Find("homebrew")!, bundle);

        var page = Assert.Single(pages); // the unticked rumour is not saved
        Assert.Equal("The Varr Glassworks", page.Title);
        Assert.Equal(WikiVisibility.DmOnly, page.Visibility);
        Assert.Equal("They made Tide-glass. Ask at [[Port Varr]].", page.Body);
        Assert.Equal(new[] { "homebrew" }, page.Tags);

        var entry = Assert.Single(entries);
        Assert.False(entry.IsPublished);
        Assert.Equal(("magic-item", "tide-glass-2", "srd5e"), (entry.Type, entry.EntryId, entry.SystemId)); // the slug was taken
        Assert.Equal("Made by The Varr Glassworks.", entry.Description);
        Assert.Equal("Rare", entry.Properties["rarity"]);
        Assert.Equal(new[] { "glass" }, entry.Tags);
        Assert.Equal((1, 1), (result.Entries.Count, result.Pages.Count));
    }

    [Fact]
    public async Task Save_UsesThePitch_ForADraftNeverWrittenInFull()
    {
        var (service, _, entries, _, _) = Service();
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Homebrew, Type = "spell", Name = "Ember Step", Pitch = "Step through a fire." });

        await service.SaveAsync(Campaign, Dm, WorldRecipes.Find("homebrew")!, bundle);

        Assert.Equal("Step through a fire.", Assert.Single(entries).Description);
    }

    [Fact]
    public async Task Save_IsForTheDmOnly()
    {
        var (service, pages, entries, _, _) = Service();

        var ex = await Assert.ThrowsAsync<LocalizedException>(() => service.SaveAsync(Campaign, new CampaignAccess("c1", "p", CampaignRole.Player), WorldRecipes.Find("homebrew")!, Linked()));

        Assert.Equal("WorldBuilder.Error.OnlyDm", ex.Key);
        Assert.Empty(pages);
        Assert.Empty(entries);
    }

    // ---- The faction recipe: people, the cast and secrets ----

    private static readonly BundleRules FactionRules = new(new[] { DraftKinds.Wiki, DraftKinds.Npc, DraftKinds.Fact }, new Dictionary<string, string>(), new[] { "port-varr" });

    [Fact]
    public void FactionRecipe_WritesAPageItsPeopleAndASecret()
    {
        var faction = WorldRecipes.Find("faction")!;

        Assert.Equal(new[] { DraftKinds.Wiki, DraftKinds.Npc, DraftKinds.Fact }, faction.Drafts);
        Assert.Equal("leaderPlusOne", faction.Question("people")!.Default);
        Assert.Equal("true", faction.Question("secret")!.Default);
        Assert.DoesNotContain(faction.Questions, q => q.Required); // everything can be left to the model
    }

    [Fact]
    public void ParseOutline_KeepsPeopleAndSecrets_WithTheirLinks()
    {
        const string reply = """
            {"drafts": [
              {"ref": "f", "kind": "wiki", "name": "The Salt Wardens", "pitch": "Smugglers who police the docks they rob.", "links": ["l", "existing:port-varr"]},
              {"ref": "l", "kind": "npc", "name": "Mother Ilse", "pitch": "Their leader, a retired harbour pilot.", "links": ["f"]},
              {"ref": "s", "kind": "fact", "name": "The wardens pay the harbour master", "links": ["f", "l", "existing:nowhere"]},
              {"kind": "homebrew", "type": "spell", "name": "Not this recipe"}
            ]}
            """;

        var bundle = WorldBundleParser.ParseOutline(reply, FactionRules);

        Assert.Equal(new[] { DraftKinds.Wiki, DraftKinds.Npc, DraftKinds.Fact }, bundle.Drafts.Select(d => d.Kind));
        Assert.Equal(new[] { "d1", "d2" }, bundle.Drafts[2].Links); // the unknown existing page is dropped
        Assert.Equal(DropReason.Kind, Assert.Single(bundle.Dropped).Reason);
        Assert.All(bundle.Drafts, d => Assert.False(d.Cast)); // the DM decides who joins the cast
    }

    private static WorldBundle Faction()
    {
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Wiki, Name = "The Salt Wardens", OutlineName = "The Salt Wardens", Body = "Led by [[Mother Ilse]].", Links = { "d2", "d3" } });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Npc, Name = "Mother Ilse", OutlineName = "Mother Ilse", Body = "She leads [[The Salt Wardens]].", Cast = true, Links = { "d1" } });
        bundle.Drafts.Add(new WorldDraft { Ref = "d3", Kind = DraftKinds.Npc, Name = "Brann", OutlineName = "Brann", Pitch = "Her nephew, a lookout." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d4", Kind = DraftKinds.Fact, Name = "The wardens pay the harbour master", OutlineName = "The wardens pay the harbour master",
            Body = "[[Mother Ilse]] pays him in salt.", Links = { "d1", "d2", "existing:port-varr" } });
        return bundle;
    }

    [Fact]
    public void ResolveLinks_LinksToPeople_ButNotToSecrets()
    {
        var bundle = Faction();

        Assert.Equal("[[Mother Ilse]] knows that the wardens pay the harbour master.",
            WorldBundleParser.ResolveLinks("[[d2]] knows that [[d4|the wardens pay the harbour master]].", bundle));
    }

    [Fact]
    public async Task Save_WritesPeoplesPages_TicksThemIntoTheCast_AndFilesTheSecretUnderWhatItConcerns()
    {
        var (service, pages, _, actors, facts) = Service(new[] { new WikiPageDocument { Id = "page-port", Slug = "port-varr", Title = "Port Varr" } });

        var result = await service.SaveAsync(Campaign, Dm, WorldRecipes.Find("faction")!, Faction());

        Assert.Equal(new[] { "The Salt Wardens", "Mother Ilse", "Brann" }, pages.Select(p => p.Title));
        Assert.All(pages, p => Assert.Equal(WikiVisibility.DmOnly, p.Visibility));
        Assert.Equal(new[] { "faction" }, pages[0].Tags);
        Assert.Equal(new[] { "faction", "person" }, pages[1].Tags);
        Assert.Equal("Led by [[Mother Ilse]].", pages[0].Body);
        Assert.Equal("Her nephew, a lookout.", pages[2].Body); // never written in full: the pitch

        var ilse = Assert.Single(actors); // only the person ticked for the cast
        Assert.Equal(("Mother Ilse", ActorKind.NonPlayerCharacter, ActorVisibility.Hidden), (ilse.Name, ilse.Kind, ilse.Visibility));
        // Players read a revealed NPC's biography; the page tells what she hides, so it goes in the DM's notes, links flattened.
        Assert.Null(ilse.Biography);
        Assert.Contains("Mother Ilse", ilse.DmNotes);
        Assert.EndsWith("She leads The Salt Wardens.", ilse.DmNotes);

        var secret = Assert.Single(facts);
        Assert.Equal(("The wardens pay the harbour master", "Mother Ilse pays him in salt.", FactVisibility.DmOnly), (secret.Summary, secret.Details, secret.Visibility));
        Assert.Equal(new[] { "page-1", "page-2", ilse.Id, "page-port" }, secret.RelatedEntityIds);
        Assert.Equal(5, result.Count); // 3 pages, 1 cast member, 1 secret
    }

    [Fact]
    public async Task Save_LeavesOutUntickedPeople_AndTheirCastMember()
    {
        var (service, pages, _, actors, facts) = Service();
        var bundle = Faction();
        bundle.Drafts[1].Selected = false;

        await service.SaveAsync(Campaign, Dm, WorldRecipes.Find("faction")!, bundle);

        Assert.DoesNotContain(pages, p => p.Title == "Mother Ilse");
        Assert.Empty(actors);
        Assert.Equal("Led by Mother Ilse.", pages[0].Body); // no page left to link to
        Assert.Equal(new[] { "page-1" }, Assert.Single(facts).RelatedEntityIds);
    }

    [Fact]
    public void ExpandPrompts_WriteAPersonInSections_AndASecretAsPlainSentences()
    {
        WorldExpandRequest For(string kind) => new("Mother Ilse", "Their leader.", null, "SRD", Array.Empty<FieldSpec>(), Array.Empty<WorldAnswer>(),
            Array.Empty<string>(), Array.Empty<string>(), Kind: kind);

        var (person, _) = AiPrompts.WorldExpandPage(For(DraftKinds.Npc));
        var (secret, secretUser) = AiPrompts.WorldExpandPage(For(DraftKinds.Fact));
        var (page, _) = AiPrompts.WorldExpandPage(For(DraftKinds.Wiki));

        Assert.Contains("one person", person);
        Assert.Contains("what they hide", person);
        Assert.Contains("[[Name]]", person);
        Assert.Contains("two to four plain sentences", secret);
        Assert.DoesNotContain("[[Name]]", secret);
        Assert.StartsWith("Secret: Mother Ilse", secretUser);
        Assert.Contains("three to six short sections", page);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("he")]
    public void ExpandPrompt_ForASecret_EndsWithNoHeadings_AfterTheStyleNote(string? language)
    {
        var (secret, _) = AiPrompts.WorldExpandPage(new WorldExpandRequest("The bell", "It rings.", null, "SRD", Array.Empty<FieldSpec>(), Array.Empty<WorldAnswer>(),
            Array.Empty<string>(), Array.Empty<string>(), Language: language, Kind: DraftKinds.Fact));

        // The general style allows headings; the secret's own rule comes last, so it wins.
        Assert.True(secret.IndexOf("short headings", StringComparison.Ordinal) < secret.IndexOf("no headings, lists or links", StringComparison.Ordinal));
        Assert.EndsWith("plain sentences only.", secret);
    }

    // ---- The settlement recipe: places on the World page ----

    [Fact]
    public void SettlementRecipe_WritesPlacesPeopleFactionsAndRumours()
    {
        var settlement = WorldRecipes.Find("settlement")!;

        Assert.Equal(new[] { DraftKinds.Wiki, DraftKinds.Place, DraftKinds.Npc, DraftKinds.Fact }, settlement.Drafts);
        Assert.Equal("sketch", settlement.Question("detail")!.Default);
        Assert.DoesNotContain(settlement.Questions, q => q.Required);
    }

    private static WorldBundle Settlement()
    {
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Wiki, Name = "Varr", OutlineName = "Varr", Body = "The [[Salt Market]] never closes." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Place, Name = "Salt Market", OutlineName = "Salt Market", Pitch = "Stalls by [[Varr]]'s quay.", Body = "## Sights\nStalls in [[Varr]].", OnMap = true });
        bundle.Drafts.Add(new WorldDraft { Ref = "d3", Kind = DraftKinds.Place, Name = "The Drowned Bell", OutlineName = "The Drowned Bell", Pitch = "A flooded inn." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d4", Kind = DraftKinds.Fact, Name = "The bell rings before every storm", OutlineName = "The bell rings before every storm", Links = { "d2", "d3" } });
        return bundle;
    }

    [Fact]
    public async Task Save_PutsTickedPlacesOnTheWorldPage_InANewRegionNamedAfterTheSettlement()
    {
        var saving = Service();

        var result = await saving.Service.SaveAsync(Campaign, Dm, WorldRecipes.Find("settlement")!, Settlement());

        Assert.Equal("The [[Salt Market]] never closes.", saving.Pages[0].Body); // places are pages too, so links stay
        Assert.Equal(new[] { "settlement", "place" }, saving.Pages[1].Tags);
        var region = Assert.Single(saving.NewRegions);
        Assert.Equal(("Varr", "c1"), (region.Name, region.CampaignId));
        var market = Assert.Single(region.Locations); // only the ticked place
        Assert.Equal(("Salt Market", ActorVisibility.Hidden), (market.Name, market.Visibility));
        // Players read a revealed place's description, so it is the pitch (links flattened), not the page that tells what it hides.
        Assert.Equal("Stalls by Varr's quay.", market.Description);
        Assert.Equal(new[] { "page-2", market.Id, "page-3" }, Assert.Single(saving.Facts).RelatedEntityIds);
        Assert.Equal(market, Assert.Single(result.Places));
    }

    [Fact]
    public async Task Save_AddsPlacesToAnExistingRegionOfTheSameName()
    {
        var saving = Service(null, new[] { new RegionDocument { Id = "region-varr", CampaignId = "c1", Name = "varr " } });

        await saving.Service.SaveAsync(Campaign, Dm, WorldRecipes.Find("settlement")!, Settlement());

        Assert.Empty(saving.NewRegions);
        var (regionId, place) = Assert.Single(saving.AddedPlaces);
        Assert.Equal(("region-varr", "Salt Market"), (regionId, place.Name));
    }

    [Fact]
    public async Task Save_WithoutTickedPlaces_LeavesTheWorldPageAlone()
    {
        var saving = Service();
        var bundle = Settlement();
        bundle.Drafts[1].OnMap = false;

        var result = await saving.Service.SaveAsync(Campaign, Dm, WorldRecipes.Find("settlement")!, bundle);

        Assert.Empty(saving.NewRegions);
        Assert.Empty(result.Places);
    }

    [Fact]
    public void ExpandPrompt_WritesAPlaceAsAPageAboutOnePlace()
    {
        var (system, _) = AiPrompts.WorldExpandPage(new WorldExpandRequest("Salt Market", "Stalls.", null, "SRD", Array.Empty<FieldSpec>(), Array.Empty<WorldAnswer>(),
            Array.Empty<string>(), Array.Empty<string>(), Kind: DraftKinds.Place));

        Assert.Contains("one place", system);
        Assert.Contains("[[Name]]", system);
    }

    // ---- The quest recipe: a fixed outline, and homebrew beside the pages ----

    [Fact]
    public void QuestRecipe_GivesThePageAFixedOutline_AndSizesHomebrewToTheParty()
    {
        var quest = WorldRecipes.Find("quest")!;

        Assert.Equal(new[] { DraftKinds.Wiki, DraftKinds.Npc, DraftKinds.Place, DraftKinds.Fact, DraftKinds.Homebrew }, quest.Drafts);
        Assert.Contains("Hook; Stakes; Acts", quest.Write[DraftKinds.Wiki]);
        Assert.Contains("If the party fails", quest.Write[DraftKinds.Wiki]);
        Assert.Contains("party's level", quest.Write[DraftKinds.Homebrew]);
        Assert.Equal(("oneShot", "true", "false"), (quest.Question("length")!.Default, quest.Question("creature")!.Default, quest.Question("reward")!.Default));
    }

    [Fact]
    public void ExpandPrompts_CarryTheRecipesInstructions_InEveryLanguage()
    {
        var guidance = "Use exactly these sections: Hook;\nStakes.";
        var page = new WorldExpandRequest("The Bell", "A bell rings.", null, "SRD", Array.Empty<FieldSpec>(), Array.Empty<WorldAnswer>(),
            Array.Empty<string>(), Array.Empty<string>(), Language: "he", Guidance: guidance);

        var (pageSystem, _) = AiPrompts.WorldExpandPage(page);
        var (homebrewSystem, _) = AiPrompts.WorldExpandHomebrew(page with { Kind = DraftKinds.Homebrew, TypeLabel = "Creature" });
        var (plain, _) = AiPrompts.WorldExpandPage(page with { Guidance = null });

        Assert.Contains("Use exactly these sections: Hook; Stakes.", pageSystem);
        Assert.Contains("in Hebrew", pageSystem); // the headings it names are translated, too
        Assert.Contains("Use exactly these sections: Hook; Stakes.", homebrewSystem);
        Assert.DoesNotContain("Use exactly these sections", plain);
    }

    [Fact]
    public void ParseOutline_ForAQuest_KeepsHomebrewBesideThePages()
    {
        var rules = new BundleRules(WorldRecipes.Find("quest")!.Drafts, new Dictionary<string, string> { ["monster"] = "monster", ["Magic Item"] = "magic-item" }, Array.Empty<string>());
        const string reply = """
            {"drafts": [
              {"ref": "q", "kind": "wiki", "name": "The Drowned Bell", "pitch": "A bell rings under the harbour.", "links": ["v", "c"]},
              {"ref": "v", "kind": "npc", "name": "Sister Maren", "pitch": "She rings it.", "links": ["q"]},
              {"ref": "c", "kind": "homebrew", "type": "monster", "name": "Bell-drowned", "links": ["q"]},
              {"ref": "i", "kind": "homebrew", "type": "Magic Item", "name": "The Clapper", "links": ["q"]}
            ]}
            """;

        var bundle = WorldBundleParser.ParseOutline(reply, rules);

        Assert.Equal(new[] { ("wiki", (string?)null), ("npc", null), ("homebrew", "monster"), ("homebrew", "magic-item") }, bundle.Drafts.Select(d => (d.Kind, d.Type)));
        Assert.Empty(bundle.Dropped);
    }

    [Fact]
    public async Task Save_AQuest_StoresItsPagesAndItsHomebrew_TaggedAsAQuest()
    {
        var (service, pages, entries, _, _) = Service();
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Wiki, Name = "The Drowned Bell", OutlineName = "The Drowned Bell", Body = "## Hook\nFight the [[Bell-drowned]] for [[The Clapper]]." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Homebrew, Type = "monster", Name = "Bell-drowned", OutlineName = "Bell-drowned", Body = "Haunts [[The Drowned Bell]]." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d3", Kind = DraftKinds.Homebrew, Type = "magic-item", Name = "The Clapper", OutlineName = "The Clapper", Pitch = "A bronze clapper." });

        var result = await service.SaveAsync(Campaign, Dm, WorldRecipes.Find("quest")!, bundle);

        var page = Assert.Single(pages);
        Assert.Equal(new[] { "quest" }, page.Tags);
        Assert.Equal("## Hook\nFight the Bell-drowned for The Clapper.", page.Body); // homebrew has no page to link to
        Assert.Equal(new[] { ("monster", "bell-drowned"), ("magic-item", "the-clapper") }, entries.Select(e => (e.Type, e.EntryId)));
        Assert.All(entries, e => Assert.False(e.IsPublished));
        Assert.Equal("Haunts The Drowned Bell.", entries[0].Description);
        Assert.Equal(3, result.Count);
    }

    // ---- Creatures on the Bestiary shortlist ----

    [Fact]
    public async Task Save_PutsATickedCreatureOnTheBestiaryShortlist_AsHomebrew()
    {
        var saving = Service();
        var campaign = new CampaignDocument { Id = "c1", SystemId = "srd5e" };
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Homebrew, Type = "monster", Name = "Bell-drowned ", Pitch = "A drowned bell-ringer.", Bestiary = true });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Homebrew, Type = "monster", Name = "Tide Eel", Pitch = "An eel." });

        var result = await saving.Service.SaveAsync(campaign, Dm, WorldRecipes.Find("quest")!, bundle);

        var listed = Assert.Single(saving.Stored.Bestiary); // the unticked eel stays in the Library only
        Assert.Equal(("monster", "bell-drowned", true, "Bell-drowned", 1), (listed.EntryType, listed.EntryId, listed.IsHomebrew, listed.Name, listed.Count));
        Assert.Equal(listed.Key, Assert.Single(result.Shortlisted).Key);
        Assert.Equal(listed.Key, Assert.Single(campaign.Bestiary).Key); // the caller's copy is refreshed
        Assert.Equal(2, result.Count); // the shortlist adds no new document
    }

    [Fact]
    public async Task Save_ShortlistsOnlyCreatures_ThatAreKept_AndNotAlreadyListed()
    {
        var saving = Service(null, new HomebrewEntryDocument { Type = "monster", EntryId = "gull-hag", Name = "Gull Hag" });
        saving.Stored.Bestiary.Add(new BestiaryEntry { EntryType = "monster", EntryId = "tide-eel", IsHomebrew = true, Name = "Tide Eel", Note = "act two" });
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Homebrew, Type = "spell", Name = "Bell Toll", Pitch = "A toll.", Bestiary = true });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Homebrew, Type = "monster", Name = "Mud Imp", Pitch = "An imp.", Bestiary = true, Selected = false });
        bundle.Drafts.Add(new WorldDraft { Ref = "d3", Kind = DraftKinds.Homebrew, Type = "monster", Name = "Gull Hag", Pitch = "A hag.", Bestiary = true });

        var result = await saving.Service.SaveAsync(Campaign, Dm, WorldRecipes.Find("homebrew")!, bundle);

        // A spell cannot stand on the table and the unticked imp is not saved; the hag's id was taken, so the new one is listed.
        Assert.Equal(new[] { ("tide-eel", "act two"), ("gull-hag-2", null) }, saving.Stored.Bestiary.Select(b => (b.EntryId, b.Note)));
        Assert.Equal("gull-hag-2", Assert.Single(result.Shortlisted).EntryId);
    }

    [Fact]
    public async Task Save_GivesEveryNewPageItsOwnTitle_AndLinksFollowTheRename()
    {
        var saving = Service(new[] { new WikiPageDocument { Id = "old", Title = "The Bell", Slug = "the-bell" } });
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Wiki, Name = "The Bell", OutlineName = "The Bell", Body = "Ask [[d2]]." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Npc, Name = "the bell ", OutlineName = "Maren", Body = "Rings [[The Bell]].", Cast = true });

        await saving.Service.SaveAsync(Campaign, Dm, WorldRecipes.Find("quest")!, bundle);

        // Links go by title, so two pages called "The Bell" would both reach the old one.
        Assert.Equal(new[] { "The Bell (2)", "the bell (3)" }, saving.Pages.Select(p => p.Title));
        Assert.Equal("Ask [[the bell (3)]].", saving.Pages[0].Body);
        Assert.Equal("Rings [[The Bell (2)]].", saving.Pages[1].Body);
        var maren = Assert.Single(saving.Actors);
        Assert.Equal("the bell", maren.Name); // a person keeps their name; only the page needs its own title
        Assert.Contains("“the bell (3)”", maren.DmNotes); // and the notes point at the page by its title
    }

    [Fact]
    public async Task Save_RetriedAfterAFailure_DoesNotWriteAnythingTwice()
    {
        var saving = Service();
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Wiki, Name = "The Drowned Bell", OutlineName = "The Drowned Bell", Body = "Fight the [[d2]]." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Homebrew, Type = "monster", Name = "Bell-drowned", OutlineName = "Bell-drowned", Pitch = "A ringer." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d3", Kind = DraftKinds.Fact, Name = "The bell rings at dusk", OutlineName = "The bell rings at dusk", Links = { "d1", "d2" } });
        saving.FailHomebrew = true;

        await Assert.ThrowsAsync<LocalizedException>(() => saving.Service.SaveAsync(Campaign, Dm, WorldRecipes.Find("quest")!, bundle));
        saving.FailHomebrew = false;
        var retried = await saving.Service.SaveAsync(Campaign, Dm, WorldRecipes.Find("quest")!, bundle);

        Assert.Single(saving.Pages); // saved by the first attempt only
        Assert.Single(saving.Entries);
        // The secret still knows the page from the first attempt, and the creature from the second.
        Assert.Equal(new[] { "page-1", "entry-1" }, Assert.Single(saving.Facts).RelatedEntityIds);
        Assert.Equal((0, 1, 1), (retried.Pages.Count, retried.Entries.Count, retried.Facts.Count));
    }

    [Fact]
    public async Task Save_WithoutTickedCreatures_LeavesTheCampaignAlone()
    {
        var saving = Service();
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Homebrew, Type = "monster", Name = "Tide Eel", Pitch = "An eel." });

        var result = await saving.Service.SaveAsync(Campaign, Dm, WorldRecipes.Find("homebrew")!, bundle);

        Assert.Empty(result.Shortlisted);
        Assert.Empty(saving.Stored.Bestiary);
    }

    // ---- Changes the DM asks for ----

    private static WorldBundle Previous()
    {
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Wiki, Name = "Glassblowers", OutlineName = "Glassblowers", Pitch = "Who made it.", Body = "Old text.", Expanded = true });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Homebrew, Type = "magic-item", Name = "Sea Lens", OutlineName = "Tide-glass", Pitch = "A lens.", Selected = false });
        bundle.Drafts.Add(new WorldDraft { Ref = "d3", Kind = DraftKinds.Wiki, Name = "Harbour", OutlineName = "Harbour", Pitch = "Docks." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d4", Kind = DraftKinds.Wiki, Name = "The Eel", OutlineName = "The Eel", Pitch = "An eel." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d5", Kind = DraftKinds.Wiki, Name = "The Bell", OutlineName = "The Bell", Pitch = "It rings.", Body = "Bell text.", Expanded = true, Links = { "d4", "d1", "existing:port-varr" } });
        return bundle;
    }

    [Fact]
    public void ParseRevision_KeepsTheDraftsTheReplyKeeps_WithTheirTextRenamesAndTicks()
    {
        var previous = Previous();
        var (glass, lens, harbour, bell) = (previous.Drafts[0], previous.Drafts[1], previous.Drafts[2], previous.Drafts[4]);
        const string reply = """
            {"drafts": [
              {"ref": "d1", "kind": "wiki", "name": "The Glassblowers' Guild", "pitch": "A darker guild.", "links": ["d3", "n1"]},
              {"ref": "d2", "kind": "homebrew", "type": "magic-item", "name": "Tide-glass", "pitch": "A cursed lens."},
              {"ref": "d3", "kind": "wiki", "name": "The Drowned Harbour", "pitch": "Flooded docks."},
              {"ref": "n1", "kind": "wiki", "name": "The Rival", "pitch": "A rival guild.", "links": ["d1"]}
            ]}
            """;

        var revised = WorldBundleParser.ParseRevision(reply, Rules, previous);

        Assert.Equal(new[] { "d1", "d2", "d3", "d6", "d5" }, revised.Drafts.Select(d => d.Ref));
        Assert.Same(glass, revised.Drafts[0]);
        // Written: its name, pitch and text stay, so they keep matching each other.
        Assert.Equal(("Glassblowers", "Who made it.", "Old text."), (glass.Name, glass.Pitch, glass.Body));
        // Renamed by the DM and unticked: the rename and the tick stay, the pitch follows the change.
        Assert.Equal(("Sea Lens", "A cursed lens.", false), (lens.Name, lens.Pitch, lens.Selected));
        // Untouched by the DM: takes the new name, and still answers to the old one for links in written texts.
        Assert.Equal(("The Drowned Harbour", "Harbour", "Flooded docks."), (harbour.Name, harbour.OutlineName, harbour.Pitch));
        Assert.Equal(new[] { "d3", "d6" }, glass.Links);
        Assert.Equal(new[] { "d1" }, revised.Drafts[3].Links);
        // Left out: the written bell stays unticked, without its link to the eel; the eel, never worked on, is gone.
        Assert.Same(bell, revised.Drafts[4]);
        Assert.False(bell.Selected);
        Assert.Equal(new[] { "d1", "existing:port-varr" }, bell.Links);
        Assert.Equal(new[] { "The Bell" }, revised.SetAside);
        Assert.DoesNotContain(revised.Drafts, d => d.Name == "The Eel");
    }

    [Fact]
    public void ParseRevision_MatchesByName_WhenTheReplyRenumbers_AndOnlyOnce_AndNeverAcrossKinds()
    {
        var previous = Previous();
        const string reply = """
            {"drafts": [
              {"ref": "a", "kind": "wiki", "name": "Harbour", "pitch": "Docks again."},
              {"ref": "b", "kind": "wiki", "name": "Harbour", "pitch": "A second harbour."},
              {"ref": "d1", "kind": "homebrew", "type": "spell", "name": "Glassblowers", "pitch": "Now a spell."}
            ]}
            """;

        var revised = WorldBundleParser.ParseRevision(reply, Rules, previous);

        Assert.Same(previous.Drafts[2], revised.Drafts[0]);
        Assert.Equal("d6", revised.Drafts[1].Ref);
        Assert.Equal(("d7", "spell"), (revised.Drafts[2].Ref, revised.Drafts[2].Type));
        // The written guild page the reply turned into a spell is kept as it was, unticked, beside the new spell.
        Assert.Contains(previous.Drafts[0], revised.Drafts);
        Assert.False(previous.Drafts[0].Selected);
    }

    [Fact]
    public void ParseRevision_OfAnUnusableReply_LeavesThePreviousOutlineAlone()
    {
        var previous = Previous();

        Assert.Throws<FormatException>(() => WorldBundleParser.ParseRevision("""{"ideas": []}""", Rules, previous));

        Assert.Equal(5, previous.Drafts.Count);
        Assert.All(previous.Drafts.Where(d => d.Ref != "d2"), d => Assert.True(d.Selected));
        Assert.Equal("Harbour", previous.Drafts[2].Name);
    }

    [Fact]
    public void OutlinePrompt_ForAChange_ListsTheCurrentOutline_AndEndsWithTheChange()
    {
        var change = AnyOutline with { Current = new[] { """{"ref":"d1","kind":"wiki","name":"Harbour","pitch":"Docks.","links":[]}""" }, Instruction = "  darker, and drop the eel  " };

        var (system, user) = AiPrompts.WorldOutline(change);
        var (plainSystem, plainUser) = AiPrompts.WorldOutline(AnyOutline);

        Assert.Contains("keep the ref of a draft you change", system);
        Assert.Contains("- {\"ref\":\"d1\",\"kind\":\"wiki\",\"name\":\"Harbour\"", user);
        Assert.EndsWith("The change the game master asks for: darker, and drop the eel" + Environment.NewLine, user);
        Assert.DoesNotContain("asks for a change", plainSystem);
        Assert.DoesNotContain("current outline", plainUser);
    }

    [Fact]
    public void ExpandPrompts_ForAChange_SendTheTextSoFar_AndEndWithTheChange()
    {
        var page = new WorldExpandRequest("The bell", "It rings.", null, "SRD", Array.Empty<FieldSpec>(), Array.Empty<WorldAnswer>(),
            Array.Empty<string>(), Array.Empty<string>(), Kind: DraftKinds.Fact, Instruction: "make it about the harbour master", CurrentText: "The bell is cursed.");

        var (secret, secretUser) = AiPrompts.WorldExpandPage(page);
        var (_, unwritten) = AiPrompts.WorldExpandPage(page with { CurrentText = null });
        var (homebrew, homebrewUser) = AiPrompts.WorldExpandHomebrew(page with { Kind = DraftKinds.Homebrew, TypeLabel = "Magic Item", CurrentText = """{"description":"A lens."}""" });
        var (plain, _) = AiPrompts.WorldExpandPage(page with { Instruction = "   " });

        Assert.Contains("Current text:" + Environment.NewLine + "The bell is cursed.", secretUser);
        Assert.EndsWith("The change the game master asks for: make it about the harbour master" + Environment.NewLine, secretUser);
        // A secret is saved as plain sentences, whatever the change asks, so that rule still comes last.
        Assert.EndsWith("plain sentences only.", secret);
        Assert.Contains("asks for a change", secret);
        Assert.Contains("(none yet - write it with the change)", unwritten);
        Assert.Contains("every field included", homebrew);
        Assert.EndsWith("The change the game master asks for: make it about the harbour master" + Environment.NewLine, homebrewUser);
        Assert.DoesNotContain("asks for a change", plain);
    }

    [Fact]
    public async Task Changes_WithoutAnInstruction_AreRefused()
    {
        var saving = Service();
        var bundle = Previous();

        var draft = await Assert.ThrowsAsync<LocalizedException>(() => saving.Service.ChangeAsync(Campaign, Dm, WorldRecipes.Find("homebrew")!, new Dictionary<string, string?>(), bundle, bundle.Drafts[0], " "));
        var outline = await Assert.ThrowsAsync<LocalizedException>(() => saving.Service.ReviseAsync(Campaign, Dm, WorldRecipes.Find("homebrew")!, new Dictionary<string, string?>(), bundle, ""));

        Assert.Equal("WorldBuilder.Error.NoChange", draft.Key);
        Assert.Equal("WorldBuilder.Error.NoChange", outline.Key);
    }

    // ---- Stats from a Library creature ----

    private static readonly ContentTypeDefinition MonsterType = new() { Type = "monster", ActorBlueprint = true };

    /// <summary>A registry holding one pack creature, the Veteran (58 hit points), as a stat block the cast can start from.</summary>
    private static ContentRegistry VeteranPack()
    {
        var registry = new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
        var veteran = new ContentEntry { Id = "veteran", Type = "monster", SystemId = "srd5e", PackId = "srd", Name = "Veteran", Properties = { ["hitPoints"] = 58L, ["challenge"] = "3" } };
        registry.RegisterEntry(veteran, 0);
        registry.RegisterActor(YamlContentPackLoader.ToActorBlueprint(veteran, MonsterType), 0);
        registry.RegisterEntry(new ContentEntry { Id = "veteran-captain", Type = "monster", SystemId = "srd5e", PackId = "srd", Name = "Veteran Captain" }, 0);
        return registry;
    }

    [Fact]
    public void ParseOutline_ReadsTheStatsAPersonPlaysLike_OnlyOnPeople()
    {
        const string reply = """
            {"drafts": [
              {"ref": "d1", "kind": "npc", "name": "Mother Ilse", "pitch": "A retired pilot.", "stats": " Veteran "},
              {"ref": "d2", "kind": "npc", "name": "Brann", "pitch": "A lookout.", "stats": ""},
              {"ref": "d3", "kind": "wiki", "name": "The Wardens", "pitch": "Smugglers.", "stats": "Bandit"}
            ]}
            """;

        var bundle = WorldBundleParser.ParseOutline(reply, new BundleRules(WorldRecipes.Find("faction")!.Drafts, new Dictionary<string, string>(), Array.Empty<string>()));

        Assert.Equal(new string?[] { "Veteran", null, null }, bundle.Drafts.Select(d => d.SuggestedStats));
    }

    [Fact]
    public void ParseRevision_KeepsTheStatsTheDmPicked_EvenABlankSheet_AndOtherwiseFollowsTheReply()
    {
        var veteran = new CreatureRef("monster", "veteran", false, "Veteran", "Veteran");
        var previous = new WorldBundle();
        previous.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Npc, Name = "Ilse", OutlineName = "Ilse", Stats = veteran, StatsChosen = true });
        previous.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Npc, Name = "Brann", OutlineName = "Brann", StatsChosen = true });
        previous.Drafts.Add(new WorldDraft { Ref = "d3", Kind = DraftKinds.Npc, Name = "Oda", OutlineName = "Oda", Stats = veteran });
        previous.Drafts.Add(new WorldDraft { Ref = "d4", Kind = DraftKinds.Npc, Name = "Pell", OutlineName = "Pell", Stats = veteran });
        previous.Drafts.Add(new WorldDraft { Ref = "d5", Kind = DraftKinds.Npc, Name = "Tam", OutlineName = "Tam", Stats = veteran, StatsChosen = true });
        const string reply = """
            {"drafts": [
              {"ref": "d1", "kind": "npc", "name": "Ilse", "pitch": "A priest now.", "stats": "Priest"},
              {"ref": "d2", "kind": "npc", "name": "Brann", "pitch": "A thug.", "stats": "Thug"},
              {"ref": "d3", "kind": "npc", "name": "Oda", "pitch": "A priest.", "stats": "Priest"},
              {"ref": "d4", "kind": "npc", "name": "Pell", "pitch": "Unchanged.", "stats": "veteran"}
            ]}
            """;

        var revised = WorldBundleParser.ParseRevision(reply, new BundleRules(new[] { DraftKinds.Npc }, new Dictionary<string, string>(), Array.Empty<string>()), previous);

        var (ilse, brann, oda, pell) = (previous.Drafts[0], previous.Drafts[1], previous.Drafts[2], previous.Drafts[3]);
        // Picked stats alone are not work worth keeping a removed person for.
        Assert.DoesNotContain(revised.Drafts, d => d.Name == "Tam");
        Assert.Empty(revised.SetAside);
        Assert.Equal((veteran, (string?)null), (ilse.Stats, ilse.SuggestedStats));
        Assert.Equal(((CreatureRef?)null, (string?)null), (brann.Stats, brann.SuggestedStats)); // the DM chose a blank sheet
        Assert.Equal(((CreatureRef?)null, "Priest"), (oda.Stats, oda.SuggestedStats)); // left to the model: looked up again
        Assert.Same(veteran, pell.Stats); // the same creature: nothing to look up
    }

    [Fact]
    public void OutlinePrompt_AsksForStats_OnlyForPeople_InASystemWithCreatures()
    {
        var people = AnyOutline with { Kinds = new[] { "npc: a person" }, CreatureStats = true };

        var (system, _) = AiPrompts.WorldOutline(people);
        var (noCreatures, _) = AiPrompts.WorldOutline(people with { CreatureStats = false });
        var (noPeople, _) = AiPrompts.WorldOutline(people with { Kinds = new[] { "wiki: a page" } });

        Assert.Contains("\"stats\": the English name of a generic creature from the SRD rules", system);
        Assert.DoesNotContain("\"stats\"", noCreatures);
        Assert.DoesNotContain("\"stats\"", noPeople);
    }

    [Fact]
    public async Task FindStats_TakesOnlyAnExactName_FromThePacksOrTheHomebrew()
    {
        var gullHag = new HomebrewEntryDocument { Type = "monster", EntryId = "gull-hag", Name = "Gull Hag", SystemId = "srd5e" };
        var saving = Service(null, Array.Empty<RegionDocument>(), VeteranPack(), gullHag);
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Npc, Name = "Ilse", SuggestedStats = "veteran" });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Npc, Name = "Brann", SuggestedStats = "Vet" });
        bundle.Drafts.Add(new WorldDraft { Ref = "d3", Kind = DraftKinds.Npc, Name = "Oda", SuggestedStats = "Gull Hag" });
        bundle.Drafts.Add(new WorldDraft { Ref = "d4", Kind = DraftKinds.Npc, Name = "Pell", SuggestedStats = "Veteran", StatsChosen = true });

        await saving.Service.FindStatsAsync(Campaign, Dm, bundle);

        Assert.Equal(new CreatureRef("monster", "veteran", false, "Veteran", "Veteran"), bundle.Drafts[0].Stats);
        Assert.Null(bundle.Drafts[1].Stats); // "Vet" is not a name the Library has; the nearest match would be a guess
        Assert.Equal(new CreatureRef("monster", "gull-hag", true, "Gull Hag", "Gull Hag"), bundle.Drafts[2].Stats);
        Assert.Null(bundle.Drafts[3].Stats); // the DM chose a blank sheet
    }

    [Fact]
    public async Task Save_StartsACastMemberFromTheirCreature_KeepingTheirOwnName()
    {
        var gullHag = new HomebrewEntryDocument { Type = "monster", EntryId = "gull-hag", Name = "Gull Hag", SystemId = "srd5e", Properties = { ["hitPoints"] = 27L } };
        var saving = Service(null, Array.Empty<RegionDocument>(), VeteranPack(), gullHag);
        var bundle = Faction();
        bundle.Drafts[1].Stats = new CreatureRef("monster", "veteran", false, "Veteran", "Veteran");
        bundle.Drafts[2].Cast = true;
        bundle.Drafts[2].Stats = new CreatureRef("monster", "gull-hag", true, "Gull Hag", "Gull Hag");

        await saving.Service.SaveAsync(Campaign, Dm, WorldRecipes.Find("faction")!, bundle);

        var (ilse, brann) = (saving.Actors[0], saving.Actors[1]);
        Assert.Equal(("Mother Ilse", ActorKind.NonPlayerCharacter, ActorVisibility.Hidden, "srd5e:veteran"), (ilse.Name, ilse.Kind, ilse.Visibility, ilse.BlueprintId));
        Assert.Equal((58L, 58L, "3"), (ilse.Sheet["HP"], ilse.Sheet["HP_Max"], ilse.Sheet["challenge"]));
        Assert.EndsWith("She leads The Salt Wardens.", ilse.DmNotes);
        // A homebrew creature has no pack id to point back to, but its stats still fill the sheet.
        Assert.Equal(("Brann", (string?)null, 27L), (brann.Name, brann.BlueprintId, brann.Sheet["HP"]));
    }

    [Fact]
    public async Task Save_OfACastMemberWhoseCreatureIsGone_StartsBlank()
    {
        var saving = Service(null, Array.Empty<RegionDocument>(), VeteranPack());
        var bundle = Faction();
        // Recorded as homebrew, which the campaign no longer has; the pack's creature of that id is not the one picked.
        bundle.Drafts[1].Stats = new CreatureRef("monster", "veteran", true, "Veteran", "Veteran");

        await saving.Service.SaveAsync(Campaign, Dm, WorldRecipes.Find("faction")!, bundle);

        var ilse = Assert.Single(saving.Actors);
        Assert.Null(ilse.BlueprintId);
        Assert.False(ilse.Sheet.ContainsKey("challenge"));
    }
}
