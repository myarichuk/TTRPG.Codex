using Codex.Persistence;

namespace Codex.Tests;

public class SessionNotesTests
{
    private const string Typed = "Found a key.\n:::dm\nIt opens the crypt.\n:::\n:::for [[actor:aria]]\nAria knows the lock.\n:::";

    [Fact]
    public void ATableNote_ReachesOtherPlayersWithoutItsDmBlocks_ButItsWriterAndTheDmReadItWhole()
    {
        var dmNote = new SessionNote { Id = "n1", AuthorId = "dm", Text = Typed };
        var aria = new HashSet<string> { "aria" };

        Assert.Equal("Found a key.", SessionNotes.TextFor(dmNote, new CampaignAccess("c", "p2", CampaignRole.Player), new HashSet<string>()));
        Assert.Equal("Found a key.\n\nAria knows the lock.", SessionNotes.TextFor(dmNote, new CampaignAccess("c", "p1", CampaignRole.Player), aria));
        Assert.Equal(Typed, SessionNotes.TextFor(dmNote, new CampaignAccess("c", "dm", CampaignRole.DM), new HashSet<string>()));

        var playersOwn = new SessionNote { Id = "n2", AuthorId = "p1", Text = Typed };
        Assert.Equal(Typed, SessionNotes.TextFor(playersOwn, new CampaignAccess("c", "p1", CampaignRole.Player), aria));
    }
}
