using System.Text.Json;
using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Consistency;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Core.WorldBuilder;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using Microsoft.Extensions.AI;

namespace Codex.Tests.Consistency;

/// <summary>
/// The <c>sheet</c> edit target (docs/WORLD_BUILDER.md, 9): the model proposes keys of a character's sheet to set or clear; the proposal passes the
/// same review gate as every edit, is checked against the system and the Library, and can claim the sheet issues it fixes.
/// </summary>
public class SheetEditParserTests
{
    private const string Pc = "actors/1-A";
    private const string Other = "actors/2-A";

    private static ProposalRules Rules() => new(new Dictionary<string, string>(), new Dictionary<string, string>(), Array.Empty<string>(),
        _ => Array.Empty<FieldSpec>(), ExistingActors: new[] { Pc, Other }, System: Fx.System(), ExistingSheets: new[] { Pc, Other });

    private static ProposalCheck Parse(string entry, IReadOnlyList<Issue>? open = null) =>
        WorldBundleParser.ParseProposal($$"""{"entries": [{{entry}}]}""", Rules(), open ?? Array.Empty<Issue>());

    private static string Sheet(string sheet, string target = "existing:sheet:" + Pc, string? resolves = null) =>
        $$"""{"ref": "s1", "kind": "edit", "target": "{{target}}", "sheet": {{sheet}}{{(resolves == null ? "" : $", \"resolves\": [\"{resolves}\"]")}}}""";

    [Fact]
    public void TheEditableKeys_AreThoseTheSystemDeclares_InAFixedOrder_AndNoDerivedValue()
    {
        var keys = SheetProposal.EditableKeys(Fx.System());
        Assert.Equal(new[] { "background", "class", "inventory" }, keys.Select(k => k.Key));
        Assert.Equal(SheetKeyKind.Picks, keys.Single(k => k.Key == "inventory").Kind);
        Assert.True(SheetProposal.IsEditable(Fx.System(), "CLASS"));
        Assert.False(SheetProposal.IsEditable(Fx.System(), "HP_Max"));
        Assert.False(SheetProposal.IsEditable(null, "class"));
    }

    [Fact]
    public void ASheetEdit_SetsAndClearsKeys_AndIsReadAsAnEditOfThatSheet()
    {
        var check = Parse(Sheet("""{"class": "fighter", "Inventory": ["longsword|equipped:1", "rope"], "background": null}"""));

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        var draft = check.Bundle.Drafts.Single();
        Assert.Equal("existing:sheet:" + Pc, draft.Target);
        Assert.Equal(EditTarget.Sheet, WorldBundle.ParseTarget(draft.Target!, out var id));
        Assert.Equal(Pc, id);
        Assert.Equal("\"fighter\"", draft.SheetSet["class"]);
        Assert.Equal("[\"longsword|equipped:1\",\"rope\"]", draft.SheetSet["inventory"]);
        Assert.Equal(new[] { "background" }, draft.SheetClear);
        Assert.Null(draft.Body);
    }

    [Theory]
    [InlineData("""{"HP_Max": 50}""", "not a sheet key a proposal can change")]
    [InlineData("""{"class": 3}""", "takes the id of a Library class entry")]
    [InlineData("""{"inventory": "rope"}""", "takes a list")]
    [InlineData("""{"inventory": [""]}""", "short non-empty string")]
    [InlineData("""{"class": "a", "CLASS": "b"}""", "given twice")]
    [InlineData("""{}""", "changes nothing")]
    [InlineData("""[1]""", "must be an object")]
    public void ABadSheetEdit_RefusesTheProposal_InWords(string sheet, string expected)
    {
        var check = Parse(Sheet(sheet));
        Assert.False(check.Ok);
        Assert.Contains(expected, string.Join(" ", check.Errors));
    }

    [Fact]
    public void ASheetHasNoTextNoRenameNoStatsAndNoDelete_AndNeedsTheSheetObject()
    {
        Assert.Contains("only sets or clears", string.Join(" ", Parse("""{"ref": "s", "kind": "edit", "target": "existing:sheet:actors/1-A", "sheet": {"class": "fighter"}, "text": "New."}""").Errors));
        Assert.Contains("only sets or clears", string.Join(" ", Parse("""{"ref": "s", "kind": "edit", "target": "existing:sheet:actors/1-A", "delete": true}""").Errors));
        Assert.Contains("only sets or clears", string.Join(" ", Parse("""{"ref": "s", "kind": "edit", "target": "existing:sheet:actors/1-A", "sheet": {"class": "fighter"}, "rename": "Bob"}""").Errors));
        Assert.Contains("needs \"sheet\"", string.Join(" ", Parse("""{"ref": "s", "kind": "edit", "target": "existing:sheet:actors/1-A"}""").Errors));
    }

