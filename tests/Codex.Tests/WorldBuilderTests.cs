using System.Text.Json;
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

/// <summary>The world builder: reading a proposal, link rewriting, what saving stores, and the chat's tool loop.</summary>
public class WorldBuilderTests
{
    // ---- Reading a proposal ----

    private static readonly FieldSpec[] SpellFields =
    {
        new("level", "Level", "Number", null, 0, 9),
        new("school", "School", "Dropdown", new[] { "Evocation (Воплощение)", "Illusion (Иллюзия)" }, null, null),
        new("components", "Components", "TagList", null, null, null),
        new("damageType", "Damage Type", "Text", null, null, null),
        new("concentration", "Concentration", "Boolean", null, null, null),
        new("actions", "Actions", "Collection", null, null, null)
    };

    private static ProposalRules Rules(Func<string, string, string>? canonical = null, IReadOnlyCollection<string>? actors = null) => new(
        new Dictionary<string, string> { ["magic-item"] = "magic-item", ["Magic Item"] = "magic-item", ["spell"] = "spell", ["monster"] = "monster" },
        new Dictionary<string, string> { ["port-varr"] = "Port Varr", ["sister-maren"] = "Sister Maren", ["sister-maren-2"] = "Sister Maren" },
        new[] { "old-town" },
        type => type == "spell" ? SpellFields : Array.Empty<FieldSpec>(),
        canonical,
        actors);

    [Fact]
    public void Place_WithoutAPitch_GetsOneWhoseRefsResolveToTheNamesTheEntriesWereSavedUnder()
    {
        // The model gives refs ([[p1]], [[n5]]) in the text and no pitch of its own, so the pitch is derived from the text.
        // Derived by flattening links to words, it kept "p1" and "n5" and the Places list showed them verbatim.
        const string json = """
            {"entries": [
              {"ref": "p1", "kind": "place", "name": "Cinderhaven", "placeKind": "settlement", "text": "The city."},
              {"ref": "n5", "kind": "npc", "name": "Brell", "text": "A grain factor."},
              {"ref": "p3", "kind": "place", "name": "Amphail", "placeKind": "settlement", "parent": "p1",
               "text": "Look: walled farm town east of [[p1]]. Who is here: horse breeders, farmers, [[p1]] grain buyers, [[n5]]."}
            ]}
            """;
        var check = WorldBundleParser.ParseProposal(json, Rules());
        Assert.True(check.Ok, string.Join("; ", check.Errors));
        var amphail = check.Bundle.Drafts.Single(d => d.Ref == "p3");

        var shown = WorldBundleParser.ResolveLinks(amphail.Pitch, check.Bundle, keepLinks: false);

        Assert.Equal("Look: walled farm town east of Cinderhaven. Who is here: horse breeders, farmers, Cinderhaven grain buyers, Brell.", shown);
    }

    [Fact]
    public void ParseProposal_ReadsEachKind_WithItsOwnFields()
    {
        const string json = """
            {"summary": "A harbour town.", "region": "The Coast", "entries": [
              {"ref": "t", "kind": "place", "name": "Varr", "placeKind": "settlement", "text": "## Sights\nStilts over [[existing:port-varr]]."},
              {"ref": "m", "kind": "place", "name": "Salt Market", "placeKind": "location", "parent": "t", "text": "Stalls."},
              {"ref": "o", "kind": "place", "name": "Old Gate", "parent": "existing:place:old-town", "text": "A gate."},
              {"ref": "i", "kind": "npc", "name": "Mother Ilse", "cast": true, "stats": "Veteran", "text": "She runs [[m]]."},
              {"ref": "w", "kind": "wiki", "name": "The Salt Wardens", "pageKind": "faction", "text": "Smugglers."},
              {"ref": "s", "kind": "fact", "name": "The wardens pay the harbour master", "links": ["w", "i", "existing:port-varr", "existing:nowhere"]}
            ]}
            """;

        var check = WorldBundleParser.ParseProposal(json, Rules());

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        var b = check.Bundle;
        Assert.Equal(("A harbour town.", "The Coast"), (b.Summary, b.Region));
        Assert.Equal(new[] { "place", "place", "place", "npc", "wiki", "fact" }, b.Drafts.Select(d => d.Kind));
        Assert.Equal(("settlement", (string?)null, true), (b.Drafts[0].PlaceKind, b.Drafts[0].Parent, b.Drafts[0].OnMap));
        Assert.Equal("## Sights\nStilts over [[Port Varr]].", b.Drafts[0].Body); // an existing page is linked by its title
        Assert.Equal(("site", "t"), (b.Drafts[1].PlaceKind, b.Drafts[1].Parent)); // "location" is a site
        Assert.Equal("existing:place:old-town", b.Drafts[2].Parent);
        Assert.Equal((true, "Veteran"), (b.Drafts[3].Cast, b.Drafts[3].SuggestedStats));
        Assert.Equal(new[] { "m" }, b.Drafts[3].Links); // [[m]] in the text links the market
        Assert.Equal("faction", b.Drafts[4].PageKind);
        Assert.Equal(new[] { "w", "i", "existing:port-varr" }, b.Drafts[5].Links); // the unknown page is dropped
        Assert.All(b.Drafts, d => Assert.True(d.Selected));
    }

    [Fact]
    public void ParseProposal_NamesEveryEntryThatCannotBeSaved()
    {
        const string json = """
            {"entries": [
              {"ref": "a", "kind": "place", "name": "A", "parent": "b"},
              {"ref": "b", "kind": "place", "name": "B", "parent": "a"},
              {"ref": "c", "kind": "place", "name": "C", "parent": "existing:place:gone"},
              {"ref": "d", "kind": "place", "name": "D", "parent": "n"},
              {"ref": "n", "kind": "npc", "name": "N"},
              {"kind": "dragon", "name": "Smaug-ish"},
              {"kind": "homebrew", "type": "vehicle", "name": "Barge"},
              {"kind": "wiki", "name": ""}
            ]}
            """;

        var check = WorldBundleParser.ParseProposal(json, Rules());

        Assert.False(check.Ok);
        Assert.Contains(check.Errors, e => e.StartsWith("a (place 'A'): its parents go round in a loop"));
        Assert.Contains(check.Errors, e => e.Contains("'C'") && e.Contains("existing:place:gone"));
        Assert.Contains(check.Errors, e => e.Contains("'D'") && e.Contains("must be another place entry's ref"));
        Assert.Contains(check.Errors, e => e.StartsWith("entries[5] (dragon 'Smaug-ish'): kind must be one of"));
        Assert.Contains(check.Errors, e => e.StartsWith("entries[6] (homebrew 'Barge'): type must be one of"));
        Assert.Contains(check.Errors, e => e.StartsWith("entries[7] (wiki ''): has no name"));
    }

    [Fact]
    public void ParseProposal_WithoutEntries_OrNotJson_IsAnError()
    {
        Assert.False(WorldBundleParser.ParseProposal("""{"summary": "x"}""", Rules()).Ok);
        Assert.Equal("The proposal is not a JSON object.", Assert.Single(WorldBundleParser.ParseProposal("nope", Rules()).Errors));
    }

    [Fact]
    public void ParseProposal_RenumbersATakenRef_AndWarnsAboutGuesses()
    {
        const string json = """
            {"entries": [
              {"ref": "p", "kind": "wiki", "name": "One", "text": "x"},
              {"ref": "p", "kind": "wiki", "name": "Two", "text": "See [[existing:nowhere|the old page]]."},
              {"ref": "q", "kind": "place", "name": "Three", "placeKind": "castle", "text": "x"},
              {"ref": "r", "kind": "npc", "name": "Four", "parent": "q"}
            ]}
            """;

        var check = WorldBundleParser.ParseProposal(json, Rules());

        Assert.True(check.Ok);
        Assert.Equal(new[] { "p", "e2", "q", "r" }, check.Bundle.Drafts.Select(d => d.Ref));
        Assert.Equal("See the old page.", check.Bundle.Drafts[1].Body);
        Assert.Null(check.Bundle.Drafts[2].PlaceKind);
        Assert.Null(check.Bundle.Drafts[3].Parent);
        Assert.Contains(check.Warnings, w => w.Contains("'p' was taken"));
        Assert.Contains(check.Warnings, w => w.Contains("castle"));
        Assert.Contains(check.Warnings, w => w.Contains("only a place has a parent"));
        Assert.Contains(check.Warnings, w => w.Contains("'Four'") && w.Contains("no text"));
    }

    [Fact]
    public void ParseProposal_FillsHomebrewFields_AndStoresTranslatedValuesAsTheRulesCompareThem()
    {
        const string json = """
            {"entries": [{"ref": "h", "kind": "homebrew", "type": "spell", "name": "Огненная дуга", "text": "Дуга пламени бьёт по цели.", "tags": ["fire", "Fire", "arc"],
             "properties": {"level": 12, "school": "Воплощение", "components": "V, S", "damageType": "огонь", "concentration": "false",
                            "actions": [{"name": "Arc", "description": "2d6 fire", "uses": 2}, {"description": "no name"}], "price": 50}},
             {"kind": "homebrew", "type": "Magic Item", "name": "Tide-glass", "bestiary": true, "properties": {"school": "Chronomancy"}}]}
            """;

        var check = WorldBundleParser.ParseProposal(json, Rules((_, value) => value == "огонь" ? "fire" : value));

        var spell = check.Bundle.Drafts[0];
        Assert.Equal(("spell", "Дуга пламени бьёт по цели."), (spell.Type, spell.Body));
        Assert.Equal(new[] { "fire", "arc" }, spell.Tags);
        Assert.Equal(9L, spell.Properties["level"]); // clamped
        Assert.Equal("Evocation", spell.Properties["school"]); // matched through the option's translated label
        Assert.Equal(new object[] { "V", "S" }, (List<object>)spell.Properties["components"]);
        Assert.Equal("fire", spell.Properties["damageType"]);
        Assert.Equal(false, spell.Properties["concentration"]);
        var row = Assert.Single((List<object>)spell.Properties["actions"]);
        Assert.Equal(new Dictionary<string, object> { ["name"] = "Arc", ["description"] = "2d6 fire", ["uses"] = 2L }, (Dictionary<string, object>)row);
        Assert.False(spell.Properties.ContainsKey("price")); // not a field of the type
        var item = check.Bundle.Drafts[1];
        Assert.Equal(("magic-item", true), (item.Type, item.Bestiary));
        Assert.Empty(item.Properties); // magic items list no fields here
    }

