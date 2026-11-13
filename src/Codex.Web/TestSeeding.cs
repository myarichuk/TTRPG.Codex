using Codex.Persistence;

namespace Codex.Web;

/// <summary>
/// Request shapes for the opt-in <c>/test/seed/*</c> endpoints (see Program.cs) - exists purely
/// so the Playwright suites can stand up users and actors in one HTTP round-trip against the same
/// RavenDB instance the app under test already owns, instead of a second process fighting it for
/// the same embedded database's file lock.
/// </summary>
public sealed class TestSeedUser
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
}

public sealed class TestSeedActor
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ActorKind Kind { get; set; } = ActorKind.NonPlayerCharacter;
    public string? OwnerUserId { get; set; }

    /// <summary>A vault character (no <see cref="CampaignId"/>): the game system it is built for.</summary>
    public string? SystemId { get; set; }

    /// <summary>A vault character's homebrew entries it carries.</summary>
    public List<Codex.Persistence.CarriedEntry>? Carried { get; set; }
    public ActorVisibility Visibility { get; set; } = ActorVisibility.Known;
    public int Hp { get; set; }
    public int HpMax { get; set; }

    /// <summary>What players see of a stranger (<see cref="ActorVisibility.Unknown"/>), and the history; null leaves them empty.</summary>
    public string? PublicName { get; set; }
    public string? PublicDescription { get; set; }
    public string? Biography { get; set; }

    /// <summary>Marks the actor as having a portrait without storing one: a picture the book cannot read (a test of what it leaves out).</summary>
    public bool HasPortrait { get; set; }

    /// <summary>Extra sheet fields (attribute scores, level, class reference...) to seed verbatim.</summary>
    public Dictionary<string, object>? Sheet { get; set; }
}