    [Fact]
    public void ACharacterWhoseSheetMayNotBeChanged_OrTwoEditsOfOneSheet_AreRefused()
    {
        var gone = Parse(Sheet("""{"class": "fighter"}""", "existing:sheet:actors/9-Z"));
        Assert.Contains("not a character whose sheet a proposal can change", string.Join(" ", gone.Errors));

        var twice = Parse(Sheet("""{"class": "fighter"}""") + "," + Sheet("""{"class": "wizard"}""").Replace("\"s1\"", "\"s2\""));
        Assert.Contains("already changed by another entry", string.Join(" ", twice.Errors));
    }

    [Fact]
    public void TheDraftsSheetChange_SurvivesBeingStoredWithTheChat()
    {
        var draft = Parse(Sheet("""{"class": "fighter", "background": null}""")).Bundle.Drafts.Single();
        draft.BeforeSheet = new Dictionary<string, string> { ["class"] = "\"rogue\"" };
        var back = JsonSerializer.Deserialize<WorldDraft>(JsonSerializer.Serialize(draft))!;
        Assert.Equal(draft.SheetSet, back.SheetSet);
        Assert.Equal(new[] { "background" }, back.SheetClear);
        Assert.Equal("\"rogue\"", back.BeforeSheet!["class"]);
    }

    // ---- what the checks say is fixable, and what an issue may be resolved by ----

    private static readonly Fx.Entries Library = new(
        Fx.Entry("class", "rogue", "Rogue"),
        Fx.Entry("class", "fighter", "Fighter", ("startingEquipment", "(a) Chain Mail or (b) 155 GP")),
        Fx.Entry("equipment", "chain-mail", "Chain Mail"));

    private static ActorView Aria(string id, string cls) => Fx.Actor(id, sheet: new Dictionary<string, object> { ["class"] = cls });

    private static IReadOnlyList<Issue> Open(params string[] classes) => ConsistencyChecker.Run(Fx.Snap(
        actors: classes.Select((c, i) => Aria($"actors/{i + 1}-A", c)), system: Fx.System(), library: Library));

    [Fact]
    public void ASheetRuleOnAKeyAnEditMaySet_AndAnEmptyInventory_AreFixableBySheet_ADerivedKeyIsNot()
    {
        var open = Open("rogue", "fighter");
        var rule = Assert.Single(open, i => i.Check == CheckNames.SheetRule);
        Assert.Equal(FixVia.Sheet, rule.FixVia);
        var kit = Assert.Single(open, i => i.Check == CheckNames.EmptyInventory);
        Assert.Equal(FixVia.Sheet, kit.FixVia);
        Assert.Equal("sheet", IssueListing.FixViaName(kit.FixVia));
        // The model is told to ask the DM and never to fill the kit in by itself.
        Assert.Contains("never invent or auto-fill gear", kit.FixHint);
        Assert.Contains("Ask the DM", kit.FixHint);
        // A violation on a key no edit can set (a value the sheet computes) is still the DM's.
        var derived = new Fx.Entries(Fx.Entry("class", "rogue", "Rogue"));
        Assert.False(SheetProposal.IsEditable(Fx.System(), "armorClass"));
        Assert.NotEmpty(derived.Candidates("class"));
    }

    [Fact]
    public void ASheetEdit_MayResolveASheetIssueOfThatCharacter_AndOnlyThat()
    {
        var open = Open("rogue", "rogue");
        var first = open.Single(i => i.Check == CheckNames.SheetRule && i.EntityId == "actors/1-A");

        var ok = Parse(Sheet("""{"class": "fighter"}""", resolves: first.Id), open);
        Assert.True(ok.Ok, string.Join("; ", ok.Errors));
        Assert.Equal(new[] { first.Id }, ok.Bundle.Drafts.Single().Resolves);

        // The same issue claimed by the sheet edit of the OTHER character: an issue is resolved only by an edit of what it is about.
        var wrongEntity = Parse(Sheet("""{"class": "fighter"}""", "existing:sheet:" + Other, first.Id), open);
        Assert.False(wrongEntity.Ok);
        Assert.Contains("an issue about actor actors/1-A", string.Join(" ", wrongEntity.Errors));
        Assert.Contains("changes actor actors/2-A", string.Join(" ", wrongEntity.Errors));
    }

