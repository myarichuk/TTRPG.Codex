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
}
