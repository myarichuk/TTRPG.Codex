using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Rules;
using Codex.Localization;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>
/// The campaign-facing AI flows. Each one gathers exactly what the model may see for the caller's
/// role, builds the request, and hands it to the user's <see cref="AiAssistant"/>. Player-facing
/// outputs are built from player-visible material only.
/// </summary>
public sealed class AiFeatures(AiProviderService providers, IContentRegistry registry, ISystemCatalog catalog)
{
    /// <summary>The language the model writes in: the one the user reads the app in.</summary>
    private static string Lang => SupportedCultures.Normalize(System.Globalization.CultureInfo.CurrentUICulture.Name);

    public Task<bool> IsConfiguredAsync(string userId) => providers.IsConfiguredForAsync(userId);

    /// <summary>A session summary for the DM (everything) or the players (public notes and rolls only). DM only: it is the DM who publishes recaps.</summary>
    public async Task<string> SessionSummaryAsync(CampaignDocument campaign, SessionDocument session, IEnumerable<NoteDocument> notes, AiAudience audience, CampaignAccess access, Func<string, string> nameOf, IEnumerable<ActorDocument> actors,
        IEnumerable<LocationDocument>? places = null, IEnumerable<MentionItem>? items = null, CancellationToken ct = default)
    {
        if (!access.IsDm)
        {
            throw new LocalizedException("Service.Ai.DmOnlySummary");
        }

        var digest = SessionDigest.Build(session, notes, forDm: audience == AiAudience.Dm, nameOf, actors, places, items);
        var assistant = await providers.AssistantForAsync(access.UserId);
        return await assistant.SummarizeSessionAsync(new SessionSummaryRequest(
            campaign.Name,
            catalog.GetDisplayName(campaign.SystemId),
            session.Title,
            audience,
            digest.Events,
            digest.Notes,
            digest.Rolls,
            digest.DmSummary,
            digest.Quotes,
            Language: Lang), ct);
    }

    /// <summary>Rewrites a character's history (player-facing) or the DM's private notes on an NPC.</summary>
    public async Task<string> RewriteAsync(ActorDocument actor, CampaignDocument campaign, SystemDefinition? system, string currentText, string instruction, AiAudience audience, string userId, CancellationToken ct = default)
    {
        var assistant = await providers.AssistantForAsync(userId);
        var facts = SheetFacts(actor, campaign.SystemId, system).ToList();
        if (audience == AiAudience.Dm && !string.IsNullOrWhiteSpace(actor.Biography))
        {
            facts.Add("Public history: " + TextFormatting.Summary(actor.Biography, 600));
        }

        return await assistant.RewriteAsync(new RewriteRequest(
            actor.Name,
            KindLabel(actor.Kind, system),
            facts,
            currentText,
            instruction,
            audience,
            $"{campaign.Name} ({catalog.GetDisplayName(campaign.SystemId)}){(campaign.Blurb(300) is { Length: > 0 } blurb ? ": " + blurb : "")}",
            Language: Lang), ct);
    }

    /// <summary>What the next level brings, with picks that fit the sheet, from the Library's class and subclass entries.</summary>
    public async Task<string> LevelUpAsync(ActorDocument actor, CampaignDocument campaign, SystemDefinition? system, string userId, CancellationToken ct = default)
    {
        var assistant = await providers.AssistantForAsync(userId);
        var level = YamlValues.GetInt(actor.Sheet, "level") ?? 1;
        var next = level + 1;
        var classId = YamlValues.GetString(actor.Sheet, "class");
        var subclassId = YamlValues.GetString(actor.Sheet, "subclass");
        var classEntry = classId == null ? null : registry.GetEntry(campaign.SystemId, "class", classId);
        var subclassEntry = subclassId == null ? null : registry.GetEntry(campaign.SystemId, "subclass", subclassId);

        var subclasses = classEntry == null
            ? new List<string>()
            : registry.GetEntries(campaign.SystemId, "subclass")
                .Where(s => string.Equals(YamlValues.GetString(s.Properties, "class"), classId, StringComparison.OrdinalIgnoreCase))
                .Select(s => $"{s.Name}: {TextFormatting.Summary(YamlValues.GetString(s.Properties, "summary") ?? s.Description, 120)}")
                .ToList();

        return await assistant.SuggestLevelUpAsync(new LevelUpRequest(
            actor.Name,
            catalog.GetDisplayName(campaign.SystemId),
            classEntry?.Name ?? classId,
            subclassEntry?.Name ?? subclassId,
            level,
            next,
            SheetFacts(actor, campaign.SystemId, system).ToList(),
            FeaturesAtLevel(classEntry, next),
            FeaturesAtLevel(subclassEntry, next),
            subclassId == null ? subclasses : new List<string>(),
            actor.Biography,
            Language: Lang,
            Choices: system?.Canonical.Ai.AdvancementChoices,
            SheetChanges: system?.Canonical.Ai.AdvancementChanges), ct);
    }

