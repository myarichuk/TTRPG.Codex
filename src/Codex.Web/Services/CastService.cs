using Codex.Core;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>Turns Library creatures (pack or homebrew) into cast members. DM only, enforced by the
/// repository. A creature added while the table is live joins it at once.</summary>
public sealed class CastService(CampaignRuntimeManager runtimes, IContentRegistry registry, ComponentRegistry components)
{
    public IActorDefinition? Blueprint(LibraryItem item, CampaignDocument campaign, SystemDefinition? system)
    {
        if (!item.IsHomebrew)
        {
            return registry.GetActor($"{campaign.SystemId}:{item.Entry.Id}");
        }

        var type = system?.FindType(item.Entry.Type);
        return type is { ActorBlueprint: true } ? YamlContentPackLoader.ToActorBlueprint(item.Entry, type) : null;
    }

    /// <summary>Adds <paramref name="count"/> copies, numbered when more than one, hidden from players until revealed. Returns the actors created.</summary>
    public async Task<IReadOnlyList<ActorDocument>> AddToCastAsync(LibraryItem item, CampaignDocument campaign, SystemDefinition? system, CampaignAccess access, int count = 1)
    {
        var blueprint = Blueprint(item, campaign, system);
        if (blueprint == null || !access.IsDm)
        {
            return Array.Empty<ActorDocument>();
        }

        var created = new List<ActorDocument>();
        for (var i = 1; i <= Math.Clamp(count, 1, 20); i++)
        {
            var actor = new ActorDocument
            {
                Id = Guid.NewGuid().ToString(),
                CampaignId = campaign.Id,
                Kind = ActorKind.Monster,
                BlueprintId = item.IsHomebrew ? null : $"{blueprint.SystemId}:{blueprint.Id}",
                Name = count > 1 ? $"{blueprint.Name} {i}" : blueprint.Name,
                Visibility = ActorVisibility.Hidden
            };
            ActorSheetSync.SeedFromBlueprint(actor, blueprint, system, components);
            if (await runtimes.TryCreateActorAsync(actor, access))
            {
                created.Add(actor);
            }
        }

        return created;
    }
}
