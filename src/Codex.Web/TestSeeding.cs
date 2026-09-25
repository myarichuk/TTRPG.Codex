using Codex.Persistence;

namespace Codex.Web;

/// <summary>
/// Request shapes for the opt-in <c>/test/seed/*</c> endpoints (see Program.cs) - exists purely
/// so the Phase 3 exit-criteria Playwright suite (and any e2e test after it) can stand up users
/// and actors in one HTTP round-trip against the same RavenDB instance the app under test already
/// owns, instead of a second process fighting it for the same embedded database's file lock.
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
    public ActorVisibility Visibility { get; set; } = ActorVisibility.Known;
    public int Hp { get; set; }
    public int HpMax { get; set; }
}
