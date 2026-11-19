using Codex.Core.Consistency;

namespace Codex.Tests.Consistency;

/// <summary>
/// The v2 checks (docs/WORLD_BUILDER.md, 9): the structure of the world, the campaign and the Library it points at; and the noisy checks, which are off
/// unless asked for. Each has a fixture that trips it, ids that name documents and never titles, and a repeated-run test.
/// </summary>
public class WorldStructureCheckTests
{
    private static readonly Fx.Entries Library = new(
        Fx.Entry("class", "fighter", "Fighter"),
        Fx.Entry("equipment", "rope", "Rope"),
        Fx.Entry("monster", "wyrm", "Wyrm"),
        Fx.Entry("hazard", "known", "Known Pit"));

    private static List<Issue> Run(IConsistencyCheck check, WorldSnapshot snapshot) => Fx.Run(check, snapshot);

    private static PlaceView Place(string id, string region, string? parent = null, string? hazard = null, bool homebrewHazard = false, string? creature = null) =>
        new(id, region, "Place " + id, null, null, parent, hazard, homebrewHazard, creature);

    // ---- place tree ----

    [Fact]
    public void ThePlaceTree_ReportsAMissingParent_ASelfParent_ACrossRegionParent_AndOneIssuePerLoop()
    {
        var places = new[]
        {
            Place("p-a", "regions/1", parent: "p-b"), Place("p-b", "regions/1", parent: "p-a"), // a loop
            Place("p-c", "regions/1", parent: "p-c"),
            Place("p-d", "regions/1", parent: "p-zzz"),
            Place("p-e", "regions/1", parent: "p-x"), Place("p-x", "regions/2"),
            Place("p-f", "regions/1", parent: "p-a"), // hangs from the loop, but is not part of it
            Place("p-ok", "regions/1")
        };

        var issues = Run(new PlaceTreeCheck(), Fx.Snap(places: places));

        Assert.Equal(new[]
        {
            "place-tree:place:p-a:parentId:cycle",
            "place-tree:place:p-c:parentId:self",
            "place-tree:place:p-d:parentId:missing:p-zzz",
            "place-tree:place:p-e:parentId:cross-region:p-x"
        }, issues.Select(i => i.Id));
        Assert.All(issues, i => { Assert.Equal(IssueSeverity.Error, i.Severity); Assert.Equal(FixVia.Place, i.FixVia); Assert.Equal("place", i.EntityKind); });
        Assert.Equal("Consistency.PlaceTreeCycle", issues[0].Code);
        Assert.Contains("Place p-a", issues[0].Args[0]);
        Assert.Contains("Place p-b", issues[0].Args[0]);
    }

    [Fact]
    public void ATidyPlaceTree_HasNoIssues()
    {
        var places = new[] { Place("p-1", "regions/1"), Place("p-2", "regions/1", parent: "p-1"), Place("p-3", "regions/1", parent: "p-2") };
        Assert.Empty(Run(new PlaceTreeCheck(), Fx.Snap(places: places)));
    }

    // ---- duplicate slugs and titles ----

    [Fact]
    public void PagesWithOneSlug_AreWarned_PagesWithNearlyOneTitle_AreInfo_AndNeitherIsReportedTwice()
    {
        var pages = new[]
        {
            Fx.Page("wikipages/1-A", "Hall"), Fx.Page("wikipages/2-A", "Hall"),
            Fx.Page("wikipages/3-A", "The Keep"), Fx.Page("wikipages/4-A", "Keep"),
            Fx.Page("wikipages/5-A", "Unique")
        };

        var issues = ConsistencyChecker.Run(Fx.Snap(pages: pages), new IConsistencyCheck[] { new DuplicateTitleCheck() });

        Assert.Equal(new[]
        {
            "duplicate-slug:page:wikipages/1-A:slug:hall",
            "duplicate-slug:page:wikipages/2-A:slug:hall",
            "near-duplicate-title:page:wikipages/3-A:title:keep",
            "near-duplicate-title:page:wikipages/4-A:title:keep"
        }, issues.Select(i => i.Id));
        Assert.Equal(new[] { IssueSeverity.Warning, IssueSeverity.Warning, IssueSeverity.Info, IssueSeverity.Info }, issues.Select(i => i.Severity));
        Assert.All(issues, i => Assert.Equal(FixVia.ReportOnly, i.FixVia));
    }

