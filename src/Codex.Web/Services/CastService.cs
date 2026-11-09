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
            if (!actor.Sheet.ContainsKey(SheetModel.KindKey)) ApplySheetKind(actor, system);
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
    public void NewSheet(ActorDocument actor, string systemId, SystemDefinition? system, CharacterDraft? draft = null, string? sheetKind = null)
    {
        SeedDefaults(actor, systemId);
        ApplySheetKind(actor, system, sheetKind);
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

        // The draft's scores are typed in. A score the model left out is the attribute's own default, not the point-buy floor the blank
        // sheet starts from: a character with a few scores missing would otherwise sit at 8 in those and look unfinished.
        var scored = false;
        foreach (var attribute in system?.Attributes ?? new List<AttributeDefinition>())
        {
            if (draft.Sheet.TryGetValue(attribute.Key, out var score))
            {
                actor.Sheet[SheetRuleEngine.BasePrefix + attribute.Key] = score;
                scored = true;
            }
        }

        if (system?.SheetRules?.Attributes != null || scored)
        {
            foreach (var attribute in (system?.Attributes ?? new List<AttributeDefinition>()).Where(a => !draft.Sheet.ContainsKey(a.Key)))
            {
                actor.Sheet[SheetRuleEngine.BasePrefix + attribute.Key] = (long)attribute.Default;
                actor.Sheet[attribute.Key] = (long)attribute.Default;
            }

            actor.Sheet[SheetRuleEngine.MethodKey] = "manual";
        }
    }

    /// <summary>The host role a sheet kind's <c>role</c> names, for an actor kind.</summary>
    public static string RoleOf(ActorKind kind) => kind switch
    {
        ActorKind.PlayerCharacter => "player",
        ActorKind.Monster => "creature",
        _ => "npc"
    };

    /// <summary>A system with sheet kinds records which one the sheet is (overhaul step 6): the one asked for, else the first for the
    /// actor's role, else the system's first. The actor kind stays the host's role for access and visibility.</summary>
    public static void ApplySheetKind(ActorDocument actor, SystemDefinition? system, string? requested = null)
    {
        if (system is not { SheetKinds.Count: > 0 }) return;
        var kind = system.SheetKinds.FirstOrDefault(k => string.Equals(k.Kind, requested, StringComparison.OrdinalIgnoreCase))
                   ?? SheetLayout.KindsFor(system, RoleOf(actor.Kind)).FirstOrDefault()
                   ?? system.SheetKinds[0];
        actor.Sheet[SheetModel.KindKey] = kind.Kind;
    }

    /// <summary>Takes a character out of play and off the live table; their document stays, so mentions still name them. False when not the DM.</summary>
    public Task<bool> RetireAsync(string actorId, CampaignAccess access) => runtimes.TryRetireActorAsync(actorId, access);

    /// <summary>Lays a creature's stat block over an existing character's sheet: what the block prints replaces the same entries and
    /// the rest of the sheet (notes, gear, anything the block does not print) stays. False, changing nothing, when the item is not a
    /// creature. The caller saves the actor.</summary>
    public bool Restat(ActorDocument actor, LibraryItem creature, CampaignDocument campaign, SystemDefinition? system)
    {
        if (Blueprint(creature, campaign, system) is not { } blueprint)
        {
            return false;
        }

        ActorSheetSync.SeedFromBlueprint(actor, blueprint, system, components);
        actor.BlueprintId = creature.IsHomebrew ? null : $"{blueprint.SystemId}:{blueprint.Id}";
        return true;
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
            if (!actor.Sheet.ContainsKey(SheetModel.KindKey)) ApplySheetKind(actor, system);
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

        // Only the link is written, onto the page as stored: the copy the caller holds may be older than an edit or a reveal.
        var linked = await wiki.TryLinkActorAsync(page.Id, actor.Id, WikiPageKind.Person, page.ActorId, access);
        if (linked != actor.Id)
        {
            // The page is gone, or someone promoted it first: this character would be a duplicate nobody links to.
            await runtimes.TryDeleteActorAsync(actor.Id, access);
            return linked == null ? null : await actors.GetVisibleAsync(linked, access);
        }

        page.Kind = WikiPageKind.Person;
        page.ActorId = actor.Id;
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
