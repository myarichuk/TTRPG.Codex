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

        await Features().ChooseAsync(new AiAssistant(chat), draft, "a pirate who licked a frog", new CampaignDocument { SystemId = "srd5e" }, system, new RegistryResolver(_registry), CancellationToken.None, propose: true);

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
        Assert.Equal("Whispering Tome", Assert.Single(evaluation.Counters).Feature.Name);
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

    private sealed class WithProposed(IEntryResolver inner, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? inner.Resolve(type, id);
        public IEnumerable<ContentEntry> Candidates(string type) => inner.Candidates(type).Concat(extra.Where(e => e.Type == type));
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
