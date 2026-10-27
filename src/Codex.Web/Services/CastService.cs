using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>Turns Library creatures (pack or homebrew), wiki people and world-builder people into cast members. DM only,
/// enforced by the repository. A creature added while the table is live joins it at once.</summary>
public sealed class CastService(CampaignRuntimeManager runtimes, IContentRegistry registry, ComponentRegistry components, ISystemCatalog catalog,
    IActorRepository actors, IWikiRepository wiki)
{
    public IActorDefinition? Blueprint(LibraryItem item, CampaignDocument campaign, SystemDefinition? system)
    {
        if (!item.IsHomebrew)
        {
            return registry.GetActor($"{campaign.SystemId}:{item.Entry.Id}");
        }

        var type = system?.FindType(item.Entry.Type);
        return type is { ActorBlueprint: true } ? YamlContentPackLoader.ToActorBlueprint(item.Entry, type, system?.PrimaryResource) : null;
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

    /// <summary>Fills <paramref name="actor"/>'s sheet with the system's defaults, so the first sheet view isn't a wall of blanks.</summary>
    public void SeedDefaults(ActorDocument actor, string systemId)
    {
        var schema = catalog.GetUISchemas(systemId).FirstOrDefault(s => s.EntityType == DataDrivenSystemPlugin.ActorSchemaType);
        foreach (var field in schema?.Fields ?? new List<FieldDefinition>())
        {
            if (field.DefaultValue != null)
            {
                actor.Sheet[field.Key] = field.DefaultValue is int i ? (long)i : field.DefaultValue;
            }
        }
    }

    /// <summary>Starts a brand-new sheet: the system's defaults, then the modes that let hit points and armor class follow the
    /// rules. A <paramref name="draft"/> from the model goes over that, its attribute scores as the base scores (typed in, so
    /// no point-buy budget argues with them).</summary>
    public void NewSheet(ActorDocument actor, string systemId, SystemDefinition? system, CharacterDraft? draft = null)
    {
        SeedDefaults(actor, systemId);
        if (system != null)
        {
            new SheetRuleEngine(system, NullEntryResolver.Instance).InitializeSheet(actor.Sheet);
        }

        if (draft == null)
        {
            return;
        }

        foreach (var (key, value) in draft.Sheet)
        {
            actor.Sheet[key] = value;
        }

        var scored = false;
        foreach (var attribute in system?.Attributes ?? new List<AttributeDefinition>())
        {
            if (draft.Sheet.TryGetValue(attribute.Key, out var score))
            {
                actor.Sheet[SheetRuleEngine.BasePrefix + attribute.Key] = score;
                scored = true;
            }
        }

        if (scored)
        {
            actor.Sheet[SheetRuleEngine.MethodKey] = "manual";
        }
    }

    /// <summary>Adds a non-player character, hidden from players until revealed. The sheet comes from <see cref="NewNpc.Creature"/>'s
    /// stat block (a Library creature that plays like them), else <see cref="NewNpc.Draft"/>, else a blank sheet (the system's
    /// defaults and starting attributes); the character keeps their own name either way. Returns null when
    /// <paramref name="access"/> may not add to the cast.</summary>
    public async Task<ActorDocument?> AddNpcAsync(NewNpc npc, CampaignDocument campaign, SystemDefinition? system, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return null;
        }

        var blueprint = npc.Creature == null ? null : Blueprint(npc.Creature, campaign, system);
        var actor = new ActorDocument
        {
            Id = Guid.NewGuid().ToString(),
            CampaignId = campaign.Id,
            Kind = ActorKind.NonPlayerCharacter,
            BlueprintId = blueprint == null || npc.Creature!.IsHomebrew ? null : $"{blueprint.SystemId}:{blueprint.Id}",
            Name = npc.Name.Trim(),
            Visibility = ActorVisibility.Hidden,
            Biography = string.IsNullOrWhiteSpace(npc.Biography) ? null : npc.Biography.Trim(),
            DmNotes = string.IsNullOrWhiteSpace(npc.DmNotes) ? null : npc.DmNotes.Trim(),
            WikiPageId = npc.WikiPageId
        };
        if (blueprint != null)
        {
            ActorSheetSync.SeedFromBlueprint(actor, blueprint, system, components);
        }
        else
        {
            NewSheet(actor, campaign.SystemId, system, npc.Draft);
        }

        // Gear picked apart from the sheet (for a stat block, which lists none) wins over a draft's.
        if (npc.Equipment is { Count: > 0 } && system?.SheetRules?.Inventory is { } inventory)
        {
            actor.Sheet[inventory.Key] = npc.Equipment.ToList();
        }

        ActorSheetSync.ApplySheetToState(actor, system, components);
        return await runtimes.TryCreateActorAsync(actor, access) ? actor : null;
    }

    /// <summary>Promotes a wiki page to the cast: a hidden NPC named after the page, whose history is the page's text (page
    /// links reduced to their labels), linked both ways. The texts are copied once and then go their own ways, as a place's
    /// do. A page already promoted returns its character. Null when <paramref name="access"/> may not add to the cast.</summary>
    public async Task<ActorDocument?> PromotePageAsync(WikiPageDocument page, NewNpc npc, CampaignDocument campaign, SystemDefinition? system, CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(page.ActorId) && await actors.GetVisibleAsync(page.ActorId, access) is { } existing)
        {
            return existing;
        }

        var actor = await AddNpcAsync(npc with
        {
            Name = string.IsNullOrWhiteSpace(npc.Name) ? page.Title : npc.Name,
            Biography = WikiText.WithoutPageLinks(page.Body),
            WikiPageId = page.Id
        }, campaign, system, access);
        if (actor == null)
        {
            return null;
        }

        page.Kind = WikiPageKind.Person;
        page.ActorId = actor.Id;
        await wiki.TrySaveAsync(page, access);
        return actor;
    }
}

/// <summary>A non-player character to add to the cast; see <see cref="CastService.AddNpcAsync"/>.</summary>
/// <param name="Equipment">Gear as the sheet stores it (<see cref="AiFeatures.StoredEquipment"/>).</param>
public sealed record NewNpc(string Name)
{
    public string? Biography { get; init; }
    public string? DmNotes { get; init; }
    public LibraryItem? Creature { get; init; }
    public CharacterDraft? Draft { get; init; }
    public IReadOnlyList<object>? Equipment { get; init; }
    public string? WikiPageId { get; init; }
}
