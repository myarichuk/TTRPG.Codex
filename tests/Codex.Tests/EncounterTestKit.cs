using Codex.Core;
using Codex.Core.Models;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using NSubstitute;

namespace Codex.Tests;

/// <summary>The prepared encounter plans in memory, with the repository's rules the service relies on: GM only, a new plan gets a prefixed id written back, and an id is
/// looked up exactly as stored (a bare GUID is found only by the bare GUID).</summary>
internal sealed class MemoryPlans : IPreparedEncounterRepository
{
    public Dictionary<string, PreparedEncounterDocument> Held { get; } = new();

    /// <summary>A copy, as a database hands out: what a page edits is never the stored object.</summary>
    private static PreparedEncounterDocument Copy(PreparedEncounterDocument plan) =>
        System.Text.Json.JsonSerializer.Deserialize<PreparedEncounterDocument>(System.Text.Json.JsonSerializer.Serialize(plan))!;

    public Task<PreparedEncounterDocument?> GetAsync(string id, CampaignAccess access) =>
        Task.FromResult(access.IsDm && Held.TryGetValue(id, out var plan) && plan.CampaignId == access.CampaignId ? Copy(plan) : null);

    public Task<IReadOnlyList<PreparedEncounterDocument>> ListAsync(CampaignAccess access) =>
        Task.FromResult<IReadOnlyList<PreparedEncounterDocument>>(access.IsDm ? Held.Values.Where(p => p.CampaignId == access.CampaignId).OrderByDescending(p => p.UpdatedAt).Select(Copy).ToList() : []);

    public Task<bool> SaveAsync(PreparedEncounterDocument document, CampaignAccess access)
    {
        if (!access.IsDm) return Task.FromResult(false);
        if (string.IsNullOrEmpty(document.Id))
        {
            document.Id = PreparedEncounterDocument.IdPrefix + Guid.NewGuid();
            document.CreatedBy = access.UserId;
        }
        else if (!Held.ContainsKey(document.Id))
        {
            return Task.FromResult(false);
        }

        // As the real repository: the run list and its time belong to MarkRunAsync, not to a copy.
        if (Held.TryGetValue(document.Id, out var held))
        {
            document.RunEncounterIds = held.RunEncounterIds.ToList();
            document.LastRunAt = held.LastRunAt;
        }

        document.UpdatedAt = DateTime.UtcNow;
        Held[document.Id] = Copy(document);
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string id, CampaignAccess access) => Task.FromResult(access.IsDm && Held.Remove(id));

    public Task DeleteAllForCampaignAsync(string campaignId) => Task.CompletedTask;

    /// <summary>A test sets this to see what a start does when the plan cannot be marked as run (a database fault).</summary>
    public bool ThrowOnMarkRun { get; set; }

    public Task<bool> MarkRunAsync(string id, string encounterId, CampaignAccess access)
    {
        if (ThrowOnMarkRun) throw new InvalidOperationException("The database is down.");
        if (!access.IsDm || !Held.TryGetValue(id, out var plan) || plan.CampaignId != access.CampaignId) return Task.FromResult(false);
        if (!plan.RunEncounterIds.Contains(encounterId)) plan.RunEncounterIds.Add(encounterId);
        plan.Status = PreparedStatus.Ran;
        plan.LastRunAt = DateTime.UtcNow;
        plan.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<PreparedEncounterDocument>> GetAllForCampaignAsync(string campaignId) =>
        Task.FromResult<IReadOnlyList<PreparedEncounterDocument>>(Held.Values.Where(p => p.CampaignId == campaignId).ToList());

    public Task RestoreAsync(PreparedEncounterDocument document)
    {
        Held[document.Id] = Copy(document);
        return Task.CompletedTask;
    }
}

/// <summary>A small campaign for the encounter builder's tests: a system with a creature type and a hazard type, a Library with two creatures and two hazards, a region with
/// a cellar (a hazard) and a vault inside it (a hazard), an NPC, a player character and a companion.</summary>
internal sealed class EncounterWorld
{
    public const string CampaignId = "c1";

    public static readonly SystemDefinition Demo = new()
    {
        Id = "demo", Name = "Demo",
        EntityTypes =
        {
            new ContentTypeDefinition { Type = "monster", Label = "Creature", ActorBlueprint = true },
            new ContentTypeDefinition { Type = "hazard", Label = "Hazard", Hazard = new HazardDefinition { Initiative = "initiative", Complex = "complex", Routine = "routine" } }
        }
    };

