using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>The drafter's second pass against the real SRD pack: the rules say what a drafted character may still pick
/// (spells of its class and level, a subclass of its class), the model picks among those, and nothing else reaches the sheet.</summary>
public class DraftChoicesTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public DraftChoicesTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private static AiFeatures Features()
    {
        var service = new AiProviderService(new EphemeralDataProtectionProvider(), Substitute.For<IUserRepository>(), new AIConfiguration(), new AIImageConfiguration(),
            Substitute.For<IHttpClientFactory>(), NullLogger<AiProviderService>.Instance, new ConfigurationBuilder().Build(), new ServerAiUsage(new ConfigurationBuilder().Build(), NullLogger<ServerAiUsage>.Instance));
        return new AiFeatures(service, Substitute.For<IContentRegistry>(), Substitute.For<ISystemCatalog>());
    }

    private static CharacterDraft Warlock(int level, string? subclass = null)
    {
        var sheet = new Dictionary<string, object> { ["class"] = "warlock", ["level"] = (long)level, ["cha"] = 15L };
        if (subclass != null) sheet["subclass"] = subclass;
        return new CharacterDraft("Ossic Varn", sheet, "A pirate who licked a frog.", null);
    }

    [Fact]
    public async Task ACaster_IsOfferedItsOwnSpellList_AndOnlyListedPicksUpToTheCountAreKept()
    {
        var chat = new QueueChat("""
            {"choices": {
              "subclass": "Fiend Patron",
              "cantrips": ["eldritch-blast", "Mage Hand", "fire-bolt", "minor-illusion"],
              "spells": ["hex", "fireball", "darkness", "hex", "bane", "charm-person", "sleep"]
            }}
            """);
        var draft = Warlock(3, subclass: "evoker"); // the first pass picked another class's subclass

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a pirate turned warlock", new CampaignDocument { SystemId = "srd5e" }, _loader.GetDefinition("srd5e")!, new RegistryResolver(_registry), CancellationToken.None);

        Assert.Equal("fiend-patron", draft.Sheet["subclass"]);
        Assert.Equal(new object[] { "eldritch-blast", "mage-hand" }, (List<object>)draft.Sheet["cantrips"]); // fire-bolt is not a warlock's; two cantrips at level 3
        Assert.Equal(new object[] { "hex", "darkness", "bane", "charm-person" }, (List<object>)draft.Sheet["spells"]); // fireball is too high and not listed; four prepared
        Assert.False(draft.Sheet.ContainsKey("grantedSpells")); // no count, so not a choice

        var prompt = chat.Prompts.Single();
        Assert.Contains("cantrips: Cantrips, pick up to 2", prompt);
        Assert.Contains("spells: Prepared spells, pick up to 4", prompt);
        Assert.Contains("- hex" + Environment.NewLine, prompt);
        Assert.Contains("- fiend-patron: Fiend Patron", prompt);
        Assert.DoesNotContain("- fireball", prompt);
        Assert.DoesNotContain("- evoker", prompt);
    }

    [Fact]
    public async Task BelowTheSubclassLevel_ASubclassIsTakenOff_AndNotAsked()
    {
        var chat = new QueueChat("""{"choices": {"cantrips": ["eldritch-blast"]}}""");
        var draft = Warlock(1, subclass: "fiend-patron");

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a pirate", new CampaignDocument { SystemId = "srd5e" }, _loader.GetDefinition("srd5e")!, new RegistryResolver(_registry), CancellationToken.None);

        Assert.False(draft.Sheet.ContainsKey("subclass"));
        Assert.DoesNotContain("subclass:", chat.Prompts.Single());
    }

    [Fact]
    public async Task ACharacterWithNothingToChoose_CostsNoCall_AndAFailedCallKeepsTheDraft()
    {
        var quiet = new QueueChat();
        var fighter = new CharacterDraft("Brakka", new Dictionary<string, object> { ["class"] = "barbarian", ["level"] = 1L, ["background"] = "criminal", ["originFeat"] = "alert" }, null, null);
        await Features().ChooseAsync(new AiAssistant(quiet), fighter, "a smith", new CampaignDocument { SystemId = "srd5e" }, _loader.GetDefinition("srd5e")!, new RegistryResolver(_registry), CancellationToken.None);
        Assert.Empty(quiet.Prompts);

        var broken = new QueueChat("not json at all");
        var draft = Warlock(3);
        await Features().ChooseAsync(new AiAssistant(broken), draft, "a pirate", new CampaignDocument { SystemId = "srd5e" }, _loader.GetDefinition("srd5e")!, new RegistryResolver(_registry), CancellationToken.None);
        Assert.False(draft.Sheet.ContainsKey("spells"));
        Assert.Equal("warlock", draft.Sheet["class"]);
    }

    [Fact]
    public async Task WhenNoListedSubclassSuits_OneIsWrittenForTheCharacter_AndBelongsToItsClass()
    {
        var chat = new QueueChat(
            """{"choices": {"cantrips": ["eldritch-blast", "mage-hand"]}}""",
            """
            {"name": "The Drowned Page", "description": "A waterlogged book that whispers.",
             "properties": {
               "class": "wizard", "summary": "Bargain with a sliver of a drowned mind",
               "features": [
                 {"name": "Whispering Tome", "level": 3, "description": "The book speaks.",
                  "adds": [{"to": "grantedSpells", "entries": ["Detect Thoughts"], "level": 3}],
                  "uses": {"max": "max(1, mod(cha))", "recoversOn": ["Long Rest"]}},
                 {"name": "No name here", "level": 6},
                 {"level": 10, "description": "A row without a name is dropped."}]}}
            """);
        var draft = Warlock(3);
        var system = _loader.GetDefinition("srd5e")!;

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a pirate who licked a frog", new CampaignDocument { SystemId = "srd5e" }, system, new RegistryResolver(_registry), CancellationToken.None, write: WriteEntries.WhenNothingFits);

        var entry = Assert.Single(draft.Proposed);
        Assert.Equal(("subclass", "the-drowned-page", "The Drowned Page"), (entry.Type, entry.Id, entry.Name));
        Assert.Equal("warlock", entry.Properties["class"]); // the rules settle it, whatever the model wrote
        Assert.Equal("the-drowned-page", draft.Sheet["subclass"]);
        Assert.Equal(2, ((List<object>)entry.Properties["features"]).Count);

        Assert.Contains("one is then written for them", chat.Prompts[0]);
        var prompt = chat.Prompts[1];
        Assert.Contains("You write a new subclass", prompt);
        Assert.Contains("Already settled (do not set these): class: warlock", prompt);
        Assert.Contains("features: ", prompt);
        Assert.Contains("rows, at levels 3", prompt); // the published one's build, not its words
        Assert.Contains("\"adds\"", prompt); // how a feature says what it does on the sheet
        Assert.DoesNotContain("Dark One", prompt); // never a published entry's words

        // On the sheet the written subclass works like any other: its spell is granted and its uses are counted.
        var engine = new SheetRuleEngine(system, new WithProposed(new RegistryResolver(_registry), entry));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        CastService.ApplyDraft(sheet, system, draft);
        var evaluation = engine.Recompute(sheet);
        Assert.Contains("detect-thoughts", SheetRuleEngine.RawList(sheet, "grantedSpells"));
        Assert.Equal("Whispering Tome", Assert.Single(evaluation.Counters, c => c.Feature.Name == "Whispering Tome").Feature.Name);
        Assert.DoesNotContain(evaluation.Violations, v => v.FieldKey == "subclass");
    }

    [Fact]
    public async Task WithoutSomewhereToKeepOne_NothingIsWritten()
    {
        var chat = new QueueChat("""{"choices": {"cantrips": ["eldritch-blast"]}}""");
        var draft = Warlock(3);

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a pirate", new CampaignDocument { SystemId = "srd5e" }, _loader.GetDefinition("srd5e")!, new RegistryResolver(_registry), CancellationToken.None);

        Assert.Empty(draft.Proposed);
        Assert.False(draft.Sheet.ContainsKey("subclass"));
        Assert.DoesNotContain("written for them", chat.Prompts.Single());
    }

    private const string SubclassReply = """
        {"name": "The Drowned Page", "description": "A waterlogged book that whispers.",
         "properties": {"summary": "Bargain with a sliver of a drowned mind",
           "features": [{"name": "Whispering Tome", "level": 3, "description": "The book speaks."}]}}
        """;

    [Fact]
    public async Task Always_ASubclassInTheLibrary_IsNotOfferedToTheModel_AndOneIsWritten()
    {
        var chat = new QueueChat("""{"choices": {"cantrips": ["eldritch-blast"], "spells": ["hex", "darkness", "bane", "charm-person"]}}""", SubclassReply, SpellReply);
        var draft = Warlock(3);

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a pirate", new CampaignDocument { SystemId = "srd5e" }, _loader.GetDefinition("srd5e")!, new RegistryResolver(_registry), CancellationToken.None, write: WriteEntries.Always);

        Assert.DoesNotContain("- fiend-patron", chat.Prompts[0]); // the Library's subclasses are not offered once one is to be written
        Assert.Contains("You write a new subclass", chat.Prompts[1]);
        Assert.Equal("the-drowned-page", draft.Sheet["subclass"]);
        Assert.Contains(draft.Proposed, e => e.Type == "subclass");
    }

    [Fact]
    public async Task Always_AWritableListIsAskedForOneFewer_AndTheWrittenEntryFillsTheOpenSlot()
    {
        var chat = new QueueChat("""{"choices": {"cantrips": ["eldritch-blast"], "spells": ["hex", "darkness", "bane", "charm-person"]}}""", SubclassReply, SpellReply);
        var draft = Warlock(3);

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a pirate who licked a frog", new CampaignDocument { SystemId = "srd5e" }, _loader.GetDefinition("srd5e")!, new RegistryResolver(_registry), CancellationToken.None, write: WriteEntries.Always);

        Assert.Contains("cantrips: Cantrips, pick up to 1", chat.Prompts[0]); // cantrips take two at level 3; one slot is left for the writer
        Assert.Contains("spells: Prepared spells, pick up to 4", chat.Prompts[0]); // the subclass and the cantrip take both writer calls, so none is held open here
        Assert.Contains("You write a new spell", chat.Prompts[2]);
        Assert.Equal(new object[] { "eldritch-blast", "frog-whisper" }, (List<object>)draft.Sheet["cantrips"]);
        Assert.Contains(draft.Proposed, e => e.Type == "spell" && e.Id == "frog-whisper");
    }

    [Fact]
    public async Task Off_TheModelPicksFromTheLibraryOnly_AndNothingIsWritten()
    {
        var chat = new QueueChat("""{"choices": {"subclass": "fiend-patron", "cantrips": ["eldritch-blast"]}}""");
        var draft = Warlock(3);

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a pirate", new CampaignDocument { SystemId = "srd5e" }, _loader.GetDefinition("srd5e")!, new RegistryResolver(_registry), CancellationToken.None, write: WriteEntries.Off);

        Assert.Equal("fiend-patron", draft.Sheet["subclass"]);
        Assert.Single(chat.Prompts);
        Assert.DoesNotContain("written for them", chat.Prompts[0]);
        Assert.Empty(draft.Proposed);
    }


    [Fact]
    public async Task Always_WhenTheWriterFails_TheLibraryFillsWhatWasHeldBack()
    {
        var chat = new QueueChat("""{"choices": {"cantrips": ["eldritch-blast"], "spells": ["hex", "darkness", "bane", "charm-person"]}}""", "not an entry", "not an entry either");
        var draft = Warlock(3);

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a pirate", new CampaignDocument { SystemId = "srd5e" }, _loader.GetDefinition("srd5e")!, new RegistryResolver(_registry), CancellationToken.None, write: WriteEntries.Always);

        Assert.Empty(draft.Proposed);
        Assert.False(string.IsNullOrEmpty(draft.Sheet["subclass"]?.ToString())); // the Library's subclass, not none
        var cantrips = (List<object>)draft.Sheet["cantrips"];
        Assert.Equal(2, cantrips.Count); // the slot held open for the writer is filled from the Library
        Assert.Contains("eldritch-blast", cantrips);
    }

    private const string SpellReply = """{"name": "Frog Whisper", "description": "A croak that carries a secret.", "properties": {"level": 7, "school": "Divination"}}""";
    private const string FilledSubclassAndSpells = """
        "subclass": "fiend-patron", "spells": ["hex", "darkness", "bane", "charm-person"]
        """;

    [Fact]
    public async Task ASlotLeftOpenInAList_IsWrittenFor_AndTheEntryLandsOnTheSheetAndInProposed()
    {
        var chat = new QueueChat("{\"choices\": {\"cantrips\": [\"eldritch-blast\"], " + FilledSubclassAndSpells + "}}", SpellReply);
        var draft = Warlock(3);
        var system = _loader.GetDefinition("srd5e")!;

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a pirate who licked a frog", new CampaignDocument { SystemId = "srd5e" }, system, new RegistryResolver(_registry), CancellationToken.None, write: WriteEntries.WhenNothingFits);

        var entry = Assert.Single(draft.Proposed);
        Assert.Equal(("spell", "frog-whisper"), (entry.Type, entry.Id));
        Assert.Equal(0L, Convert.ToInt64(entry.Properties["level"])); // the list asks for a cantrip, whatever the model wrote
        Assert.Equal(new object[] { "Warlock" }, ((IEnumerable<object>)entry.Properties["classes"]).ToArray());
        Assert.Equal(new object[] { "eldritch-blast", "frog-whisper" }, (List<object>)draft.Sheet["cantrips"]);

        Assert.Contains("each slot left open may be written for them", chat.Prompts[0]);
        Assert.Contains("You write a new spell", chat.Prompts[1]);
        Assert.Contains("level: 0", chat.Prompts[1]);
        Assert.Contains("classes: Warlock", chat.Prompts[1]);

        var evaluation = new SheetRuleEngine(system, new WithProposed(new RegistryResolver(_registry), entry)).Evaluate(draft.Sheet.ToDictionary(p => p.Key, p => p.Value));
        Assert.DoesNotContain(evaluation.Violations, v => v.FieldKey == "cantrips");
    }

    [Fact]
    public async Task AWrittenEntryTheRulesRefuse_IsDropped()
    {
        // Prepared spells: at most level 2 for this warlock, which the rule can only say in words, so the model's level 7 is refused.
        var chat = new QueueChat("""{"choices": {"cantrips": ["eldritch-blast", "mage-hand"], "subclass": "fiend-patron", "spells": ["hex", "darkness", "bane"]}}""", SpellReply);
        var draft = Warlock(3);

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a pirate", new CampaignDocument { SystemId = "srd5e" }, _loader.GetDefinition("srd5e")!, new RegistryResolver(_registry), CancellationToken.None, write: WriteEntries.WhenNothingFits);

        Assert.Equal(2, chat.Prompts.Count);
        Assert.Contains("Keep to these limits: level: at least 1 and at most 2", chat.Prompts[1]);
        Assert.Empty(draft.Proposed);
        Assert.Equal(new object[] { "hex", "darkness", "bane" }, (List<object>)draft.Sheet["spells"]);
    }

    [Fact]
    public async Task AHighLevelCasterWithManyOpenSlots_GetsNoMoreThanTwoEntriesWritten()
    {
        var chat = new QueueChat("""{"choices": {}}""", """{"name": "The Quiet Margin", "properties": {}}""", SpellReply, SpellReply);
        var draft = new CharacterDraft("Isolde", new Dictionary<string, object> { ["class"] = "wizard", ["level"] = 9L, ["int"] = 16L }, null, null);

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a scholar", new CampaignDocument { SystemId = "srd5e" }, _loader.GetDefinition("srd5e")!, new RegistryResolver(_registry), CancellationToken.None, write: WriteEntries.WhenNothingFits);

        Assert.Equal(3, chat.Prompts.Count); // the choice, then the subclass and the cantrips, not the prepared spells
        Assert.Equal(new[] { "subclass", "spell" }, draft.Proposed.Select(e => e.Type).ToArray());
        Assert.False(draft.Sheet.ContainsKey("spells"));
    }

    [Fact]
    public async Task ASlotWhoseRuleNamesAnotherEntrysProperty_IsNotWrittenFor()
    {
        var chat = new QueueChat("""{"choices": {}}""");
        var draft = new CharacterDraft("Brakka", new Dictionary<string, object> { ["class"] = "barbarian", ["level"] = 1L, ["background"] = "criminal" }, null, null);

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a smith", new CampaignDocument { SystemId = "srd5e" }, _loader.GetDefinition("srd5e")!, new RegistryResolver(_registry), CancellationToken.None, write: WriteEntries.WhenNothingFits);

        var prompt = Assert.Single(chat.Prompts);
        Assert.Contains("originFeat:", prompt);
        Assert.DoesNotContain("written for them", prompt); // the feat the background names cannot be written
        Assert.Empty(draft.Proposed);
    }

    [Fact]
    public async Task AClassWithNoSubclassToChoose_GetsOneWrittenOnceItIsDue_AndNotBefore()
    {
        var system = _loader.GetDefinition("srd5e")!;
        var sheet = () => new Dictionary<string, object> { ["class"] = "barbarian", ["level"] = 3L, ["background"] = "criminal", ["originFeat"] = "alert",
        // Primal Knowledge is already picked, so the only thing left to write is the subclass.
        ["choice__barbarian__primal-knowledge-3"] = new List<object> { "athletics" } };
        var chat = new QueueChat("""{"name": "Path of the Hollow Drum", "description": "Rage keeps time.", "properties": {"features": [{"name": "Beat", "level": 3, "description": "A pulse."}]}}""");
        var due = new CharacterDraft("Brakka", sheet(), null, null);

        await Features().ChooseAsync(new AiAssistant(chat), due, "a drummer", new CampaignDocument { SystemId = "srd5e" }, system, new Without(new RegistryResolver(_registry), "subclass"), CancellationToken.None, write: WriteEntries.WhenNothingFits);

        var entry = Assert.Single(due.Proposed);
        Assert.Equal(("subclass", "barbarian"), (entry.Type, entry.Properties["class"]));
        Assert.Equal("path-of-the-hollow-drum", due.Sheet["subclass"]);
        var prompt = Assert.Single(chat.Prompts);
        Assert.Contains("You write a new subclass", prompt);
        Assert.DoesNotContain("How a published one is built", prompt); // nothing to copy the build from

        var early = new CharacterDraft("Brakka", sheet(), null, null);
        early.Sheet["level"] = 2L;
        var quiet = new QueueChat();
        await Features().ChooseAsync(new AiAssistant(quiet), early, "a drummer", new CampaignDocument { SystemId = "srd5e" }, system, new Without(new RegistryResolver(_registry), "subclass"), CancellationToken.None, write: WriteEntries.WhenNothingFits);
        Assert.Empty(quiet.Prompts);
        Assert.Empty(early.Proposed);
    }

    [Fact]
    public void TheGmsReview_AddsSwapsOrLeavesOut_EachEntryTheCharacterBrings()
    {
        var system = _loader.GetDefinition("srd5e")!;
        var request = new CharacterRequestDocument
        {
            Sheet = new Dictionary<string, object>
            {
                ["class"] = "warlock", ["subclass"] = "the-drowned-page",
                ["feats"] = new List<object> { "frog-licker|str:1", "alert" },
                ["grantedSpells"] = new List<object> { "tome-whisper", "hex" }
            },
            Brings =
            {
                new CarriedEntry { Type = "subclass", EntryId = "the-drowned-page", Name = "The Drowned Page" },
                new CarriedEntry { Type = "feat", EntryId = "frog-licker", Name = "Frog Licker" },
                new CarriedEntry { Type = "spell", EntryId = "tome-whisper", Name = "Tome Whisper" }
            }
        };

        var (sheet, add) = VaultService.Reviewed(system, request, new[]
        {
            new BringChoice("subclass", "the-drowned-page", BringKind.UseOther, "fiend-patron"),
            new BringChoice("feat", "frog-licker", BringKind.LeaveOut)
        });

        Assert.Equal("fiend-patron", sheet["subclass"]);
        Assert.Equal(new[] { "alert" }, SheetRuleEngine.RawList(sheet, "feats"));
        Assert.Equal(new[] { "tome-whisper", "hex" }, SheetRuleEngine.RawList(sheet, "grantedSpells")); // no choice made: added as it is
        Assert.Equal("tome-whisper", Assert.Single(add).EntryId);
        Assert.Equal("the-drowned-page", request.Sheet["subclass"]); // the request itself is not changed

        // A pick keeps what the player chose for it when it is pointed at another entry.
        var repointed = SheetReferences.Repointed(system, request.Sheet, "feat", "frog-licker", "tough");
        Assert.Equal(new[] { "tough|str:1", "alert" }, SheetRuleEngine.RawList(repointed, "feats"));
    }

    // ---- The choices a feature leaves to the player (docs/FEATURE_GRANTS_DESIGN.md, the AI) ----

    private static ContentEntry Stacker()
    {
        var entry = new ContentEntry { Type = "subclass", Id = "stacker", Name = "Stacker", SystemId = "srd5e", PackId = "homebrew" };
        entry.Properties["class"] = "sorcerer";
        entry.Properties["features"] = new List<object>
        {
            new Dictionary<string, object>
            {
                ["name"] = "Expertise", ["level"] = 3L, ["description"] = "Two more.",
                ["adds"] = new List<object>
                {
                    new Dictionary<string, object> { ["to"] = "skills", ["choice"] = "expertise-3", ["choose"] = 2L, ["minRank"] = "proficient", ["rank"] = "expert" }
                }
            }
        };
        return entry;
    }

    private const string ExpertiseKey = "choice__stacker__expertise-3";

    private static CharacterDraft Sorcerer(params string[] proficient) => new("Mira Vael", new Dictionary<string, object>
    {
        ["class"] = "sorcerer", ["level"] = 5L, ["subclass"] = "stacker", ["cha"] = 16L,
        ["skillProficiencies"] = proficient.Cast<object>().ToList()
    }, "A scholar of storms.", null);

    // The campaign's library, with a subclass of its own beside the pack's.
    private IEntryResolver Library() => new WithProposed(new RegistryResolver(_registry), Stacker());

    [Fact]
    public async Task AnOpenChoice_IsAskedWithTheKeysTheRulesAllow_AndTheAnswerIsWrittenAsThePicks()
    {
        var chat = new ByPromptChat(prompt => prompt.Contains(ExpertiseKey)
            ? """{"choices": {"choice__stacker__expertise-3": ["stealth", "Perception", "arcana", "athletics"]}}"""
            : """{"choices": {}}""");
        var draft = Sorcerer("stealth", "perception", "athletics");
        var system = _loader.GetDefinition("srd5e")!;

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a storm scholar", new CampaignDocument { SystemId = "srd5e" }, system, Library(), CancellationToken.None);

        Assert.Equal(new object[] { "stealth", "perception" }, (List<object>)draft.Sheet[ExpertiseKey]); // arcana is not proficient, athletics is the third of two
        var prompt = Assert.Single(chat.Prompts, p => p.Contains(ExpertiseKey));
        Assert.Contains("Expertise (Stacker, level 3), pick up to 2", prompt);
        Assert.Contains("- stealth", prompt); // the key and its name are one word, so it is listed once
        Assert.DoesNotContain("- arcana", prompt);

        // On the sheet the picks are the player's own: expertise, with nothing left open.
        var engine = new SheetRuleEngine(system, Library());
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        CastService.ApplyDraft(sheet, system, draft);
        var evaluation = engine.Recompute(sheet);
        Assert.Equal(2, evaluation.Skills.Single(s => s.Key == "stealth").Rank);
        Assert.Equal(1, evaluation.Skills.Single(s => s.Key == "athletics").Rank);
        Assert.DoesNotContain(evaluation.PendingChoices, c => c.Key == ExpertiseKey);
    }

    private const string LineageKey = "choice__dragonborn__lineage";

    private static CharacterDraft Dragonborn() => new("Ka Vrash", new Dictionary<string, object> { ["class"] = "fighter", ["level"] = 1L, ["species"] = "dragonborn" }, "A scarred duelist.", null);

    [Fact]
    public async Task ARowChoice_IsAskedWithTheRowsAsOptions_AndTheAnswerIsWrittenAsTheRowsName()
    {
        var chat = new ByPromptChat(prompt => prompt.Contains(LineageKey)
            ? """{"choices": {"choice__dragonborn__lineage": "Draconic Ancestor: Red"}}"""
            : """{"choices": {}}""");
        var draft = Dragonborn();
        var system = _loader.GetDefinition("srd5e")!;

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a fire duelist", new CampaignDocument { SystemId = "srd5e" }, system, new RegistryResolver(_registry), CancellationToken.None);

        Assert.Equal(new object[] { "Draconic Ancestor: Red" }, (List<object>)draft.Sheet[LineageKey]);
        var prompt = Assert.Single(chat.Prompts, p => p.Contains(LineageKey));
        Assert.Contains("Lineage (Dragonborn)", prompt);
        Assert.Contains("- Draconic Ancestor: Black", prompt);

        var engine = new SheetRuleEngine(system, new RegistryResolver(_registry));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        CastService.ApplyDraft(sheet, system, draft);
        var evaluation = engine.Recompute(sheet);
        Assert.DoesNotContain(evaluation.PendingChoices, c => c.Key == LineageKey);
        Assert.Contains(evaluation.Features, f => f.Name == "Draconic Ancestor: Red");
    }

    [Fact]
    public async Task ARowChoice_RefusesAPickThatIsNoRow_AndStaysOpen()
    {
        var chat = new ByPromptChat(prompt => prompt.Contains(LineageKey)
            ? """{"choices": {"choice__dragonborn__lineage": ["Draconic Ancestor: Plaid", "Silver"]}}"""
            : """{"choices": {}}""");
        var draft = Dragonborn();
        var system = _loader.GetDefinition("srd5e")!;

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a fire duelist", new CampaignDocument { SystemId = "srd5e" }, system, new RegistryResolver(_registry), CancellationToken.None);

        Assert.DoesNotContain(LineageKey, draft.Sheet.Keys);
    }

    [Fact]
    public async Task AChoiceTheCharactersOwnTicksCover_IsConfirmedWithoutACall_AndAFailedCallLeavesItOpen()
    {
        var system = _loader.GetDefinition("srd5e")!;
        var covered = Sorcerer("stealth", "perception", "athletics");
        covered.Sheet["skillExpertise"] = new List<object> { "stealth", "perception" };
        var quiet = new ByPromptChat(_ => """{"choices": {}}""");

        await Features().ChooseAsync(new AiAssistant(quiet), covered, "a storm scholar", new CampaignDocument { SystemId = "srd5e" }, system, Library(), CancellationToken.None);

        Assert.DoesNotContain(quiet.Prompts, p => p.Contains(ExpertiseKey)); // nothing to ask
        Assert.Equal(new object[] { "perception", "stealth" }, (List<object>)covered.Sheet[ExpertiseKey]); // the ticks, confirmed

        var broken = new ByPromptChat(prompt => prompt.Contains(ExpertiseKey) ? "not json at all" : """{"choices": {}}""");
        var open = Sorcerer("stealth", "perception", "athletics");
        await Features().ChooseAsync(new AiAssistant(broken), open, "a storm scholar", new CampaignDocument { SystemId = "srd5e" }, system, Library(), CancellationToken.None);
        Assert.False(open.Sheet.ContainsKey(ExpertiseKey)); // still a warning to make on the sheet
    }

    private sealed class ByPromptChat(Func<string, string> reply) : IChatClient
    {
        public List<string> Prompts { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n", messages.Select(m => m.Text));
            Prompts.Add(prompt);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(prompt))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>A Library that publishes nothing of one type (a class with no subclass).</summary>
    private sealed class Without(IEntryResolver inner, string type) : IEntryResolver
    {
        public ContentEntry? Resolve(string t, string id) => t == type ? null : inner.Resolve(t, id);
        public IEnumerable<ContentEntry> Candidates(string t) => t == type ? Enumerable.Empty<ContentEntry>() : inner.Candidates(t);
    }

    private sealed class RegistryResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type);
    }

    private sealed class QueueChat(params string[] replies) : IChatClient
    {
        private readonly Queue<string> queue = new(replies);
        public List<string> Prompts { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Prompts.Add(string.Join("\n", messages.Select(m => m.Text)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, queue.Dequeue())));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
