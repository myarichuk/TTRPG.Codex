using Codex.Core.AI;

namespace Codex.Tests;

public class AiPromptsTests
{
    [Fact]
    public void WikiPage_AboutAPerson_AsksForTheirHistory_AgreesWithTheCast_AndKeepsSecretsInDmBlocks()
    {
        var (system, user) = AiPrompts.WikiPage(new WikiPageRequest("Keeper Olm", "person", "Runs the lighthouse.", "",
            new[] { "Saltmere" }, new[] { "History of Keeper Olm (their character in the cast): Washed ashore as a child." }, "Saltmere Shadows"));

        Assert.Contains("their history", system);
        Assert.Contains(":::dm", system);
        Assert.Contains("never contradict", system);
        Assert.Contains("Washed ashore as a child.", user);
        Assert.Contains("[[Name]]", user);
        Assert.Contains("Saltmere", user);
    }

    [Fact]
    public void WikiPage_AboutAPlace_AsksForItsHistoryAndEncounters()
    {
        var (system, _) = AiPrompts.WikiPage(new WikiPageRequest("Lighthouse", "place", "", "", Array.Empty<string>(), Array.Empty<string>()));

        Assert.Contains("its history", system);
        Assert.Contains("encounters", system);
    }

    [Fact]
    public void WorldChat_WritesHistoriesAndKeepsSecretsInDmBlocks()
    {
        var prompt = AiPrompts.WorldChat(new WorldChatRequest("Fifth Edition", null, Array.Empty<string>()));

        Assert.Contains("their history", prompt);
        Assert.Contains("its history", prompt);
        Assert.Contains(":::dm", prompt);
    }

    [Fact]
    public void Rewrite_OfATextWithDmBlocks_KeepsThem()
    {
        var (system, _) = AiPrompts.Rewrite(new RewriteRequest("Aria", "character", Array.Empty<string>(), "Born at sea.\n:::dm\nSecret.\n:::", "", AiAudience.Players));
        Assert.Contains("Keep every one of them", system);
        var (plain, _) = AiPrompts.Rewrite(new RewriteRequest("Aria", "character", Array.Empty<string>(), "Born at sea.", "", AiAudience.Players));
        Assert.DoesNotContain(":::", plain);
    }

    [Fact]
    public void SessionSummary_PlayerAudience_AsksForInWorldRecapAndForbidsSecrets()
    {
        var (system, user) = AiPrompts.SessionSummary(new SessionSummaryRequest(
            "Greyharbour", "SRD", "Session one", AiAudience.Players,
            new[] { "10:00 [Damage] Goblin takes 7" }, new[] { "p2: We found the key" }, Array.Empty<string>()));

        Assert.Contains("players will read", system);
        Assert.Contains("never reveal anything the log marks as secret", system);
        Assert.Contains("Goblin takes 7", user);
        Assert.Contains("We found the key", user);
        Assert.DoesNotContain("Dice rolls", user);
    }

    [Fact]
    public void SessionSummary_DmAudience_IncludesPreviousSummaryAndSecretHeading()
    {
        var (system, user) = AiPrompts.SessionSummary(new SessionSummaryRequest(
            "Greyharbour", "SRD", "", AiAudience.Dm,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "The mayor is the villain."));

        Assert.Contains("Secrets still hidden", system);
        Assert.Contains("The mayor is the villain.", user);
        Assert.Contains("The log is empty", user);
    }

    [Fact]
    public void Rewrite_PlayerAudience_KeepsSecretsOut()
    {
        var (system, user) = AiPrompts.Rewrite(new RewriteRequest("Aria", "player character", new[] { "Class: Fighter" }, "", "Make it grim", AiAudience.Players));

        Assert.Contains("read by the players", system);
        Assert.Contains("Class: Fighter", user);
        Assert.Contains("Make it grim", user);
        Assert.Contains("write it from the facts", user);
    }

    [Fact]
    public void LevelUp_ListsFeaturesAndSubclassChoices()
    {
        var (system, user) = AiPrompts.LevelUp(new LevelUpRequest("Aria", "SRD", "Fighter", null, 2, 3,
            new[] { "Strength: 16" }, new[] { "Fighter Subclass: choose one" }, Array.Empty<string>(), new[] { "Champion: crits on 19" }, null));

        Assert.Contains("level 2 to 3", system);
        Assert.Contains("Fighter Subclass", user);
        Assert.Contains("Champion", user);
    }

    [Fact]
    public void CreateCharacter_DescribesEveryFieldAndDemandsJson()
    {
        var (system, user) = AiPrompts.CreateCharacter(new CharacterConceptRequest(
            "A tired guard", "player character", "SRD",
            new[] { new FieldSpec("str", "Strength", "Number", null, 1, 30) },
            new[] { new FieldSpec("class", "Class", "EntityReference", new[] { "fighter (Fighter)" }, null, null) },
            "HP", "HP_Max"));

        Assert.Contains("ONE JSON object", system);
        Assert.Contains("str: Strength, 1-30", user);
        Assert.Contains("fighter (Fighter)", user);
        Assert.Contains("\"HP\"", user);
    }

    [Fact]
    public void Describe_PlayerAudience_StaysInWorldAndCarriesFacts()
    {
        var (system, user) = AiPrompts.Describe(new DescribeRequest("Bog Toad", "homebrew creature (Large beast)", "SRD 5.2.1",
            new[] { "Challenge Rating: 2", "Actions: Bite; Swallow" }, "", "", AiAudience.Players));

        Assert.Contains("Players will read this", system);
        Assert.Contains("SRD 5.2.1", system);
        Assert.Contains("Challenge Rating: 2", user);
        Assert.Contains("Write the description from the facts.", user);
    }

    [Fact]
    public void Describe_WithText_PolishesAndKeepsInstruction()
    {
        var (_, user) = AiPrompts.Describe(new DescribeRequest("Greyharbour", "place on the world map", "SRD", Array.Empty<string>(), "A grey port.", "mention the gulls", AiAudience.Dm));

        Assert.Contains("A grey port.", user);
        Assert.Contains("mention the gulls", user);
    }

    [Fact]
    public void Image_KeepsLetteringOutAndPicksAShot()
    {
        Assert.Contains("Full-figure portrait of Bog Toad", AiPrompts.Image("Bog Toad", "creature", "Warty and huge.", ArtShape.Tall));
        Assert.Contains("No text", AiPrompts.Image("Greyharbour", "place", null, ArtShape.Wide));
        Assert.StartsWith("Wide establishing view", AiPrompts.Image("Greyharbour", "place", null, ArtShape.Wide));
        Assert.StartsWith("Head-and-shoulders portrait", AiPrompts.Image("Aria Vell", "player character", null, ArtShape.Square));
    }
}