    public CampaignDocument Campaign { get; } = new() { Id = CampaignId, SystemId = "demo", Name = "Salt" };
    public CampaignAccess Gm { get; } = new(CampaignId, "gm", CampaignRole.DM);
    public CampaignAccess Player { get; } = new(CampaignId, "p1", CampaignRole.Player);
    public MemoryPlans Plans { get; } = new();
    public ContentRegistry Registry { get; } = new();
    public List<HomebrewEntryDocument> Brews { get; } = new();
    public List<ActorDocument> Actors { get; } = new();
    public RegionDocument Region { get; }
    public IRegionRepository RegionRepo { get; } = Substitute.For<IRegionRepository>();
    public IActorRepository ActorRepo { get; } = Substitute.For<IActorRepository>();
    public IHomebrewRepository HomebrewRepo { get; } = Substitute.For<IHomebrewRepository>();
    public CampaignLibrary Library { get; }
    public PreparedEncounterService Service { get; }

    public EncounterWorld(IPreparedEncounterRepository? plans = null)
    {
        Registry.RegisterEntry(new ContentEntry { Id = "bandit", Type = "monster", SystemId = "demo", PackId = "base", Name = "Bandit" }, 0);
        Registry.RegisterEntry(new ContentEntry { Id = "wolf", Type = "monster", SystemId = "demo", PackId = "base", Name = "Wolf" }, 0);
        Registry.RegisterEntry(new ContentEntry { Id = "spiked-pit", Type = "hazard", SystemId = "demo", PackId = "base", Name = "Spiked pit" }, 0);
        Registry.RegisterEntry(new ContentEntry { Id = "rolling-stone", Type = "hazard", SystemId = "demo", PackId = "base", Name = "Rolling stone" }, 0);

        Region = new RegionDocument
        {
            Id = "r1", CampaignId = CampaignId, Name = "Greywater",
            Locations =
            {
                new LocationDocument { Id = "cellar", Name = "Cellar", Feature = new PlaceFeature { Hazard = new PlacedHazard { Entry = "hazard/spiked-pit", State = HazardState.Noticed } } },
                new LocationDocument { Id = "vault", Name = "Vault", ParentId = "cellar", Feature = new PlaceFeature { Hazard = new PlacedHazard { Entry = "hazard/rolling-stone" } } },
                new LocationDocument { Id = "hall", Name = "Hall" }
            }
        };
        Actors.Add(new ActorDocument { Id = "npc-1", CampaignId = CampaignId, Name = "Wendel Tarr", Kind = ActorKind.NonPlayerCharacter });
        Actors.Add(new ActorDocument { Id = "pc-1", CampaignId = CampaignId, Name = "Mira", Kind = ActorKind.PlayerCharacter, OwnerUserId = "p1" });
        Actors.Add(new ActorDocument { Id = "pet-1", CampaignId = CampaignId, Name = "Mira's hawk", Kind = ActorKind.PlayerCharacter, OwnerUserId = "p1" });
        // A creature cast from the Library is a monster actor; a complex hazard that took a turn is an actor with a link to its placement.
        Actors.Add(new ActorDocument { Id = "rat-1", CampaignId = CampaignId, Name = "Cellar rat", Kind = ActorKind.Monster });
        Actors.Add(new ActorDocument { Id = "stone-1", CampaignId = CampaignId, Name = "Rolling stone", Kind = ActorKind.Monster, Hazard = new HazardActorLink { RegionId = "r1", LocationId = "vault", Entry = "hazard/rolling-stone" } });
        Registry.RegisterTranslation("demo", "monster", "bandit", "ru", "base", new Dictionary<string, object> { ["name"] = "Бандит" });
        Registry.RegisterTranslation("demo", "hazard", "spiked-pit", "ru", "base", new Dictionary<string, object> { ["name"] = "Шипастая яма" });

        RegionRepo.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(ci => ci.Arg<CampaignAccess>().IsDm ? new[] { Region } : Array.Empty<RegionDocument>());
        ActorRepo.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Actors.AsEnumerable());

        var homebrew = HomebrewRepo;
        homebrew.GetVisibleForCampaignAsync(Arg.Any<CampaignAccess>()).Returns(_ => Brews.AsEnumerable());
        var catalog = Substitute.For<ISystemCatalog>();
        catalog.GetDefinition("demo").Returns(Demo);
        Library = new CampaignLibrary(Registry, homebrew, catalog);
        Service = new PreparedEncounterService(plans ?? Plans, RegionRepo, ActorRepo, Library, Registry);
    }

    public static PreparedLine Bandits(int count = 3) => new() { Kind = PreparedLineKind.Creature, EntryType = "monster", EntryId = "bandit", Name = "Bandit", Count = count };

    public static PreparedLine Cast(string actorId, string name) => new() { Kind = PreparedLineKind.CastMember, ActorId = actorId, Name = name };

    public PreparedEncounterDocument Plan(string name = "Ambush", string? placeId = "cellar", params PreparedLine[] lines) => new()
    {
        CampaignId = CampaignId, Name = name, RegionId = placeId == null ? null : "r1", PlaceId = placeId, Lines = lines.ToList()
    };
}
