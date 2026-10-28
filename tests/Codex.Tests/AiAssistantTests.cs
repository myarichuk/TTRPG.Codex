using Microsoft.Extensions.AI;
using Codex.Core.AI;

namespace Codex.Tests;

public class AiAssistantTests
{
    private static readonly CharacterConceptRequest Request = new(
        "A tired guard", "player character", "SRD",
        new[] { new FieldSpec("str", "Strength", "Number", null, 1, 30), new FieldSpec("dex", "Dexterity", "Number", null, 1, 30) },
        new[]
        {
            new FieldSpec("level", "Level", "Number", null, 1, 20),
            new FieldSpec("class", "Class", "EntityReference", new[] { "fighter (Fighter)", "cleric (Cleric)" }, null, null),
            new FieldSpec("alignment", "Alignment", "Dropdown", new[] { "Lawful Good", "Neutral" }, null, null),
            new FieldSpec("languages", "Languages", "TagList", null, null, null),
            new FieldSpec("ritual", "Ritual caster", "Boolean", null, null, null),
            new FieldSpec("notes", "Notes", "LongText", null, null, null)
        },
        "HP", "HP_Max");

    [Fact]
    public void ParseDraft_CoercesTypes_ResolvesReferencesByLabel_AndDropsUnknownIds()
    {
        const string reply = """
            Here is your character:
            ```json
            {
              "name": "Marta Vell",
              "sheet": {
                "str": "16", "dex": 40, "level": 3,
                "class": "Cleric", "alignment": "lawful good",
                "languages": "Common, Dwarvish", "ritual": "true",
                "notes": "Owes the guild money", "species": "elf", "HP": 24, "HP_Max": 24
              },
              "biography": "She stood on the wall for twenty years."
            }
            ```
            """;

        var draft = AiAssistant.ParseDraft(reply, Request);

        Assert.Equal("Marta Vell", draft.Name);
        Assert.Equal(16L, draft.Sheet["str"]);
        Assert.Equal(30L, draft.Sheet["dex"]); // clamped to max
        Assert.Equal(3L, draft.Sheet["level"]);
        Assert.Equal("cleric", draft.Sheet["class"]); // label resolved to id
        Assert.Equal("Lawful Good", draft.Sheet["alignment"]);
        Assert.Equal(new object[] { "Common", "Dwarvish" }, (List<object>)draft.Sheet["languages"]);
        Assert.Equal(true, draft.Sheet["ritual"]);
        Assert.Equal("Owes the guild money", draft.Sheet["notes"]);
        Assert.Equal(24L, draft.Sheet["HP"]);
        Assert.False(draft.Sheet.ContainsKey("species")); // not a declared field
        Assert.Equal("She stood on the wall for twenty years.", draft.Biography);
    }

    [Fact]
    public void ParseDraft_AttributeAnsweredUnderItsLabel_IsKept()
    {
        var draft = AiAssistant.ParseDraft("""{"name":"X","sheet":{"Strength":15,"DEX":"12"}}""", Request);

        Assert.Equal(15L, draft.Sheet["str"]);
        Assert.Equal(12L, draft.Sheet["dex"]);
    }

    [Fact]
    public void CreateCharacterPrompt_AsksForEveryScore_WithTheSystemsGeneration()
    {
        var (_, user) = AiPrompts.CreateCharacter(Request with { AttributeAdvice = "use the standard array 15, 14, 13, 12, 10, 8" });

        Assert.Contains("Give EVERY attribute", user);
        Assert.Contains("standard array 15, 14, 13, 12, 10, 8", user);
    }

    [Fact]
    public void ParseDraft_UnknownReferenceId_IsDroppedNotSaved()
    {
        var draft = AiAssistant.ParseDraft("""{"name":"X","sheet":{"class":"paladin"}}""", Request);

        Assert.False(draft.Sheet.ContainsKey("class"));
    }

    [Fact]
    public void ParseDraft_WithoutJson_Throws()
    {
        Assert.Throws<FormatException>(() => AiAssistant.ParseDraft("Sorry, I can't do that.", Request));
    }