    [Fact]
    public void ParseProposal_KeepsAtMostThirtyEntries_AndCutsLongNames()
    {
        var entries = Enumerable.Range(1, 33).Select(i => $$"""{"kind": "wiki", "name": "Page {{i}}{{(i == 1 ? new string('x', 500) : "")}}", "text": "x"}""");
        var check = WorldBundleParser.ParseProposal($$"""{"entries": [{{string.Join(',', entries)}}]}""", Rules());

        Assert.Equal(WorldBundleParser.MaxDrafts, check.Bundle.Drafts.Count);
        Assert.Equal(3, check.Errors.Count(e => e.Contains("at most 30")));
        Assert.Equal(200, check.Bundle.Drafts[0].Name.Length);
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
    public void ResolveLinks_PrefersTheRef_ThenTheProposedName_ThenTheRename()
    {
        var bundle = Linked();
        bundle.Drafts.Add(new WorldDraft { Ref = "d4", Kind = DraftKinds.Wiki, Name = "Old Varr", OutlineName = "Old Varr" });
        bundle.Drafts[1].Name = "Old Varr"; // the DM renamed the glassworks to the other page's proposed name

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
    public void ResolveLinks_LinksToPeople_ButNotToSecrets()
    {
        Assert.Equal("[[Mother Ilse]] knows that the wardens pay the harbour master.",
            WorldBundleParser.ResolveLinks("[[d2]] knows that [[d4|the wardens pay the harbour master]].", Faction()));
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

    // ---- The chat prompt ----

    [Fact]
    public void ChatPrompt_TeachesTheProposalShape_AndKeepsKeysInEnglishInAnotherLanguage()
    {
        var english = AiPrompts.WorldChat(new WorldChatRequest("SRD", "Salt and Ash: a port.", new[] { "monster (Monster, a monster)" }, Creature: "monster"));
        var hebrew = AiPrompts.WorldChat(new WorldChatRequest("SRD", null, Array.Empty<string>(), Language: "he"));

        Assert.Contains("propose_changes", english);
        Assert.Contains("existing:place:<id>", english);
        Assert.Contains("never say something was added or saved", english);
        Assert.Contains("what they want and why; what they fear", english);
        Assert.Contains("full, flowing sentences, never telegraphic notes", english); // a game master reads and runs these pages
        Assert.Contains("\"pitch\"", english);
        Assert.Contains("generic monster from the SRD rules", english);
        Assert.Contains("Homebrew types: monster (Monster, a monster).", english);
        Assert.Contains("The campaign: Salt and Ash: a port.", english);
        Assert.Contains("in Hebrew; keep every JSON key, kind, ref, id and type exactly as given", hebrew);
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
        EntityTypes = new List<ContentTypeDefinition>
        {
            new() { Type = "monster", ActorBlueprint = true },
            new()
            {
                Type = "spell",
                Fields = { new FieldDefinition("level", "Level", FieldType.Number), new FieldDefinition("school", "School", FieldType.Text), new FieldDefinition("damageType", "Damage Type", FieldType.Text) }
            }
        }
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
        // The fakes return what was saved, as the repositories do, so a retried save can find what its first attempt stored.
        wiki.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => (existingPages ?? Array.Empty<WikiPageDocument>()).Concat(pages).ToList().AsEnumerable());
        wiki.TrySaveAsync(Arg.Any<WikiPageDocument>(), Arg.Any<CampaignAccess>()).Returns(ci =>
        {
            var page = ci.Arg<WikiPageDocument>();
            // As the repository does: a page that already has an id is updated, not stored again.
            var stored = string.IsNullOrEmpty(page.Id) ? -1 : pages.FindIndex(p => p.Id == page.Id);
            if (stored >= 0)
            {
                pages[stored] = page;
                return ci.Arg<CampaignAccess>().IsDm;
            }

            page.Id = $"page-{pages.Count + 1}";
            page.Slug = WikiText.Slugify(page.Title);
            pages.Add(page);
            return ci.Arg<CampaignAccess>().IsDm;
        });
        var homebrew = Substitute.For<IHomebrewRepository>();
        homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => existing.Concat(entries).ToList().AsEnumerable());
        var factRepo = Substitute.For<IFactRepository>();
        factRepo.SaveAsync(Arg.Do<FactDocument>(facts.Add), Arg.Any<CampaignAccess>()).Returns(true);
        factRepo.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>(), Arg.Any<IReadOnlySet<string>>()).Returns(_ => facts.ToList().AsEnumerable());
        var actorRepo = Substitute.For<IActorRepository>();
        actorRepo.TrySaveAsync(Arg.Do<ActorDocument>(actors.Add), Arg.Any<CampaignAccess>()).Returns(true);
        actorRepo.GetVisibleAsync(Arg.Any<string>(), Arg.Any<CampaignAccess>()).Returns(ci => actors.FirstOrDefault(a => a.Id == ci.ArgAt<string>(0)));
        actorRepo.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => actors.ToList().AsEnumerable());
        var catalog = Substitute.For<ISystemCatalog>();
        catalog.GetUISchemas(Arg.Any<string>()).Returns(Enumerable.Empty<UISchema>());
        catalog.GetDefinition("srd5e").Returns(CreatureSystem);
        catalog.GetDisplayName(Arg.Any<string>()).Returns("SRD");
        var campaigns = Substitute.For<ICampaignRepository>();
        var library = new CampaignLibrary(registry, homebrew, catalog);
        var providers = new AiProviderService(new EphemeralDataProtectionProvider(), Substitute.For<IUserRepository>(), new AIConfiguration(), new AIImageConfiguration(),
            Substitute.For<IHttpClientFactory>(), NullLogger<AiProviderService>.Instance, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var components = new ComponentRegistry();
        var runtimes = new Codex.Persistence.Runtime.CampaignRuntimeManager(actorRepo, Substitute.For<ISessionRepository>(), Substitute.For<ICampaignRepository>(),
            Substitute.For<IEncounterRepository>(), components, new PluginLoader(NullLogger<PluginLoader>.Instance, components, Substitute.For<IContentPackLoader>()), NullLoggerFactory.Instance);
        var cast = new CastService(runtimes, registry, components, catalog, actorRepo, wiki);
        var regions = Substitute.For<IRegionRepository>();
        // No model is set up: picking a cast member's gear fails quietly and they are saved without it.
        var ai = new AiFeatures(providers, registry, catalog);
        var saving = new Saving(new WorldBuilderService(catalog, library, homebrew, wiki, factRepo, cast, regions, campaigns, actorRepo, ai), pages, entries, actors, facts);
        regions.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => existingRegions.Concat(saving.NewRegions).ToList().AsEnumerable());
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

        var result = await service.SaveAsync(Campaign, Dm, bundle);

        var page = Assert.Single(pages); // the unticked rumour is not saved
        Assert.Equal("The Varr Glassworks", page.Title);
        Assert.Equal((WikiVisibility.DmOnly, WikiPageKind.General), (page.Visibility, page.Kind));
        Assert.Equal("They made Tide-glass. Ask at [[Port Varr]].", page.Body);
        Assert.Empty(page.Tags); // plain lore needs no tag