    // ---- fact lists ----

    [Fact]
    public void ALoreList_NamingAThingThatIsGone_AndTheTextNoLongerMentions_IsReported_AMentionedOneIsTheMentionChecks()
    {
        var fact = new FactView("facts/1", "A secret.", "See [[actor:actors/gone]].",
            new[] { "actor:actors/gone", "actor:actors/old", "place:p-1", "place:p-old", "item:class/ghost", "item:class/fighter", "wiki:some-page" },
            new[] { "actors/1-A", "actors/nope" });
        var snapshot = Fx.Snap(actors: new[] { Fx.Actor("actors/1-A") }, places: new[] { Place("p-1", "regions/1") }, facts: new[] { fact }, system: Fx.System(), library: Library);

        var issues = Run(new FactReferenceCheck(), snapshot);

        Assert.Equal(new[]
        {
            "fact-reference:fact:facts/1:knownBy:actors/nope",
            "fact-reference:fact:facts/1:relatedEntityIds:actor:actors/old",
            "fact-reference:fact:facts/1:relatedEntityIds:item:class/ghost",
            "fact-reference:fact:facts/1:relatedEntityIds:place:p-old"
        }, issues.Select(i => i.Id));
        Assert.All(issues, i => Assert.Equal(FixVia.ReportOnly, i.FixVia));
    }

    // ---- placements ----

    [Fact]
    public void APlacement_WhoseOtherEndIsGone_IsReported_AHomebrewHazardOnlyAgainstTheHomebrewList()
    {
        var places = new[]
        {
            Place("p-1", "regions/1", creature: "actors/ghost"),
            Place("p-2", "regions/1", hazard: "hazard/pit"),
            Place("p-3", "regions/1", hazard: "hazard/mine", homebrewHazard: true),
            Place("p-4", "regions/1", hazard: "hazard/gone", homebrewHazard: true),
            Place("p-5", "regions/1", hazard: "hazard/known")
        };
        var hazardActor = Fx.Actor("actors/h1", role: ActorRole.Monster, hazard: new HazardLinkView("regions/1", "p-lost", "hazard/known", false));
        var onMap = Fx.Actor("actors/h2", role: ActorRole.Monster, hazard: new HazardLinkView("regions/1", "p-5", "hazard/known", false));
        var homebrew = new[] { Fx.Brew("hb/1", "hazard", "mine") };

        var issues = Run(new PlacementCheck(), Fx.Snap(places: places, actors: new[] { hazardActor, onMap }, system: Fx.System(), library: Library, homebrew: homebrew));

        Assert.Equal(new[]
        {
            "dangling-placement:actor:actors/h1:hazard:p-lost",
            "dangling-placement:place:p-1:creatureActorId:actors/ghost",
            "dangling-placement:place:p-2:hazard:hazard/pit",
            "dangling-placement:place:p-4:hazard:hazard/gone"
        }, issues.Select(i => i.Id));

        // With no homebrew read, a homebrew hazard is not judged at all.
        var unread = Run(new PlacementCheck(), Fx.Snap(places: places, system: Fx.System(), library: Library));
        Assert.DoesNotContain(unread, i => i.EntityId is "p-3" or "p-4");
    }

    // ---- owner ----

    [Fact]
    public void ACharacterWhoseOwnerLeftTheCampaign_IsReported_NotTheDmsOrAMembersOrARetiredOne()
    {
        var actors = new[]
        {
            Fx.Actor("actors/1-A", owner: "users/p1"), Fx.Actor("actors/2-A", owner: "users/gone"), Fx.Actor("actors/3-A", owner: "users/dm"),
            Fx.Actor("actors/4-A", owner: "users/gone", retired: true), Fx.Actor("actors/5-A")
        };
        var issues = Run(new OwnerMembershipCheck(), Fx.Snap(actors: actors, campaign: Fx.Campaign()));

        var issue = Assert.Single(issues);
        Assert.Equal("owner-not-member:actor:actors/2-A:ownerUserId:users/gone", issue.Id);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        // Without a campaign to compare with, nothing is said.
        Assert.Empty(Run(new OwnerMembershipCheck(), Fx.Snap(actors: actors)));
    }