    /// <summary>Writes or expands a wiki page for the DM. <paramref name="context"/> is what the campaign already says about the
    /// page's subject (the history of the character it is about, the description of its place), which the page must agree with.
    /// The page may hold secrets, in DM blocks: the DM's is the only audience, and players are given the page without them.</summary>
    /// <param name="outline">The DM's template for pages of this kind (see <c>LiveTemplate</c>), which the page follows; null for none.</param>
    public async Task<string> ExpandWikiAsync(CampaignDocument campaign, string title, WikiPageKind kind, string body, IEnumerable<string> relatedTitles, IReadOnlyList<string> context, string instruction, string userId, CancellationToken ct = default,
        string? outline = null)
    {
        var assistant = await providers.AssistantForAsync(userId);
        var setting = $"{catalog.GetDisplayName(campaign.SystemId)} campaign “{campaign.Name}”{(campaign.Blurb(400) is { Length: > 0 } blurb ? ": " + blurb : "")}";
        return await assistant.WriteWikiPageAsync(new WikiPageRequest(
            title,
            kind == WikiPageKind.General ? "lore" : kind.ToString().ToLowerInvariant(),
            body,
            instruction,
            relatedTitles.Take(30).ToList(),
            context,
            setting,
            Language: Lang,
            Structure: string.IsNullOrWhiteSpace(outline) ? null : LiveTemplate.Sample(outline)), ct);
    }

    /// <summary>Drafts the players' version of one of the DM's secrets on a wiki page, for the DM to read before it goes in.
    /// <paramref name="shown"/> is what players already read on the page.</summary>
    public async Task<string> PlayersVersionAsync(CampaignDocument campaign, string title, WikiPageKind kind, string secret, string shown, string? current, string instruction, string userId, CancellationToken ct = default)
    {
        var assistant = await providers.AssistantForAsync(userId);
        return await assistant.WritePlayersVersionAsync(new PlayersVersionRequest(
            title,
            kind == WikiPageKind.General ? "lore" : kind.ToString().ToLowerInvariant(),
            secret,
            shown,
            current,
            instruction,
            $"{catalog.GetDisplayName(campaign.SystemId)} campaign “{campaign.Name}”{(campaign.Blurb(300) is { Length: > 0 } blurb ? ": " + blurb : "")}",
            Language: Lang), ct);
    }

    /// <summary>Writes or polishes the description of a Library entry or a place. The facts are the
    /// filled-in fields, so the prose agrees with the numbers; <paramref name="audience"/> Players
    /// keeps it to what someone there would see.</summary>
    public async Task<string> DescribeAsync(CampaignDocument campaign, string subject, string subjectKind, IReadOnlyList<string> facts, string currentText, string instruction, AiAudience audience, string userId, CancellationToken ct = default)
    {
        var assistant = await providers.AssistantForAsync(userId);
        return await assistant.DescribeAsync(new DescribeRequest(
            subject,
            subjectKind,
            catalog.GetDisplayName(campaign.SystemId),
            facts,
            currentText,
            instruction,
            audience,
            $"{campaign.Name}{(campaign.Blurb(300) is { Length: > 0 } blurb ? ": " + blurb : "")}",
            Language: Lang), ct);
    }

