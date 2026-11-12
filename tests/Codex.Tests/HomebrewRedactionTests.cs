using Codex.Persistence;

namespace Codex.Tests;

public class HomebrewRedactionTests
{
    /// <summary>A published homebrew entry reaches a player without the GM's blocks: not in the description, and not in a
    /// text nested in its properties (a subclass feature's description).</summary>
    [Fact]
    public void APlayersEntry_HasNoGmBlocks_InItsDescriptionOrItsFeatures()
    {
        var entry = new HomebrewEntryDocument
        {
            Name = "Shadowmarked",
            Description = "Your magic has a shadow in it.\n\n:::dm\nThe mark is a thread of the Gloaming's power.\n:::\n\nAnimals fear you.",
            Properties = new Dictionary<string, object>
            {
                ["summary"] = "Carry the mark",
                ["level"] = 3L,
                ["features"] = new List<object>
                {
                    new Dictionary<string, object> { ["name"] = "Dim Sight", ["level"] = 3L, ["description"] = "You see in the dark.\n:::dm\nThe cult can see through it too.\n:::" }
                }
            }
        };

        var seen = RavenHomebrewRepository.ForPlayers(entry);

        Assert.Equal("Your magic has a shadow in it.\n\nAnimals fear you.", seen.Description);
        var feature = (Dictionary<string, object>)((List<object>)seen.Properties["features"])[0];
        Assert.Equal("You see in the dark.", ((string)feature["description"]).Trim());
        Assert.Equal("Dim Sight", feature["name"]);
        Assert.Equal(3L, seen.Properties["level"]);
        Assert.DoesNotContain("Gloaming", System.Text.Json.JsonSerializer.Serialize(seen));
        Assert.DoesNotContain("cult", System.Text.Json.JsonSerializer.Serialize(seen));
    }
}