    // ---- homebrew ----

    [Fact]
    public void Homebrew_NamingAMissingEntry_OrSharingAnId_IsReported_AndOnlyWhenTheHomebrewWasRead()
    {
        var brews = new[]
        {
            Fx.Brew("hb/1", "spell", "fireball", "Fireball", null, ("class", "ghost")),
            Fx.Brew("hb/2", "spell", "fine", "Fine", null, ("class", "fighter")),
            Fx.Brew("hb/3", "spell", "dup", "Dup One"), Fx.Brew("hb/4", "spell", "Dup", "Dup Two")
        };

        var issues = Run(new HomebrewChecks(), Fx.Snap(system: Fx.System(), library: Library, homebrew: brews));

        Assert.Equal(new[]
        {
            "homebrew-duplicate:homebrew:spell/Dup:entryId:hb-4",
            "homebrew-duplicate:homebrew:spell/dup:entryId:hb-3",
            "homebrew-reference:homebrew:spell/fireball:class:ghost"
        }, issues.Select(i => i.Id));
        Assert.Equal(new[] { FixVia.ReportOnly, FixVia.ReportOnly, FixVia.Homebrew }, issues.Select(i => i.FixVia));
        Assert.Empty(Run(new HomebrewChecks(), Fx.Snap(system: Fx.System(), library: Library)));
    }

    [Fact]
    public void AHomebrewIssue_IsResolvedByAnEditOfThatEntry_AndOnlyThatEntry()
    {
        var brews = new[] { Fx.Brew("hb/1", "spell", "fireball", "Fireball", null, ("class", "ghost")), Fx.Brew("hb/2", "spell", "other", "Other", null, ("class", "ghost")) };
        var open = ConsistencyChecker.Run(Fx.Snap(system: Fx.System(), library: Library, homebrew: brews));
        var issue = open.Single(i => i.EntityId == "spell/fireball");
        Assert.Equal("homebrew", IssueListing.FixViaName(issue.FixVia));

        var rules = new Codex.Core.WorldBuilder.ProposalRules(new Dictionary<string, string>(), new Dictionary<string, string>(), Array.Empty<string>(), _ => Array.Empty<Codex.Core.AI.FieldSpec>(),
            ExistingHomebrew: new Dictionary<string, string> { ["spell/fireball"] = "Fireball", ["spell/other"] = "Other" });
        string Edit(string key) => $$"""{"entries": [{"ref": "h", "kind": "edit", "target": "existing:homebrew:{{key}}", "text": "New rules.", "resolves": ["{{issue.Id}}"]}]}""";

        var ok = Codex.Core.WorldBuilder.WorldBundleParser.ParseProposal(Edit("spell/fireball"), rules, open);
        Assert.True(ok.Ok, string.Join("; ", ok.Errors));
        var wrong = Codex.Core.WorldBuilder.WorldBundleParser.ParseProposal(Edit("spell/other"), rules, open);
        Assert.False(wrong.Ok);
        Assert.Contains("changes homebrew spell/other", string.Join(" ", wrong.Errors));
    }

    [Fact]
    public void AHazardWhoseScaledRowNamesAFieldItDoesNotHave_IsReported_WithTheEditorsOwnWords()
    {
        var rows = new List<Dictionary<string, object>> { new() { ["levels"] = "5-10", ["bogus"] = 3L } };
        var brews = new[] { Fx.Brew("hb/1", "hazard", "pit", "Pit", null, ("higherLevels", rows)), Fx.Brew("hb/2", "hazard", "fine", "Fine") };

        var issue = Assert.Single(Run(new HazardShapeCheck(), Fx.Snap(system: Fx.System(), library: Library, homebrew: brews)));

        Assert.Equal(CheckNames.HazardShape, issue.Check);
        Assert.Equal("hazard/pit", issue.EntityId);
        Assert.Equal(FixVia.Homebrew, issue.FixVia);
        Assert.Contains("'bogus'", issue.Message);
        Assert.StartsWith("hazard-shape:homebrew:hazard/pit:properties:", issue.Id);
    }

    // ---- tags ----