    [Fact]
    public void ASheetIssueCannotBeResolvedByAnEditOfTheHistory_NorAHistoryIssueByASheetEdit()
    {
        var open = ConsistencyChecker.Run(Fx.Snap(actors: new[] { Fx.Actor(Pc, bio: "", sheet: new Dictionary<string, object> { ["class"] = "rogue" }) }, system: Fx.System(), library: Library));
        var rule = open.Single(i => i.Check == CheckNames.SheetRule);
        var bio = open.Single(i => i.Check == CheckNames.EmptyBio);

        var historyForSheet = Parse($$"""{"ref": "h", "kind": "edit", "target": "existing:actor:actors/1-A", "text": "She sails.", "resolves": ["{{rule.Id}}"]}""", open);
        Assert.False(historyForSheet.Ok);
        Assert.Contains("fixed by an edit of the character's sheet", string.Join(" ", historyForSheet.Errors));

        var sheetForHistory = Parse(Sheet("""{"class": "fighter"}""", resolves: bio.Id), open);
        Assert.False(sheetForHistory.Ok);
        Assert.Contains("an edit of a sheet", string.Join(" ", sheetForHistory.Errors));
        Assert.Contains("by a change of its history", string.Join(" ", sheetForHistory.Errors));
    }

    // ---- values against the Library and the sheet's rules ----

    private static (IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings) Check(Dictionary<string, object> sheet, string setKey, string json, string enforcement = "strict")
    {
        var system = Fx.System();
        return SheetProposal.Check(system, Library, enforcement, sheet, new Dictionary<string, string> { [setKey] = json }, Array.Empty<string>(), "s1");
    }

    [Fact]
    public void AnIdTheLibraryDoesNotHave_IsRefused_NothingIsInvented()
    {
        var sheet = new Dictionary<string, object> { ["class"] = "rogue" };
        Assert.Contains("'phantom' (for 'class') is not in the Library", Assert.Single(Check(sheet, "class", "\"phantom\"").Errors));
        Assert.Contains("'sword-of-dawn' (for 'inventory')", Assert.Single(Check(sheet, "inventory", "[\"sword-of-dawn|equipped:1\"]").Errors));
        Assert.Empty(Check(sheet, "inventory", "[\"chain-mail|equipped:1\"]").Errors);
    }

    [Fact]
    public void AChangeThatBreaksARuleTheSheetDidNotBreak_IsRefused_AnOldBreakageIsNotHeldAgainstIt()
    {
        // The constraint: the class must be fighter or wizard. Rogue breaks it already.
        var clean = new Dictionary<string, object> { ["class"] = "fighter" };
        var broken = new Dictionary<string, object> { ["class"] = "rogue" };
        var library = new Fx.Entries(Fx.Entry("class", "fighter", "Fighter"), Fx.Entry("class", "rogue", "Rogue"), Fx.Entry("class", "wizard", "Wizard"),
            Fx.Entry("equipment", "rope", "Rope"));

        var (errors, _) = SheetProposal.Check(Fx.System(), library, "strict", clean, new Dictionary<string, string> { ["class"] = "\"rogue\"" }, Array.Empty<string>(), "s1");
        Assert.Contains("would break a rule of the sheet", Assert.Single(errors));

        // An edit that leaves an old violation alone (it only adds rope) is not blamed for it.
        Assert.Empty(SheetProposal.Check(Fx.System(), library, "strict", broken, new Dictionary<string, string> { ["inventory"] = "[\"rope\"]" }, Array.Empty<string>(), "s1").Errors);
        // And fixing it is allowed.
        Assert.Empty(SheetProposal.Check(Fx.System(), library, "strict", broken, new Dictionary<string, string> { ["class"] = "\"wizard\"" }, Array.Empty<string>(), "s1").Errors);
        // With enforcement off the rules are not run, though the Library still is.
        Assert.Empty(SheetProposal.Check(Fx.System(), library, "off", clean, new Dictionary<string, string> { ["class"] = "\"rogue\"" }, Array.Empty<string>(), "s1").Errors);
        Assert.NotEmpty(SheetProposal.Check(Fx.System(), library, "off", clean, new Dictionary<string, string> { ["class"] = "\"phantom\"" }, Array.Empty<string>(), "s1").Errors);
    }

