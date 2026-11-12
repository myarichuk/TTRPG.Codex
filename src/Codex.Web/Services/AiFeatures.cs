using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Rules;
using Codex.Localization;
using Codex.Core.Systems;
using Codex.Core.WorldBuilder;
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
            WithSetting(campaign, $"{campaign.Name} ({catalog.GetDisplayName(campaign.SystemId)}){(campaign.Blurb(300) is { Length: > 0 } blurb ? ": " + blurb : "")}"),
            Language: Lang), ct);
    }

    /// <summary>The campaign line with the system's setting after it (<see cref="AiPrompts.WithSetting"/>). The catalog's definition
    /// is the original, so the setting reaches the model in the language system.yaml wrote it in, like the rest of a prompt.</summary>
    private string WithSetting(CampaignDocument campaign, string line) =>
        AiPrompts.WithSetting(line, catalog.GetDefinition(campaign.SystemId)?.Setting);

    /// <summary>Someone's own notes tidied (spelling, layout, structure), or a <c>Service.Ai.TidyLostMention</c> refusal when the
    /// reply lost or changed a mention: <paramref name="encode"/> reads the typed mentions ("@Mara") as the page would save them,
    /// and the tidied notes must name the same people, places and items. Only the notes are sent; nothing else of the campaign.</summary>
    public async Task<string> TidyNotesAsync(string text, Func<string, string> encode, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var assistant = await providers.AssistantForAsync(userId);
        return Tidied(text, await assistant.TidyAsync(new TidyRequest(text), ct), encode);
    }

    /// <summary>The model's reply as the tidied notes, or the <c>Service.Ai.TidyLostMention</c> refusal (empty, or a mention lost or changed).</summary>
    public static string Tidied(string text, string reply, Func<string, string> encode)
    {
        var tidied = reply.Trim();
        if (tidied.Length == 0 || !SameMentions(encode(text), encode(tidied))) throw new LocalizedException("Service.Ai.TidyLostMention");
        return tidied;
    }

    /// <summary>Whether two saved texts mention the same people, places and items.</summary>
    public static bool SameMentions(string before, string after) =>
        new[] { MentionKind.Actor, MentionKind.Place, MentionKind.Item }.All(kind =>
            SessionMentions.MentionedIds(before, kind).ToHashSet(StringComparer.Ordinal).SetEquals(SessionMentions.MentionedIds(after, kind)));

    /// <summary>What the next level brings, with picks that fit the sheet, from the Library's class and subclass entries.</summary>
    public async Task<string> LevelUpAsync(ActorDocument actor, CampaignDocument campaign, SystemDefinition? system, string userId, CancellationToken ct = default)
    {
        var assistant = await providers.AssistantForAsync(userId);
        // The level, class and subclass are the fields the system names for them (FieldRoles).
        var roles = system?.FieldRoles ?? new FieldRoles();
        string? Read(string? field) => field == null ? null : YamlValues.GetString(actor.Sheet, field);
        string? TypeOf(string? field) => system?.ActorFields.FirstOrDefault(f => f.Key == field)?.TargetEntityType;
        var level = roles.Level == null ? 1 : YamlValues.GetInt(actor.Sheet, roles.Level) ?? 1;
        var next = level + 1;
        var classId = Read(roles.Class);
        var subclassId = Read(roles.Subclass);
        var classType = TypeOf(roles.Class);
        var subclassType = TypeOf(roles.Subclass);
        var classEntry = classId == null || classType == null ? null : registry.GetEntry(campaign.SystemId, classType, classId);
        var subclassEntry = subclassId == null || subclassType == null ? null : registry.GetEntry(campaign.SystemId, subclassType, subclassId);
        var subclassOf = system?.SheetRules?.Multiclass?.SubclassClassProperty ?? new MulticlassRules().SubclassClassProperty;

        var subclasses = classEntry == null || subclassType == null
            ? new List<string>()
            : registry.GetEntries(campaign.SystemId, subclassType)
                .Where(s => string.Equals(YamlValues.GetString(s.Properties, subclassOf), classId, StringComparison.OrdinalIgnoreCase))
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
        var setting = WithSetting(campaign, $"{catalog.GetDisplayName(campaign.SystemId)} campaign “{campaign.Name}”{(campaign.Blurb(400) is { Length: > 0 } blurb ? ": " + blurb : "")}");
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
            WithSetting(campaign, $"{catalog.GetDisplayName(campaign.SystemId)} campaign “{campaign.Name}”{(campaign.Blurb(300) is { Length: > 0 } blurb ? ": " + blurb : "")}"),
            Language: Lang), ct);
    }

    /// <summary>Writes or polishes the description of a Library entry or a place. The facts are the
    /// filled-in fields, so the prose agrees with the numbers; <paramref name="audience"/> Players
    /// keeps it to what someone there would see.</summary>
    public async Task<string> DescribeAsync(CampaignDocument campaign, string subject, string subjectKind, IReadOnlyList<string> facts, string currentText, string instruction, AiAudience audience, string userId, CancellationToken ct = default)
    {
        var assistant = await providers.AssistantForAsync(userId);
        return await WithSecretsAside(currentText, audience, text => assistant.DescribeAsync(new DescribeRequest(
            subject,
            subjectKind,
            catalog.GetDisplayName(campaign.SystemId),
            facts,
            text,
            instruction,
            audience,
            WithSetting(campaign, $"{campaign.Name}{(campaign.Blurb(300) is { Length: > 0 } blurb ? ": " + blurb : "")}"),
            Language: Lang), ct));
    }

    /// <summary>Runs <paramref name="write"/> on the text as players read it and puts the stored secret blocks back on
    /// the reply. A Players-audience prompt is told to leave secrets out, so given the DM's blocks the model either dropped
    /// them (lost on save) or worked them into the visible prose; the box then took the reply as it came (S4).</summary>
    public static async Task<string> WithSecretsAside(string currentText, AiAudience audience, Func<string, Task<string>> write)
    {
        if (audience != AiAudience.Players || !SecretText.HasMarkers(currentText))
        {
            return await write(currentText);
        }

        var reply = await write(SecretText.ForPlayers(currentText));
        return SecretText.KeepSecrets(currentText, reply) ?? reply;
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
        Func<string, Task<IReadOnlyList<ContentEntry>>>? itemsFor = null, CancellationToken ct = default, Func<Task<IEntryResolver>>? entriesFor = null, WriteEntries write = WriteEntries.Off)
    {
        system = system?.Canonical; // the prompt names fields in one language, whatever the reader sees
        var assistant = await providers.AssistantForAsync(userId);
        var draft = await DraftAsync(assistant, concept, kind, campaign, system, optionsFor, itemsFor, ct);
        if (system?.SheetRules != null && entriesFor != null)
        {
            // With write set, an entry may be written for the character (CharacterDraft.Proposed); the caller says so only
            // when it has somewhere to keep such an entry for a GM to review.
            await ChooseAsync(assistant, draft, concept, campaign, system, await entriesFor(), ct, write);
        }

        return draft;
    }

    private async Task<CharacterDraft> DraftAsync(AiAssistant assistant, string concept, ActorKind kind, CampaignDocument campaign, SystemDefinition? system, Func<string, Task<IReadOnlyList<(string Id, string Name)>>> optionsFor,
        Func<string, Task<IReadOnlyList<ContentEntry>>>? itemsFor, CancellationToken ct)
    {
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
            WithSetting(campaign, $"{campaign.Name}{(campaign.Blurb(300) is { Length: > 0 } blurb ? ": " + blurb : "")}"),
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
        var picked = draft.Equipment is { Count: > 0 } equipment
            ? equipment
            : await GearOrNoneAsync(() => assistant.EquipAsync(new EquipRequest($"{draft.Name}: {concept.Trim()}\n{TextFormatting.Summary(draft.Biography ?? string.Empty, 600)}",
                catalog.GetDisplayName(campaign.SystemId), Options(carriable), armor != null), ct), ct);

        if (picked.Count > 0)
        {
            draft.Sheet[inventory.Key] = StoredEquipment(picked, carriable, inventory, armor);
        }

        return draft;
    }

    /// <summary>The most entries a draft has written for it: each is a model call, so a high-level caster with many open slots
    /// must not turn into one call per slot. The subclass counts, and a list gets at most one.</summary>
    internal const int MaxWrittenEntries = 2;

    /// <summary>The most options one choice is listed with. A high-level caster's whole spell list fits; a homebrew Library with
    /// thousands of entries of a type does not flood the prompt.</summary>
    private const int MaxChoiceOptions = 250;

    /// <summary>The second pass of a draft: what the rules leave to the player and depends on the class and level the model has
    /// just picked, so it could not be listed in the first prompt. The lists a sheet fills up to a count (spells, feats), and the
    /// reference fields the rules narrow (a subclass of that class). A field the first pass filled against the rules is cleared
    /// and asked again with the options that are allowed. If the call fails the draft stands without these picks.</summary>
    public async Task ChooseAsync(AiAssistant assistant, CharacterDraft draft, string concept, CampaignDocument campaign, SystemDefinition system, IEntryResolver entries, CancellationToken ct, WriteEntries write = WriteEntries.Off)
    {
        await ChooseListsAsync(assistant, draft, concept, campaign, system, entries, ct, write);
        await ChooseGrantsAsync(assistant, draft, concept, campaign, system, entries, ct);
    }

    /// <summary>The third pass: the choices the features of the drafted character leave to the player (an expertise pick, a skill), which only
    /// exist once the lists and the entries are settled. The sheet is read again, one <see cref="ChoiceSpec"/> goes to the model for each choice
    /// still open (its options are the keys the rules allow, with their names), and the answers are written as the player's picks
    /// (<see cref="SheetRuleEngine.SetChoice"/>). A choice the character's own ticks already cover is confirmed as they stand. If the call fails
    /// the choices stay open on the sheet, as a warning to make.</summary>
    private async Task ChooseGrantsAsync(AiAssistant assistant, CharacterDraft draft, string concept, CampaignDocument campaign, SystemDefinition system, IEntryResolver entries, CancellationToken ct)
    {
        if (system.SheetRules?.Skills == null || system.SheetRules.Features.Count == 0) return;

        var known = new WithProposed(entries, draft.Proposed.ToArray());
        var engine = new SheetRuleEngine(system, known);
        var evaluation = engine.Evaluate(SheetOf(draft, campaign, system, engine));
        foreach (var adopted in evaluation.PendingChoices.Where(c => c.Adopted)) SheetRuleEngine.SetChoice(draft.Sheet, adopted.Key, adopted.Made);

        var open = evaluation.PendingChoices.Where(c => !c.Adopted && c.Remaining > 0 && c.Options.Any(o => !c.Made.Contains(o.Key, StringComparer.OrdinalIgnoreCase))).ToList();
        if (open.Count == 0) return;

        var choices = open.Select(c =>
        {
            var from = c.Feature.Level is { } level ? $"{c.Source}, level {level}" : c.Source;
            var hint = c.Raise ? "Each pick is raised one rank." : null;
            var options = c.Options.Where(o => !c.Made.Contains(o.Key, StringComparer.OrdinalIgnoreCase)).ToList();
            return new ChoiceSpec(c.Key, $"{c.Label} ({from})", hint, c.Remaining, ChoiceOptions(options));
        }).ToList();

        var sheetKnown = string.Join(", ", draft.Sheet.Where(p => p.Value is string or long or int or double).Select(p => $"{p.Key} {p.Value}"));
        var character = $"{draft.Name}: {concept.Trim()}\n{TextFormatting.Summary(sheetKnown, 600)}\n{TextFormatting.Summary(draft.Biography ?? string.Empty, 600)}";
        IReadOnlyDictionary<string, IReadOnlyList<string>> picked;
        try
        {
            picked = await assistant.ChooseAsync(new ChoicesRequest(character, catalog.GetDisplayName(campaign.SystemId), choices), ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return;
        }

        foreach (var choice in open)
        {
            if (picked.TryGetValue(choice.Key, out var keys)) SheetRuleEngine.SetChoice(draft.Sheet, choice.Key, choice.Made.Concat(keys));
        }
    }

    private async Task ChooseListsAsync(AiAssistant assistant, CharacterDraft draft, string concept, CampaignDocument campaign, SystemDefinition system, IEntryResolver entries, CancellationToken ct, WriteEntries write)
    {
        var rules = system.SheetRules!;
        if (rules.Lists.Count == 0 && rules.Constraints.Count == 0) return;

        var engine = new SheetRuleEngine(system, entries);
        var initial = SheetOf(draft, campaign, system, engine);
        var evaluation = engine.Evaluate(initial);
        var choices = new List<ChoiceSpec>();
        var fields = new Dictionary<string, (FieldDefinition Field, FieldConstraint Constraint, ContentEntry? Example)>(StringComparer.OrdinalIgnoreCase);
        var withheld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var constraint in rules.Constraints)
        {
            var field = system.ActorFields.FirstOrDefault(f => f.Key == constraint.Field);
            if (field == null || !evaluation.AllowedOptions.TryGetValue(constraint.Field, out var allowed)) continue;
            var chosen = draft.Sheet.TryGetValue(field.Key, out var value) ? value?.ToString() : null;
            if (!string.IsNullOrEmpty(chosen) && allowed.Any(e => e.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase))) continue;

            draft.Sheet.Remove(field.Key);
            var writable = write != WriteEntries.Off && field.TargetEntityType != null && CanPropose(system, entries, field.TargetEntityType, constraint.Where, initial);
            if (allowed.Count == 0)
            {
                // A class that publishes no subclass still gets one once it is due, written with no example to copy the build from.
                if (writable && SubclassDue(rules.Multiclass, constraint.Field, evaluation))
                {
                    fields[field.Key] = (field, constraint, null);
                    withheld.Add(field.Key);
                }
                continue;
            }

            fields[field.Key] = (field, constraint, allowed[0]);
            // Always: the writer takes this field, so the model is not offered the Library's options for it.
            if (writable && write == WriteEntries.Always)
            {
                withheld.Add(field.Key);
                continue;
            }
            // Said only where one can be written: a model told to leave it empty with nothing to follow would just leave gaps.
            var hint = writable ? $"{field.Description} Leave it empty when none of these suits the character: one is then written for them.".Trim() : field.Description;
            choices.Add(new ChoiceSpec(field.Key, field.Label, hint, 1, ChoiceOptions(allowed)));
        }

        // The lists are read off the sheet as it stands with the fields above cleared.
        var current = fields.Count > 0 || evaluation.HasViolations ? SheetOf(draft, campaign, system, engine) : initial;
        if (!ReferenceEquals(current, initial)) evaluation = engine.Evaluate(current);
        var lists = new List<(ListRule List, ContentEntry Example, int Limit)>();
        // Always: a list slot is held open only while the writer has calls left for it after the fields it takes, so a caster
        // with many lists is not left with empty slots nobody writes for.
        var openSlots = MaxWrittenEntries - withheld.Count;
        var held = new Dictionary<string, IReadOnlyList<ContentEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in rules.Lists)
        {
            // A list with no count (spells a feature grants) is not a choice the rules can check; one already filled is left alone.
            if (evaluation.ListLimits.GetValueOrDefault(list.Key) is not { } limit || limit <= 0 || draft.Sheet.ContainsKey(list.Key)) continue;
            if (!evaluation.ListOptions.TryGetValue(list.Key, out var options) || options.Count == 0) continue;
            var hint = list.Description;
            var asked = limit;
            if (write != WriteEntries.Off && CanPropose(system, entries, list.TargetEntityType, list.Where, current))
            {
                hint = $"{list.Description} Pick fewer when none of these suits the character: each slot left open may be written for them.".Trim();
                lists.Add((list, options[0], limit));
                // Always: one slot stays open for the writer, so the model is asked for one pick fewer.
                if (write == WriteEntries.Always && openSlots > 0)
                {
                    asked = limit - 1;
                    openSlots--;
                    held[list.Key] = options;
                }
            }

            if (asked > 0) choices.Add(new ChoiceSpec(list.Key, list.Label, hint, asked, ChoiceOptions(options)));
        }

        if (choices.Count == 0 && fields.Count == 0 && lists.Count == 0) return;

        var known = string.Join(", ", draft.Sheet.Where(p => p.Value is string or long or int or double).Select(p => $"{p.Key} {p.Value}"));
        var character = $"{draft.Name}: {concept.Trim()}\n{TextFormatting.Summary(known, 600)}\n{TextFormatting.Summary(draft.Biography ?? string.Empty, 600)}";
        IReadOnlyDictionary<string, IReadOnlyList<string>> picked = new Dictionary<string, IReadOnlyList<string>>();
        if (choices.Count > 0)
        {
            try
            {
                picked = await assistant.ChooseAsync(new ChoicesRequest(character, catalog.GetDisplayName(campaign.SystemId), choices), ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return;
            }
        }

        foreach (var (key, ids) in picked)
        {
            draft.Sheet[key] = fields.ContainsKey(key) ? ids[0] : ids.Cast<object>().ToList();
        }

        if (write == WriteEntries.Off) return;

        // A call costs, so a draft gets only a few entries written: the subclass first, then one for each list left with a slot open.
        var budget = MaxWrittenEntries;
        foreach (var (key, (field, constraint, example)) in fields.Where(f => !picked.ContainsKey(f.Key)))
        {
            if (budget <= 0) break;
            if (await ProposeAsync(assistant, draft, character, campaign, system, entries, key, field.TargetEntityType!, constraint.Where, false, example, ct)) budget--;
        }

        foreach (var (list, example, limit) in lists)
        {
            if (budget <= 0) break;
            if (SheetRuleEngine.RawList(draft.Sheet, list.Key).Count >= limit) continue;
            if (await ProposeAsync(assistant, draft, character, campaign, system, entries, list.Key, list.TargetEntityType, list.Where, true, example, ct)) budget--;
        }

        if (write != WriteEntries.Always) return;

        // Always took choices away from the model; where the writer failed or its entry was refused, the Library fills them
        // instead, so asking for homebrew never leaves the character with less than asking for none.
        foreach (var key in withheld)
        {
            if (!draft.Sheet.ContainsKey(key) && fields[key].Example is { } fallback) draft.Sheet[key] = fallback.Id;
        }

        foreach (var (key, options) in held)
        {
            var have = SheetRuleEngine.RawList(draft.Sheet, key);
            var limit = lists.First(l => l.List.Key == key).Limit;
            if (have.Count >= limit) continue;
            var extra = options.FirstOrDefault(o => !have.Contains(o.Id, StringComparer.OrdinalIgnoreCase));
            if (extra != null) draft.Sheet[key] = have.Cast<object>().Append(extra.Id).ToList();
        }
    }

    /// <summary>Whether an entry can be written for a field or a list: its type is one the system describes, and every rule on it is
    /// one the writer can be made to meet (<see cref="EntryRequirements"/>).</summary>
    private static bool CanPropose(SystemDefinition system, IEntryResolver entries, string entityType, IReadOnlyList<RuleCondition> where, IReadOnlyDictionary<string, object> sheet) =>
        system.FindType(entityType) != null && EntryRequirements.For(where, system, entries, new SheetRuleEngine(system, entries), sheet) != null;

    /// <summary>Whether the first class has reached the level a subclass is chosen at, for the field the rules call the subclass.</summary>
    private static bool SubclassDue(MulticlassRules? multiclass, string field, SheetEvaluation evaluation) =>
        multiclass?.SubclassField is { } subclassField && subclassField.Equals(field, StringComparison.OrdinalIgnoreCase)
        && evaluation.Classes.FirstOrDefault(c => c.IsFirst) is { } first && first.Level >= multiclass.SubclassLevel;

    private static string Describe(object value) => value is IEnumerable<object> items ? string.Join(", ", items) : value.ToString() ?? string.Empty;

    /// <summary>Writes an entry for a field or a list slot no listed entry suited (a subclass, a spell), sets what the rules settle
    /// about it (the class it belongs to), and puts it on the sheet and among the draft's own entries. A call that fails leaves
    /// the field empty.</summary>
    private async Task<bool> ProposeAsync(AiAssistant assistant, CharacterDraft draft, string character, CampaignDocument campaign, SystemDefinition system, IEntryResolver entries,
        string key, string entityType, IReadOnlyList<RuleCondition> where, bool isList, ContentEntry? example, CancellationToken ct)
    {
        // The rules see what was already written for this draft.
        var known = new WithProposed(entries, draft.Proposed.ToArray());
        var engine = new SheetRuleEngine(system, known);
        var sheet = SheetOf(draft, campaign, system, engine);
        var type = system.FindType(entityType);
        if (type == null || EntryRequirements.For(where, system, known, engine, sheet) is not { } required) return false;

        var settled = required.Properties;
        var specs = type.EditorFields()
            .Where(f => f.Type is not (FieldType.ImagePicker or FieldType.EntityReference) && f.Key != "imagePrompt" && !settled.ContainsKey(f.Key))
            .Select(f => new FieldSpec(f.Key, f.Label, f.Type.ToString(), f.Type == FieldType.Dropdown ? f.Options ?? new List<string>() : null, f.Min, f.Max, f.Description))
            .Concat(EntryGuide.Extras(system, type.Type).Where(e => !settled.ContainsKey(e.Key)))
            .ToList();

        DraftEntry? written;
        try
        {
            written = await assistant.WriteEntryAsync(new EntryRequest(character, catalog.GetDisplayName(campaign.SystemId), type.Label.ToLowerInvariant(), specs,
                EntryGuide.For(system, type.Type), example == null ? null : BuildOf(example, type), settled.Select(s => $"{s.Key}: {Describe(s.Value)}").ToList(), Lang, required.Hints), ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return true;
        }

        if (written == null) return true;
        var slug = RavenHomebrewRepository.Slugify(written.Name);
        var id = slug;
        for (var n = 2; known.Resolve(type.Type, id) != null; n++) id = $"{slug}-{n}";

        var entry = new ContentEntry { Type = type.Type, Id = id, Name = written.Name, Description = written.Description, SystemId = system.Id, PackId = CarriedEntry.PackId, Source = "Homebrew" };
        foreach (var (property, value) in written.Properties) entry.Properties[property] = value;
        foreach (var (property, value) in settled) entry.Properties[property] = value;

        // What it grants to the ranked lists must be sound first: a choice needs an id and a count, ids are unique, ranks exist.
        if (FeatureGrantCheck.Problems(system, entry).Count > 0) return true;

        // The engine has the last word: the entry stays only if the sheet that picks it breaks no rule it did not break before.
        var picks = isList ? SheetRuleEngine.RawList(sheet, key).Cast<object>().Append(id).ToList() : null;
        var checker = new SheetRuleEngine(system, new WithProposed(known, entry));
        var before = checker.Evaluate(sheet).Violations.Where(v => v.FieldKey == key).Select(v => v.Message).ToHashSet();
        var withEntry = new Dictionary<string, object>(sheet) { [key] = picks ?? (object)id };
        if (checker.Evaluate(withEntry).Violations.Any(v => v.FieldKey == key && !before.Contains(v.Message))) return true;

        draft.Proposed.Add(entry);
        draft.Sheet[key] = picks ?? (object)id;
        return true;
    }

    // How an entry is built, without its words: for each of its tables, the levels its rows come at.
    private static string? BuildOf(ContentEntry example, ContentTypeDefinition type)
    {
        var parts = new List<string>();
        foreach (var field in type.Fields.Where(f => f.Type == FieldType.Collection))
        {
            if (!example.Properties.TryGetValue(field.Key, out var raw) || raw is not IEnumerable<object> rows) continue;
            var levels = rows.OfType<IDictionary<string, object>>().Select(r => r.TryGetValue("level", out var l) ? l?.ToString() : null).ToList();
            if (levels.Count == 0) continue;
            parts.Add(levels.All(l => l != null) ? $"{field.Key}: {levels.Count} rows, at levels {string.Join(", ", levels)}" : $"{field.Key}: {levels.Count} rows");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static List<EquipmentOption> ChoiceOptions(IReadOnlyList<ContentEntry> entries) =>
        entries.Take(MaxChoiceOptions).Select(e => new EquipmentOption(e.Id, e.Name)).ToList();

    // The keys of a ranked list (a skill, an attribute) are not entries: the key is the id the model answers with.
    private static List<EquipmentOption> ChoiceOptions(IReadOnlyList<ChoiceOption> options) =>
        options.Take(MaxChoiceOptions).Select(o => new EquipmentOption(o.Key, o.Label)).ToList();

    /// <summary>The sheet <paramref name="draft"/> becomes when it is saved, as far as the rules read it.</summary>
    private Dictionary<string, object> SheetOf(CharacterDraft draft, CampaignDocument campaign, SystemDefinition system, SheetRuleEngine engine)
    {
        var sheet = new Dictionary<string, object>();
        var schema = catalog.GetUISchemas(campaign.SystemId).FirstOrDefault(s => s.EntityType == DataDrivenSystemPlugin.ActorSchemaType);
        foreach (var field in schema?.Fields ?? new List<FieldDefinition>())
        {
            if (field.DefaultValue != null) sheet[field.Key] = field.DefaultValue is int i ? (long)i : field.DefaultValue;
        }

        engine.InitializeSheet(sheet);
        CastService.ApplyDraft(sheet, system, draft);
        return sheet;
    }

    /// <summary>The gear <paramref name="pick"/> chose, or none when it failed. A provider that timed out throws a cancellation
    /// too, but only the person's own stop (<paramref name="ct"/>) should cost them the rest of what the model made.</summary>
    public static async Task<IReadOnlyList<DraftItem>> GearOrNoneAsync(Func<Task<IReadOnlyList<DraftItem>>> pick, CancellationToken ct)
    {
        try
        {
            return await pick();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Array.Empty<DraftItem>();
        }
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

    /// <summary>Gear for several characters at once, a few calls at a time, as the proposal shows it; null when the system has no
    /// inventory or no model is set up. A character whose pick failed gets none.</summary>
    /// <param name="characters">Who each one is: a name and a line or a paragraph about them.</param>
    public async Task<IReadOnlyList<List<DraftGear>>?> PickGearAsync(IReadOnlyList<string> characters, CampaignDocument campaign, SystemDefinition? system,
        string userId, Func<string, Task<IReadOnlyList<ContentEntry>>> itemsFor, CancellationToken ct = default)
    {
        system = system?.Canonical;
        var inventory = system?.SheetRules?.Inventory;
        var armor = system?.SheetRules?.ArmorClass;
        var carriable = await CarriableAsync(inventory, itemsFor);
        if (inventory == null || carriable.Count == 0 || characters.Count == 0)
        {
            return null;
        }

        AiAssistant assistant;
        try
        {
            assistant = await providers.AssistantForAsync(userId);
        }
        catch (AiNotConfiguredException)
        {
            return null;
        }

        var options = Options(carriable);
        var systemName = catalog.GetDisplayName(campaign.SystemId);
        using var turns = new SemaphoreSlim(MaxGearCalls);
        return await Task.WhenAll(characters.Select(async character =>
        {
            await turns.WaitAsync(ct);
            try
            {
                var picked = await GearOrNoneAsync(() => assistant.EquipAsync(new EquipRequest(character, systemName, options, armor != null), ct), ct);
                return picked.Where(p => carriable.ContainsKey(p.Id))
                    .Select(p => new DraftGear { Id = carriable[p.Id].Id, Name = carriable[p.Id].Name, Quantity = Math.Max(1, p.Quantity), Equipped = p.Equipped })
                    .ToList();
            }
            finally
            {
                turns.Release();
            }
        }));
    }

    /// <summary>Gear picked earlier (<see cref="PickGearAsync"/>) as the sheet stores it, with no model call; null when there is
    /// none or the system has no inventory. Items no longer in the Library are left out.</summary>
    public async Task<List<object>?> StoredGearAsync(IReadOnlyList<DraftGear>? gear, SystemDefinition? system, Func<string, Task<IReadOnlyList<ContentEntry>>> itemsFor)
    {
        system = system?.Canonical;
        var inventory = system?.SheetRules?.Inventory;
        if (gear is not { Count: > 0 } || inventory == null)
        {
            return null;
        }

        var carriable = await CarriableAsync(inventory, itemsFor);
        var stored = StoredEquipment(gear.Select(g => new DraftItem(g.Id, g.Quantity, g.Equipped)), carriable, inventory, system?.SheetRules?.ArmorClass);
        return stored.Count == 0 ? null : stored;
    }

    /// <summary>Most gear picks that run at once for one proposal.</summary>
    private const int MaxGearCalls = 4;

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

/// <summary>When the character drafter writes a homebrew entry for the character.</summary>
public enum WriteEntries { Off, WhenNothingFits, Always }
