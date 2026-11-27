using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Web.Components.Shared;

namespace Codex.Tests;

public class TableVisibilityTests
{
    private static readonly IReadOnlySet<string> Seen = new HashSet<string> { "hero", "cleric" };

    private static EncounterSnapshot Order(int turnIndex) => new("e1", 1, turnIndex, new[]
    {
        new ParticipantSnapshot("goblin-1", 19, 0, TurnState.Normal, false),
        new ParticipantSnapshot("hero", 15, 0, TurnState.Normal, false),
        new ParticipantSnapshot("goblin-2", 12, 0, TurnState.Normal, false),
        new ParticipantSnapshot("cleric", 8, 0, TurnState.Normal, false),
    });

    [Fact]
    public void APlayer_CountsOnlyTheRowsTheyCanSee_AndGetsNoCountWhileAHiddenOneActs()
    {
        Assert.Equal((4, 4), TableVisibility.TurnPosition(Order(3), isDm: true, Seen));
        Assert.Equal((2, 2), TableVisibility.TurnPosition(Order(3), isDm: false, Seen));
        Assert.Equal((1, 2), TableVisibility.TurnPosition(Order(1), isDm: false, Seen));
        Assert.Null(TableVisibility.TurnPosition(Order(0), isDm: false, Seen));
    }

    private static IReadOnlyDictionary<string, ActorDocument> Known(params ActorDocument[] actors) => actors.ToDictionary(a => a.Id);

    [Fact]
    public void ARollByAnActorRetiredSince_IsStillNamed_NotShownByItsId()
    {
        var stone = new ActorDocument { Id = "actors/9", Name = "Rolling Stone", Hazard = new HazardActorLink(), Retired = true };

        // The retired actor is in the viewer's full list, though not in the list of those in play.
        Assert.Equal("Rolling Stone", TableVisibility.RollerName(stone.Id, Known(stone), isDm: true, "someone unseen"));
        Assert.Equal("actors/9", TableVisibility.RollerName(stone.Id, Known(), isDm: true, "someone unseen"));
    }

    [Fact]
    public void APlayer_IsNeverToldAHiddenHazardsName_NorItsId()
    {
        // A player's list leaves a Hidden actor out altogether, retired or not.
        Assert.Equal("someone unseen", TableVisibility.RollerName("actors/9", Known(new ActorDocument { Id = "actors/1", Name = "Aria" }), isDm: false, "someone unseen"));
    }

    [Fact]
    public void ASecretRollOfARetiredHazard_StaysWithTheGm()
    {
        var log = new[] { new RollLogEntry { ActorId = "actors/9", Expression = "1d20+8", Total = 20, IsSecret = true, Timestamp = DateTime.UtcNow } };
        var everyone = new HashSet<string> { "actors/9" };

        Assert.Single(TableVisibility.Rolls(log, isDm: true, everyone));
        Assert.Empty(TableVisibility.Rolls(log, isDm: false, everyone));
    }

    [Fact]
    public void APlayer_SeesNoRollMadeForAHiddenActor_NorASecretOne()
    {
        var log = new[]
        {
            new RollLogEntry { ActorId = "goblin-1", Expression = "1d20+2", Total = 16, Timestamp = DateTime.UtcNow.AddSeconds(-3) },
            new RollLogEntry { ActorId = "hero", Expression = "1d20+3", Total = 11, Timestamp = DateTime.UtcNow.AddSeconds(-2) },
            new RollLogEntry { ActorId = null, Expression = "1d6", Total = 4, Timestamp = DateTime.UtcNow.AddSeconds(-1) },
            new RollLogEntry { ActorId = null, Expression = "1d20", Total = 20, IsSecret = true, Timestamp = DateTime.UtcNow },
        };

        Assert.Equal(new[] { "1d6", "1d20+3" }, TableVisibility.Rolls(log, isDm: false, Seen).Select(e => e.Expression));
        Assert.Equal(4, TableVisibility.Rolls(log, isDm: true, Seen).Count());
    }
}