    [Fact]
    public void AKeyTheSystemNoLongerListsAsEditable_IsRefused_NotSkipped_WhetherSetOrCleared()
    {
        var sheet = new Dictionary<string, object> { ["class"] = "fighter", ["HP_Max"] = 9L };

        var (setErrors, _) = SheetProposal.Check(Fx.System(), Library, "strict", sheet, new Dictionary<string, string> { ["HP_Max"] = "50" }, Array.Empty<string>(), "s1");
        Assert.Contains("'HP_Max' is not a sheet key a proposal can change", Assert.Single(setErrors));

        var (clearErrors, _) = SheetProposal.Check(Fx.System(), Library, "strict", sheet, new Dictionary<string, string>(), new[] { "HP_Max" }, "s1");
        Assert.Contains("'HP_Max' is not a sheet key a proposal can change", Assert.Single(clearErrors));

        // A key that is editable is not blamed, in any case.
        Assert.Empty(SheetProposal.Check(Fx.System(), Library, "strict", sheet, new Dictionary<string, string> { ["CLASS"] = "\"fighter\"" }, new[] { "background" }, "s1").Errors);
    }

    [Fact]
    public void NumbersAndChoices_AreCheckedAgainstTheirBoundsAndOptions()
    {
        var system = SystemDefinitionLoader.Parse("""
            id: demo
            name: Demo
            attributes:
              - { key: str, label: Strength, default: 10, min: 3, max: 18, modifier: half-minus-five }
            actorFields:
              - { key: alignment, label: Alignment, type: Dropdown, options: [Good, Evil] }
              - { key: level, label: Level, type: Number, min: 1, max: 20 }
            """);
        var errors = new List<string>();
        var (set, clear) = SheetProposal.Read(system, JsonDocument.Parse("""{"str": 19, "alignment": "evil", "level": 2.5}""").RootElement, "s1", errors);

        Assert.Contains(errors, e => e.Contains("'str' is 19, above its highest value 18"));
        Assert.Empty(clear);
        Assert.Equal("\"Evil\"", set["alignment"]); // the option as the system spells it
        Assert.Equal("2.5", set["level"]);

        errors.Clear();
        SheetProposal.Read(system, JsonDocument.Parse("""{"str": 12.5, "alignment": "Chaotic"}""").RootElement, "s1", errors);
        Assert.Contains(errors, e => e.Contains("'str' takes a whole number"));
        Assert.Contains(errors, e => e.Contains("'alignment' is not one of its options: Good, Evil"));
    }

    [Fact]
    public void TheSheetThatApplyingProduces_IsACopy_WithTheKeysSetAndCleared()
    {
        var sheet = new Dictionary<string, object> { ["class"] = "rogue", ["background"] = "sailor" };
        var next = SheetProposal.Apply(sheet, new Dictionary<string, string> { ["inventory"] = "[\"rope\"]" }, new[] { "background" });
        Assert.Equal("rogue", next["class"]);
        Assert.False(next.ContainsKey("background"));
        Assert.Equal(new List<object> { "rope" }, (List<object>)next["inventory"]);
        Assert.Equal("sailor", sheet["background"]); // the original is untouched

        var before = SheetProposal.Current(sheet, new[] { "class", "inventory" });
        Assert.Equal("\"rogue\"", before["class"]);
        Assert.Equal("null", before["inventory"]);
        Assert.True(SheetProposal.Unchanged(sheet, before));
        sheet["class"] = "wizard";
        Assert.False(SheetProposal.Unchanged(sheet, before));
    }
}