        var entry = Assert.Single(entries);
        Assert.False(entry.IsPublished);
        Assert.Equal(("magic-item", "tide-glass-2", "srd5e"), (entry.Type, entry.EntryId, entry.SystemId)); // the slug was taken
        Assert.Equal("Made by The Varr Glassworks.", entry.Description);
        Assert.Equal("Rare", entry.Properties["rarity"]);
        Assert.Equal(new[] { "glass" }, entry.Tags);
        Assert.Equal((1, 1), (result.Entries.Count, result.Pages.Count));
    }

    [Fact]
    public async Task Save_UsesThePitch_ForADraftWithoutText()
    {
        var (service, _, entries, _, _) = Service();
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Homebrew, Type = "spell", Name = "Ember Step", Pitch = "Step through a fire." });

        await service.SaveAsync(Campaign, Dm, bundle);

        Assert.Equal("Step through a fire.", Assert.Single(entries).Description);
    }

    [Fact]
    public async Task Save_IsForTheDmOnly()
    {
        var (service, pages, entries, _, _) = Service();

        var ex = await Assert.ThrowsAsync<LocalizedException>(() => service.SaveAsync(Campaign, new CampaignAccess("c1", "p", CampaignRole.Player), Linked()));

        Assert.Equal("WorldBuilder.Error.OnlyDm", ex.Key);
        Assert.Empty(pages);
        Assert.Empty(entries);
    }

    // ---- People, the cast and secrets ----

    private static WorldBundle Faction()
    {
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Wiki, PageKind = "faction", Name = "The Salt Wardens", OutlineName = "The Salt Wardens", Body = "Led by [[Mother Ilse]].", Links = { "d2", "d3" } });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Npc, Name = "Mother Ilse", OutlineName = "Mother Ilse", Body = "She leads [[The Salt Wardens]].", Cast = true, Links = { "d1" } });
        bundle.Drafts.Add(new WorldDraft { Ref = "d3", Kind = DraftKinds.Npc, Name = "Brann", OutlineName = "Brann", Pitch = "Her nephew, a lookout." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d4", Kind = DraftKinds.Fact, Name = "The wardens pay the harbour master", OutlineName = "The wardens pay the harbour master",
            Body = "[[Mother Ilse]] pays him in salt.", Links = { "d1", "d2", "existing:port-varr" } });
        return bundle;
    }

    [Fact]
    public async Task Save_WritesPeoplesPages_TicksThemIntoTheCast_AndFilesTheSecretUnderWhatItConcerns()
    {
        var (service, pages, _, actors, facts) = Service(new[] { new WikiPageDocument { Id = "page-port", Slug = "port-varr", Title = "Port Varr" } });

        var result = await service.SaveAsync(Campaign, Dm, Faction());

        Assert.Equal(new[] { "The Salt Wardens", "Mother Ilse", "Brann" }, pages.Select(p => p.Title));
        Assert.All(pages, p => Assert.Equal(WikiVisibility.DmOnly, p.Visibility));
        Assert.Equal(new[] { "faction" }, pages[0].Tags);
        Assert.Equal(new[] { "person" }, pages[1].Tags);
        Assert.Equal("Led by [[Mother Ilse]].", pages[0].Body);
        Assert.Equal("Her nephew, a lookout.", pages[2].Body); // no text: the pitch

        var ilse = Assert.Single(actors); // only the person ticked for the cast
        Assert.Equal(("Mother Ilse", ActorKind.NonPlayerCharacter, ActorVisibility.Hidden), (ilse.Name, ilse.Kind, ilse.Visibility));
        // Her page becomes her history, links flattened, and the page and the character point at each other.
        Assert.EndsWith("She leads The Salt Wardens.", ilse.Biography);
        Assert.Null(ilse.DmNotes);
        Assert.Equal((pages[1].Id, ilse.Id, WikiPageKind.Person), (ilse.WikiPageId, pages[1].ActorId, pages[1].Kind));
        Assert.Equal(WikiPageKind.Faction, pages[0].Kind);

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

        await service.SaveAsync(Campaign, Dm, bundle);

        Assert.DoesNotContain(pages, p => p.Title == "Mother Ilse");
        Assert.Empty(actors);
        Assert.Equal("Led by Mother Ilse.", pages[0].Body); // no page left to link to
        Assert.Equal(new[] { "page-1" }, Assert.Single(facts).RelatedEntityIds);
    }

    // ---- Places, nested ----

    private static WorldBundle Settlement()
    {
        var bundle = new WorldBundle { Region = "The Coast" };
        bundle.Drafts.Add(new WorldDraft { Ref = "t", Kind = DraftKinds.Place, PlaceKind = "settlement", Name = "Varr", OutlineName = "Varr", Pitch = "Stilts over the water.", Body = "The [[Salt Market]] never closes.", OnMap = true });
        bundle.Drafts.Add(new WorldDraft { Ref = "m", Kind = DraftKinds.Place, Name = "Salt Market", OutlineName = "Salt Market", Parent = "t", Pitch = "Stalls by [[Varr]]'s quay.", Body = "## Sights\nStalls in [[Varr]].", OnMap = true });
        bundle.Drafts.Add(new WorldDraft { Ref = "b", Kind = DraftKinds.Place, PlaceKind = "site", Name = "The Drowned Bell", OutlineName = "The Drowned Bell", Parent = "m", Pitch = "A flooded inn.", OnMap = true });
        bundle.Drafts.Add(new WorldDraft { Ref = "s", Kind = DraftKinds.Fact, Name = "The bell rings before every storm", OutlineName = "The bell rings before every storm", Links = { "m", "b" } });
        return bundle;
    }

    [Fact]
    public async Task Save_NestsPlacesInsideTheirParents_InANewRegionTheProposalNames()
    {
        var saving = Service();

        var result = await saving.Service.SaveAsync(Campaign, Dm, Settlement());

        Assert.Equal("The [[Salt Market]] never closes.", saving.Pages[0].Body); // places are pages too, so links stay
        Assert.Equal(new[] { "settlement" }, saving.Pages[0].Tags);
        Assert.Equal(new[] { "place" }, saving.Pages[1].Tags);
        var region = Assert.Single(saving.NewRegions);
        Assert.Equal(("The Coast", "c1"), (region.Name, region.CampaignId));
        var (town, market, inn) = (region.Locations[0], region.Locations[1], region.Locations[2]);
        Assert.Equal(("Varr", PlaceKind.Settlement, (string?)null), (town.Name, town.Kind, town.ParentId));
        // No kind given: one step smaller than its parent.
        Assert.Equal(("Salt Market", PlaceKind.District, town.Id), (market.Name, market.Kind, market.ParentId));
        Assert.Equal((PlaceKind.Site, market.Id), (inn.Kind, inn.ParentId));
        Assert.All(region.Locations, p => Assert.Equal(ActorVisibility.Hidden, p.Visibility));
        // Players read a revealed place's description, so it is the pitch (links flattened), not the page that tells what it hides.
        Assert.Equal("Stalls by Varr's quay.", market.Description);
        Assert.Equal(new[] { "page-2", market.Id, "page-3", inn.Id }, Assert.Single(saving.Facts).RelatedEntityIds);
        Assert.Equal(3, result.Places.Count);
        // The place and the page written for it point at each other.
        var marketPage = saving.Pages.Single(p => p.Title == "Salt Market");
        Assert.Equal((marketPage.Id, market.Id, WikiPageKind.Place), (market.WikiPageId, marketPage.PlaceId, marketPage.Kind));
    }

    [Fact]
    public async Task Save_PutsAPlaceInsideOneTheCampaignHas_InThatPlacesRegion()
    {
        var hills = new RegionDocument { Id = "region-hills", CampaignId = "c1", Name = "Hills", Locations = { new LocationDocument { Id = "old-town", Name = "Old Town", Kind = PlaceKind.Settlement } } };
        var coast = new RegionDocument { Id = "region-coast", CampaignId = "c1", Name = "the coast " };
        var saving = Service(null, new[] { hills, coast });
        var bundle = Settlement();
        bundle.Drafts.Add(new WorldDraft { Ref = "g", Kind = DraftKinds.Place, Name = "Old Gate", OutlineName = "Old Gate", Parent = "existing:place:old-town", OnMap = true });

        await saving.Service.SaveAsync(Campaign, Dm, bundle);

        Assert.Empty(saving.NewRegions); // "The Coast" is found whatever its case and spacing
        Assert.Equal(new[] { "region-coast", "region-coast", "region-coast", "region-hills" }, saving.AddedPlaces.Select(p => p.RegionId).OrderBy(id => id == "region-hills"));
        var gate = saving.AddedPlaces.Single(p => p.Place.Name == "Old Gate").Place;
        Assert.Equal(("old-town", PlaceKind.District), (gate.ParentId, gate.Kind));
    }

    [Fact]
    public async Task Save_WithoutARegion_UsesTheCampaignsFirst_AndAnUntickedParentLeavesThePlaceAtTheTop()
    {
        var saving = Service(null, new[] { new RegionDocument { Id = "region-b", CampaignId = "c1", Name = "Bay" }, new RegionDocument { Id = "region-a", CampaignId = "c1", Name = "Arch" } });
        var bundle = Settlement();
        bundle.Region = null;
        bundle.Drafts[0].Selected = false;

        await saving.Service.SaveAsync(Campaign, Dm, bundle);

        Assert.All(saving.AddedPlaces, p => Assert.Equal("region-a", p.RegionId));
        var market = saving.AddedPlaces.Single(p => p.Place.Name == "Salt Market").Place;
        Assert.Null(market.ParentId);
        Assert.Equal(PlaceKind.Area, market.Kind); // at the top, it is taken for an area
    }

    [Fact]
    public async Task Save_AQuest_StoresItsPagesAndItsHomebrew_TaggedAsAQuest()
    {
        var (service, pages, entries, _, _) = Service();
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Wiki, PageKind = "quest", Name = "The Drowned Bell", OutlineName = "The Drowned Bell", Body = "## Hook\nFight the [[Bell-drowned]] for [[The Clapper]]." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Homebrew, Type = "monster", Name = "Bell-drowned", OutlineName = "Bell-drowned", Body = "Haunts [[The Drowned Bell]]." });
        bundle.Drafts.Add(new WorldDraft { Ref = "d3", Kind = DraftKinds.Homebrew, Type = "magic-item", Name = "The Clapper", OutlineName = "The Clapper", Pitch = "A bronze clapper." });

        var result = await service.SaveAsync(Campaign, Dm, bundle);

        var page = Assert.Single(pages);
        Assert.Equal(new[] { "quest" }, page.Tags);
        Assert.Equal(WikiPageKind.Quest, page.Kind);
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

        var result = await saving.Service.SaveAsync(campaign, Dm, bundle);

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

        var result = await saving.Service.SaveAsync(Campaign, Dm, bundle);

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

        await saving.Service.SaveAsync(Campaign, Dm, bundle);

        // Links go by title, so two pages called "The Bell" would both reach the old one.
        Assert.Equal(new[] { "The Bell (2)", "the bell (3)" }, saving.Pages.Select(p => p.Title));
        Assert.Equal("Ask [[the bell (3)]].", saving.Pages[0].Body);
        Assert.Equal("Rings [[The Bell (2)]].", saving.Pages[1].Body);
        var maren = Assert.Single(saving.Actors);
        Assert.Equal("the bell", maren.Name); // a person keeps their name; only the page needs its own title
        Assert.Equal("Rings The Bell (2).", maren.Biography); // and the history names the page as it is titled
        Assert.Equal(saving.Pages[1].Id, maren.WikiPageId);
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

        await Assert.ThrowsAsync<LocalizedException>(() => saving.Service.SaveAsync(Campaign, Dm, bundle));
        saving.FailHomebrew = false;
        var retried = await saving.Service.SaveAsync(Campaign, Dm, bundle);

        Assert.Single(saving.Pages); // saved by the first attempt only
        Assert.Single(saving.Entries);
        // The secret still knows the page from the first attempt, and the creature from the second.
        Assert.Equal(new[] { "page-1", "entry-1" }, Assert.Single(saving.Facts).RelatedEntityIds);
        // The retry reports the whole save, the page the first attempt stored included, so the DM is told 3 and taken to it.
        Assert.Equal((1, 1, 1, 3), (retried.Pages.Count, retried.Entries.Count, retried.Facts.Count, retried.Count));
        Assert.Equal("The Drowned Bell", retried.Pages[0].Title);
    }

    [Fact]
    public async Task Save_WithoutTickedCreatures_LeavesTheCampaignAlone()
    {
        var saving = Service();
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Homebrew, Type = "monster", Name = "Tide Eel", Pitch = "An eel." });

        var result = await saving.Service.SaveAsync(Campaign, Dm, bundle);

        Assert.Empty(result.Shortlisted);
        Assert.Empty(saving.Stored.Bestiary);
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
    public async Task FindStats_TakesOnlyAnExactName_OrTheTypeAndId_FromThePacksOrTheHomebrew()
    {
        var gullHag = new HomebrewEntryDocument { Type = "monster", EntryId = "gull-hag", Name = "Gull Hag", SystemId = "srd5e" };
        var saving = Service(null, Array.Empty<RegionDocument>(), VeteranPack(), gullHag);
        var bundle = new WorldBundle();
        bundle.Drafts.Add(new WorldDraft { Ref = "d1", Kind = DraftKinds.Npc, Name = "Ilse", SuggestedStats = "veteran" });
        bundle.Drafts.Add(new WorldDraft { Ref = "d2", Kind = DraftKinds.Npc, Name = "Brann", SuggestedStats = "Vet" });
        bundle.Drafts.Add(new WorldDraft { Ref = "d3", Kind = DraftKinds.Npc, Name = "Oda", SuggestedStats = "Gull Hag" });
        bundle.Drafts.Add(new WorldDraft { Ref = "d4", Kind = DraftKinds.Npc, Name = "Pell", SuggestedStats = "monster/veteran-captain" });

        await saving.Service.FindStatsAsync(Campaign, Dm, bundle);

        Assert.Equal(new CreatureRef("monster", "veteran", false, "Veteran", "Veteran"), bundle.Drafts[0].Stats);
        Assert.Null(bundle.Drafts[1].Stats); // "Vet" is not a name the Library has; the nearest match would be a guess
        Assert.Equal(new CreatureRef("monster", "gull-hag", true, "Gull Hag", "Gull Hag"), bundle.Drafts[2].Stats);
        Assert.Equal("veteran-captain", bundle.Drafts[3].Stats?.Id); // as search_library lists it
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

        await saving.Service.SaveAsync(Campaign, Dm, bundle);

        var (ilse, brann) = (saving.Actors[0], saving.Actors[1]);
        Assert.Equal(("Mother Ilse", ActorKind.NonPlayerCharacter, ActorVisibility.Hidden, "srd5e:veteran"), (ilse.Name, ilse.Kind, ilse.Visibility, ilse.BlueprintId));
        Assert.Equal((58L, 58L, "3"), (ilse.Sheet["HP"], ilse.Sheet["HP_Max"], ilse.Sheet["challenge"]));
        Assert.EndsWith("She leads The Salt Wardens.", ilse.Biography);
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

        await saving.Service.SaveAsync(Campaign, Dm, bundle);

        var ilse = Assert.Single(saving.Actors);
        Assert.Null(ilse.BlueprintId);
        Assert.False(ilse.Sheet.ContainsKey("challenge"));
    }

    // ---- The chat ----

    /// <summary>A model that answers each call with the next scripted reply (a tool call or text) and keeps what it was sent.</summary>
    private sealed class ScriptedModel(params Func<IList<ChatMessage>, ChatMessage>[] replies) : IChatClient
    {
        private int next;
        public List<List<ChatMessage>> Calls { get; } = new();

        /// <summary>Finish reasons by the number of the call (0 first) that ends with one; the others end normally.</summary>
        public Dictionary<int, ChatFinishReason> Finish { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sent = messages.ToList();
            Calls.Add(sent);
            var call = next++;
            return Task.FromResult(new ChatResponse(replies[call](sent)) { FinishReason = Finish.TryGetValue(call, out var reason) ? reason : null });
        }

        // The chat streams its turns; the scripted reply comes as the updates a real model would send.
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatMessage Calls(string id, string tool, string argumentsJson) =>
        new(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(id, tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson))
        });

    private static string ToolResult(IList<ChatMessage> sent) =>
        sent.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last().Result?.ToString() ?? string.Empty;

    [Fact]
    public async Task Chat_FeedsProposalErrorsBack_ThenHoldsTheFixedProposal_UntilTheDmApplies()
    {
        var saving = Service();
        const string broken = """{"proposal": {"entries": [{"ref": "m", "kind": "place", "name": "Salt Market", "parent": "nowhere", "text": "Stalls."}]}}""";
        const string fixedUp = """{"proposal": {"region": "The Coast", "entries": [{"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "A town."}, {"ref": "m", "kind": "place", "name": "Salt Market", "parent": "t", "text": "Stalls."}]}}""";
        var model = new ScriptedModel(
            _ => Calls("c1", "list_places", "{}"),
            _ => Calls("c2", "propose_changes", broken),
            sent => ToolResult(sent).Contains("Nothing was proposed") ? Calls("c3", "propose_changes", fixedUp) : new ChatMessage(ChatRole.Assistant, "gave up"),
            _ => new ChatMessage(ChatRole.Assistant, "Varr and its market are ready to review."));
        var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);
        var progress = new List<WorldChatStep>();

        await chat.SendAsync("A harbour town, please.", progress.Add, CancellationToken.None);

        // The first proposal's error went back to the model, which fixed it; nothing was saved by proposing.
        Assert.Contains("parent 'nowhere' must be another place entry's ref", ToolResult(model.Calls[2]));
        Assert.NotNull(chat.Proposal);
        Assert.Equal(new[] { "Varr", "Salt Market" }, chat.Proposal!.Drafts.Select(d => d.Name));
        Assert.Empty(saving.Pages);
        Assert.Equal(new[] { WorldChatSpeaker.Dm, WorldChatSpeaker.Model }, chat.Lines.Select(l => l.Speaker));
        Assert.Equal("Varr and its market are ready to review.", chat.Lines[^1].Text);
        // list_places and two proposals, each heard when it starts and again when it ends; the first proposal was refused.
        var steps = progress.Distinct().ToList();
        Assert.Equal(6, progress.Count);
        Assert.Equal(new[] { "list_places", "propose_changes", "propose_changes" }, steps.Select(s => s.Tool));
        Assert.All(steps, s => Assert.Equal(WorldChatStepStatus.Done, s.Status));
        Assert.Equal(new[] { false, true, false }, steps.Select(s => s.Rejected));
        Assert.Contains("\"parent\": \"nowhere\"", steps[1].Arguments);
        Assert.StartsWith("Nothing was proposed", steps[1].Result);
        Assert.Equal(steps, chat.Lines[^1].Steps);
        Assert.StartsWith("You help the game master", model.Calls[0][0].Text);

        var result = await chat.ApplyAsync();

        Assert.Equal(2, result.Places.Count);
        Assert.Equal(result.Places[0].Id, result.Places[1].ParentId);
        Assert.Null(chat.Proposal);
        Assert.Equal(2, saving.Pages.Count);
    }

    [Fact]
    public async Task Chat_HandsTheReplyAndTheReasoningOn_AsTheyArrive()
    {
        var model = new ScriptedModel(_ => new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextReasoningContent("A harbour wants a pier."),
            new TextContent("Varr, then.")
        }));
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);
        var deltas = new List<WorldChatDelta>();

        await chat.SendAsync("A harbour, please.", _ => { }, CancellationToken.None, deltas.Add);

        Assert.Equal(new[] { new WorldChatDelta(true, "A harbour wants a pier."), new WorldChatDelta(false, "Varr, then.") }, deltas);
        Assert.Equal("Varr, then.", chat.Lines[^1].Text); // what was streamed is what the log keeps
    }

    [Fact]
    public async Task Chat_ThatHitsTheToolLimit_LeavesNoCallWithoutItsResult_ForTheNextMessage()
    {
        var saving = Service();
        var turn = 0;
        // A model that only ever looks things up, until the DM's second message.
        var model = new ScriptedModel(Enumerable.Range(0, 60).Select<int, Func<IList<ChatMessage>, ChatMessage>>(_ => sent =>
            sent.Count(m => m.Role == ChatRole.User) > 1 && !sent.Last().Contents.OfType<FunctionResultContent>().Any()
                ? new ChatMessage(ChatRole.Assistant, "Done.")
                : Calls($"c{++turn}", "search_world", """{"query": "salt"}""")).ToArray());
        var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);

        await chat.SendAsync("Look around.", _ => { }, CancellationToken.None);
        await chat.SendAsync("Now just answer.", _ => { }, CancellationToken.None);

        // Every tool call the model is shown again has its result: OpenAI-compatible servers refuse a history without one.
        var history = model.Calls.Last();
        var calls = history.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToList();
        var results = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet();
        Assert.NotEmpty(calls);
        Assert.All(calls, id => Assert.Contains(id, results));
        Assert.Equal("Done.", chat.Lines[^1].Text);
    }

    [Fact]
    public async Task Chat_ThatIsStopped_LeavesTheConversationAsItWas()
    {
        var saving = Service();
        using var stop = new CancellationTokenSource();
        var model = new ScriptedModel(_ =>
        {
            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
            return new ChatMessage(ChatRole.Assistant, "never");
        });
        var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.SendAsync("Hello", _ => { }, stop.Token));

        Assert.Null(chat.Proposal);
        Assert.Empty(chat.Lines); // the message was never answered, so it is not in the log; the page gives it back to the DM
    }

    [Fact]
    public async Task Chat_ThatFailsAfterProposing_ForgetsThatProposal_AndKeepsTheEarlierOne()
    {
        const string varr = """{"proposal": {"region": "The Coast", "entries": [{"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "A town."}]}}""";
        const string kell = """{"proposal": {"region": "The Coast", "entries": [{"ref": "k", "kind": "place", "placeKind": "settlement", "name": "Kell", "text": "A fort."}]}}""";
        var model = new ScriptedModel(
            _ => Calls("c1", "propose_changes", varr),
            _ => new ChatMessage(ChatRole.Assistant, "Varr is ready."),
            _ => Calls("c2", "propose_changes", kell),
            _ => throw new IOException("connection lost"),
            _ => new ChatMessage(ChatRole.Assistant, "Kell, then."));
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);
        await chat.SendAsync("A town, please.", _ => { }, CancellationToken.None);
        var lines = chat.Lines.Count;

        await Assert.ThrowsAsync<IOException>(() => chat.SendAsync("A fort, please.", _ => { }, CancellationToken.None));

        // The abandoned turn proposed Kell, but the model never got to say so and the DM never saw a reply: Varr is still what waits.
        Assert.Equal(new[] { "Varr" }, chat.Proposal!.Drafts.Select(d => d.Name));
        Assert.Equal(lines, chat.Lines.Count);

        await chat.SendAsync("A fort, please.", _ => { }, CancellationToken.None);

        // Sent again, the message is in the conversation once, with nothing of the turn that was cut off.
        var sent = model.Calls[^1];
        Assert.Single(sent, m => m.Role == ChatRole.User && m.Text == "A fort, please.");
        Assert.DoesNotContain(sent.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => c.CallId == "c2");
    }

    [Fact]
    public async Task Chat_KeepsOnlyTheFinalAnswer_NotTheGotItBeforeEachToolCall_AndKeepsTheSteps()
    {
        var model = new ScriptedModel(
            _ => new ChatMessage(ChatRole.Assistant, new List<AIContent> { new TextContent("Got it, let me look."), new FunctionCallContent("c1", "list_places") }),
            _ => new ChatMessage(ChatRole.Assistant, new List<AIContent> { new TextContent("Got it, one more look."), new FunctionCallContent("c2", "list_places") }),
            _ => new ChatMessage(ChatRole.Assistant, "Here is the lay of the land."));
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);

        await chat.SendAsync("What is there?", _ => { }, CancellationToken.None);

        var reply = chat.Lines[^1];
        Assert.Equal("Here is the lay of the land.", reply.Text); // not "Got it, let me look.Got it, one more look.Here is…"
        Assert.Equal(2, reply.Steps!.Count);
        Assert.All(reply.Steps, step => Assert.Equal(("list_places", WorldChatStepStatus.Done), (step.Tool, step.Status)));
        Assert.All(reply.Steps, step => Assert.NotNull(step.Result));
    }

    [Fact]
    public async Task Chat_ThatGoesQuietAfterItsTools_IsNudgedOnce_AndTheNudgeGetsTheAnswer()
    {
        var model = new ScriptedModel(
            _ => Calls("c1", "list_places", "{}"),
            _ => new ChatMessage(ChatRole.Assistant, ""),
            _ => new ChatMessage(ChatRole.Assistant, "Here is the lay of the land."));
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);

        await chat.SendAsync("What is there?", _ => { }, CancellationToken.None);

        Assert.Equal("Here is the lay of the land.", chat.Lines[^1].Text);
        Assert.Contains("ended without a reply", model.Calls[2][^1].Text);
        Assert.Equal(ChatRole.User, model.Calls[2][^1].Role);
    }

    [Fact]
    public async Task Chat_WhoseOnlyWordsCameBeforeItsLastCall_IsAskedForTheAnswer()
    {
        var model = new ScriptedModel(
            _ => new ChatMessage(ChatRole.Assistant, new List<AIContent> { new TextContent("Let me check the places."), new FunctionCallContent("c1", "list_places") }),
            _ => new ChatMessage(ChatRole.Assistant, new List<AIContent>()),
            _ => new ChatMessage(ChatRole.Assistant, "The coast is empty; I would start with a harbour."));
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);

        await chat.SendAsync("What is there?", _ => { }, CancellationToken.None);

        Assert.Contains("has not seen anything from you yet", model.Calls[2][^1].Text);
        Assert.Equal("The coast is empty; I would start with a harbour.", chat.Lines[^1].Text);
    }

    [Fact]
    public async Task Chat_WhoseWordsCameBeforeItsLastCall_KeepsThemWhenTheNudgeBringsNothing()
    {
        var model = new ScriptedModel(
            _ => new ChatMessage(ChatRole.Assistant, new List<AIContent> { new TextContent("The coast is empty."), new FunctionCallContent("c1", "list_places") }),
            _ => new ChatMessage(ChatRole.Assistant, new List<AIContent>()),
            _ => new ChatMessage(ChatRole.Assistant, new List<AIContent>()));
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);

        await chat.SendAsync("What is there?", _ => { }, CancellationToken.None);

        Assert.Equal("The coast is empty.", chat.Lines[^1].Text);
    }

    [Fact]
    public async Task Chat_ThatStaysQuiet_IsNotAnswered_NotAPlaceholder_AndLeavesNoTraceForTheNextMessage()
    {
        var model = new ScriptedModel(
            _ => Calls("c1", "list_places", "{}"),
            _ => new ChatMessage(ChatRole.Assistant, ""),
            _ => new ChatMessage(ChatRole.Assistant, ""),
            _ => new ChatMessage(ChatRole.Assistant, "Now I can talk."));
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);

        var error = await Assert.ThrowsAsync<LocalizedException>(() => chat.SendAsync("What is there?", _ => { }, CancellationToken.None));

        Assert.Contains("returned nothing", error.Message);
        Assert.Empty(chat.Lines); // the page hands the message back; the log has no line for it or for a placeholder

        await chat.SendAsync("What is there?", _ => { }, CancellationToken.None);

        var sent = model.Calls[^1];
        Assert.Single(sent, m => m.Role == ChatRole.User && m.Text == "What is there?");
        Assert.DoesNotContain(sent, m => m.Text.Contains("note from the app"));
    }

    [Fact]
    public async Task Chat_ThatProposesAndSaysNothing_PointsAtTheProposal_WithoutANudgeToProposeAgain()
    {
        const string varr = """{"proposal": {"region": "The Coast", "entries": [{"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "A town."}]}}""";
        var model = new ScriptedModel(
            _ => Calls("c1", "propose_changes", varr),
            _ => new ChatMessage(ChatRole.Assistant, new List<AIContent>()),
            _ => new ChatMessage(ChatRole.Assistant, "Anything else?"));
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);

        await chat.SendAsync("A town, please.", _ => { }, CancellationToken.None);

        Assert.NotNull(chat.Proposal);
        Assert.Equal("Proposal ready to review.", chat.Lines[^1].Text);
        Assert.Equal(2, model.Calls.Count); // the proposal made the turn an answer: no second go

        await chat.SendAsync("And a fort?", _ => { }, CancellationToken.None);

        // The next message follows an assistant turn, not a bare tool result.
        var sent = model.Calls[^1];
        Assert.Equal(ChatRole.Assistant, sent[^2].Role);
        Assert.DoesNotContain(sent, m => m.Text.Contains("note from the app"));
    }

    [Fact]
    public async Task Chat_CutOffByTheLengthLimit_GetsTheShorterNudge()
    {
        var model = new ScriptedModel(
            _ => Calls("c1", "list_places", "{}"),
            _ => new ChatMessage(ChatRole.Assistant, new List<AIContent>()),
            _ => new ChatMessage(ChatRole.Assistant, "A shorter answer."));
        model.Finish[1] = ChatFinishReason.Length;
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);

        await chat.SendAsync("A whole kingdom, please.", _ => { }, CancellationToken.None);

        Assert.Contains("length limit", model.Calls[2][^1].Text);
        Assert.Equal("A shorter answer.", chat.Lines[^1].Text);
    }

    // ---- Reviewing: skipping, and what is mentioned but not written ----

    [Fact]
    public void Skip_AlsoSkipsThePlacesInsideIt_AndSecretsOnlyAboutWhatWasSkipped()
    {
        const string json = """
            {"entries": [
              {"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "A town."},
              {"ref": "m", "kind": "place", "name": "Salt Market", "parent": "t", "text": "Stalls."},
              {"ref": "d", "kind": "place", "name": "Dock", "parent": "m", "text": "Ropes."},
              {"ref": "x", "kind": "place", "placeKind": "settlement", "name": "Elsewhere", "text": "Far."},
              {"ref": "f", "kind": "fact", "name": "The harbourmaster takes bribes.", "links": ["m"], "text": "Everyone knows."},
              {"ref": "g", "kind": "fact", "name": "Varr and Elsewhere trade in secret.", "links": ["t", "x"], "text": "Few know."}
            ]}
            """;
        var bundle = WorldBundleParser.ParseProposal(json, Rules()).Bundle;

        var also = bundle.Skip(bundle.Find("t")!);

        Assert.Equal(new[] { "d", "f", "m" }, also.Select(d => d.Ref).Order());
        Assert.False(bundle.Find("t")!.Selected);
        Assert.True(bundle.Find("x")!.Selected); // not inside Varr
        Assert.True(bundle.Find("g")!.Selected); // about Elsewhere too
    }

    [Fact]
    public void MissingMentions_ListsWhatTextsLinkToByNameThatNobodyHas_OnceEach()
    {
        const string json = """
            {"entries": [
              {"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "South lies [[Amphail]]. Ships come from [[Amphail]] and [[Port Varr]]; ask [[p]] or [[existing:port-varr]] or [[actor:a1|Mira]]."},
              {"ref": "p", "kind": "npc", "name": "Old Hal", "text": "Fishes off [[Varr]] and sells to [[Brinewick]], like [[Amphail]]'s people."}
            ]}
            """;
        var bundle = WorldBundleParser.ParseProposal(json, Rules(actors: new[] { "a1" })).Bundle;

        var missing = WorldBundleParser.MissingMentions(bundle, new[] { "Port Varr" });

        Assert.Equal(new[] { "Amphail", "Brinewick" }, missing.Select(m => m.Name));
        Assert.Equal(new[] { "Varr", "Old Hal" }, missing[0].By);
        Assert.Equal(new[] { "Old Hal" }, missing[1].By);
    }

    [Fact]
    public void MissingMentions_IgnoresAnEntryTheDmSkipped_AndNamesItWasRenamedTo()
    {
        const string json = """
            {"entries": [
              {"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "See [[Amphail]] and [[Kell]]."},
              {"ref": "k", "kind": "place", "placeKind": "settlement", "name": "Kell", "text": "A fort."}
            ]}
            """;
        var bundle = WorldBundleParser.ParseProposal(json, Rules()).Bundle;
        bundle.Find("k")!.Name = "Fort Kell"; // renamed: the link still reaches it by the name it was proposed under

        Assert.Equal("Amphail", Assert.Single(WorldBundleParser.MissingMentions(bundle, Array.Empty<string>())).Name);

        bundle.Skip(bundle.Find("t")!); // a skipped entry's text is not going to be saved, so what it mentions is not missing
        Assert.Empty(WorldBundleParser.MissingMentions(bundle, Array.Empty<string>()));
    }

    [Fact]
    public async Task Chat_AddEntries_PutsNewEntriesBesideTheWaitingOnes_AndKeepsTheDmsChoices()
    {
        const string varr = """{"proposal": {"region": "The Coast", "entries": [{"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "South lies [[Amphail]]."}]}}""";
        const string amphail = """{"additions": {"entries": [{"ref": "a", "kind": "place", "placeKind": "settlement", "name": "Amphail", "text": "A fishing town south of [[t]]."}]}}""";
        var model = new ScriptedModel(
            _ => Calls("c1", "propose_changes", varr),
            _ => new ChatMessage(ChatRole.Assistant, "Varr is ready."),
            _ => Calls("c2", "add_entries", amphail),
            _ => new ChatMessage(ChatRole.Assistant, "Amphail is in too."));
        var saving = Service();
        var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);
        await chat.SendAsync("A town, please.", _ => { }, CancellationToken.None);
        Assert.Equal("Amphail", Assert.Single(await saving.Service.MissingMentionsAsync(Dm, chat.Proposal!)).Name);
        chat.Proposal!.Find("t")!.Name = "Port Varr"; // the DM's rename
        chat.Proposal.Find("t")!.Cast = true;

        await chat.SendAsync("Write Amphail.", _ => { }, CancellationToken.None);

        Assert.Equal(new[] { "Port Varr", "Amphail" }, chat.Proposal!.Drafts.Select(d => d.Name));
        Assert.Empty(await saving.Service.MissingMentionsAsync(Dm, chat.Proposal)); // what was mentioned is now written
        Assert.Contains("ready for the game master", ToolResult(model.Calls[3]));
    }

    [Fact]
    public void WouldSkip_ChangesNothing_AndSelectBringsBackThePlacesItSitsIn()
    {
        const string json = """
            {"entries": [
              {"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "A town."},
              {"ref": "m", "kind": "place", "name": "Salt Market", "parent": "t", "text": "Stalls."},
              {"ref": "d", "kind": "place", "name": "Dock", "parent": "m", "text": "Ropes."}
            ]}
            """;
        var bundle = WorldBundleParser.ParseProposal(json, Rules()).Bundle;

        Assert.Equal(2, bundle.WouldSkip(bundle.Find("t")!).Count);
        Assert.All(bundle.Drafts, d => Assert.True(d.Selected)); // looking is not skipping

        bundle.Skip(bundle.Find("t")!);
        var back = bundle.Select(bundle.Find("d")!);

        Assert.Equal(new[] { "m", "t" }, back.Select(d => d.Ref).Order()); // a Dock kept means its Market and town are kept
        Assert.All(bundle.Drafts, d => Assert.True(d.Selected));
    }

    [Fact]
    public void MissingMentions_MatchesTheWayTheWikiDoes_BySlug_AndListsASpellingsOnce()
    {
        const string json = """
            {"entries": [
              {"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "Near [[the kings’ road]], [[Brinewick]], [[BRINEWICK]], [[Brine wick]], [[Brine-wick]] and [[Hal's Rest]]."}
            ]}
            """;
        var bundle = WorldBundleParser.ParseProposal(json, Rules()).Bundle;

        var missing = WorldBundleParser.MissingMentions(bundle, new[] { "The Kings' Road", "Hal’s Rest" });

        // Curly or straight apostrophe, any case: the page the wiki finds. A space and a hyphen are one name, and listed once.
        Assert.Equal(new[] { "Brinewick", "Brine wick" }, missing.Select(m => m.Name));
    }

    [Fact]
    public void MissingMentions_KnowsRegionNames_AndAColonInANameIsNotAKey()
    {
        const string json = """
            {"region": "The Coast", "entries": [
              {"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "On [[The Coast]], near [[Order of the Gauntlet: Neverwinter]] and [[Hills]]; see [[existing:port-varr]] and [[place:x|Old Town]]."}
            ]}
            """;
        var bundle = WorldBundleParser.ParseProposal(json, Rules()).Bundle;

        var missing = WorldBundleParser.MissingMentions(bundle, new[] { "Hills", "Port Varr" }); // the parser turns existing:port-varr into its title, which the campaign has

        Assert.Equal("Order of the Gauntlet: Neverwinter", Assert.Single(missing).Name);
        Assert.Equal(new[] { "t" }, missing[0].ByRefs);
    }

    [Fact]
    public async Task Chat_AddEntries_RefusesARefTheProposalUsesOrNone_AndTooMany_WithoutChangingTheProposal()
    {
        const string varr = """{"proposal": {"region": "The Coast", "entries": [{"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "A town."}]}}""";
        const string clash = """{"additions": {"entries": [{"ref": "T", "kind": "place", "placeKind": "settlement", "name": "Other", "text": "x"}]}}""";
        const string none = """{"additions": {"entries": [{"kind": "place", "placeKind": "settlement", "name": "Other", "text": "x"}]}}""";
        var many = "{\"additions\": {\"entries\": [" + string.Join(",", Enumerable.Range(0, 30).Select(i => $$"""{"ref": "n{{i}}", "kind": "wiki", "name": "Page {{i}}", "text": "x"}""")) + "]}}";
        var model = new ScriptedModel(
            _ => Calls("c1", "propose_changes", varr),
            _ => new ChatMessage(ChatRole.Assistant, "Ready."),
            _ => Calls("c2", "add_entries", clash),
            _ => Calls("c3", "add_entries", none),
            _ => Calls("c4", "add_entries", many),
            _ => new ChatMessage(ChatRole.Assistant, "Sorry."));
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);
        await chat.SendAsync("A town.", _ => { }, CancellationToken.None);
        var waiting = chat.Proposal;
        var epoch = chat.ProposalEpoch;

        await chat.SendAsync("More.", _ => { }, CancellationToken.None);

        Assert.Same(waiting, chat.Proposal); // none of the three changed it
        Assert.Equal(epoch, chat.ProposalEpoch);
        var results = model.Calls[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString() ?? string.Empty).ToList();
        Assert.Contains("already used: T", results[^3]);
        Assert.Contains("one has none", results[^2]);
        Assert.Contains("at most 30 fit", results[^1]);
    }

    [Fact]
    public async Task Chat_AddEntries_KeepsWhatSavingAlreadyMadeOfTheOldEntries()
    {
        const string varr = """{"proposal": {"region": "The Coast", "entries": [{"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "A town."}]}}""";
        const string more = """{"additions": {"entries": [{"ref": "a", "kind": "wiki", "name": "Amphail", "text": "A port."}]}}""";
        var model = new ScriptedModel(
            _ => Calls("c1", "propose_changes", varr),
            _ => new ChatMessage(ChatRole.Assistant, "Ready."),
            _ => Calls("c2", "add_entries", more),
            _ => new ChatMessage(ChatRole.Assistant, "Added."));
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);
        await chat.SendAsync("A town.", _ => { }, CancellationToken.None);
        chat.Proposal!.Find("t")!.SavedAs["page"] = "page-1"; // an apply that failed half-way
        chat.Proposal.Find("t")!.PageTitle = "Varr (2)";
        chat.Proposal.Find("t")!.Selected = false;

        await chat.SendAsync("And Amphail.", _ => { }, CancellationToken.None);

        var varrDraft = chat.Proposal.Find("t")!;
        Assert.Equal("page-1", varrDraft.SavedAs["page"]); // or the next apply would write Varr twice
        Assert.Equal("Varr (2)", varrDraft.PageTitle);
        Assert.False(varrDraft.Selected);
        Assert.True(chat.Proposal.Find("a")!.Selected);
    }

    [Fact]
    public async Task Chat_ThatAddedEntriesAndThenFailed_GetsItsWaitingProposalBack_AndItsEpoch()
    {
        const string varr = """{"proposal": {"region": "The Coast", "entries": [{"ref": "t", "kind": "place", "placeKind": "settlement", "name": "Varr", "text": "A town."}]}}""";
        const string more = """{"additions": {"entries": [{"ref": "a", "kind": "wiki", "name": "Amphail", "text": "A port."}]}}""";
        var model = new ScriptedModel(
            _ => Calls("c1", "propose_changes", varr),
            _ => new ChatMessage(ChatRole.Assistant, "Ready."),
            _ => Calls("c2", "add_entries", more),
            _ => throw new IOException("connection lost"));
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);
        await chat.SendAsync("A town.", _ => { }, CancellationToken.None);
        var waiting = chat.Proposal;

        await Assert.ThrowsAsync<IOException>(() => chat.SendAsync("And Amphail.", _ => { }, CancellationToken.None));

        Assert.Same(waiting, chat.Proposal);
        Assert.Single(chat.Proposal!.Drafts);
    }

    // ---- Editing what exists ----

    [Fact]
    public void ParseProposal_Edit_NamesATarget_AndTakesTheCompleteNewText()
    {
        const string json = """
            {"entries": [
              {"ref": "e1", "kind": "edit", "target": "existing:port-varr", "text": "Rewritten. See [[existing:sister-maren]]."},
              {"ref": "e2", "kind": "edit", "target": "existing:place:old-town", "text": "A ruined gate."},
              {"ref": "e3", "kind": "edit", "target": "existing:actor:a1", "text": "Born in the Wood."}
            ]}
            """;

        var check = WorldBundleParser.ParseProposal(json, Rules(actors: new[] { "a1" }));

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Equal(new[] { "existing:port-varr", "existing:place:old-town", "existing:actor:a1" }, check.Bundle.Drafts.Select(d => d.Target));
        Assert.Equal("Port Varr", check.Bundle.Drafts[0].Name); // named after what it changes; the model need not repeat it
        Assert.Equal("Rewritten. See [[Sister Maren]].", check.Bundle.Drafts[0].Body);
        Assert.Empty(check.Warnings);
    }

    [Theory]
    [InlineData("""{"kind": "edit", "text": "x"}""", "needs a \"target\"")]
    [InlineData("""{"kind": "edit", "target": "existing:nowhere", "text": "x"}""", "not something the campaign has")]
    [InlineData("""{"kind": "edit", "target": "existing:place:gone", "text": "x"}""", "not something the campaign has")]
    [InlineData("""{"kind": "edit", "target": "existing:actor:a9", "text": "x"}""", "not something the campaign has")]
    [InlineData("""{"kind": "edit", "target": "existing:port-varr"}""", "COMPLETE new text")]
    public void ParseProposal_Edit_NeedsARealTarget_AndItsText(string entry, string expected)
    {
        var check = WorldBundleParser.ParseProposal($$"""{"entries": [{{entry}}]}""", Rules(actors: new[] { "a1" }));

        Assert.False(check.Ok);
        Assert.Contains(expected, Assert.Single(check.Errors));
    }

    [Fact]
    public void ParseProposal_Edit_ChangesATargetOnce()
    {
        const string json = """
            {"entries": [
              {"ref": "e1", "kind": "edit", "target": "existing:port-varr", "text": "One."},
              {"ref": "e2", "kind": "edit", "target": "port-varr", "text": "Two."}
            ]}
            """;

        var check = WorldBundleParser.ParseProposal(json, Rules());

        Assert.Contains("already changed by another entry", Assert.Single(check.Errors));
    }

    [Fact]
    public void ResolveLinks_AnEditNamedLikeAPage_DoesNotTurnLinksToThatPageIntoPlainText()
    {
        const string json = """
            {"entries": [
              {"ref": "e1", "kind": "edit", "target": "existing:port-varr", "text": "Rewritten."},
              {"ref": "w", "kind": "wiki", "name": "Salt Wardens", "text": "They watch [[Port Varr]]."}
            ]}
            """;
        var check = WorldBundleParser.ParseProposal(json, Rules());

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Equal("They watch [[Port Varr]].", WorldBundleParser.ResolveLinks(check.Bundle.Drafts[1].Body!, check.Bundle));
    }

    private static WikiPageDocument VarrPage() => new() { Id = "page-varr", Slug = "port-varr", Title = "Port Varr", Body = "Old text.", Visibility = WikiVisibility.Party };

    private static RegionDocument HillRegion() => new()
    {
        Id = "region-hills", CampaignId = "c1", Name = "Hills",
        Locations = { new LocationDocument { Id = "old-town", Name = "Old Town", Kind = PlaceKind.Settlement, Description = "Old description.", Visibility = ActorVisibility.Hidden } }
    };

    [Fact]
    public async Task Check_Edit_ShowsTheCurrentText_AndSaysWhenPlayersCanAlreadyReadIt()
    {
        var saving = Service(new[] { VarrPage() }, new[] { HillRegion() });
        saving.Actors.Add(new ActorDocument { Id = "a1", Name = "Mira Greystone", Biography = "Old history.", Visibility = ActorVisibility.Hidden, Kind = ActorKind.NonPlayerCharacter });
        const string json = """
            {"entries": [
              {"ref": "e1", "kind": "edit", "target": "existing:port-varr", "text": "New page."},
              {"ref": "e2", "kind": "edit", "target": "existing:place:old-town", "text": "New description."},
              {"ref": "e3", "kind": "edit", "target": "existing:actor:a1", "text": "New history."}
            ]}
            """;

        var check = await saving.Service.CheckAsync(Campaign, Dm, json);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Equal(new[] { "Old text.", "Old description.", "Old history." }, check.Bundle.Drafts.Select(d => d.Before));
        Assert.Equal(new[] { "Port Varr", "Old Town", "Mira Greystone" }, check.Bundle.Drafts.Select(d => d.Name));
        Assert.Equal(new[] { true, false, false }, check.Bundle.Drafts.Select(d => d.SharedWithPlayers)); // only the shared page
    }

    [Fact]
    public async Task Save_Edit_ReplacesTheTextOfAPage_APlaceAndACastMember()
    {
        var page = VarrPage();
        var region = HillRegion();
        var saving = Service(new[] { page }, new[] { region });
        var mira = new ActorDocument { Id = "a1", Name = "Mira Greystone", Biography = "Old history.", Kind = ActorKind.NonPlayerCharacter };
        saving.Actors.Add(mira);
        const string json = """
            {"entries": [
              {"ref": "e1", "kind": "edit", "target": "existing:port-varr", "text": "The port, rewritten, beside [[existing:port-varr]] and [[e2x]]."},
              {"ref": "e2", "kind": "edit", "target": "existing:place:old-town", "text": "A walled farm town."},
              {"ref": "e3", "kind": "edit", "target": "existing:actor:a1", "text": "Born in the Wood."}
            ]}
            """;
        var check = await saving.Service.CheckAsync(Campaign, Dm, json);
        Assert.True(check.Ok, string.Join("; ", check.Errors));

        var result = await saving.Service.SaveAsync(Campaign, Dm, check.Bundle);

        Assert.Equal("The port, rewritten, beside [[Port Varr]] and [[e2x]].", page.Body);
        Assert.Equal("A walled farm town.", region.Locations[0].Description);
        Assert.Contains(saving.AddedPlaces, p => p.RegionId == "region-hills" && p.Place.Description == "A walled farm town.");
        Assert.Equal("Born in the Wood.", mira.Biography);
        Assert.Equal(new[] { EditTarget.Page, EditTarget.Place, EditTarget.Actor }, result.Changes.Select(c => c.Target));
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Save_Edit_OfSomethingThatWentAfterTheReview_SaysSoRatherThanSkippingIt()
    {
        var saving = Service(new[] { VarrPage() });
        var check = await saving.Service.CheckAsync(Campaign, Dm, """{"entries": [{"ref": "e1", "kind": "edit", "target": "existing:port-varr", "text": "New."}]}""");
        Assert.True(check.Ok);
        saving.Pages.Clear();

        // Service() lists its fixed existing pages, so point the draft at one that was never there.
        check.Bundle.Drafts[0].Target = "existing:vanished";
        var ex = await Assert.ThrowsAsync<LocalizedException>(() => saving.Service.SaveAsync(Campaign, Dm, check.Bundle));

        Assert.Equal("WorldBuilder.Error.EditTargetGone", ex.Key);
    }

    [Fact]
    public async Task Edit_KeepsTheMentionsInTheText_WhenTheModelCopiesThemBack()
    {
        var page = new WikiPageDocument { Id = "page-varr", Slug = "port-varr", Title = "Port Varr", Body = "Sworn to [[actor:a1]] at [[place:old-town]]. See [[Port Varr]]." };
        var mira = new ActorDocument { Id = "a1", Name = "Mira Greystone", Biography = "Loyal to [[actor:a1]].", Kind = ActorKind.NonPlayerCharacter };
        var saving = Service(new[] { page }, new[] { HillRegion() });
        saving.Actors.Add(mira);

        // What the model reads keeps each mention as a marker with its name, so copying a passage back keeps the link.
        var read = await saving.Service.PageAsync(Dm, "existing:port-varr");
        Assert.Contains("[[actor:a1|Mira Greystone]]", read);
        Assert.Contains("[[place:old-town|Old Town]]", read);

        const string json = """
            {"entries": [
              {"ref": "e1", "kind": "edit", "target": "existing:port-varr", "text": "Sworn to [[actor:a1|Mira Greystone]] at [[place:old-town|Old Town]], and fearing fire. See [[Port Varr]]."},
              {"ref": "e2", "kind": "edit", "target": "existing:actor:a1", "text": "Loyal to [[actor:a1|Mira Greystone]], who fears fire."}
            ]}
            """;
        var check = await saving.Service.CheckAsync(Campaign, Dm, json);
        Assert.True(check.Ok, string.Join("; ", check.Errors));

        await saving.Service.SaveAsync(Campaign, Dm, check.Bundle);

        Assert.Equal("Sworn to [[actor:a1]] at [[place:old-town]], and fearing fire. See [[Port Varr]].", page.Body); // stored form, page link kept
        Assert.Equal("Loyal to [[actor:a1]], who fears fire.", mira.Biography);                                       // not "actor:a1" as text
    }

    [Fact]
    public async Task Check_Edit_RefusesATextTooLongToHaveBeenReadWhole()
    {
        var page = VarrPage();
        page.Body = new string('x', 6001);
        var saving = Service(new[] { page });

        var check = await saving.Service.CheckAsync(Campaign, Dm, """{"entries": [{"ref": "e1", "kind": "edit", "target": "existing:port-varr", "text": "Short."}]}""");

        Assert.False(check.Ok);
        Assert.Contains("more than get_page shows", Assert.Single(check.Errors));
        Assert.Contains("too long to show whole", await saving.Service.PageAsync(Dm, "existing:port-varr"));
    }

    [Fact]
    public void ParseProposal_Edit_DoesNotQuietlyCutTheTextThatReplacesAnother()
    {
        var json = $$"""{"entries": [{"ref": "e1", "kind": "edit", "target": "existing:port-varr", "text": "{{new string('y', 20_001)}}"}]}""";

        var check = WorldBundleParser.ParseProposal(json, Rules());

        Assert.Contains("holds at most", Assert.Single(check.Errors));
    }

    [Fact]
    public async Task Save_Edit_OfTextChangedSinceTheReview_IsRefusedRatherThanOverwritingIt()
    {
        var page = VarrPage();
        var saving = Service(new[] { page });
        var check = await saving.Service.CheckAsync(Campaign, Dm, """{"entries": [{"ref": "e1", "kind": "edit", "target": "existing:port-varr", "text": "New."}]}""");
        Assert.True(check.Ok);

        page.Body = "Someone else edited this in another tab."; // after the DM reviewed the old text
        var ex = await Assert.ThrowsAsync<LocalizedException>(() => saving.Service.SaveAsync(Campaign, Dm, check.Bundle));

        Assert.Equal("WorldBuilder.Error.EditChanged", ex.Key);
        Assert.Equal("Someone else edited this in another tab.", page.Body);
    }

    [Fact]
    public async Task Check_Edit_OfAPlace_FindsItWhateverTheCaseOfTheKey()
    {
        var saving = Service(null, new[] { HillRegion() });

        var check = await saving.Service.CheckAsync(Campaign, Dm, """{"entries": [{"ref": "e1", "kind": "edit", "target": "existing:place:OLD-TOWN", "text": "New."}]}""");

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Equal("existing:place:old-town", check.Bundle.Drafts[0].Target); // as stored, so the save finds it
        Assert.Equal("Old description.", check.Bundle.Drafts[0].Before);
    }

    [Fact]
    public async Task Chat_AnswerWrittenBesideTheLastToolCall_IsStillTheAnswer()
    {
        var model = new ScriptedModel(
            _ => new ChatMessage(ChatRole.Assistant, new List<AIContent> { new TextContent("Here is the lay of the land."), new FunctionCallContent("c1", "list_places") }),
            _ => new ChatMessage(ChatRole.Assistant, string.Empty),
            _ => new ChatMessage(ChatRole.Assistant, string.Empty)); // the nudge brings nothing either
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(model), Campaign, Dm);

        await chat.SendAsync("What is there?", _ => { }, CancellationToken.None);

        Assert.Equal("Here is the lay of the land.", chat.Lines[^1].Text);
    }

    [Fact]
    public async Task GetPage_ReadsAPlacesDescription_AndACastMembersHistory_ByTheirKeys()
    {
        var saving = Service(new[] { VarrPage() }, new[] { HillRegion() });
        saving.Actors.Add(new ActorDocument { Id = "a1", Name = "Mira Greystone", Biography = "Old history.", Kind = ActorKind.NonPlayerCharacter });

        var place = await saving.Service.PageAsync(Dm, "existing:place:old-town");
        var actor = await saving.Service.PageAsync(Dm, "existing:actor:a1");
        var search = await saving.Service.SearchWorldAsync(Campaign, Dm, "Mira");

        Assert.Contains("Old description.", place);
        Assert.Contains("hidden from players", place);
        Assert.Contains("Old history.", actor);
        Assert.Contains("key: existing:actor:a1", actor);
        Assert.Contains("existing:actor:a1", search); // search_world shows the key an edit needs
    }

    [Fact]
    public async Task Chat_IsForTheDmOnly()
    {
        var chat = new WorldChat(Service().Service, () => throw new InvalidOperationException("no model needed"), Campaign, new CampaignAccess("c1", "p", CampaignRole.Player));

        var ex = await Assert.ThrowsAsync<LocalizedException>(() => chat.SendAsync("Hello", _ => { }, CancellationToken.None));

        Assert.Equal("WorldBuilder.Error.OnlyDm", ex.Key);
    }

    // ---- Changes to what exists: homebrew edits, tags, Bestiary picks and variants ----

    private static ProposalRules ChangeRules() => Rules() with
    {
        ExistingHomebrew = new Dictionary<string, string> { ["spell/brine-ward"] = "Brine Ward", ["monster/gull-hag"] = "Gull Hag" }
    };

    [Fact]
    public void ParseProposal_ReadsHomebrewEdits_Tags_BestiaryPicks_Variants_AndPageTags()
    {
        const string json = """
            {"entries": [
              {"ref": "e", "kind": "edit", "target": "existing:homebrew:Spell/Brine-Ward", "rename": "Greater Brine Ward", "properties": {"level": "3", "school": "Evocation"}, "tags": ["salt", "ward"]},
              {"ref": "g", "kind": "tags", "targets": ["existing:port-varr", "existing:homebrew:spell/brine-ward"], "add": ["Grey Coast", "grey  coast"], "remove": ["town"]},
              {"ref": "b", "kind": "bestiary", "entry": "existing:homebrew:monster/gull-hag"},
              {"ref": "v", "kind": "homebrew", "type": "spell", "name": "Rimeburst", "basedOn": "spell/fireball", "properties": {"damageType": "Cold"}},
              {"ref": "w", "kind": "wiki", "name": "The Wardens", "tags": ["faction", "grey-coast"], "text": "Smugglers."}
            ]}
            """;

        var check = WorldBundleParser.ParseProposal(json, ChangeRules());

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        var b = check.Bundle;
        var edit = b.Find("e")!;
        // The key as stored, whatever its case in the proposal; named after the entry it changes.
        Assert.Equal(("existing:homebrew:spell/brine-ward", "spell", "Brine Ward", "Greater Brine Ward"), (edit.Target, edit.Type, edit.Name, edit.Rename));
        Assert.Equal(3L, edit.Properties["level"]);
        Assert.True(edit.Retag);
        Assert.Equal(new[] { "salt", "ward" }, edit.Tags);
        Assert.Null(edit.Body); // the rules text stays as it is

        var tags = b.Find("g")!;
        Assert.Equal(new[] { "existing:port-varr", "existing:homebrew:spell/brine-ward" }, tags.Targets);
        Assert.Equal(new[] { "Grey Coast" }, tags.TagsAdded); // the same tag twice, spelled two ways, is one
        Assert.Equal(new[] { "town" }, tags.TagsRemoved);
        Assert.Equal("+Grey Coast −town", tags.Name);

        Assert.Equal("monster/gull-hag", b.Find("b")!.SuggestedStats);
        Assert.Equal(("spell/fireball", "Cold"), (b.Find("v")!.BasedOn, b.Find("v")!.Properties["damageType"]));
        Assert.Equal(new[] { "faction", "grey-coast" }, b.Find("w")!.Tags);
        // A variant without text of its own takes its base's; that is not worth a warning.
        Assert.DoesNotContain(check.Warnings, w => w.Contains("'Rimeburst'"));
    }

    [Theory]
    [InlineData("""{"ref": "e", "kind": "edit", "target": "existing:homebrew:spell/fireball", "text": "Hotter."}""", "make a homebrew variant of it instead")]
    [InlineData("""{"ref": "e", "kind": "edit", "target": "existing:homebrew:spell/brine-ward"}""", "the edit changes nothing")]
    [InlineData("""{"ref": "g", "kind": "tags", "targets": ["existing:nowhere"], "add": ["x"]}""", "target 'existing:nowhere' is not a wiki page")]
    [InlineData("""{"ref": "g", "kind": "tags", "targets": ["existing:place:old-town"], "add": ["x"]}""", "only those carry tags")]
    [InlineData("""{"ref": "g", "kind": "tags", "targets": ["existing:port-varr"]}""", "needs \"add\" or \"remove\"")]
    [InlineData("""{"ref": "g", "kind": "tags", "add": ["x"]}""", "needs \"targets\"")]
    [InlineData("""{"ref": "b", "kind": "bestiary"}""", "needs \"entry\"")]
    public void ParseProposal_RefusesChangesItCannotMake(string entry, string error)
    {
        var check = WorldBundleParser.ParseProposal($$"""{"entries": [{{entry}}]}""", ChangeRules());

        Assert.False(check.Ok);
        Assert.Contains(check.Errors, e => e.Contains(error));
    }

    [Fact]
    public void ParseProposal_RefusesTwoEditsOfOneHomebrewEntry()
    {
        const string json = """
            {"entries": [
              {"ref": "a", "kind": "edit", "target": "existing:homebrew:spell/brine-ward", "rename": "One"},
              {"ref": "b", "kind": "edit", "target": "existing:homebrew:SPELL/brine-ward", "rename": "Two"}
            ]}
            """;

        Assert.Contains(WorldBundleParser.ParseProposal(json, ChangeRules()).Errors, e => e.Contains("already changed by another entry"));
    }

    private static HomebrewEntryDocument BrineWard() => new()
    {
        Id = "hb-1", Type = "spell", EntryId = "brine-ward", Name = "Brine Ward", SystemId = "srd5e", Description = "A circle of salt.",
        Tags = { "salt", "town" }, Properties = { ["level"] = 2L, ["school"] = "Abjuration" }, UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public async Task Check_ShowsWhatAHomebrewEditChanges_AndSaveChangesOnlyThat()
    {
        var ward = BrineWard();
        var saving = Service(null, ward);
        const string json = """{"entries": [{"ref": "e", "kind": "edit", "target": "existing:homebrew:spell/brine-ward", "rename": "Greater Brine Ward", "properties": {"level": 3, "school": "Abjuration"}}]}""";

        var check = await saving.Service.CheckAsync(Campaign, Dm, json);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        var edit = check.Bundle.Drafts.Single();
        Assert.Equal(("Brine Ward", "A circle of salt.", "A circle of salt."), (edit.Name, edit.Before, edit.Body)); // no new text: the old one stays
        Assert.Equal(2L, edit.BeforeProperties!["level"]);

        var result = await saving.Service.SaveAsync(Campaign, Dm, check.Bundle);

        Assert.Equal(("Greater Brine Ward", 3L, "Abjuration", "A circle of salt."), (ward.Name, ward.Properties["level"], ward.Properties["school"], ward.Description));
        Assert.Equal(new[] { "salt", "town" }, ward.Tags); // tags not named: kept
        var change = Assert.Single(result.Changes);
        Assert.Equal((EditTarget.Homebrew, "spell/brine-ward", "Greater Brine Ward"), (change.Target, change.Id, change.Name));
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public async Task Save_OfAHomebrewEdit_RefusesWhenTheEntryWasSavedSinceTheReview()
    {
        var ward = BrineWard();
        var saving = Service(null, ward);
        var check = await saving.Service.CheckAsync(Campaign, Dm, """{"entries": [{"ref": "e", "kind": "edit", "target": "existing:homebrew:spell/brine-ward", "rename": "Mine"}]}""");
        ward.UpdatedAt = ward.UpdatedAt.AddMinutes(5); // the DM edited it in the Library meanwhile

        var error = await Assert.ThrowsAsync<LocalizedException>(() => saving.Service.SaveAsync(Campaign, Dm, check.Bundle));

        Assert.Equal("WorldBuilder.Error.EditChanged", error.Key);
        Assert.Equal("Brine Ward", ward.Name);
    }

    [Fact]
    public async Task Tags_AreShownWithWhatEachTargetHasNow_AndSavedOntoPagesAndHomebrew()
    {
        var ward = BrineWard();
        var varr = new WikiPageDocument { Id = "p-varr", Slug = "port-varr", Title = "Port Varr", Tags = { "Town", "harbour" } };
        var saving = Service(new[] { varr }, ward);
        const string json = """{"entries": [{"ref": "g", "kind": "tags", "targets": ["existing:port-varr", "existing:homebrew:spell/brine-ward"], "add": ["grey-coast"], "remove": ["town"]}]}""";

        var check = await saving.Service.CheckAsync(Campaign, Dm, json);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Equal(new[] { ("Port Varr", "Town, harbour"), ("Brine Ward", "salt, town") },
            check.Bundle.Drafts.Single().Tagged.Select(t => (t.Name, string.Join(", ", t.Before))));

        var result = await saving.Service.SaveAsync(Campaign, Dm, check.Bundle);

        Assert.Equal(new[] { "harbour", "grey-coast" }, saving.Pages.Single(p => p.Slug == "port-varr").Tags); // "town" goes whatever its case
        Assert.Equal(new[] { "salt", "grey-coast" }, ward.Tags);
        Assert.Equal((2, 2), (result.Retagged, result.Count));
    }

    [Fact]
    public async Task Variant_StartsFromItsLibraryEntry_AndBestiaryPick_PutsAPackCreatureOnTheShortlist()
    {
        var registry = VeteranPack();
        registry.RegisterEntry(new ContentEntry
        {
            Id = "fireball", Type = "spell", SystemId = "srd5e", PackId = "srd", Name = "Fireball", Description = "A bright streak.",
            Tags = { "evocation" }, Properties = { ["level"] = 3L, ["damageType"] = "Fire" }
        }, 0);
        var saving = Service(null, Array.Empty<RegionDocument>(), registry);
        const string json = """
            {"entries": [
              {"ref": "v", "kind": "homebrew", "type": "spell", "name": "Rimeburst", "basedOn": "spell/fireball", "properties": {"damageType": "Cold"}},
              {"ref": "b", "kind": "bestiary", "entry": "monster/veteran"}
            ]}
            """;

        var check = await saving.Service.CheckAsync(Campaign, Dm, json);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        var rime = check.Bundle.Find("v")!;
        Assert.Equal((3L, "Cold", "A bright streak.", "Fireball"), (rime.Properties["level"], rime.Properties["damageType"], rime.Body, rime.BasedOnName));
        Assert.Equal(new[] { "evocation" }, rime.Tags);
        Assert.Equal(("Veteran", "veteran"), (check.Bundle.Find("b")!.Name, check.Bundle.Find("b")!.Stats?.Id));

        var result = await saving.Service.SaveAsync(Campaign, Dm, check.Bundle);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(("Rimeburst", "Cold", false), (entry.Name, entry.Properties["damageType"], entry.IsPublished));
        Assert.Equal("srd", entry.ImportedFrom); // the pack it came from, for the attribution line
        Assert.Contains(saving.Stored.Bestiary, s => s is { EntryType: "monster", EntryId: "veteran", IsHomebrew: false });
        Assert.Equal((1, 2), (result.Picked, result.Count));
    }

    [Theory]
    [InlineData("""{"ref": "v", "kind": "homebrew", "type": "spell", "name": "X", "basedOn": "spell/nothing"}""", "is not a Library entry")]
    [InlineData("""{"ref": "v", "kind": "homebrew", "type": "spell", "name": "X", "basedOn": "monster/veteran"}""", "a variant has its base's type")]
    [InlineData("""{"ref": "b", "kind": "bestiary", "entry": "monster/nobody"}""", "is not a creature in the Library")]
    public async Task Check_RefusesAVariantOrAPickTheLibraryCannotBack(string entry, string error)
    {
        var saving = Service(null, Array.Empty<RegionDocument>(), VeteranPack());

        var check = await saving.Service.CheckAsync(Campaign, Dm, $$"""{"entries": [{{entry}}]}""");

        Assert.Contains(check.Errors, e => e.Contains(error));
    }

    // ---- The chat as it is stored ----

    [Fact]
    public async Task Chat_IsStoredAfterEachTurn_AndCarriesOnFromTheStoredHistory()
    {
        var saving = Service();
        var stored = new List<WorldChatDocument>();
        var first = new ScriptedModel(
            _ => Calls("c1", "list_places", "{}"),
            _ => new ChatMessage(ChatRole.Assistant, "The coast is empty."));
        var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(first), Campaign, Dm, store: doc =>
        {
            stored.Add(doc);
            return Task.CompletedTask;
        });

        await chat.SendAsync("What is there along the coast, and who lives by the water?", null, CancellationToken.None);

        var doc = Assert.Single(stored);
        Assert.Equal("What is there along the coast, and who lives by the water?", doc.Title);
        // The DM's message, the call, its result and the answer; the system prompt is made afresh each time, so not stored.
        Assert.Equal(new[] { "user", "assistant", "tool", "assistant" }, doc.History.Select(m => m.Role));
        Assert.Equal("list_places", doc.History[1].Calls![0].Name);
        Assert.StartsWith("The campaign has no places yet", doc.History[2].Results![0].Result);
        Assert.Equal(2, doc.Lines[^1].Usage!.Rounds); // the round that called the tool, and the answer

        // Opened again (another connection), the model reads the whole conversation so far after a fresh system prompt.
        var second = new ScriptedModel(_ => new ChatMessage(ChatRole.Assistant, "Then a harbour."));
        var again = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(second), Campaign, Dm, doc);
        await again.SendAsync("Start with a harbour.", null, CancellationToken.None);

        var sent = second.Calls.Single();
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Equal(new[] { "What is there along the coast, and who lives by the water?", "", "", "The coast is empty.", "Start with a harbour." }, sent.Skip(1).Select(m => m.Text));
        Assert.Equal("c1", sent[2].Contents.OfType<FunctionCallContent>().Single().CallId);
        Assert.Equal("c1", sent[3].Contents.OfType<FunctionResultContent>().Single().CallId);
    }

    [Fact]
    public async Task Chat_ThatIsStopped_IsNotBusyAfterwards_AndKeepsNothingOfTheTurn()
    {
        var chat = new WorldChat(Service().Service, () => Task.FromResult<IChatClient>(new SilentModel()), Campaign, Dm);
        var changes = 0;
        chat.Changed += () => changes++;

        var turn = chat.SendAsync("Hello", null, CancellationToken.None);
        // The turn is under way once the model is asked; the system prompt is read first.
        for (var waited = 0; !chat.IsBusy && waited < 200; waited++) await Task.Delay(10);
        Assert.True(chat.IsBusy);
        chat.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);

        Assert.False(chat.IsBusy);
        Assert.Null(chat.Live);
        Assert.Empty(chat.Lines);
        Assert.Equal(string.Empty, chat.Title);
        Assert.True(changes >= 2); // the turn started, and ended
    }

    /// <summary>A model that says nothing until it is stopped, as a slow one does.</summary>
    private sealed class SilentModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData("A harbour town", "A harbour town")]
    [InlineData("A harbour town on the grey coast where the tide decides who eats tonight", "A harbour town on the grey coast where the tide decides who…")]
    [InlineData("First line\nsecond line", "First line")]
    public void ChatTitle_IsTheFirstLineOfTheFirstMessage_CutAtAWord(string message, string title) =>
        Assert.Equal(title, WorldChat.TitleFrom(message));
}