    /// <summary>An entry's filled-in fields as "Label: value" lines; collections become the names of their rows.</summary>
    public static List<string> EntryFacts(ContentEntry entry, ContentTypeDefinition? type)
    {
        var facts = new List<string>();
        foreach (var field in type?.Fields ?? new List<FieldDefinition>())
        {
            if (field.Key is "flavor" or "imagePrompt" || !entry.Properties.TryGetValue(field.Key, out var value) || value == null)
            {
                continue;
            }

            var text = value switch
            {
                IEnumerable<object> rows when field.Type == FieldType.Collection =>
                    string.Join("; ", rows.OfType<IDictionary<string, object>>().Select(r => r.TryGetValue("name", out var n) ? n?.ToString() : null).Where(n => !string.IsNullOrWhiteSpace(n))),
                IEnumerable<object> list => string.Join(", ", list.Select(o => o?.ToString())),
                bool b => b ? "yes" : "no",
                _ => value.ToString() ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                facts.Add($"{field.Label}: {TextFormatting.Summary(text, 200)}");
            }
        }

        if (entry.Tags.Count > 0)
        {
            facts.Add("Tags: " + string.Join(", ", entry.Tags));
        }

        return facts;
    }

    /// <summary>Turns a one-line concept into a draft sheet. Reference fields get the ids the campaign's Library offers, so the model can only pick real content.</summary>
    /// <param name="itemsFor">The Library entries of an inventory type, for the gear the character carries; without it (or without inventory rules) the sheet gets no equipment.</param>
    public async Task<CharacterDraft> CreateCharacterAsync(string concept, ActorKind kind, CampaignDocument campaign, SystemDefinition? system, string userId, Func<string, Task<IReadOnlyList<(string Id, string Name)>>> optionsFor,
        Func<string, Task<IReadOnlyList<ContentEntry>>>? itemsFor = null, CancellationToken ct = default)
    {
        system = system?.Canonical; // the prompt names fields in one language, whatever the reader sees
        var assistant = await providers.AssistantForAsync(userId);
        var attributes = (system?.Attributes ?? new List<AttributeDefinition>())
            .Select(a => new FieldSpec(a.Key, a.Label, "Number", null, a.Min, a.Max))
            .ToList();

        var fields = new List<FieldSpec>();
        var schema = catalog.GetUISchemas(campaign.SystemId).FirstOrDefault(s => s.EntityType == DataDrivenSystemPlugin.ActorSchemaType);
        var attributeKeys = attributes.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resourceKeys = (system?.Resources ?? new List<ResourceDefinition>()).SelectMany(r => new[] { r.Key, r.MaxKey }).Where(k => k != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var field in schema?.Fields ?? new List<FieldDefinition>())
        {
            if (attributeKeys.Contains(field.Key) || resourceKeys.Contains(field.Key))
            {
                continue;
            }

            IReadOnlyList<string>? options = field.Type switch
            {
                FieldType.Dropdown => field.Options ?? new List<string>(),
                FieldType.EntityReference when field.TargetEntityType != null => (await optionsFor(field.TargetEntityType)).Take(80).Select(o => $"{o.Id} ({o.Name})").ToList(),
                _ => null
            };
            fields.Add(new FieldSpec(field.Key, field.Label, field.Type.ToString(), options, field.Min, field.Max));
        }

        var inventory = itemsFor == null ? null : system?.SheetRules?.Inventory;
        var armor = system?.SheetRules?.ArmorClass;
        var carriable = await CarriableAsync(inventory, itemsFor);

        var primary = system?.PrimaryResource;
        var draft = await assistant.CreateCharacterAsync(new CharacterConceptRequest(
            concept,
            KindLabel(kind, system),
            catalog.GetDisplayName(campaign.SystemId),
            attributes,
            fields,
            primary?.Key,
            primary?.MaxKey,
            $"{campaign.Name}{(campaign.Blurb(300) is { Length: > 0 } blurb ? ": " + blurb : "")}",
            Language: Lang,
            HitPointsLabel: primary?.Label,
            HitPointsBasis: system?.Ai.PrimaryResourceBasis,
            Equipment: carriable.Count == 0 ? null : Options(carriable),
            OneArmorAndShield: armor != null,
            AttributeAdvice: AttributeAdvice(system?.SheetRules?.Attributes)), ct);

        if (inventory == null || carriable.Count == 0)
        {
            return draft;
        }

        // A model that left the gear out gets one short call that asks for nothing else; if that fails too, the character is
        // still worth keeping, without gear.
        IReadOnlyList<DraftItem> picked;
        try
        {
            picked = draft.Equipment is { Count: > 0 } equipment
                ? equipment
                : await assistant.EquipAsync(new EquipRequest($"{draft.Name}: {concept.Trim()}\n{TextFormatting.Summary(draft.Biography ?? string.Empty, 600)}",
                    catalog.GetDisplayName(campaign.SystemId), Options(carriable), armor != null), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            picked = Array.Empty<DraftItem>();
        }

        if (picked.Count > 0)
        {
            draft.Sheet[inventory.Key] = StoredEquipment(picked, carriable, inventory, armor);
        }

        return draft;
    }

    /// <summary>How the system generates scores, in a sentence for the prompt; null when it says nothing.</summary>
    internal static string? AttributeAdvice(AttributeRules? rules)
    {
        if (rules == null) return null;
        if (rules.StandardArray.Count > 0)
        {
            return $"use the standard array {string.Join(", ", rules.StandardArray)}, each value once, the best scores on what they do best";
        }

        return rules.PointBuy is { } pointBuy
            ? $"scores from {pointBuy.Min} to {pointBuy.Max} as if bought with {pointBuy.Budget} points, the best scores on what they do best"
            : null;
    }

    /// <summary>Gear for a character who has none yet (one made from a stat block or a blank sheet), as the sheet stores it;
    /// null when the system has no inventory or the model picked nothing usable.</summary>
    /// <param name="character">Who they are: a name and a line or a paragraph about them.</param>
    public async Task<List<object>?> EquipAsync(string character, CampaignDocument campaign, SystemDefinition? system, string userId,
        Func<string, Task<IReadOnlyList<ContentEntry>>> itemsFor, CancellationToken ct = default)
    {
        system = system?.Canonical;
        var inventory = system?.SheetRules?.Inventory;
        var armor = system?.SheetRules?.ArmorClass;
        var carriable = await CarriableAsync(inventory, itemsFor);
        if (inventory == null || carriable.Count == 0)
        {
            return null;
        }

        var assistant = await providers.AssistantForAsync(userId);
        var picked = await assistant.EquipAsync(new EquipRequest(character, catalog.GetDisplayName(campaign.SystemId), Options(carriable), armor != null), ct);
        return picked.Count == 0 ? null : StoredEquipment(picked, carriable, inventory, armor);
    }

    /// <summary>Most items a prompt lists; keeps a system with a vast Library from filling the model's input.</summary>
    private const int MaxCarriable = 600;

    /// <summary>The gear a sheet may carry: every entry of the system's inventory types, the everyday ones first.</summary>
    private static async Task<Dictionary<string, ContentEntry>> CarriableAsync(InventoryRules? inventory, Func<string, Task<IReadOnlyList<ContentEntry>>>? itemsFor)
    {
        var carriable = new Dictionary<string, ContentEntry>(StringComparer.OrdinalIgnoreCase);
        if (inventory == null || itemsFor == null)
        {
            return carriable;
        }

        foreach (var type in inventory.Types)
        {
            foreach (var entry in await itemsFor(type))
            {
                if (carriable.Count < MaxCarriable) carriable.TryAdd(entry.Id, entry);
            }
        }

        return carriable;
    }

    private static List<EquipmentOption> Options(Dictionary<string, ContentEntry> carriable) =>
        carriable.Values.Select(e => new EquipmentOption(e.Id, e.Name)).ToList();

    /// <summary>The generated gear as the sheet stores it ("id|qty:2,equipped:1"). The rules allow one suit of body armor and one
    /// shield on at a time, so a second of either is carried, not worn.</summary>
    public static List<object> StoredEquipment(IEnumerable<DraftItem> items, IReadOnlyDictionary<string, ContentEntry> entries, InventoryRules inventory, ArmorClassRules? armor)
    {
        var stored = new List<object>();
        var armorWorn = false;
        var shieldWorn = false;
        foreach (var item in items)
        {
            if (!entries.TryGetValue(item.Id, out var entry)) continue;
            var equipped = item.Equipped;
            if (equipped && armor != null)
            {
                if (SheetRuleEngine.IsBodyArmor(entry, armor)) { equipped = !armorWorn; armorWorn |= equipped; }
                else if (SheetRuleEngine.IsShield(entry, armor)) { equipped = !shieldWorn; shieldWorn |= equipped; }
            }

            stored.Add(SheetRuleEngine.FormatItem(inventory, entry.Id, item.Quantity, equipped));
        }

        return stored;
    }

    /// <summary>What a character's portrait is drawn from. Everyone who can see the character sees the picture, so only
    /// what they could read anyway goes in: the sheet's visible choices, the public description and the history.</summary>
    public string PortraitVisual(ActorDocument actor, string systemId, SystemDefinition? system) =>
        PortraitVisual(actor, system, (type, id) => registry.GetEntry(systemId, type, id)?.Name);

    /// <param name="entryName">Resolves a reference field's id to the entry's name (a species, a class); null keeps the id.</param>
    public static string PortraitVisual(ActorDocument actor, SystemDefinition? system, Func<string, string, string?> entryName)
    {
        system = system?.Canonical; // image prompts stay in one language
        // Choices that show on a person (species, class, background, size, alignment), not numbers or notes.
        var looks = new List<string>();
        foreach (var field in system?.ActorFields ?? new List<FieldDefinition>())
        {
            if (field.Type is not (FieldType.EntityReference or FieldType.Dropdown or FieldType.Text)) continue;
            var value = YamlValues.GetString(actor.Sheet, field.Key);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (field.Type == FieldType.EntityReference && field.TargetEntityType != null)
            {
                value = entryName(field.TargetEntityType, value) ?? value;
            }

            looks.Add($"{field.Label}: {value}");
        }

        var parts = new List<string>();
        if (looks.Count > 0) parts.Add(string.Join("; ", looks) + ".");
        if (!string.IsNullOrWhiteSpace(actor.PublicDescription)) parts.Add(TextFormatting.Summary(actor.PublicDescription, 200));
        if (!string.IsNullOrWhiteSpace(actor.Biography)) parts.Add(TextFormatting.Summary(actor.Biography, 300));
        return string.Join(" ", parts);
    }

    /// <summary>The sheet as readable lines: attributes with labels, resources, and reference fields resolved to names.</summary>
    public IEnumerable<string> SheetFacts(ActorDocument actor, string systemId, SystemDefinition? system)
    {
        system = system?.Canonical;
        yield return $"Kind: {KindLabel(actor.Kind, system)}";
        foreach (var attribute in system?.Attributes ?? new List<AttributeDefinition>())
        {
            var score = YamlValues.GetInt(actor.Sheet, attribute.Key);
            if (score != null)
            {
                yield return $"{attribute.Label}: {score}";
            }
        }

        var hp = ActorSheetSync.PrimaryResource(actor, system);
        if (hp.Max > 0)
        {
            yield return $"{hp.Label}: {hp.Current}/{hp.Max}";
        }

        var skip = (system?.Attributes.Select(a => a.Key) ?? Enumerable.Empty<string>())
            .Concat((system?.Resources ?? new List<ResourceDefinition>()).SelectMany(r => new[] { r.Key, r.MaxKey ?? "" }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var field in system?.ActorFields ?? new List<FieldDefinition>())
        {
            if (skip.Contains(field.Key) || !actor.Sheet.TryGetValue(field.Key, out var raw) || raw == null)
            {
                continue;
            }

            var text = raw is IEnumerable<object> list ? string.Join(", ", list.Select(o => o?.ToString())) : raw.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (field.Type == FieldType.EntityReference && field.TargetEntityType != null)
            {
                text = registry.GetEntry(systemId, field.TargetEntityType, text)?.Name ?? text;
            }

            yield return $"{field.Label}: {(field.Type == FieldType.LongText ? TextFormatting.Summary(text, 400) : text)}";
        }
    }

    private static List<string> FeaturesAtLevel(ContentEntry? entry, int level)
    {
        if (entry == null || !entry.Properties.TryGetValue("features", out var raw) || raw is not IEnumerable<object> rows)
        {
            return new List<string>();
        }

        var result = new List<string>();
        foreach (var row in rows.OfType<IDictionary<string, object>>())
        {
            var rowLevel = row.TryGetValue("level", out var l) && int.TryParse(l?.ToString(), out var parsed) ? parsed : (int?)null;
            if (rowLevel != level)
            {
                continue;
            }

            var name = row.TryGetValue("name", out var n) ? n?.ToString() : null;
            var description = row.TryGetValue("description", out var d) ? d?.ToString() : null;
            result.Add($"{name}: {TextFormatting.Summary(description, 700)}");
        }

        return result;
    }

    /// <summary>The kind as a prompt words it; a creature takes the system's own word for one (<see cref="AiVocabulary.Creature"/>).</summary>
    public static string KindLabel(ActorKind kind, SystemDefinition? system = null) => kind switch
    {
        ActorKind.PlayerCharacter => "player character",
        ActorKind.Monster => system?.Canonical.Ai.Creature ?? "creature",
        _ => "non-player character"
    };
}
