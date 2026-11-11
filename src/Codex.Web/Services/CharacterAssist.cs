using System.Globalization;
using System.Text;
using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.AI;

namespace Codex.Web.Services;

/// <summary>One request to change a character or highlight what fits it: the character as the caller sees it, the sheet as the page
/// holds it now (edits not yet saved included), and the Library the caller may use. What the model proposes is kept here.</summary>
public sealed class CharacterAssistSession
{
    /// <summary>The character: its name, kind and history, with mentions as plain names.</summary>
    public required ActorDocument Actor { get; init; }
    public required IReadOnlyDictionary<string, object> Sheet { get; init; }
    public required CampaignDocument Campaign { get; init; }
    public required CampaignAccess Access { get; init; }

    /// <summary>The definition the sheet is evaluated with.</summary>
    public required SystemDefinition System { get; init; }
    public required IEntryResolver Resolver { get; init; }

    public List<SheetChange>? Proposal { get; set; }
    public List<SheetHighlight>? Highlights { get; set; }
    public List<string> LooseWarnings { get; } = new();
}

/// <summary>
/// Asks the caller's model for changes to a character sheet, or for highlights that fit its concept (docs/SERVER_ACCESS_AND_VAULT.md
/// 6). The model reads the sheet and the system's fields and searches the caller's Library through tools, then proposes changes,
/// each a field, an operation, a value and a reason. Every change is checked here (the field exists and may be changed, the value
/// fits, the entry is in the caller's Library, the rules still hold) before the person sees it; a refused proposal goes back to the
/// model to fix. Nothing is saved here: the person takes the changes they want into the sheet, and the sheet's own Save stores them.
/// The same tools are meant for the companion MCP server.
/// </summary>
public sealed class CharacterAssist(CampaignLibrary library, ISystemCatalog catalog)
{
    /// <summary>Most model round trips one request may take, tool calls included, so a looping model can't run up a bill.</summary>
    public const int MaxToolRounds = 12;

    /// <summary>Most changes or highlights one proposal may hold.</summary>
    public const int MaxItems = 20;

    private const int MaxText = 2000;

    private enum FieldKind { Scalar, LongText, Attribute, List, Inventory, Skills }

    private sealed record FieldSpec(string Key, string Label, FieldKind Kind, string? Description = null, FieldDefinition? Field = null,
        AttributeDefinition? Attribute = null, ListRule? List = null, InventoryRules? Inventory = null, SkillRules? Skills = null);

    /// <summary>A session for <paramref name="actor"/> with the sheet as the page holds it, the Library read for the caller.</summary>
    public async Task<CharacterAssistSession?> StartAsync(ActorDocument actor, IReadOnlyDictionary<string, object> sheet, CampaignDocument campaign, CampaignAccess access)
    {
        if (catalog.GetDefinition(campaign.SystemId) is not { } system) return null;
        return new CharacterAssistSession
        {
            Actor = actor,
            Sheet = new Dictionary<string, object>(sheet),
            Campaign = campaign,
            Access = access,
            System = system,
            Resolver = await CachedEntryResolver.LoadAsync(library, campaign, access, system)
        };
    }

