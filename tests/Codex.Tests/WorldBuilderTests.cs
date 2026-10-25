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

    private sealed record Saving(WorldBuilderService Service, List<WikiPageDocument> Pages, List<HomebrewEntryDocument> Entries, List<ActorDocument> Actors, List<FactDocument> Facts);

    private static Saving Service(IEnumerable<WikiPageDocument>? existingPages = null, params HomebrewEntryDocument[] existing)
    {
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
        homebrew.TrySaveAsync(Arg.Do<HomebrewEntryDocument>(entries.Add), Arg.Any<CampaignAccess>()).Returns(true);
        var factRepo = Substitute.For<IFactRepository>();
        factRepo.SaveAsync(Arg.Do<FactDocument>(facts.Add), Arg.Any<CampaignAccess>()).Returns(true);
        var actorRepo = Substitute.For<IActorRepository>();
        actorRepo.TrySaveAsync(Arg.Do<ActorDocument>(actors.Add), Arg.Any<CampaignAccess>()).Returns(true);
        var catalog = Substitute.For<ISystemCatalog>();
        catalog.GetUISchemas(Arg.Any<string>()).Returns(Enumerable.Empty<UISchema>());
        var library = new CampaignLibrary(new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance)), homebrew, catalog);
        var providers = new AiProviderService(new EphemeralDataProtectionProvider(), Substitute.For<IUserRepository>(), new AIConfiguration(), new AIImageConfiguration(),
            Substitute.For<IHttpClientFactory>(), NullLogger<AiProviderService>.Instance);
        var components = new ComponentRegistry();
        var runtimes = new Codex.Persistence.Runtime.CampaignRuntimeManager(actorRepo, Substitute.For<ISessionRepository>(), Substitute.For<ICampaignRepository>(),
            Substitute.For<IEncounterRepository>(), components, new PluginLoader(NullLogger<PluginLoader>.Instance, components, Substitute.For<IContentPackLoader>()), NullLoggerFactory.Instance);
        var cast = new CastService(runtimes, Substitute.For<IContentRegistry>(), components, catalog);
        return new Saving(new WorldBuilderService(providers, catalog, library, homebrew, wiki, factRepo, cast), pages, entries, actors, facts);
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
        Assert.Equal("She leads The Salt Wardens.", ilse.Biography); // sheet prose has no wiki links
        Assert.Contains("Mother Ilse", ilse.DmNotes);

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
}
