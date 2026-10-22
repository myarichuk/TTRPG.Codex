using Codex.Core;
using Codex.Core.AI;
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
    public Task<bool> IsConfiguredAsync(string userId) => providers.IsConfiguredForAsync(userId);

    /// <summary>A session summary for the DM (everything) or the players (public notes and rolls only). DM only: it is the DM who publishes recaps.</summary>
    public async Task<string> SessionSummaryAsync(CampaignDocument campaign, SessionDocument session, IEnumerable<NoteDocument> notes, AiAudience audience, CampaignAccess access, Func<string, string> nameOf, IEnumerable<ActorDocument> actors)
    {
        if (!access.IsDm)
        {
            throw new UnauthorizedAccessException("Only the DM drafts session summaries.");
        }

        var digest = SessionDigest.Build(session, notes, forDm: audience == AiAudience.Dm, nameOf, actors);
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
            digest.Quotes));
    }

    /// <summary>Rewrites a character's history (player-facing) or the DM's private notes on an NPC.</summary>
    public async Task<string> RewriteAsync(ActorDocument actor, CampaignDocument campaign, SystemDefinition? system, string currentText, string instruction, AiAudience audience, string userId)
    {
        var assistant = await providers.AssistantForAsync(userId);
        var facts = SheetFacts(actor, campaign.SystemId, system).ToList();
        if (audience == AiAudience.Dm && !string.IsNullOrWhiteSpace(actor.Biography))
        {
            facts.Add("Public history: " + TextFormatting.Summary(actor.Biography, 600));
        }

        return await assistant.RewriteAsync(new RewriteRequest(
            actor.Name,
            KindLabel(actor.Kind),
            facts,
            currentText,
            instruction,
            audience,
            $"{campaign.Name} ({catalog.GetDisplayName(campaign.SystemId)}){(string.IsNullOrWhiteSpace(campaign.Description) ? "" : ": " + TextFormatting.Summary(campaign.Description, 300))}"));
    }

    /// <summary>What the next level brings, with picks that fit the sheet, from the Library's class and subclass entries.</summary>
    public async Task<string> LevelUpAsync(ActorDocument actor, CampaignDocument campaign, SystemDefinition? system, string userId)
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
            actor.Biography));
    }

    /// <summary>Expands or rewrites a wiki page from its current text, the campaign blurb and the titles it links to.</summary>
    public async Task<string> ExpandWikiAsync(CampaignDocument campaign, string title, string body, IEnumerable<string> relatedTitles, string instruction, AiAudience audience, string userId)
    {
        var assistant = await providers.AssistantForAsync(userId);
        var facts = new List<string> { $"Setting: {catalog.GetDisplayName(campaign.SystemId)} campaign “{campaign.Name}”" };
        var related = relatedTitles.Take(30).ToList();
        if (related.Count > 0)
        {
            facts.Add("Related wiki pages (keep their names and link to them with [[Name]]): " + string.Join(", ", related));
        }

        return await assistant.RewriteAsync(new RewriteRequest(
            title,
            "world wiki page",
            facts,
            body,
            string.IsNullOrWhiteSpace(instruction) ? "Expand the current notes into a well-structured wiki page with short headings. Keep [[links]] as written and add [[links]] to the related pages where they fit." : instruction,
            audience,
            string.IsNullOrWhiteSpace(campaign.Description) ? null : TextFormatting.Summary(campaign.Description, 400)));
    }

    /// <summary>Writes or polishes the description of a Library entry or a place. The facts are the
    /// filled-in fields, so the prose agrees with the numbers; <paramref name="audience"/> Players
    /// keeps it to what someone there would see.</summary>
    public async Task<string> DescribeAsync(CampaignDocument campaign, string subject, string subjectKind, IReadOnlyList<string> facts, string currentText, string instruction, AiAudience audience, string userId)
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
            $"{campaign.Name}{(string.IsNullOrWhiteSpace(campaign.Description) ? "" : ": " + TextFormatting.Summary(campaign.Description, 300))}"));
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
    public async Task<CharacterDraft> CreateCharacterAsync(string concept, ActorKind kind, CampaignDocument campaign, SystemDefinition? system, string userId, Func<string, Task<IReadOnlyList<(string Id, string Name)>>> optionsFor)
    {
        var assistant = await providers.AssistantForAsync(userId);
        var attributes = (system?.Attributes ?? new List<AttributeDefinition>())
            .Select(a => new CharacterFieldSpec(a.Key, a.Label, "Number", null, a.Min, a.Max))
            .ToList();

        var fields = new List<CharacterFieldSpec>();
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
            fields.Add(new CharacterFieldSpec(field.Key, field.Label, field.Type.ToString(), options, field.Min, field.Max));
        }

        var primary = system?.PrimaryResource;
        return await assistant.CreateCharacterAsync(new CharacterConceptRequest(
            concept,
            KindLabel(kind),
            catalog.GetDisplayName(campaign.SystemId),
            attributes,
            fields,
            primary?.Key,
            primary?.MaxKey,
            $"{campaign.Name}{(string.IsNullOrWhiteSpace(campaign.Description) ? "" : ": " + TextFormatting.Summary(campaign.Description, 300))}"));
    }

    /// <summary>The sheet as readable lines: attributes with labels, resources, and reference fields resolved to names.</summary>
    public IEnumerable<string> SheetFacts(ActorDocument actor, string systemId, SystemDefinition? system)
    {
        yield return $"Kind: {KindLabel(actor.Kind)}";
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

    public static string KindLabel(ActorKind kind) => kind switch
    {
        ActorKind.PlayerCharacter => "player character",
        ActorKind.Monster => "monster",
        _ => "non-player character"
    };
}