/// <summary>A sheet edit through the whole chat: proposed, ticked, applied, then re-checked by the code.</summary>
public class SheetEditThroughTheGateTests
{
    private const string Pc = "actors/1-A";

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: srd5e
        name: Demo
        actorFields:
          - { key: class, label: Class, type: EntityReference, targetEntityType: class }
          - { key: background, label: Background, type: EntityReference, targetEntityType: background }
        entityTypes:
          - { type: class, label: Class }
          - { type: background, label: Background }
          - { type: equipment, label: Equipment }
        sheetRules:
          enforcement: strict
          inventory: { key: inventory, types: [equipment] }
          constraints:
            - field: class
              where: [{ property: id, in: [fighter, wizard], message: "no such class." }]
        """);

    private static ContentRegistry Pack()
    {
        var registry = new ContentRegistry();
        foreach (var (type, id, name, kit) in new[]
        {
            ("class", "rogue", "Rogue", (string?)null), ("class", "fighter", "Fighter", "(a) Chain Mail, (b) 155 GP"), ("class", "wizard", "Wizard", null),
            ("equipment", "chain-mail", "Chain Mail", null), ("equipment", "rope", "Rope", null)
        })
        {
            var entry = new ContentEntry { Id = id, Type = type, SystemId = "srd5e", PackId = "srd", Name = name };
            if (kit != null) entry.Properties["startingEquipment"] = kit;
            registry.RegisterEntry(entry, 0);
        }

        return registry;
    }

    private static ActorDocument Aria(string cls = "rogue") => new()
    {
        Id = Pc, CampaignId = "c1", Name = "Aria", Kind = ActorKind.PlayerCharacter, Biography = "A sailor.",
        Sheet = new Dictionary<string, object> { ["class"] = cls }
    };

    private sealed record Run(WorldChat Chat, WorldBuilderTests.Saving Saving, ActorDocument Actor, string IssueId);

    private static async Task<(WorldBuilderTests.Saving Saving, ActorDocument Actor, IReadOnlyList<JsonElement> Issues)> WorldAsync(string cls = "rogue")
    {
        var saving = WorldBuilderTests.ServiceFor(System, Pack());
        var actor = Aria(cls);
        saving.Actors.Add(actor);
        var listed = await saving.Service.IssuesAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm, null, null);
        var issues = JsonDocument.Parse(listed).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        return (saving, actor, issues);
    }

    private static async Task<(Run Run, WorldChat Chat)> ProposedAsync(string sheetJson, string? resolves, string cls = "rogue", string target = "existing:sheet:" + Pc)
    {
        var (saving, actor, issues) = await WorldAsync(cls);
        var id = issues.FirstOrDefault(i => i.GetProperty("check").GetString() == CheckNames.SheetRule).ValueKind == JsonValueKind.Object
            ? issues.First(i => i.GetProperty("check").GetString() == CheckNames.SheetRule).GetProperty("id").GetString()!
            : string.Empty;
        var proposal = $$$"""{"proposal": {"entries": [{"ref": "s", "kind": "edit", "target": "{{{target}}}", "sheet": {{{sheetJson}}}{{{(resolves == null ? "" : $", \"resolves\": [\"{(resolves == "ISSUE" ? id : resolves)}\"]")}}}}]}}""";
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", proposal),
            _ => new ChatMessage(ChatRole.Assistant, "Ready to review."),
            _ => new ChatMessage(ChatRole.Assistant, "Noted."));
        var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), WorldBuilderTests.Campaign, WorldBuilderTests.Dm);
        await chat.SendAsync("Fix Aria's class.", _ => { }, CancellationToken.None);
        return (new Run(chat, saving, actor, id), chat);
    }

    [Fact]
    public async Task TheIssueListSaysASheetRuleIsFixableBySheet()
    {
        var (_, _, issues) = await WorldAsync();
        var rule = issues.Single(i => i.GetProperty("check").GetString() == CheckNames.SheetRule);
        Assert.Equal("sheet", rule.GetProperty("fix_via").GetString());
        Assert.Equal(Pc, rule.GetProperty("entity_id").GetString());
    }

    [Fact]
    public async Task AFixThroughTheGate_ShowsADiff_ChangesOnlyOnApply_AndTheRecheckSeesItResolved()
    {
        var (run, chat) = await ProposedAsync("""{"class": "fighter"}""", "ISSUE");

        var draft = Assert.IsType<WorldBundle>(chat.Proposal).Drafts.Single();
        // The card: what the sheet holds now and what it would hold; nothing is changed until the DM applies.
        Assert.Equal("Aria", draft.Name);
        Assert.Equal("\"rogue\"", draft.BeforeSheet!["class"]);
        Assert.Equal("\"fighter\"", draft.SheetSet["class"]);
        Assert.NotNull(draft.BeforeStamp);
        Assert.Equal("rogue", run.Actor.Sheet["class"]);

        var result = await chat.ApplyAsync();

        Assert.Equal("fighter", run.Actor.Sheet["class"]);
        Assert.Equal(EditTarget.Sheet, result.Changes.Single().Target);
        Assert.False(result.Recheck!.Failed);
        Assert.Equal(new[] { run.IssueId }, result.Recheck.Resolved);
        Assert.Empty(result.Recheck.StillOpen);
        Assert.DoesNotContain(result.Recheck.New, id => id.StartsWith("sheet-rule:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUntickedSheetEdit_ChangesNothing()
    {
        var (run, chat) = await ProposedAsync("""{"class": "fighter"}""", null);
        chat.Proposal!.Drafts.Single().Selected = false;

        await chat.ApplyAsync();

        Assert.Equal("rogue", run.Actor.Sheet["class"]);
    }

    [Fact]
    public async Task ASheetChangedAfterTheReview_IsNotOverwritten()
    {
        var (run, chat) = await ProposedAsync("""{"class": "fighter"}""", null);
        run.Actor.Sheet["class"] = "wizard"; // someone changed it while the DM was reading the card

        var failure = await Assert.ThrowsAsync<LocalizedException>(() => chat.ApplyAsync());

        Assert.Equal("WorldBuilder.Error.EditChanged", failure.Key);
        Assert.Equal("wizard", run.Actor.Sheet["class"]);
    }

    [Fact]
    public async Task ACharacterWhoBecameACreatureAfterTheReview_IsNotEdited()
    {
        var (run, chat) = await ProposedAsync("""{"class": "fighter"}""", null);
        run.Actor.Kind = ActorKind.Monster; // as the proposal was checked, a bestiary creature's sheet is out of reach

        var failure = await Assert.ThrowsAsync<LocalizedException>(() => chat.ApplyAsync());

        Assert.Equal("WorldBuilder.Error.EditTargetGone", failure.Key);
        Assert.Equal("rogue", run.Actor.Sheet["class"]);
    }

    [Fact]
    public async Task AKeyThatIsNotEditableAtTheApply_IsRefused_AndNothingIsWritten()
    {
        var (run, chat) = await ProposedAsync("""{"class": "fighter"}""", null);
        // The system was reloaded between the review and the apply, and now lists HP_Max as derived: the entry still carries it.
        chat.Proposal!.Drafts.Single().SheetSet["HP_Max"] = "50";

        var failure = await Assert.ThrowsAsync<LocalizedException>(() => chat.ApplyAsync());

        Assert.Equal("WorldBuilder.Error.SheetRefused", failure.Key);
        Assert.Equal("rogue", run.Actor.Sheet["class"]);
        Assert.False(run.Actor.Sheet.ContainsKey("HP_Max"));
    }

    [Fact]
    public async Task ARetiredCharacter_AnUnknownId_AndAnInventedItem_AreRefusedBeforeAnyReview()
    {
        var (saving, actor, _) = await WorldAsync();
        string Reply(string sheet, string target = "existing:sheet:" + Pc) =>
            $$$"""{"proposal": {"entries": [{"ref": "s", "kind": "edit", "target": "{{{target}}}", "sheet": {{{sheet}}}}]}}""";
        async Task<string> TryAsync(string argumentsJson)
        {
            var model = new WorldBuilderTests.ScriptedModel(_ => WorldBuilderTests.Calls("c1", "propose_changes", argumentsJson), _ => new ChatMessage(ChatRole.Assistant, "ok"));
            var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), WorldBuilderTests.Campaign, WorldBuilderTests.Dm);
            await chat.SendAsync("Go.", _ => { }, CancellationToken.None);
            Assert.Null(chat.Proposal);
            return WorldBuilderTests.ToolResult(model.Calls[1]);
        }

        Assert.Contains("is not in the Library", await TryAsync(Reply("""{"inventory": ["sword-of-dawn"]}""")));
        actor.Retired = true;
        Assert.Contains("not a character whose sheet a proposal can change", await TryAsync(Reply("""{"class": "fighter"}""")));
    }
}