    /// <summary>Runs one request on <paramref name="model"/>: changes for <paramref name="instruction"/>, or with
    /// <paramref name="highlights"/> what on the sheet and in the Library fits the character.</summary>
    public async Task<CharacterAssistResult> RunAsync(CharacterAssistSession session, IChatClient model, string instruction, bool highlights, string? language, CancellationToken ct)
    {
        // Not disposed: disposing the pipeline disposes the model client under it, which the provider service keeps for every call.
        var client = new ChatClientBuilder(model)
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = MaxToolRounds;
                // An error in a tool goes back to the model as text it can act on, not as an exception here.
                f.IncludeDetailedErrors = true;
            })
            .Build();

        var prompt = AiPrompts.CharacterAssist(new CharacterAssistRequest(
            catalog.GetDisplayName(session.Campaign.SystemId),
            AiFeatures.KindLabel(session.Actor.Kind, session.System.Canonical),
            session.Access.IsDm,
            highlights,
            instruction,
            language));
        var messages = new List<ChatMessage> { new(ChatRole.System, prompt.System), new(ChatRole.User, prompt.User) };
        // A tool loop: every round sends the tools and the prompt again, which the provider's prompt cache can serve.
        var response = await client.GetResponseAsync(messages, PromptCachingChatClient.WithPromptCaching(new ChatOptions { Tools = Tools(session), Temperature = 0.7f }), ct);
        return new CharacterAssistResult
        {
            Changes = session.Proposal ?? new List<SheetChange>(),
            Highlights = session.Highlights ?? new List<SheetHighlight>(),
            Note = string.IsNullOrWhiteSpace(response.Text) ? null : response.Text.Trim(),
            Warnings = session.LooseWarnings.ToList()
        };
    }

    public IList<AITool> Tools(CharacterAssistSession session) => new List<AITool>
    {
        AIFunctionFactory.Create(() => WorldChat.Fenced(ReadSheet(session)), "read_sheet",
            "Reads the character: name, history, every field with its value, the attributes, the lists, the gear and the skills, and what the rules say about the sheet now."),
        AIFunctionFactory.Create(() => Fields(session), "sheet_fields",
            "Lists the fields a change may name, with what each takes and the operations it allows."),
        AIFunctionFactory.Create(async ([System.ComponentModel.Description("Words to look for; empty lists the type's entries.")] string? query,
                [System.ComponentModel.Description("The content type to search, as sheet_fields names it (\"spell\", \"feat\", \"equipment\"); empty searches them all.")] string? type) =>
                WorldChat.Fenced(await SearchLibraryAsync(session, query, type)), "search_library",
            "Searches the Library the character may use. Each line gives an entry's type/id and name; a change names the id."),
        AIFunctionFactory.Create(async ([System.ComponentModel.Description("The entry as search_library lists it: type/id.")] string entry) =>
                WorldChat.Fenced(await EntryAsync(session, entry)), "get_entry",
            "Reads one Library entry in full: its fields and its rules text."),
        AIFunctionFactory.Create(async (ChangesInput proposal) => await ProposeAsync(session, proposal), "propose_changes",
            "Proposes changes to the sheet, for the person to review and take one by one. Saves nothing. Returns errors to fix (then call it again with the whole corrected list) or confirms the proposal is ready."),
        AIFunctionFactory.Create(async (HighlightsInput highlights) => await HighlightAsync(session, highlights), "highlight",
            "Marks what on the sheet or in the Library fits the character, with why, for the person to take or ignore. Changes nothing. Returns errors to fix or confirms.")
    };

    // ---- What the model reads ----

    /// <summary>The character as the caller may see it: a player gets the history without secret blocks and never the GM's notes.</summary>
    public string ReadSheet(CharacterAssistSession s)
    {
        var sb = new StringBuilder();
        var actor = s.Actor;
        sb.AppendLine($"Name: {actor.Name} ({AiFeatures.KindLabel(actor.Kind, s.System.Canonical)})");
        var history = s.Access.IsDm ? actor.Biography : SecretText.ForPlayers(actor.Biography);
        if (!string.IsNullOrWhiteSpace(history)) sb.AppendLine("History: " + TextFormatting.Summary(history, 1500));
        if (s.Access.IsDm && !string.IsNullOrWhiteSpace(actor.DmNotes)) sb.AppendLine("Game master's notes: " + TextFormatting.Summary(actor.DmNotes, 800));

        sb.AppendLine().AppendLine("Sheet:");
        foreach (var spec in Specs(s.System))
        {
            sb.AppendLine($"- {spec.Key} ({spec.Label}): {Current(s, spec)}");
        }

        var evaluation = new SheetRuleEngine(s.System, s.Resolver).Evaluate(s.Sheet);
        var problems = evaluation.Violations.Concat(evaluation.Warnings).Select(v => v.Message).Distinct().ToList();
        if (problems.Count > 0)
        {
            sb.AppendLine().AppendLine("What the rules say about it now:");
            foreach (var problem in problems.Take(20)) sb.AppendLine("- " + problem);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The fields a change may name, with what each takes.</summary>
    public string Fields(CharacterAssistSession s)
    {
        var sb = new StringBuilder("Fields a change may name (key, label, what it takes, operations):\n");
        foreach (var spec in Specs(s.System))
        {
            var takes = spec.Kind switch
            {
                FieldKind.Attribute => $"a score from {spec.Attribute!.Min ?? 1} to {spec.Attribute.Max ?? 30}; set",
                FieldKind.LongText => "text; append adds a paragraph (the text already there stays)",
                FieldKind.List => $"entries of type \"{spec.List!.TargetEntityType}\"; add or remove one at a time",
                FieldKind.Inventory => $"items of type {string.Join(" or ", spec.Inventory!.Types.Select(t => $"\"{t}\""))}, with a quantity; add or remove",
                FieldKind.Skills => $"skills, by key: {string.Join(", ", spec.Skills!.List.Select(k => $"{k.Key} ({k.CanonicalLabel ?? k.Label})"))}; add or remove",
                _ => spec.Field!.Type switch
                {
                    FieldType.Number => $"a number{Range(spec.Field)}; set",
                    FieldType.Boolean => "true or false; set",
                    FieldType.Dropdown => $"one of: {string.Join(", ", spec.Field.Options ?? new List<string>())}; set",
                    FieldType.EntityReference => $"an entry of type \"{spec.Field.TargetEntityType}\"; set",
                    _ => "a short text; set"
                }
            };
            sb.AppendLine($"- {spec.Key} ({spec.Label}): {takes}.{(string.IsNullOrWhiteSpace(spec.Description) ? "" : " " + spec.Description.Trim())}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Range(FieldDefinition field) =>
        field.Min is { } min && field.Max is { } max ? $" from {min:0.##} to {max:0.##}"
        : field.Min is { } low ? $" of at least {low:0.##}"
        : field.Max is { } high ? $" of at most {high:0.##}"
        : "";

    public async Task<string> SearchLibraryAsync(CharacterAssistSession s, string? query, string? type)
    {
        var wanted = string.IsNullOrWhiteSpace(type) ? null : type.Trim();
        if (wanted != null && !library.CanRead(s.Campaign, s.Access, wanted)) return $"There is no type \"{wanted}\" you can use.";
        var found = (await library.SearchAsync(s.Campaign, s.Access, query?.Trim() ?? string.Empty, wanted, limit: 60))
            .Where(i => library.CanRead(s.Campaign, s.Access, i.Entry.Type))
            .Take(25)
            .Select(i => $"{i.Entry.Type}/{i.Entry.Id}: {i.Entry.Name}{Summary(i.Entry)}")
            .ToList();
        return found.Count == 0 ? "No Library entry matches." : string.Join('\n', found);
    }

    private static string Summary(ContentEntry entry)
    {
        var text = YamlValues.GetString(entry.Properties, "summary") ?? entry.Description;
        return string.IsNullOrWhiteSpace(text) ? "" : " - " + TextFormatting.Summary(text, 100);
    }

    public async Task<string> EntryAsync(CharacterAssistSession s, string key)
    {
        if (key.Trim().Split('/') is not [var type, var id] || type.Length == 0 || id.Length == 0)
        {
            return $"\"{key}\" is not an entry; give type/id as search_library lists it (\"spell/fireball\").";
        }

        if (await library.GetAsync(s.Campaign, s.Access, type, id) is not { } item) return $"There is no Library entry {type}/{id} you can use. Use search_library.";
        var sb = new StringBuilder($"# {item.Entry.Name} ({item.Entry.Type}/{item.Entry.Id})\n");
        foreach (var (name, value) in item.Entry.Properties.Take(40))
        {
            var shown = value is string text ? text : System.Text.Json.JsonSerializer.Serialize(value);
            sb.AppendLine($"{name}: {TextFormatting.Summary(shown, 400)}");
        }

        if (!string.IsNullOrWhiteSpace(item.Entry.Description)) sb.AppendLine().AppendLine(TextFormatting.Summary(item.Entry.Description, 3000));
        return sb.ToString().TrimEnd();
    }

    // ---- Proposals ----

    /// <summary>Checks the model's changes and keeps them for review, or says what to fix. Nothing is changed on the sheet.</summary>
    public async Task<string> ProposeAsync(CharacterAssistSession s, ChangesInput input)
    {
        var errors = new List<string>();
        var changes = new List<SheetChange>();
        var given = input.Changes ?? new List<ChangeInput>();
        if (given.Count == 0) errors.Add("The proposal has no changes.");
        if (given.Count > MaxItems) errors.Add($"Propose at most {MaxItems} changes.");

        var specs = Specs(s.System).ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (change, n) in given.Take(MaxItems).Select((c, i) => (c, i + 1)))
        {
            var (made, error) = await CheckAsync(s, specs, change);
            if (error != null) errors.Add($"Change {n}: {error}");
            else changes.Add(made!);
        }

        foreach (var twice in changes.Where(c => c.Op is SheetChangeOp.Set).GroupBy(c => c.Field, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"Two changes set {twice.Key}; propose one.");
        }

        foreach (var twice in changes.Where(c => c.Op is SheetChangeOp.Add or SheetChangeOp.Remove).GroupBy(c => (c.Field.ToLowerInvariant(), c.Op, c.Value)).Where(g => g.Count() > 1))
        {
            errors.Add($"The same change to {twice.First().Field} ({twice.First().Shown}) is there twice.");
        }

        if (errors.Count == 0)
        {
            // Taken together, the changes must leave a sheet the rules accept; what they only warn about goes with the change.
            var engine = new SheetRuleEngine(s.System, s.Resolver);
            var before = engine.Evaluate(s.Sheet);
            var (after, _) = Apply(s.System, s.Sheet, changes);
            var evaluation = engine.Evaluate(after);
            var known = before.Violations.Select(v => v.Message).ToHashSet(StringComparer.Ordinal);
            errors.AddRange(evaluation.Violations.Where(v => !known.Contains(v.Message)).Select(v => "The rules would not allow it: " + v.Message));

            var warned = before.Warnings.Select(v => v.Message).ToHashSet(StringComparer.Ordinal);
            s.LooseWarnings.Clear();
            foreach (var warning in evaluation.Warnings.Where(w => !warned.Contains(w.Message)))
            {
                if (changes.FirstOrDefault(c => string.Equals(c.Field, warning.FieldKey, StringComparison.OrdinalIgnoreCase)) is { } change) change.Warnings.Add(warning.Message);
                else s.LooseWarnings.Add(warning.Message);
            }
        }

        if (errors.Count > 0)
        {
            return "Nothing was proposed. Fix these and call propose_changes again with the whole corrected list:\n- " + string.Join("\n- ", errors);
        }

        s.Proposal = changes;
        return $"The proposal is ready for the person to review: {changes.Count} change{(changes.Count == 1 ? "" : "s")}. Say in a sentence or two what it does.";
    }

    private async Task<(SheetChange? Change, string? Error)> CheckAsync(CharacterAssistSession s, IReadOnlyDictionary<string, FieldSpec> specs, ChangeInput input)
    {
        var key = input.Field?.Trim() ?? string.Empty;
        if (!specs.TryGetValue(key, out var spec)) return (null, $"\"{key}\" is not a field a change may name; use a key from sheet_fields.");
        var reason = input.Reason?.Trim() ?? string.Empty;
        if (reason.Length == 0) return (null, "give a one-line reason.");
        if (reason.Length > 300) reason = reason[..300];
        var value = input.Value?.Trim() ?? string.Empty;
        var op = (input.Op?.Trim().ToLowerInvariant()) switch
        {
            "set" => SheetChangeOp.Set,
            "append" => SheetChangeOp.Append,
            "add" => SheetChangeOp.Add,
            "remove" => SheetChangeOp.Remove,
            _ => (SheetChangeOp?)null
        };
        var allowed = spec.Kind switch
        {
            FieldKind.LongText => new[] { SheetChangeOp.Append },
            FieldKind.List or FieldKind.Inventory or FieldKind.Skills => new[] { SheetChangeOp.Add, SheetChangeOp.Remove },
            _ => new[] { SheetChangeOp.Set }
        };
        if (op is not { } chosen || !allowed.Contains(chosen))
        {
            return (null, $"{spec.Key} takes {string.Join(" or ", allowed.Select(a => $"\"{a.ToString().ToLowerInvariant()}\""))}.");
        }

        SheetChange Made(string stored, string shown, int quantity = 1) => new()
        {
            Field = spec.Key,
            Label = spec.Label,
            Op = chosen,
            Value = stored,
            Shown = shown,
            Quantity = quantity,
            Before = chosen == SheetChangeOp.Set ? Raw(s.Sheet, spec) : null,
            BeforeShown = chosen == SheetChangeOp.Set ? ForPeople(s, spec) : null,
            Reason = reason
        };

        switch (spec.Kind)
        {
            case FieldKind.LongText:
                if (value.Length == 0) return (null, "the text to append is empty.");
                return (Made(value.Length > MaxText ? value[..MaxText] : value, value.Length > MaxText ? value[..MaxText] : value), null);

            case FieldKind.Attribute:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var score)) return (null, $"{spec.Key} takes a whole number.");
                if (score < (spec.Attribute!.Min ?? 1) || score > (spec.Attribute.Max ?? 30)) return (null, $"{spec.Key} takes a score from {spec.Attribute.Min ?? 1} to {spec.Attribute.Max ?? 30}.");
                return (Made(score.ToString(CultureInfo.InvariantCulture), score.ToString(CultureInfo.InvariantCulture)), null);

            case FieldKind.List:
            {
                var picks = SheetRuleEngine.RawList(s.Sheet, spec.Key).Select(r => SheetRuleEngine.ParsePick(r).Id).ToList();
                if (chosen == SheetChangeOp.Remove)
                {
                    return picks.Contains(value, StringComparer.OrdinalIgnoreCase)
                        ? (Made(picks.First(p => p.Equals(value, StringComparison.OrdinalIgnoreCase)), Name(s, spec.List!.TargetEntityType, value)), null)
                        : (null, $"{spec.Key} has no \"{value}\" to remove.");
                }

                if (await FindAsync(s, new[] { spec.List!.TargetEntityType }, value) is not { } entry) return (null, NotInLibrary(value, spec.List.TargetEntityType));
                if (picks.Contains(entry.Id, StringComparer.OrdinalIgnoreCase)) return (null, $"{spec.Key} already has {entry.Name}.");
                return (Made(entry.Id, entry.Name), null);
            }

            case FieldKind.Inventory:
            {
                var carried = SheetRuleEngine.RawList(s.Sheet, spec.Key).Select(r => SheetRuleEngine.ParsePick(r).Id).ToList();
                if (chosen == SheetChangeOp.Remove)
                {
                    return carried.FirstOrDefault(p => p.Equals(value, StringComparison.OrdinalIgnoreCase)) is { } id
                        ? (Made(id, Name(s, spec.Inventory!.Types, id)), null)
                        : (null, $"the inventory has no \"{value}\" to remove.");
                }

                if (await FindAsync(s, spec.Inventory!.Types, value) is not { } item) return (null, NotInLibrary(value, string.Join(" or ", spec.Inventory.Types)));
                var quantity = Math.Clamp(input.Quantity ?? 1, 1, 99);
                return (Made(item.Id, item.Name, quantity), null);
            }

            case FieldKind.Skills:
            {
                var skill = spec.Skills!.List.FirstOrDefault(k => k.Key.Equals(value, StringComparison.OrdinalIgnoreCase)
                    || k.Label.Equals(value, StringComparison.OrdinalIgnoreCase) || (k.CanonicalLabel?.Equals(value, StringComparison.OrdinalIgnoreCase) ?? false));
                if (skill == null) return (null, $"\"{value}\" is not a skill; use a key sheet_fields lists.");
                var has = SheetRuleEngine.RawList(s.Sheet, spec.Key).Any(r => r.Equals(skill.Key, StringComparison.OrdinalIgnoreCase));
                if (chosen == SheetChangeOp.Add && has) return (null, $"{spec.Key} already has {skill.Key}.");
                if (chosen == SheetChangeOp.Remove && !has) return (null, $"{spec.Key} has no {skill.Key} to remove.");
                return (Made(skill.Key, skill.CanonicalLabel ?? skill.Label), null);
            }
        }

        var field = spec.Field!;
        switch (field.Type)
        {
            case FieldType.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return (null, $"{spec.Key} takes a number.");
                if ((field.Min is { } min && number < min) || (field.Max is { } max && number > max)) return (null, $"{spec.Key} takes a number{Range(field)}.");
                var text = number.ToString("0.##", CultureInfo.InvariantCulture);
                return (Made(text, text), null);
            case FieldType.Boolean:
                return value.ToLowerInvariant() switch
                {
                    "true" or "yes" => (Made("true", "yes"), null),
                    "false" or "no" => (Made("false", "no"), null),
                    _ => (null, $"{spec.Key} takes true or false.")
                };
            case FieldType.Dropdown:
                return (field.Options ?? new List<string>()).FirstOrDefault(o => o.Equals(value, StringComparison.OrdinalIgnoreCase)) is { } option
                    ? (Made(option, option), null)
                    : (null, $"{spec.Key} takes one of: {string.Join(", ", field.Options ?? new List<string>())}.");
            case FieldType.EntityReference:
                return await FindAsync(s, new[] { field.TargetEntityType! }, value) is { } entry
                    ? (Made(entry.Id, entry.Name), null)
                    : (null, NotInLibrary(value, field.TargetEntityType!));
            default:
                if (value.Length == 0) return (null, $"{spec.Key} needs a value.");
                return (Made(value.Length > 200 ? value[..200] : value, value.Length > 200 ? value[..200] : value), null);
        }
    }

    private static string NotInLibrary(string value, string type) =>
        $"\"{value}\" is not an entry of type {type} in the Library this character may use; find one with search_library, or put it in a text field as your own words.";

    /// <summary>The entry of one of <paramref name="types"/> by id, or by its exact name, among those the caller may use.</summary>
    private async Task<ContentEntry?> FindAsync(CharacterAssistSession s, IEnumerable<string> types, string value)
    {
        if (value.Length == 0) return null;
        var id = value.Contains('/') ? value[(value.IndexOf('/') + 1)..] : value;
        foreach (var type in types)
        {
            if (!library.CanRead(s.Campaign, s.Access, type)) continue;
            if (s.Resolver.Resolve(type, id) is { } byId) return byId;
            var named = (await library.SearchAsync(s.Campaign, s.Access, value, type, limit: 10))
                .FirstOrDefault(i => i.Entry.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (named != null && s.Resolver.Resolve(type, named.Entry.Id) is { } byName) return byName;
        }

        return null;
    }

    // ---- Highlights ----

    public async Task<string> HighlightAsync(CharacterAssistSession s, HighlightsInput input)
    {
        var errors = new List<string>();
        var found = new List<SheetHighlight>();
        var given = input.Highlights ?? new List<HighlightInput>();
        if (given.Count == 0) errors.Add("There are no highlights.");
        var specs = Specs(s.System).ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (h, n) in given.Take(MaxItems).Select((h, i) => (h, i + 1)))
        {
            var key = h.Field?.Trim() ?? string.Empty;
            var reason = h.Reason?.Trim() ?? string.Empty;
            if (!specs.TryGetValue(key, out var spec))
            {
                errors.Add($"Highlight {n}: \"{key}\" is not a field; use a key from sheet_fields.");
                continue;
            }

            if (reason.Length == 0)
            {
                errors.Add($"Highlight {n}: give a one-line reason.");
                continue;
            }

            var value = h.Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                found.Add(new SheetHighlight(spec.Key, spec.Label, null, null, reason));
                continue;
            }

            var types = spec.Kind switch
            {
                FieldKind.List => new[] { spec.List!.TargetEntityType },
                FieldKind.Inventory => spec.Inventory!.Types.ToArray(),
                FieldKind.Scalar when spec.Field!.Type == FieldType.EntityReference => new[] { spec.Field.TargetEntityType! },
                _ => Array.Empty<string>()
            };
            if (spec.Kind == FieldKind.Skills)
            {
                var skill = spec.Skills!.List.FirstOrDefault(k => k.Key.Equals(value, StringComparison.OrdinalIgnoreCase) || k.Label.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (skill == null) errors.Add($"Highlight {n}: \"{value}\" is not a skill.");
                else found.Add(new SheetHighlight(spec.Key, spec.Label, skill.Key, skill.CanonicalLabel ?? skill.Label, reason));
            }
            else if (types.Length > 0)
            {
                if (await FindAsync(s, types, value) is { } entry) found.Add(new SheetHighlight(spec.Key, spec.Label, entry.Id, entry.Name, reason));
                else errors.Add($"Highlight {n}: {NotInLibrary(value, string.Join(" or ", types))}");
            }
            else
            {
                found.Add(new SheetHighlight(spec.Key, spec.Label, null, value.Length > 120 ? value[..120] : value, reason));
            }
        }

        if (errors.Count > 0) return "Nothing was highlighted. Fix these and call highlight again with the whole list:\n- " + string.Join("\n- ", errors);
        s.Highlights = found;
        return $"{found.Count} highlight{(found.Count == 1 ? "" : "s")} ready for the person. Say in a sentence what ties them together.";
    }

    // ---- Taking changes into the sheet ----

    /// <summary>A copy of <paramref name="sheet"/> with <paramref name="changes"/> made, and the changes it could not make because
    /// the sheet moved on since they were proposed: a field no longer holding what the change replaces, an entry already there or
    /// already gone. A list only ever gains or loses the one entry a change names.</summary>
    public static (Dictionary<string, object> Sheet, List<SheetChange> Skipped) Apply(SystemDefinition system, IReadOnlyDictionary<string, object> sheet, IEnumerable<SheetChange> changes)
    {
        var copy = new Dictionary<string, object>(sheet);
        var skipped = new List<SheetChange>();
        var specs = Specs(system).ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (!specs.TryGetValue(change.Field, out var spec) || !ApplyOne(copy, spec, change)) skipped.Add(change);
        }

        return (copy, skipped);
    }

    private static bool ApplyOne(Dictionary<string, object> sheet, FieldSpec spec, SheetChange change)
    {
        switch (change.Op)
        {
            case SheetChangeOp.Set:
                if (!string.Equals(Raw(sheet, spec), change.Before, StringComparison.Ordinal)) return false;
                sheet[spec.Key] = spec.Kind == FieldKind.Attribute ? long.Parse(change.Value, CultureInfo.InvariantCulture)
                    : spec.Field?.Type == FieldType.Number ? Number(change.Value)
                    : spec.Field?.Type == FieldType.Boolean ? change.Value == "true"
                    : change.Value;
                return true;

            case SheetChangeOp.Append:
                var existing = YamlValues.GetString(sheet, spec.Key) ?? string.Empty;
                sheet[spec.Key] = (existing.TrimEnd() + "\n\n" + change.Value).Trim();
                return true;

            case SheetChangeOp.Add:
            {
                var list = SheetRuleEngine.RawList(sheet, spec.Key);
                if (spec.Kind == FieldKind.Inventory)
                {
                    var at = list.FindIndex(r => SheetRuleEngine.ParsePick(r).Id.Equals(change.Value, StringComparison.OrdinalIgnoreCase));
                    var quantityKey = spec.Inventory!.QuantityChoice;
                    if (at >= 0)
                    {
                        var (id, choices) = SheetRuleEngine.ParsePick(list[at]);
                        choices[quantityKey] = Math.Max(1, choices.GetValueOrDefault(quantityKey, 1)) + change.Quantity;
                        list[at] = SheetRuleEngine.FormatPick(id, choices);
                    }
                    else
                    {
                        list.Add(SheetRuleEngine.FormatPick(change.Value, new Dictionary<string, int> { [quantityKey] = change.Quantity }));
                    }
                }
                else
                {
                    if (list.Any(r => SheetRuleEngine.ParsePick(r).Id.Equals(change.Value, StringComparison.OrdinalIgnoreCase))) return false;
                    list.Add(change.Value);
                }

                sheet[spec.Key] = list.Cast<object>().ToList();
                return true;
            }

            case SheetChangeOp.Remove:
            {
                var list = SheetRuleEngine.RawList(sheet, spec.Key);
                var at = list.FindIndex(r => SheetRuleEngine.ParsePick(r).Id.Equals(change.Value, StringComparison.OrdinalIgnoreCase));
                if (at < 0) return false;
                list.RemoveAt(at);
                sheet[spec.Key] = list.Cast<object>().ToList();
                return true;
            }
        }

        return false;
    }

    private static object Number(string text)
    {
        var number = double.Parse(text, CultureInfo.InvariantCulture);
        return number == Math.Floor(number) ? (object)(long)number : number;
    }

    // ---- The fields ----

    /// <summary>What a change may name: the system's own fields (not its derived values, resources or images), the attributes'
    /// base scores, the pick lists, the inventory and the skills. Other classes are not offered.</summary>
    private static IEnumerable<FieldSpec> Specs(SystemDefinition system)
    {
        var canonical = system.Canonical;
        foreach (var field in canonical.ActorFields)
        {
            switch (field.Type)
            {
                case FieldType.Text or FieldType.Number or FieldType.Boolean or FieldType.Dropdown:
                    yield return new FieldSpec(field.Key, field.Label, FieldKind.Scalar, field.Description, Field: field);
                    break;
                case FieldType.EntityReference when !string.IsNullOrEmpty(field.TargetEntityType):
                    yield return new FieldSpec(field.Key, field.Label, FieldKind.Scalar, field.Description, Field: field);
                    break;
                case FieldType.LongText:
                    yield return new FieldSpec(field.Key, field.Label, FieldKind.LongText, field.Description, Field: field);
                    break;
            }
        }

        foreach (var attribute in canonical.Attributes)
        {
            yield return new FieldSpec(SheetRuleEngine.BasePrefix + attribute.Key, attribute.CanonicalLabel ?? attribute.Label, FieldKind.Attribute,
                "The base score, before species, feats and items.", Attribute: attribute);
        }

        var rules = canonical.SheetRules;
        foreach (var list in rules?.Lists ?? new List<ListRule>())
        {
            yield return new FieldSpec(list.Key, list.Label, FieldKind.List, list.Description, List: list);
        }

        if (rules?.Inventory is { } inventory) yield return new FieldSpec(inventory.Key, inventory.Label, FieldKind.Inventory, Inventory: inventory);
        if (rules?.Skills is { List.Count: > 0 } skills)
        {
            yield return new FieldSpec(skills.ProficienciesKey, skills.Label, FieldKind.Skills, "Skills the character is proficient in.", Skills: skills);
            yield return new FieldSpec(skills.ExpertiseKey, skills.Label + " expertise", FieldKind.Skills, "Expertise: a second tick on a skill already proficient.", Skills: skills);
        }
    }

    /// <summary>The field's stored value as text, for telling whether a change still applies; null when empty.</summary>
    private static string? Raw(IReadOnlyDictionary<string, object> sheet, FieldSpec spec)
    {
        if (!sheet.TryGetValue(spec.Key, out var value) || value == null) return null;
        var text = value switch
        {
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>The field as a person reads it now: entries by name with their id, lists in order.</summary>
    private static string Current(CharacterAssistSession s, FieldSpec spec)
    {
        switch (spec.Kind)
        {
            case FieldKind.Attribute:
                return (YamlValues.GetInt(s.Sheet, spec.Key) ?? YamlValues.GetInt(s.Sheet, spec.Attribute!.Key) ?? spec.Attribute!.Default).ToString(CultureInfo.InvariantCulture);
            case FieldKind.List:
                return Listed(SheetRuleEngine.RawList(s.Sheet, spec.Key).Select(r => SheetRuleEngine.ParsePick(r).Id), id => Name(s, spec.List!.TargetEntityType, id));
            case FieldKind.Inventory:
                return Listed(SheetRuleEngine.RawList(s.Sheet, spec.Key), raw =>
                {
                    var (id, choices) = SheetRuleEngine.ParsePick(raw);
                    var quantity = choices.GetValueOrDefault(spec.Inventory!.QuantityChoice, 1);
                    return Name(s, spec.Inventory.Types, id) + (quantity > 1 ? $" x{quantity}" : "") + (choices.GetValueOrDefault(spec.Inventory.EquippedChoice) > 0 ? ", worn or held" : "");
                });
            case FieldKind.Skills:
                return Listed(SheetRuleEngine.RawList(s.Sheet, spec.Key), key => key);
            case FieldKind.LongText:
                return Raw(s.Sheet, spec) is { } text ? TextFormatting.Summary(text, 600) : "(empty)";
        }

        var raw = Raw(s.Sheet, spec);
        if (raw == null) return "(empty)";
        return spec.Field!.Type == FieldType.EntityReference ? Name(s, spec.Field.TargetEntityType!, raw) : raw;
    }

    /// <summary>A field's value as the review shows it: an entry by its name alone; null when empty.</summary>
    private static string? ForPeople(CharacterAssistSession s, FieldSpec spec) =>
        spec.Kind == FieldKind.Attribute ? Current(s, spec)
        : Raw(s.Sheet, spec) is not { } raw ? null
        : spec.Field?.Type == FieldType.EntityReference ? s.Resolver.Resolve(spec.Field.TargetEntityType!, raw)?.Name ?? raw
        : raw;

    private static string Listed(IEnumerable<string> items, Func<string, string> show)
    {
        var shown = items.Select(show).ToList();
        return shown.Count == 0 ? "(none)" : string.Join("; ", shown);
    }

    private static string Name(CharacterAssistSession s, string type, string id) => Name(s, new[] { type }, id);

    private static string Name(CharacterAssistSession s, IEnumerable<string> types, string id) =>
        types.Select(t => s.Resolver.Resolve(t, id)?.Name).FirstOrDefault(n => n != null) is { } name ? $"{name} [{id}]" : $"[{id}, not in the Library]";
}
