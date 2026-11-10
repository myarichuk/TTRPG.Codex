using System.Text.Json;
using Codex.Core;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Codex.Tests;

/// <summary>Asking the AI for changes to a character sheet (docs/SERVER_ACCESS_AND_VAULT.md 6): every change is checked against the
/// system and the caller's own Library before anyone sees it, and nothing lands on the sheet until the person takes it.</summary>
public class CharacterAssistTests
{
    private static readonly SystemDefinition System = new()
    {
        Id = "test5e",
        Name = "Test",
        Attributes = { new AttributeDefinition { Key = "str", Label = "Strength", Min = 1, Max = 30 } },
        ActorFields =
        {
            new FieldDefinition("level", "Level", FieldType.Number) { Min = 1, Max = 20 },
            new FieldDefinition("class", "Class", FieldType.EntityReference) { TargetEntityType = "class" },
            new FieldDefinition("size", "Size", FieldType.Dropdown) { Options = new List<string> { "Small", "Medium" } },
            new FieldDefinition("notes", "Notes", FieldType.LongText),
            new FieldDefinition("companion", "Companion", FieldType.EntityReference) { TargetEntityType = "monster" }
        },
        EntityTypes =
        {
            new ContentTypeDefinition { Type = "class" },
            new ContentTypeDefinition { Type = "spell" },
            new ContentTypeDefinition { Type = "equipment" },
            new ContentTypeDefinition { Type = "monster", PlayerFacing = false }
        },
        SheetRules = new SheetRules
        {
            Lists =
            {
                new ListRule
                {
                    Key = "spells", Label = "Spells", TargetEntityType = "spell",
                    Where = { new RuleCondition { Property = "level", Gte = 1, Message = "cantrips go elsewhere." } }
                }
            },
            Inventory = new InventoryRules { Types = { "equipment" } },
            Skills = new SkillRules { List = { new SkillDefinition { Key = "athletics", Label = "Athletics", Attribute = "str" }, new SkillDefinition { Key = "stealth", Label = "Stealth", Attribute = "str" } } }
        }
    };

    private static readonly CampaignDocument Campaign = new() { Id = "c1", SystemId = "test5e", Name = "Salt" };
    private static readonly CampaignAccess Player = new("c1", "u-player", CampaignRole.Player);
    private static readonly CampaignAccess Gm = new("c1", "u-gm", CampaignRole.DM);

    private static CharacterAssist Service()
    {
        var registry = new ContentRegistry();
        void Add(string type, string id, string name, Dictionary<string, object>? properties = null) =>
            registry.RegisterEntry(new ContentEntry { SystemId = "test5e", Type = type, Id = id, Name = name, PackId = "core", Properties = properties ?? new() }, 0);
        Add("class", "fighter", "Fighter");
        Add("class", "wizard", "Wizard");
        Add("spell", "fireball", "Fireball", new() { ["level"] = 3L });
        Add("spell", "light", "Light", new() { ["level"] = 0L });
        Add("equipment", "rope", "Rope");
        Add("monster", "dragon", "Dragon");

        // A draft homebrew spell only the GM sees; the vault (no campaign) sees no homebrew at all.
        var homebrew = Substitute.For<IHomebrewRepository>();
        homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(ci =>
        {
            var access = ci.Arg<CampaignAccess>();
            return access.IsDm && access.CampaignId == "c1"
                ? new[] { new HomebrewEntryDocument { CampaignId = "c1", SystemId = "test5e", Type = "spell", EntryId = "hidden-flame", Name = "Hidden Flame", Properties = { ["level"] = 2L } } }.AsEnumerable()
                : Enumerable.Empty<HomebrewEntryDocument>();
        });
        var catalog = Substitute.For<ISystemCatalog>();
        catalog.GetDefinition("test5e").Returns(System);
        catalog.GetDisplayName(Arg.Any<string>()).Returns("Test");
        return new CharacterAssist(new CampaignLibrary(registry, homebrew, catalog), catalog);
    }

    private static Dictionary<string, object> Sheet() => new()
    {
        ["level"] = 3L,
        ["class"] = "fighter",
        ["base_str"] = 15L,
        ["spells"] = new List<object> { "fireball" },
        ["inventory"] = new List<object> { "rope|qty:1" },
        ["notes"] = "Owes the harbourmaster."
    };

    private static ActorDocument Actor() => new()
    {
        Id = "a1", CampaignId = "c1", Name = "Corin", Kind = ActorKind.PlayerCharacter,
        Biography = "Sailed with smugglers.\n:::dm\nHe sold out his crew.\n:::", DmNotes = "Will betray the party."
    };

    private static ChangeInput Change(string field, string op, string value, string reason = "fits") => new() { Field = field, Op = op, Value = value, Reason = reason };

