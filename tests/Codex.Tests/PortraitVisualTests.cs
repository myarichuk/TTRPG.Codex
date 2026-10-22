using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;

namespace Codex.Tests;

public class PortraitVisualTests
{
    private static readonly SystemDefinition System = new()
    {
        ActorFields =
        {
            new FieldDefinition("level", "Level", FieldType.Number),
            new FieldDefinition("species", "Species", FieldType.EntityReference, targetEntityType: "species"),
            new FieldDefinition("class", "Class", FieldType.EntityReference, targetEntityType: "class"),
            new FieldDefinition("size", "Size", FieldType.Dropdown),
            new FieldDefinition("notes", "Notes", FieldType.LongText)
        }
    };

    private static string? EntryName(string type, string id) => (type, id) switch
    {
        ("species", "elf") => "Elf",
        ("class", "wizard") => "Wizard",
        _ => null
    };

    [Fact]
    public void Portrait_IsDrawnFromVisibleChoicesAndHistory_NeverTheDmsNotes()
    {
        var actor = new ActorDocument
        {
            Name = "Aria Vell",
            Sheet = { ["level"] = 3, ["species"] = "elf", ["class"] = "wizard", ["size"] = "Medium", ["notes"] = "Owes the thieves' guild." },
            Biography = "A tide-scarred scholar with silver-streaked hair.",
            DmNotes = "Secretly the drowned mayor's daughter."
        };

        var visual = AiFeatures.PortraitVisual(actor, System, EntryName);

        Assert.Equal("Species: Elf; Class: Wizard; Size: Medium. A tide-scarred scholar with silver-streaked hair.", visual);
        Assert.DoesNotContain("mayor", visual);
        Assert.DoesNotContain("guild", visual);
    }

    [Fact]
    public void UnresolvedReference_KeepsItsId_AndAnEmptySheetGivesNothing()
    {
        var actor = new ActorDocument { Name = "Stranger", Sheet = { ["species"] = "homebrew-merfolk" } };

        Assert.Equal("Species: homebrew-merfolk.", AiFeatures.PortraitVisual(actor, System, EntryName));
        Assert.Equal(string.Empty, AiFeatures.PortraitVisual(new ActorDocument { Name = "Blank" }, System, EntryName));
    }
}
