using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Systems;
using Codex.Core.WorldBuilder;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using Microsoft.Extensions.AI;

namespace Codex.Tests;

/// <summary>Step H6 of HAZARDS_DESIGN.md: a world builder proposal places a hazard on a feature, a homebrew hazard is held to the system's
/// scaling before the game master sees it, and a system with no hazard type keeps the free-text trap. Driven through the chat's own tool loop
/// with the scripted model, as the other world builder chat tests are.</summary>
public class WorldBuilderHazardTests
{
    private static readonly SystemDefinition Srd5e = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));

    private static ContentRegistry LibraryWithAPit()
    {
        var registry = new ContentRegistry();
        registry.RegisterEntry(new ContentEntry { Id = "spiked-pit", Type = "hazard", SystemId = "srd5e", PackId = "srd", Name = "Spiked Pit" }, 0);
        return registry;
    }

    private static WorldBuilderTests.Saving Hazards() => WorldBuilderTests.ServiceFor(Srd5e, LibraryWithAPit());

    // A level 5 trap: its save DC and damage sit on the system's level 5 row (or within a row of it).
    private static string Blades(string saveDc = "15", string dice = "4d10") =>
        $$$"""
        {"ref": "h", "kind": "homebrew", "type": "hazard", "name": "Falling Blades", "text": "Blades drop from the ceiling.", "tags": ["trap"],
         "properties": {"kind": "Trap", "severity": "Deadly", "level": "5-10", "trigger": "A creature steps on the middle flagstone.",
           "detect": [{"id": "glint", "name": "Glint of steel", "check": "Perception", "dc": 15}],
           "disarm": [{"name": "Jam the plate", "needs": "an Iron Spike"}],
           "saveAttribute": "Dexterity", "saveDc": {{{saveDc}}}, "onSave": "half",
           "damage": [{"name": "Blades", "dice": "{{{dice}}}", "type": "slashing"}],
           "reset": "The blades rise again after a minute."}}
        """;

    private static string Vault(string hazard, string trap = "") =>
        $$$"""{"ref": "v", "kind": "place", "placeKind": "feature", "name": "Vault Door", "text": "A heavy door.", "feature": {"hazard": "{{{hazard}}}"{{{trap}}}}}""";

    private static string Proposal(params string[] entries) => $$$"""{"proposal": {"region": "The Coast", "entries": [{{{string.Join(",", entries)}}}]}}""";

    [Fact]
    public async Task APlaceCanCarryALibraryHazard_ByItsId_HiddenAndGmOnly()
    {
        var saving = Hazards();
        var check = await saving.Service.CheckAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm,
            """{"region": "The Coast", "entries": [{"ref": "v", "kind": "place", "placeKind": "feature", "name": "Vault Door", "text": "A door.", "feature": {"hazard": "hazard/spiked-pit"}}]}""");

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        var feature = check.Bundle.Drafts.Single().Feature!;
        Assert.Equal("hazard/spiked-pit", feature.HazardEntry);
        Assert.Equal("Spiked Pit", feature.HazardName);

        await saving.Service.SaveAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm, check.Bundle);

        var placed = saving.NewRegions.Single().Locations.Single();
        Assert.Equal(ActorVisibility.Hidden, placed.Visibility);
        var hazard = placed.Feature!.Hazard!;
        Assert.Equal(("hazard/spiked-pit", false, HazardState.Hidden), (hazard.Entry, hazard.IsHomebrew, hazard.State));
        Assert.Null(placed.Feature.Trap);
    }

    [Fact]
    public async Task ALibraryHazardTheLibraryDoesNotHave_IsAnError_NotADanglingLink()
    {
        var saving = Hazards();

        var check = await saving.Service.CheckAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm,
            """{"entries": [{"ref": "v", "kind": "place", "name": "Vault Door", "text": "A door.", "feature": {"hazard": "hazard/nothing"}}]}""");

        Assert.False(check.Ok);
        Assert.Contains("not in the Library", Assert.Single(check.Errors));
    }

    [Fact]
    public async Task AHomebrewHazardInRange_IsReviewedWithItsPlace_AndSavedHiddenAndUnpublished()
    {
        var saving = Hazards();
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", Proposal(Blades(), Vault("h"))),
            _ => new ChatMessage(ChatRole.Assistant, "A vault with a blade trap is ready to review."));
        var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), WorldBuilderTests.Campaign, WorldBuilderTests.Dm);

        await chat.SendAsync("A trapped vault, please.", _ => { }, CancellationToken.None);

        Assert.NotNull(chat.Proposal);
        var door = chat.Proposal!.Drafts.Single(d => d.Kind == DraftKinds.Place);
        Assert.Equal(("h", "Falling Blades"), (door.Feature!.HazardRef, door.Feature.HazardName));
        // Nothing is saved by proposing.
        Assert.Empty(saving.Entries);

        await chat.ApplyAsync();

        // The entry reads as a hazard through the table's own readers: a coercion that dropped the rows would make these empty.
        var entry = Assert.Single(saving.Entries);
        var type = Srd5e.FindType("hazard")!;
        var numbers = HazardNumbers.For(new ContentEntry { Type = "hazard", Properties = entry.Properties }, type, null);
        Assert.Equal(("4d10", "slashing"), (HazardNumbers.Damage(numbers, type.Hazard!).Single().Dice, HazardNumbers.Damage(numbers, type.Hazard!).Single().Type));
        Assert.Equal(15, HazardNumbers.NoticeRoutes(numbers, type.Hazard!).Single().Dc);
        Assert.Single(HazardNumbers.DisableRoutes(numbers, type.Hazard!));
        Assert.Empty(HazardNumbers.Problems(new ContentEntry { Type = "hazard", Properties = entry.Properties }, type, Srd5e));

        // Secrecy: the entry is unpublished, the place is hidden, the hazard is only on its feature and starts Hidden.
        Assert.False(entry.IsPublished);
        var place = saving.NewRegions.Single().Locations.Single();
        Assert.Equal(ActorVisibility.Hidden, place.Visibility);
        Assert.Equal(($"hazard/{entry.EntryId}", true, HazardState.Hidden), (place.Feature!.Hazard!.Entry, place.Feature.Hazard.IsHomebrew, place.Feature.Hazard.State));
        Assert.All(saving.Pages, p => Assert.Equal(WikiVisibility.DmOnly, p.Visibility));
    }

    [Theory]
    [InlineData("25", "4d10", "saveDc is too high")] // the save DC is far above the level 5 row
    [InlineData("15", "18d10", "damage is too high")] // only the damage is out of range
    [InlineData("15", "1d4", "damage is too low")]
    public async Task AHomebrewHazardOutOfRange_IsRefusedWithItsProblem_AndNothingIsHeld(string saveDc, string dice, string problem)
    {
        var saving = Hazards();
        var proposal = Proposal(Blades(saveDc, dice), Vault("h"));
        var model = new WorldBuilderTests.ScriptedModel(
            _ => WorldBuilderTests.Calls("c1", "propose_changes", proposal),
            _ => new ChatMessage(ChatRole.Assistant, "I will fix the numbers."));
        var chat = new WorldChat(saving.Service, () => Task.FromResult<IChatClient>(model), WorldBuilderTests.Campaign, WorldBuilderTests.Dm);
        var progress = new List<WorldChatStep>();

        await chat.SendAsync("A trapped vault, please.", progress.Add, CancellationToken.None);

        var step = progress.Distinct().Single();
        Assert.True(step.Rejected);
        Assert.StartsWith("Nothing was proposed", step.Result);
        Assert.Contains(problem, step.Result);
        Assert.Contains("level 5", step.Result);
        Assert.Null(chat.Proposal);
        Assert.Empty(saving.Entries);
    }

    [Fact]
    public async Task AHazardWithNumbersAndNoLevel_IsRefused_AsIsOneWhoseRowsTheShapeRejects()
    {
        var saving = Hazards();
        const string noLevel = """{"ref": "h", "kind": "homebrew", "type": "hazard", "name": "Mystery Plate", "text": "A plate.", "properties": {"saveAttribute": "Dexterity", "saveDc": 15}}""";
        const string badRoute = """{"ref": "h", "kind": "homebrew", "type": "hazard", "name": "Mystery Plate", "text": "A plate.", "properties": {"level": "5", "detect": [{"name": "Look", "dc": 12}]}}""";

        var first = await saving.Service.CheckAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm, $$"""{"entries": [{{noLevel}}]}""");
        var second = await saving.Service.CheckAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm, $$"""{"entries": [{{badRoute}}]}""");

        Assert.Contains("no level to read them against", Assert.Single(first.Errors));
        Assert.Contains("has a dc and no check", second.Errors.Single(e => e.Contains("dc")));
    }

    [Fact]
    public async Task AListOfUnnamedRows_IsWarnedAbout_NotSilentlyDropped()
    {
        var saving = Hazards();
        var unnamed = Blades().Replace("{\"name\": \"Blades\", \"dice\"", "{\"dice\"");

        var check = await saving.Service.CheckAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm, $$"""{"entries": [{{unnamed}}]}""");

        Assert.Contains(check.Warnings, w => w.Contains("row(s) of 'damage' have no \"name\""));
    }

    [Fact]
    public async Task AHazardLinkThatNamesNothing_IsDropped_WithAWarning()
    {
        var saving = Hazards();

        var check = await saving.Service.CheckAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm, $$"""{"entries": [{{Vault("ghost", ", \"lock\": \"DC 10\"")}}]}""");

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Contains(check.Warnings, w => w.Contains("hazard 'ghost'"));
        Assert.Null(check.Bundle.Drafts.Single().Feature!.HazardRef);
        Assert.Equal("DC 10", check.Bundle.Drafts.Single().Feature!.Lock);
    }

    [Fact]
    public async Task ASystemWithNoHazardType_KeepsTheTrapText_AndIgnoresAHazard()
    {
        var saving = WorldBuilderTests.ServiceFor(new SystemDefinition { Id = "srd5e" }, new ContentRegistry());
        var check = await saving.Service.CheckAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm,
            """{"entries": [{"ref": "v", "kind": "place", "name": "Chest", "text": "A chest.", "feature": {"trap": "A poison needle, DC 13 to spot.", "hazard": "hazard/spiked-pit"}}]}""");

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.Contains(check.Warnings, w => w.Contains("no hazard type"));

        await saving.Service.SaveAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm, check.Bundle);

        var feature = saving.NewRegions.Single().Locations.Single().Feature!;
        Assert.Equal("A poison needle, DC 13 to spot.", feature.Trap);
        Assert.Null(feature.Hazard);
    }

    [Fact]
    public async Task ThePromptSaysTheShapeAndScalingExist_ByFieldNameOnly_AndOnlyWhereTheSystemHasHazards()
    {
        var withHazards = AiPrompts.WorldChat(await Hazards().Service.ChatRequestAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));
        var without = AiPrompts.WorldChat(await WorldBuilderTests.ServiceFor(new SystemDefinition { Id = "srd5e" }, new ContentRegistry()).Service.ChatRequestAsync(WorldBuilderTests.Campaign, WorldBuilderTests.Dm));

        Assert.Contains("This system has hazards (type \"hazard\")", withHazards);
        Assert.Contains("\"saveDc\", \"damage\"", withHazards);
        Assert.Contains("\"level\"", withHazards);
        // Field names, never the table's numbers.
        Assert.DoesNotContain("2d10", withHazards);
        Assert.DoesNotContain("18d10", withHazards);
        Assert.DoesNotContain("This system has hazards", without);
    }

    [Theory]
    [InlineData("5-10", "15", "4d10", 0)]
    [InlineData("5-10", "13", "2d10", 0)] // a row below, still inside the window
    [InlineData("5-10", "17", "10d10", 0)] // a row above
    [InlineData("1-4", "13", "1d4", 0)] // below the first row there is no floor
    [InlineData("1-4", "19", "2d10", 1)] // three rows above
    [InlineData("11-16", "13", "10d10", 1)] // two rows below
    public void TheScalingCheck_AllowsOneRowEitherSide(string level, string saveDc, string dice, int problems)
    {
        var type = Srd5e.FindType("hazard")!;
        var entry = new ContentEntry
        {
            Type = "hazard",
            Properties = new Dictionary<string, object>
            {
                ["level"] = level, ["saveAttribute"] = "Dexterity", ["saveDc"] = long.Parse(saveDc),
                ["damage"] = new List<object> { new Dictionary<string, object> { ["name"] = "Blades", ["dice"] = dice } }
            }
        };

        Assert.Equal(problems, HazardNumbers.ScalingProblems(entry, type, Srd5e).Count());
    }
}
