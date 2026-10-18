using Codex.Core.AI;

namespace Codex.Tests;

public class AiPromptsTests
{
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
            new[] { new CharacterFieldSpec("str", "Strength", "Number", null, 1, 30) },
            new[] { new CharacterFieldSpec("class", "Class", "EntityReference", new[] { "fighter (Fighter)" }, null, null) },
            "HP", "HP_Max"));

        Assert.Contains("ONE JSON object", system);
        Assert.Contains("str: Strength, 1-30", user);
        Assert.Contains("fighter (Fighter)", user);
        Assert.Contains("\"HP\"", user);
    }
}