    [Fact]
    public async Task APlayersRead_CarriesNoGmNotesOrSecretBlocks_ButTheGmsDoes()
    {
        var service = Service();
        var player = await service.StartAsync(Actor(), Sheet(), Campaign, Player);
        var read = service.ReadSheet(player!);
        Assert.Contains("Sailed with smugglers.", read);
        Assert.DoesNotContain("sold out his crew", read);
        Assert.DoesNotContain("betray", read);
        Assert.Contains("Fighter [fighter]", read);
        Assert.Contains("Fireball [fireball]", read);

        var gm = service.ReadSheet((await service.StartAsync(Actor(), Sheet(), Campaign, Gm))!);
        Assert.Contains("He sold out his crew.", gm);
        Assert.Contains("Will betray the party.", gm);
    }

    [Fact]
    public async Task UnknownFields_WrongValues_AndEntriesOutsideTheCallersLibrary_AreRefused_AndNothingIsProposed()
    {
        var service = Service();
        var session = (await service.StartAsync(Actor(), Sheet(), Campaign, Player))!;

        async Task<string> Refused(ChangeInput change)
        {
            var answer = await service.ProposeAsync(session, new ChangesInput { Changes = new() { change } });
            Assert.StartsWith("Nothing was proposed", answer);
            Assert.Null(session.Proposal);
            return answer;
        }

        Assert.Contains("not a field", await Refused(Change("HP", "set", "99")));
        Assert.Contains("not a field", await Refused(Change("proficiencyBonus", "set", "4")));
        Assert.Contains("from 1 to 20", await Refused(Change("level", "set", "40")));
        Assert.Contains("one of: Small, Medium", await Refused(Change("size", "set", "Huge")));
        Assert.Contains("takes \"append\"", await Refused(Change("notes", "set", "Wiped.")));
        Assert.Contains("takes \"add\" or \"remove\"", await Refused(Change("spells", "set", "light")));
        // The GM's draft homebrew is not the player's to pick, and a type players can't open isn't either.
        Assert.Contains("not an entry", await Refused(Change("spells", "add", "hidden-flame")));
        Assert.Contains("not an entry", await Refused(Change("class", "set", "dragon")));
        Assert.Contains("not an entry", await Refused(Change("companion", "set", "dragon")));
        Assert.Contains("one-line reason", await Refused(Change("level", "set", "4", reason: " ")));

        // Searching everything, a player meets no monster and no draft; the GM meets both.
        var everything = await service.SearchLibraryAsync(session, "", null);
        Assert.Contains("class/fighter: Fighter", everything);
        Assert.DoesNotContain("dragon", everything);
        Assert.DoesNotContain("hidden-flame", everything);
        var gm = await service.SearchLibraryAsync((await service.StartAsync(Actor(), Sheet(), Campaign, Gm))!, "", null);
        Assert.Contains("monster/dragon: Dragon", gm);
        Assert.Contains("spell/hidden-flame: Hidden Flame", gm);
        var gmSession = (await service.StartAsync(Actor(), Sheet(), Campaign, Gm))!;
        Assert.StartsWith("The proposal is ready", await service.ProposeAsync(gmSession, new ChangesInput { Changes = new() { Change("companion", "set", "dragon") } }));
    }

    [Fact]
    public async Task AVaultCharacter_SeesNoCampaignsHomebrew_ButTheGmPicksTheirOwn()
    {
        var service = Service();
        var vault = (await service.StartAsync(Actor(), Sheet(), VaultService.CampaignFor("test5e"), CampaignAccess.Vault("u-gm")))!;
        Assert.StartsWith("Nothing was proposed", await service.ProposeAsync(vault, new ChangesInput { Changes = new() { Change("spells", "add", "hidden-flame") } }));

        var gm = (await service.StartAsync(Actor(), Sheet(), Campaign, Gm))!;
        Assert.StartsWith("The proposal is ready", await service.ProposeAsync(gm, new ChangesInput { Changes = new() { Change("spells", "add", "hidden-flame") } }));
    }

    [Fact]
    public async Task ARuleTheChangesWouldBreak_GoesBackToTheModel()
    {
        var service = Service();
        var session = (await service.StartAsync(Actor(), Sheet(), Campaign, Player))!;
        var answer = await service.ProposeAsync(session, new ChangesInput { Changes = new() { Change("spells", "add", "light") } });
        Assert.StartsWith("Nothing was proposed", answer);
        Assert.Contains("cantrips go elsewhere", answer);
    }