    [Fact]
    public void ATagWrittenInTwoCases_IsReportedOnTheLessUsedSpelling_AcrossPagesAndHomebrew()
    {
        var pages = new[]
        {
            Fx.Page("wikipages/1-A", "One", tags: new[] { "Harbor" }), Fx.Page("wikipages/2-A", "Two", tags: new[] { "harbor" }),
            Fx.Page("wikipages/3-A", "Three", tags: new[] { "harbor", "port" })
        };
        var brews = new[] { Fx.Brew("hb/1", "spell", "wave", "Wave", new[] { "HARBOR" }) };

        var issues = Run(new TagCaseCheck(), Fx.Snap(pages: pages, homebrew: brews));

        Assert.Equal(new[] { "tag-case:homebrew:spell/wave:tags:harbor", "tag-case:page:wikipages/1-A:tags:harbor" }, issues.Select(i => i.Id));
        Assert.Equal(new[] { FixVia.Homebrew, FixVia.ReportOnly }, issues.Select(i => i.FixVia));
        Assert.Equal(new[] { "harbor" }, issues.Select(i => i.Args[1]).Distinct());
    }

    // ---- the campaign ----

    [Fact]
    public void ASystemThatIsNotInstalled_IsOneError_NothingElseOfTheCampaignIsSaid()
    {
        var issues = Run(new CampaignHealthCheck(), Fx.Snap(campaign: Fx.Campaign("ghost-system", installed: false, changes: new[] { "x changed" })));
        var issue = Assert.Single(issues);
        Assert.Equal("system-unknown:campaign:c1:systemId:ghost-system", issue.Id);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal(FixVia.ReportOnly, issue.FixVia);
    }

    [Fact]
    public void ADriftedSystem_AnUnknownPack_AndLoadProblems_AreReported()
    {
        var campaign = Fx.Campaign(namedPacks: new[] { "core", "ghost-pack" }, knownPacks: new[] { "core" }, changes: new[] { "version 1 to 2", "system.yaml changed" },
            problems: new[] { "Pack 'x': bad file", "Pack 'x': bad file" });

        var issues = Run(new CampaignHealthCheck(), Fx.Snap(campaign: campaign));

        Assert.Equal(new[] { "system-drift:campaign:c1:systemStamp", "pack-drift:campaign:c1:packs:ghost-pack", "pack-problem:campaign:c1:load:pack-x-bad-file" },
            issues.Select(i => i.Id).OrderBy(i => i == "system-drift:campaign:c1:systemStamp" ? 0 : i.StartsWith("pack-drift") ? 1 : 2));
        Assert.Contains("2 change(s)", issues.Single(i => i.Check == CheckNames.SystemDrift).Message);
        Assert.Empty(Run(new CampaignHealthCheck(), Fx.Snap(campaign: Fx.Campaign())));
    }

    // ---- unresolved Library references ----

    [Fact]
    public void ALibraryEntryTheCampaignNames_ThatIsGone_IsReported_ForACreaturesSourceGearAndTheBestiary()
    {
        var actors = new[]
        {
            Fx.Actor("actors/1-A", role: ActorRole.NonPlayerCharacter, blueprint: "demo:ghost"),
            Fx.Actor("actors/2-A", role: ActorRole.NonPlayerCharacter, blueprint: "demo:wyrm"),
            Fx.Actor("actors/3-A", sheet: new Dictionary<string, object> { ["inventory"] = new List<object> { "rope|equipped:1", "ghost-item" } })
        };
        var campaign = Fx.Campaign(bestiary: new[] { new BestiaryView("monster", "wyrm", false, "Wyrm"), new BestiaryView("monster", "ghost", false, "Ghost"),
            new BestiaryView("monster", "mine", true, "Mine"), new BestiaryView("monster", "lost", true, "Lost") });
        var brews = new[] { Fx.Brew("hb/1", "monster", "mine") };

        // Where the sheet rules run they already name a missing item, so only the creature's source and the Bestiary are here.
        var withRules = Run(new LibraryReferenceCheck(), Fx.Snap(actors: actors, system: Fx.System(), library: Library, campaign: campaign, homebrew: brews));
        Assert.Equal(new[]
        {
            "library-reference:campaign:c1:bestiary:monster-ghost",
            "library-reference:campaign:c1:bestiary:monster-lost",
            "library-reference:actor:actors/1-A:blueprintId:demo-ghost" // an info sorts after the warnings
        }, withRules.Select(i => i.Id));

        // With the rules off the gear is this check's to report.
        var rulesOff = Run(new LibraryReferenceCheck(), Fx.Snap(actors: actors, system: Fx.System(), library: Library, campaign: campaign, homebrew: brews, enforcement: "off"));
        Assert.Contains("library-reference:actor:actors/3-A:inventory:ghost-item", rulesOff.Select(i => i.Id));
        Assert.DoesNotContain(rulesOff, i => i.Id.EndsWith("rope"));

        // Homebrew not read: a homebrew Bestiary entry is not judged.
        var unread = Run(new LibraryReferenceCheck(), Fx.Snap(actors: actors, system: Fx.System(), library: Library, campaign: campaign));
        Assert.DoesNotContain(unread, i => i.Id.EndsWith("monster-lost"));
    }