    private static readonly CharacterConceptRequest WithGear = Request with
    {
        Equipment = new[] { new EquipmentOption("longsword", "Longsword"), new EquipmentOption("chain-mail", "Chain Mail"), new EquipmentOption("shield", "Shield"), new EquipmentOption("rope", "Rope") },
        OneArmorAndShield = true
    };

    [Fact]
    public void ParseDraft_Equipment_AcceptsAnItemByItsName()
    {
        // Models often answer with the name they read next to the id.
        var draft = AiAssistant.ParseDraft("""{"name":"X","equipment":[{"id":"Chain Mail","equipped":true},{"name":"rope","quantity":2}]}""", WithGear);

        Assert.Equal(new[] { new DraftItem("chain-mail", 1, true), new DraftItem("rope", 2, false) }, draft.Equipment);
    }

    [Fact]
    public async Task CreateCharacter_WithoutGearInTheReply_AsksForItOnItsOwn()
    {
        var chat = new QueueChat("""{"name":"Brakka","sheet":{},"biography":"A smith."}""", """{"equipment":[{"id":"longsword","equipped":true}]}""");
        var assistant = new AiAssistant(chat);

        var draft = await assistant.CreateCharacterAsync(WithGear);
        var gear = draft.Equipment is { Count: > 0 } ? draft.Equipment : await assistant.EquipAsync(new EquipRequest("Brakka: a smith", "SRD", WithGear.Equipment!, true));

        Assert.Equal(new[] { new DraftItem("longsword", 1, true) }, gear);
        Assert.Contains("Character: Brakka: a smith", chat.Prompts[^1]);
        Assert.Contains("- chain-mail: Chain Mail", chat.Prompts[^1]);
        Assert.Contains("- longsword" + Environment.NewLine, chat.Prompts[^1]); // a name that only says the id again is left out
    }

    /// <summary>Answers each call with the next reply in turn and keeps the prompts it was sent.</summary>
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

    [Fact]
    public void ParseDraft_Equipment_KeepsListedIdsAddsRepeatsAndClampsQuantity()
    {
        const string reply = """
            {"name":"X","sheet":{},"equipment":[
              {"id":"Longsword","quantity":1,"equipped":true},
              {"id":"rope","quantity":"3"},
              {"id":"rope","quantity":2},
              {"id":"vorpal-blade","quantity":1,"equipped":true},
              "shield",
              {"id":"chain-mail","quantity":500,"equipped":"true"}
            ]}
            """;

        var draft = AiAssistant.ParseDraft(reply, WithGear);

        Assert.NotNull(draft.Equipment);
        Assert.Equal(new[] { "longsword", "rope", "shield", "chain-mail" }, draft.Equipment!.Select(i => i.Id));
        Assert.Equal(new DraftItem("longsword", 1, true), draft.Equipment[0]);
        Assert.Equal(5, draft.Equipment[1].Quantity);
        Assert.False(draft.Equipment[2].Equipped);
        Assert.Equal(99, draft.Equipment[3].Quantity);
        Assert.True(draft.Equipment[3].Equipped);
    }

    [Fact]
    public void ParseDraft_WithoutAnInventory_IgnoresEquipment()
    {
        var draft = AiAssistant.ParseDraft("""{"name":"X","equipment":[{"id":"longsword"}]}""", Request);

        Assert.Null(draft.Equipment);
    }

    [Fact]
    public void CreateCharacter_AsksForEquipmentOnlyWhenTheSystemHasAnInventory()
    {
        var (without, withoutUser) = AiPrompts.CreateCharacter(Request);
        var (system, user) = AiPrompts.CreateCharacter(WithGear);

        Assert.DoesNotContain("equipment", without);
        Assert.DoesNotContain("Items (", withoutUser);
        Assert.Contains("\"biography\": string, \"equipment\": [", system); // part of the one shape the reply follows
        Assert.Contains("one suit of body armor and one shield", system);
        Assert.Contains("- chain-mail: Chain Mail", user);
    }
}