    [Fact]
    public async Task TakingChanges_AddsToAListWithoutLosingItsPicks_AndSkipsOneTheSheetMovedPast()
    {
        var service = Service();
        var session = (await service.StartAsync(Actor(), Sheet(), Campaign, Player))!;
        var answer = await service.ProposeAsync(session, new ChangesInput
        {
            Changes = new()
            {
                Change("class", "set", "Wizard"),
                Change("level", "set", "4"),
                Change("spells", "remove", "fireball"),
                Change("inventory", "add", "rope"),
                Change("skillProficiencies", "add", "Athletics"),
                Change("notes", "append", "Fears deep water."),
                Change("base_str", "set", "16")
            }
        });
        Assert.StartsWith("The proposal is ready", answer);
        var changes = session.Proposal!;
        Assert.Equal(("wizard", "Wizard", "fighter"), (changes[0].Value, changes[0].Shown, changes[0].Before));

        // Since the proposal, the person set the level themselves: that change no longer applies; the rest do.
        var now = Sheet();
        now["level"] = 5L;
        var (taken, skipped) = CharacterAssist.Apply(System, now, changes);
        Assert.Equal(new[] { "level" }, skipped.Select(c => c.Field));
        Assert.Equal(5L, taken["level"]);
        Assert.Equal("wizard", taken["class"]);
        Assert.Empty((List<object>)taken["spells"]);
        Assert.Equal(new object[] { "rope|qty:2" }, (List<object>)taken["inventory"]);
        Assert.Equal(new object[] { "athletics" }, (List<object>)taken["skillProficiencies"]);
        Assert.Equal("Owes the harbourmaster.\n\nFears deep water.", taken["notes"]);
        Assert.Equal(16L, taken["base_str"]);

        // Adding a spell keeps the ones already there.
        var add = (await service.StartAsync(Actor(), Sheet(), Campaign, Gm))!;
        await service.ProposeAsync(add, new ChangesInput { Changes = new() { Change("spells", "add", "hidden-flame") } });
        var (withSpell, _) = CharacterAssist.Apply(System, Sheet(), add.Proposal!);
        Assert.Equal(new object[] { "fireball", "hidden-flame" }, (List<object>)withSpell["spells"]);
    }

    [Fact]
    public async Task TheModelReadsThroughTools_FixesARefusedProposal_AndOnlyTheCheckedOneComesBack()
    {
        var service = Service();
        var session = (await service.StartAsync(Actor(), Sheet(), Campaign, Player))!;
        var model = new ToolModel(
            _ => Calls("c1", "read_sheet", "{}"),
            _ => Calls("c2", "search_library", """{"query": "light", "type": "spell"}"""),
            _ => Calls("c3", "propose_changes", """{"proposal": {"changes": [{"field": "spells", "op": "add", "value": "light", "reason": "a light"}]}}"""),
            sent => LastResult(sent).Contains("Nothing was proposed")
                ? Calls("c4", "propose_changes", """{"proposal": {"changes": [{"field": "notes", "op": "append", "value": "Carries a lantern.", "reason": "no spell fits"}]}}""")
                : new ChatMessage(ChatRole.Assistant, "gave up"),
            _ => new ChatMessage(ChatRole.Assistant, "A lantern note instead of a cantrip."));

        var result = await service.RunAsync(session, model, "give them light", highlights: false, "en", CancellationToken.None);

        Assert.Equal("A lantern note instead of a cantrip.", result.Note);
        Assert.Equal(new[] { "notes" }, result.Changes.Select(c => c.Field));
        // The sheet came to the model fenced as data, and the search found only what the player may use.
        Assert.Contains("[campaign data", LastResult(model.Calls[1]));
        Assert.Contains("spell/light: Light", LastResult(model.Calls[2]));
        Assert.DoesNotContain("hidden-flame", LastResult(model.Calls[2]));
        Assert.Contains("cantrips go elsewhere", LastResult(model.Calls[3]));
    }

    [Fact]
    public async Task Highlights_PointAtRealEntriesAndSkills_AndChangeNothing()
    {
        var service = Service();
        var session = (await service.StartAsync(Actor(), Sheet(), Campaign, Player))!;
        var refused = await service.HighlightAsync(session, new HighlightsInput { Highlights = new() { new() { Field = "spells", Value = "hidden-flame", Reason = "fire" } } });
        Assert.StartsWith("Nothing was highlighted", refused);

        await service.HighlightAsync(session, new HighlightsInput
        {
            Highlights = new()
            {
                new() { Field = "base_str", Reason = "hauls rope all day" },
                new() { Field = "skillProficiencies", Value = "Athletics", Reason = "climbs rigging" },
                new() { Field = "inventory", Value = "rope", Reason = "a sailor's" }
            }
        });
        Assert.Equal(new[] { ("base_str", (string?)null), ("skillProficiencies", "athletics"), ("inventory", "rope") }, session.Highlights!.Select(h => (h.Field, h.Value)));
        Assert.Null(session.Proposal);
    }

    private static ChatMessage Calls(string id, string tool, string json) =>
        new(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent(id, tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(json)) });

    private static string LastResult(IList<ChatMessage> sent) =>
        sent.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last().Result?.ToString() ?? string.Empty;

    private sealed class ToolModel(params Func<IList<ChatMessage>, ChatMessage>[] replies) : IChatClient
    {
        private int next;
        public List<List<ChatMessage>> Calls { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var sent = messages.ToList();
            Calls.Add(sent);
            return Task.FromResult(new ChatResponse(replies[next++](sent)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