    // ---- noise: off by default ----

    private static (List<PageView> Pages, List<ActorView> Actors, List<PlaceView> Places) NoisyWorld() => (
        new List<PageView>
        {
            Fx.Page("wikipages/1-A", "Index", "See [[Linked]]."),
            Fx.Page("wikipages/2-A", "Linked", "Mentions [[actor:actors/dead]]."),
            Fx.Page("wikipages/3-A", "Lonely"),
            Fx.Page("wikipages/4-A", "Tam", kind: "person"),
            Fx.Page("wikipages/5-A", "Aria Page", actorId: "actors/1-A")
        },
        new List<ActorView> { Fx.Actor("actors/1-A"), Fx.Actor("actors/dead", retired: true) },
        new List<PlaceView>());

    private static WorldSnapshot Noisy(bool reversed = false)
    {
        var (pages, actors, places) = NoisyWorld();
        if (reversed) { pages.Reverse(); actors.Reverse(); }
        return Fx.Snap(pages, actors, places);
    }

    [Fact]
    public void TheNoisyChecks_FindWhatTheyAreFor()
    {
        var issues = ConsistencyChecker.Run(Noisy(), ConsistencyChecker.Noisy);

        Assert.Equal(new[]
        {
            "backlink-mismatch:page:wikipages/5-A:actorId:actors/1-A",
            "orphan-page:page:wikipages/1-A:links",
            "orphan-page:page:wikipages/3-A:links",
            "orphan-page:page:wikipages/4-A:links",
            "person-page-no-actor:page:wikipages/4-A:actorId",
            "retired-mention:page:wikipages/2-A:body:actors/dead"
        }.OrderBy(i => i, StringComparer.Ordinal), issues.Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal));
        Assert.All(issues, i => Assert.Equal(IssueSeverity.Info, i.Severity));
    }

    [Fact]
    public void TheNoisyChecks_AreNeverInTheDefaultRun_NorInAnyDefaultFilter()
    {
        Assert.Empty(ConsistencyChecker.Default.Select(c => c.Name).Intersect(CheckNames.Noise));
        Assert.Equal(CheckNames.Noise.OrderBy(n => n), ConsistencyChecker.Noisy.Select(c => c.Name).OrderBy(n => n));

        var snapshot = Noisy();
        var byDefault = ConsistencyChecker.Run(snapshot);
        Assert.DoesNotContain(byDefault, i => CheckNames.Noise.Contains(i.Check));

        var all = ConsistencyChecker.Run(snapshot, ConsistencyChecker.For(new IssueFilter("noise")));
        Assert.Contains(all, i => i.Check == CheckNames.OrphanPage);
        // "all" and no scope list only what the default run found; the noise scope lists only the noise.
        Assert.Equal(byDefault.Select(i => i.Id), new IssueFilter("all").Apply(byDefault).Select(i => i.Id));
        Assert.Same(ConsistencyChecker.Default, ConsistencyChecker.For(new IssueFilter("all")));
        Assert.Same(ConsistencyChecker.Default, ConsistencyChecker.For(new IssueFilter("page:wikipages/1-A")));
        Assert.All(new IssueFilter("noise").Apply(all), i => Assert.Contains(i.Check, CheckNames.Noise));
        Assert.DoesNotContain(new IssueFilter("noise").Apply(all), i => !CheckNames.Noise.Contains(i.Check));
        Assert.True(new IssueFilter("orphan-page").WantsNoise);
        Assert.False(new IssueFilter("broken-link").WantsNoise);
        Assert.True(IssueFilter.IsKnownScope("noise"));
        Assert.True(IssueFilter.IsKnownScope("homebrew:spell/x"));
        Assert.True(IssueFilter.IsKnownScope("campaign:c1"));
    }

    // ---- determinism of the whole v2 set ----

    private static WorldSnapshot V2World(bool reversed = false)
    {
        var pages = new List<PageView>
        {
            Fx.Page("wikipages/1-A", "Hall", tags: new[] { "Harbor" }), Fx.Page("wikipages/2-A", "Hall", tags: new[] { "harbor" }), Fx.Page("wikipages/3-A", "The Keep"), Fx.Page("wikipages/4-A", "Keep")
        };
        var places = new List<PlaceView>
        {
            Place("p-a", "regions/1", parent: "p-b"), Place("p-b", "regions/1", parent: "p-a"), Place("p-1", "regions/1", creature: "actors/ghost", hazard: "hazard/pit")
        };
        var actors = new List<ActorView> { Fx.Actor("actors/1-A", owner: "users/gone", role: ActorRole.NonPlayerCharacter, blueprint: "demo:ghost") };
        var facts = new List<FactView> { new("facts/1", "S.", null, new[] { "place:p-old" }, new[] { "actors/nope" }) };
        var brews = new List<HomebrewView> { Fx.Brew("hb/1", "spell", "a", "A", null, ("class", "ghost")), Fx.Brew("hb/2", "spell", "a", "A2") };
        if (reversed) { pages.Reverse(); places.Reverse(); brews.Reverse(); }
        return Fx.Snap(pages, actors, places, facts, Fx.System(), Library, campaign: Fx.Campaign(changes: new[] { "x changed" }, problems: new[] { "bad" }, namedPacks: new[] { "g" }, knownPacks: new[] { "core" }), homebrew: brews);
    }

    [Fact]
    public void EveryV2Check_IsTrippedByTheV2World_AndTheWholeList_IsTheSameOnEveryRun_InAnyDocumentOrder()
    {
        var first = ConsistencyChecker.Run(V2World(), ConsistencyChecker.Default);
        var found = first.Select(i => i.Check).Distinct().ToList();
        // The checks that cannot share a world with another: an uninstalled system hides the campaign's other findings, and a retired owner is skipped.
        Assert.Empty(CheckNames.V2.Except(found).Except(new[] { CheckNames.CampaignSystem, CheckNames.HazardShape }));

        Assert.Equal(first.Select(Fx.Flat), ConsistencyChecker.Run(V2World(), ConsistencyChecker.Default).Select(Fx.Flat));
        Assert.Equal(first.Select(Fx.Flat), ConsistencyChecker.Run(V2World(reversed: true), ConsistencyChecker.Default).Select(Fx.Flat));
        Assert.Equal(first.Select(Fx.Flat), ConsistencyChecker.Run(V2World(), ConsistencyChecker.Default.Reverse().ToList()).Select(Fx.Flat));
        foreach (var check in ConsistencyChecker.Default.Concat(ConsistencyChecker.Noisy))
        {
            var snapshot = V2World();
            Assert.Equal(check.Run(snapshot).Select(Fx.Flat), check.Run(snapshot).Select(Fx.Flat));
        }

        Assert.Equal(first.Count, first.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(first, i => System.Text.RegularExpressions.Regex.IsMatch(i.Message + i.FixHint, @"\b\d{4}-\d{2}-\d{2}\b|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}"));
    }

    [Fact]
    public void EveryCheckName_IsListedOnce_AndEveryIssueOfTheV2World_NamesAKnownCheck()
    {
        Assert.Equal(CheckNames.All.Count, CheckNames.All.Distinct().Count());
        var issues = ConsistencyChecker.Run(V2World(), ConsistencyChecker.Default.Concat(ConsistencyChecker.Noisy).ToList());
        Assert.All(issues, i => Assert.Contains(i.Check, CheckNames.All));
        Assert.All(issues, i => Assert.Contains(i.EntityKind, new[] { "page", "actor", "place", "fact", "homebrew", "campaign" }));
        Assert.All(issues, i => Assert.True(IssueFilter.IsKnownScope($"{i.EntityKind}:{i.EntityId}")));
    }
}
