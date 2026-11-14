using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>Links between sheets (docs/CREW_LINKS_DESIGN.md, CL1: the engine). A <c>member</c> sheet links to a <c>group</c> sheet, and the group's
/// entries carry <c>members</c> grants that reach each member. The system is a fixture and has no game's names in it.</summary>
public class CrewLinksTests
{
    private sealed class Entries(params ContentEntry[] entries) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => entries.Where(e => e.Type == type);
    }

    /// <summary>The actors' sheets by id; an id it does not have is an actor that is gone.</summary>
    private sealed class Actors(params (string Id, Dictionary<string, object> Sheet)[] sheets) : ILinkedSheets
    {
        public int Reads { get; private set; }

        public IReadOnlyDictionary<string, object>? Sheet(string actorId)
        {
            Reads++;
            return sheets.FirstOrDefault(s => s.Id == actorId).Sheet;
        }
    }

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    private static ContentEntry Entry(string type, string id, object members, params (string Key, object Value)[] own)
    {
        var entry = new ContentEntry { Type = type, Id = id, Name = char.ToUpperInvariant(id[0]) + id[1..], SystemId = "links", PackId = "test", Description = id + " text." };
        entry.Properties["members"] = members;
        foreach (var (key, value) in own) entry.Properties[key] = value;
        return entry;
    }

    private static Dictionary<string, object> Modifier(string target, long amount) => Map(("target", target), ("amount", amount));

    private static readonly ContentEntry[] All =
    {
        Entry("upgrade", "composed", Map(("modifiers", Items(Modifier("stressBoxes", 1))))),
        Entry("upgrade", "hardened", Map(("modifiers", Items(Modifier("traumaBoxes", 1))))),
        Entry("upgrade", "mastery", Map(("modifiers", Items(Modifier("actionCap", 1))))),
        Entry("ability", "deadly", Map(("adds", Items(Map(("to", "actions"), ("choice", "deadly"), ("choose", 1L), ("from", Items("hunt", "prowl", "skirmish"))))))),
        Entry("ability", "resolve", Map(("uses", Map(("key", "resolve"), ("max", 2L), ("recoversOn", Items("Long Rest")))))),
        Entry("ability", "anointed", Map(("modifiers", Items(Map(("target", "stressBoxes"), ("amount", 1L), ("optIn", Map(("label", "Against a threat")))))))),
        Entry("ability", "buddy", Map(("modifiers", Items(Modifier("rank", 10))))),
        Entry("ability", "own-trick", Map(), ("modifiers", Items(Modifier("stressBoxes", 100))))
    };

    private const string Yaml = """
        id: links
        name: Links
        entityTypes:
          - { type: ability, label: Ability }
          - { type: upgrade, label: Upgrade }
        sheetKinds:
          - kind: member
            links:
              - { key: group, kind: group, label: Group }
            features:
              - { list: abilities }
              - { link: group, list: abilities, property: members }
              - { link: group, list: upgrades, property: members }
            values:
              - { key: tier, input: true, default: 1 }
              - { key: actionCap, formula: "3", modifiers: actionCap }
              - { key: groupRank, formula: "link('group', 'rank')" }
            lists:
              - { key: abilities, label: Abilities, type: ability }
            keyedLists:
              - key: actions
                label: Actions
                itemFields: [{ key: rating }]
                keys: [{ key: hunt, label: Hunt }, { key: prowl, label: Prowl }, { key: skirmish, label: Skirmish }]
                rules:
                  - { check: "max(actions, a => a.rating, 0) <= actionCap", key: Actor.Rule.ActionCap, message: "an action rating is above {actionCap}." }
            tracks:
              - { key: stress, max: "9 + modifiers('stressBoxes')" }
              - key: trauma
                max: "4 + modifiers('traumaBoxes')"
                thresholds: [{ atOrAbove: "4 + modifiers('traumaBoxes')", condition: Retired }]
          - kind: group
            links:
              - { key: member, kind: member, label: Member }
            features:
              - { link: member, list: abilities, property: members }
            values:
              - { key: rank, formula: "2 + link('member', 'tier')", modifiers: rank }
            lists:
              - { key: abilities, label: Abilities, type: ability }
              - { key: upgrades, label: Upgrades, type: upgrade }
        """;

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse(Yaml);

    private static SheetRuleEngine Engine() => new(System, new Entries(All));

    private static SheetModel Model(Dictionary<string, object> sheet, ILinkedSheets? linked = null) => new(System, new Entries(All), sheet, linked);

    private static Dictionary<string, object> Group(string[] upgrades, params string[] abilities) => Map(("kind", "group"), ("upgrades", Items(upgrades.Cast<object>().ToArray())), ("abilities", Items(abilities.Cast<object>().ToArray())));

    private static Dictionary<string, object> Member(params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object> { ["kind"] = "member", ["link__group"] = "g1" };
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    private static Actors Crew(string[] upgrades, params string[] abilities) => new(("g1", Group(upgrades, abilities)));

    private static string[] None => Array.Empty<string>();

    // ---- Load ----

    private static string[] Errors(string yaml) => SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(yaml))).ToArray();

    [Fact]
    public void TheFixture_LoadsClean()
    {
        Assert.Empty(SystemDefinitionLoader.Validate(System));
        Assert.Equal("group", System.SheetKinds[0].Links.Single().Key);
        Assert.Equal("group", System.SheetKinds[0].Features[1].Link);
    }

    [Fact]
    public void TheLoader_RefusesALinkToAnUnknownKind_ADuplicateKey_AndAnEmptyOne()
    {
        Assert.Contains(Errors(Yaml.Replace("{ key: group, kind: group, label: Group }", "{ key: group, kind: nowhere, label: Group }", StringComparison.Ordinal)),
            e => e.Contains("'nowhere' is not one of the system's sheet kinds"));
        Assert.Contains(Errors(Yaml.Replace("- { key: group, kind: group, label: Group }", "- { key: group, kind: group }\n      - { key: group, kind: group }", StringComparison.Ordinal)),
            e => e.Contains("the key is used twice"));
        Assert.Contains(Errors(Yaml.Replace("{ key: group, kind: group, label: Group }", "{ key: group, label: Group }", StringComparison.Ordinal)), e => e.Contains("needs the kind"));
    }

    [Fact]
    public void TheLoader_RefusesALinkSourceNamingAnUnknownLink_AListTheLinkedKindLacks_APlainRecordList_AndNoProperty()
    {
        Assert.Contains(Errors(Yaml.Replace("{ link: group, list: upgrades", "{ link: nowhere, list: upgrades", StringComparison.Ordinal)), e => e.Contains("'nowhere' is not one of the kind's links"));
        Assert.Contains(Errors(Yaml.Replace("{ link: group, list: upgrades", "{ link: group, list: relics", StringComparison.Ordinal)), e => e.Contains("'relics' is not one of the lists of 'group'"));
        Assert.Contains(Errors(Yaml.Replace("{ link: group, list: upgrades, property: members }", "{ link: group, list: upgrades }", StringComparison.Ordinal)), e => e.Contains("needs a property"));
        Assert.Contains(Errors(Yaml.Replace("{ key: upgrades, label: Upgrades, type: upgrade }", "{ key: upgrades, label: Upgrades }", StringComparison.Ordinal)), e => e.Contains("plain records"));
        Assert.Contains(Errors(Yaml.Replace("{ link: group, list: upgrades, property: members }", "{ link: group, property: members }", StringComparison.Ordinal)), e => e.Contains("needs the list"));
        Assert.Contains(Errors(Yaml.Replace("{ link: group, list: upgrades, property: members }", "{ link: group, list: upgrades, property: members, choose: pick }", StringComparison.Ordinal)),
            e => e.Contains("choose is not read on a link source"));
    }

    [Fact]
    public void TheLoader_RefusesALinkFunctionNamingALinkTheKindLacks()
    {
        Assert.Contains(Errors(Yaml.Replace("link('group', 'rank')", "link('crew', 'rank')", StringComparison.Ordinal)), e => e.Contains("link('crew', ...) names a link the kind does not have"));
        Assert.Contains(Errors(Yaml.Replace("\"9 + modifiers('stressBoxes')\"", "\"9 + link('nope', 'x')\"", StringComparison.Ordinal)), e => e.Contains("link('nope'"));
    }

    // ---- Members ----

    [Fact]
    public void AMemberOfAGroupHoldingAStressBoxUpgrade_HasTheBiggerTrack_AndUnlinkedItDoesNot()
    {
        var linked = Model(Member(), Crew(new[] { "composed" })).Evaluate();
        Assert.Equal(10d, linked.Values["stress_Max"]);

        Assert.Equal(9d, Model(Member(), Crew(None)).Evaluate().Values["stress_Max"]);
        var unlinked = Model(new Dictionary<string, object> { ["kind"] = "member" }, Crew(new[] { "composed" })).Evaluate();
        Assert.Equal(9d, unlinked.Values["stress_Max"]);
    }

    [Fact]
    public void ALinkFeature_IsNamedForTheEntryAndTheLinksLabel_AndOnlyTheLinkedSheetsPicksAreRead()
    {
        var sheet = Member(("abilities", Items("own-trick")));
        var features = Engine().ActiveFeatures(sheet, Crew(new[] { "composed" }, "resolve"));

        Assert.Equal(new[] { "Own-trick", "Resolve (Group)", "Composed (Group)" }, features.Select(f => f.Name).OrderBy(n => n == "Own-trick" ? 0 : n.StartsWith("Resolve") ? 1 : 2));
        Assert.Equal(new string?[] { null, "group", "group" }, features.Select(f => f.Link));
        Assert.All(features, f => Assert.True(f.OnKind));
        // The member's own entry carries a `members` grant of nothing, and the group's own picks are not the member's.
        Assert.DoesNotContain(features, f => f.Name == "Own-trick (Group)");
    }

    [Fact]
    public void ALinkedSheetOfAnotherKind_BringsNothing()
    {
        var wrong = new Actors(("g1", Map(("kind", "member"), ("upgrades", Items("composed")))));

        Assert.Empty(Engine().ActiveFeatures(Member(), wrong));
        Assert.Equal(9d, Model(Member(), wrong).Evaluate().Values["stress_Max"]);
    }

    [Fact]
    public void Hardened_MovesTheTraumaSizeAndTheRetiredThreshold()
    {
        var sheet = Member(("trauma", 4L));

        var plain = Model(sheet, Crew(None)).Evaluate();
        Assert.Equal(4d, plain.Values["trauma_Max"]);
        Assert.Contains("Retired", plain.Conditions);

        var hardened = Model(sheet, Crew(new[] { "hardened" })).Evaluate();
        Assert.Equal(5d, hardened.Values["trauma_Max"]);
        Assert.DoesNotContain("Retired", hardened.Conditions);
        Assert.Contains("Retired", Model(Member(("trauma", 5L)), Crew(new[] { "hardened" })).Evaluate().Conditions);
    }

    [Fact]
    public void Mastery_RaisesTheActionCapARuleReads()
    {
        var sheet = Member(("actions", Map(("hunt", Map(("rating", 4L))))));

        Assert.Contains(Engine().Evaluate(sheet, Crew(None)).Violations, v => v.Key == "Actor.Rule.ActionCap");
        Assert.DoesNotContain(Engine().Evaluate(sheet, Crew(new[] { "mastery" })).Violations, v => v.Key == "Actor.Rule.ActionCap");
        Assert.Equal(4d, Model(sheet, Crew(new[] { "mastery" })).Evaluate().Values["actionCap"]);
    }

    [Fact]
    public void ADeadlyStyleChoice_IsStoredOnEachMembersSheet_UnderTheCrewEntrysId()
    {
        var crew = Crew(None, "deadly");
        var first = Member(("choice__deadly__deadly", Items("hunt")));
        var second = Member(("choice__deadly__deadly", Items("prowl")));
        var none = Member();

        var a = Engine().Evaluate(first, crew);
        var b = Engine().Evaluate(second, crew);
        var c = Engine().Evaluate(none, crew);

        Assert.Equal(new[] { "hunt" }, Assert.Single(a.MadeChoices, x => x.Key == "choice__deadly__deadly").Made);
        Assert.Equal(new[] { "prowl" }, Assert.Single(b.MadeChoices, x => x.Key == "choice__deadly__deadly").Made);
        Assert.Contains(c.PendingChoices, x => x.Key == "choice__deadly__deadly");
        Assert.Equal("Deadly (Group)", a.MadeChoices.Single().Feature?.Name);
    }

    [Fact]
    public void ALinkMadeCounter_IsTheMembersOwn_AndStartsFull()
    {
        var sheet = Member();

        var evaluation = Engine().Recompute(sheet, Crew(None, "resolve"));

        Assert.Equal(2L, sheet["uses_resolve"]);
        Assert.Equal(2L, sheet["uses_resolve_Max"]);
        Assert.Equal("Resolve (Group)", sheet["uses_resolve_Label"]);
        Assert.Single(evaluation.Counters);
    }

    [Fact]
    public void ALinkOptIn_HasTheLinkId_AndNeverCountsOnTheSheet()
    {
        var crew = Crew(None, "anointed");
        var model = Model(Member(), crew);

        Assert.Equal(9d, model.Evaluate().Values["stress_Max"]);
        var offered = Assert.Single(model.OptIns(new[] { "stressBoxes" }));
        Assert.Equal(OptInIds.Link("group", "anointed", 0), offered.Id);
        Assert.Equal("Against a threat", offered.Label);
    }

    // ---- link() ----

    [Fact]
    public void LinkReadsTheLinkedSheetsWorkedOutValue_AndIsZeroWithNoLinkAnUnknownLinkOrAMissingActor()
    {
        var crew = Crew(None, "buddy"); // not the group's own list: the group reads its members' picks, not its own
        var withLink = Model(Member(), crew).Evaluate();
        Assert.Equal(2d, withLink.Values["groupRank"]); // the nested group sheet has no resolver: link('member', ...) is 0 there

        Assert.Equal(0d, Model(new Dictionary<string, object> { ["kind"] = "member" }, crew).Evaluate().Values["groupRank"]);
        Assert.Equal(0d, Model(Member()).Evaluate().Values["groupRank"]); // unknown: no resolver
        Assert.Equal(0d, Model(Member(), new Actors()).Evaluate().Values["groupRank"]); // the actor is gone
    }

    [Fact]
    public void Links_AreOneStepDeep_WhenBothKindsLinkToEachOther()
    {
        var member = Member(("abilities", Items("buddy")));
        var group = Map(("kind", "group"), ("link__member", "m1"), ("upgrades", Items()), ("abilities", Items()));
        var actors = new Actors(("g1", group), ("m1", member));

        // The group reads its member's picks and the member's tier; the member reads the group, which is not worked out through its own link.
        Assert.Equal(13d, Model(group, actors).Evaluate().Values["rank"]); // 2 + tier 1 + buddy's 10
        Assert.Equal(2d, Model(member, actors).Evaluate().Values["groupRank"]);
        Assert.DoesNotContain(Engine().ActiveFeatures(member, actors), f => f.Name.Contains("(Member)"));
        Assert.Contains(Engine().ActiveFeatures(group, actors), f => f.Name == "Buddy (Member)");
    }

    // ---- A link to a missing actor, and the unknown-link rule ----

    private static Dictionary<string, object> StoredMember() => Member(
        ("stress", 10L), ("stress_Max", 10L), ("trauma", 0L),
        ("choice__deadly__deadly", Items("hunt")),
        ("uses_resolve", 1L), ("uses_resolve_Max", 2L), ("uses_resolve_Label", "Resolve (Group)"),
        ("actions", Map(("hunt", Map(("rating", 4L))))));

    [Fact]
    public void ALinkToAMissingActor_IsUnknown_SoNothingTheLinkMadeIsSwept()
    {
        var sheet = StoredMember();
        var gone = new Actors();

        Assert.Equal(LinkState.Unknown, SheetLinks.State(sheet, "group", gone));
        Assert.Empty(Engine().ActiveFeatures(sheet, gone));
        var evaluation = Engine().Recompute(sheet, gone);
        Engine().PruneChoices(sheet, gone);

        Assert.True(evaluation.LinksUnknown);
        AssertKept(sheet);
    }

    /// <summary>What the link made on <see cref="StoredMember"/>: the counter, the Deadly pick and stress past 9.</summary>
    private static void AssertKept(Dictionary<string, object> sheet)
    {
        Assert.Equal(1L, sheet["uses_resolve"]);
        Assert.True(sheet.ContainsKey("choice__deadly__deadly"));
        Assert.Equal(10L, sheet["stress"]);
        Assert.Equal(10L, sheet["stress_Max"]);
    }

    /// <summary>A crew in the trash: it has no sheet to give, and says it was deleted.</summary>
    private sealed class Trash : ILinkedSheets
    {
        public IReadOnlyDictionary<string, object>? Sheet(string actorId) => null;
        public LinkedActorStatus Status(string actorId) => LinkedActorStatus.Deleted;
    }

    [Fact]
    public void ACrewInTheTrash_KeepsTheMembersData_RestoringItCountsItAgain_ClearingTheLinkSweeps()
    {
        var engine = Engine();
        var sheet = StoredMember();
        var trash = new Trash();

        // Saved while the crew is deleted, twice: nothing is lost, and the sheet says why.
        for (var i = 0; i < 2; i++)
        {
            var evaluation = engine.Recompute(sheet, trash);
            engine.PruneChoices(sheet, trash);
            Assert.True(evaluation.LinksUnknown);
            AssertKept(sheet);
        }

        Assert.Equal(LinkedActorStatus.Deleted, SheetLinks.Gone(sheet, "group", trash));

        // Restored: the crew is supplied again and counts as before (stress 10 boxes, the pick and the counter stay).
        var crew = Crew(new[] { "composed" }, "deadly", "resolve");
        var restored = engine.Recompute(sheet, crew);
        engine.PruneChoices(sheet, crew);
        Assert.False(restored.LinksUnknown);
        Assert.Null(SheetLinks.Gone(sheet, "group", crew));
        AssertKept(sheet);

        // The player clears the link: the sheet is evaluated as having no crew, and what the crew made goes.
        sheet.Remove("link__group");
        var cleared = engine.Recompute(sheet, trash);
        engine.PruneChoices(sheet, trash);
        Assert.False(cleared.LinksUnknown);
        Assert.DoesNotContain(sheet.Keys, k => k.StartsWith("uses_", StringComparison.Ordinal));
        Assert.False(sheet.ContainsKey("choice__deadly__deadly"));
        Assert.Equal(9L, sheet["stress_Max"]);
    }

    [Fact]
    public void ChangingTheLinkOfADeletedCrew_EvaluatesTheNewOneAsKnown()
    {
        var engine = Engine();
        var sheet = StoredMember();
        sheet["link__group"] = "g2";
        var other = new Actors(("g2", Group(None)));

        var evaluation = engine.Recompute(sheet, other);
        engine.PruneChoices(sheet, other);

        Assert.False(evaluation.LinksUnknown);
        Assert.False(sheet.ContainsKey("choice__deadly__deadly"));
        Assert.Equal(9L, sheet["stress_Max"]);
    }

    [Fact]
    public void ALinkedActorOfTheWrongKind_StaysKnownAndEmpty()
    {
        var sheet = StoredMember();
        var wrong = new Actors(("g1", Map(("kind", "member"))));

        Assert.Equal(LinkState.Known, SheetLinks.State(sheet, "group", wrong));
        Assert.Empty(Engine().ActiveFeatures(sheet, wrong));
        Assert.False(Engine().Recompute(sheet, wrong).LinksUnknown);
    }

    [Fact]
    public void ASaveWithNoLinkedSheets_KeepsWhatTheLinkOwns_ThenAKnownLinkThatLostTheEntryDropsIt()
    {
        var engine = Engine();
        var sheet = StoredMember();

        // The save path: nothing supplies the linked sheet, so nothing the link could own is swept, pruned or clamped.
        var evaluation = engine.Recompute(sheet);
        engine.PruneChoices(sheet);

        Assert.True(evaluation.LinksUnknown);
        Assert.Equal(new[] { "hunt" }, sheet["choice__deadly__deadly"] is List<object> picks ? picks.Select(p => p.ToString()!) : Array.Empty<string>());
        Assert.Equal(1L, sheet["uses_resolve"]);
        Assert.Equal(2L, sheet["uses_resolve_Max"]);
        Assert.Equal("Resolve (Group)", sheet["uses_resolve_Label"]);
        Assert.Equal(10L, sheet["stress"]);
        Assert.Equal(10L, sheet["stress_Max"]);
        Assert.DoesNotContain(evaluation.Violations, v => v.Key is "Actor.Rule.TrackRange" or "Actor.Rule.ActionCap");

        // The same sheet with the crew supplied and holding the entries: all still there, the counter's spent use kept.
        var crew = Crew(new[] { "composed", "mastery" }, "deadly", "resolve");
        var known = engine.Recompute(sheet, crew);
        engine.PruneChoices(sheet, crew);
        Assert.False(known.LinksUnknown);
        Assert.Equal(1L, sheet["uses_resolve"]);
        Assert.Equal(10L, sheet["stress_Max"]);
        Assert.True(sheet.ContainsKey("choice__deadly__deadly"));
        Assert.Empty(known.Violations.Where(v => v.Key is "Actor.Rule.TrackRange" or "Actor.Rule.ActionCap"));

        // The crew no longer holds them: they go stale as any key does.
        var bare = Crew(None);
        engine.Recompute(sheet, bare);
        engine.PruneChoices(sheet, bare);
        Assert.DoesNotContain(sheet.Keys, k => k.StartsWith("uses_", StringComparison.Ordinal));
        Assert.False(sheet.ContainsKey("choice__deadly__deadly"));
        Assert.Equal(9L, sheet["stress_Max"]);
    }

    [Fact]
    public void WhileALinkIsUnknown_ATrackWhoseMaxReadsModifiersIsNotClamped_AndItsMaxIsNotWritten()
    {
        var sheet = StoredMember();
        var model = Model(sheet);

        Assert.True(model.LinksUnknown);
        Assert.Null(model.TrackBounds("stress")!.Value.Max);
        Assert.DoesNotContain(model.Evaluate().Violations, v => v.Key == "Actor.Rule.TrackRange");

        // An intent against it is not clamped to 9 either.
        var applied = SheetIntents.Apply(System, new Entries(All), sheet, new[] { new SheetIntent("set", null, new Dictionary<string, object?> { ["track"] = "stress", ["to"] = 11L }) });
        Assert.Empty(applied.Left);
        Assert.Equal(11L, sheet["stress"]);

        // With the crew supplied it is the crew's number again.
        Assert.Equal(10d, Model(sheet, Crew(new[] { "composed" })).TrackBounds("stress")!.Value.Max);
        Assert.Equal(9d, Model(sheet, Crew(None)).TrackBounds("stress")!.Value.Max);
    }
}
