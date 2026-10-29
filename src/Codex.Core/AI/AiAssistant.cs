using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Codex.Core.AI;

/// <summary>An item a generated character carries: a library id, how many, and whether it is wielded or worn.</summary>
public sealed record DraftItem(string Id, int Quantity, bool Equipped);

/// <summary>A generated character: the sheet values keyed like the actor schema, plus prose and the gear they carry.</summary>
public sealed record CharacterDraft(string Name, Dictionary<string, object> Sheet, string? Biography, IReadOnlyList<DraftItem>? Equipment = null);

/// <summary>
/// The AI features, each one prompt in, one text out. Nothing here decides what the model may
/// see: callers build the request from already-filtered material (see <c>SessionDigest</c>).
/// </summary>
public sealed class AiAssistant(IChatClient chat)
{
    public Task<string> SummarizeSessionAsync(SessionSummaryRequest request, CancellationToken ct = default) =>
        CompleteAsync(AiPrompts.SessionSummary(request), ct);

    public Task<string> RewriteAsync(RewriteRequest request, CancellationToken ct = default) =>
        CompleteAsync(AiPrompts.Rewrite(request), ct);

    public Task<string> WriteWikiPageAsync(WikiPageRequest request, CancellationToken ct = default) =>
        CompleteAsync(AiPrompts.WikiPage(request), ct);

    public Task<string> DescribeAsync(DescribeRequest request, CancellationToken ct = default) =>
        CompleteAsync(AiPrompts.Describe(request), ct);

    public Task<string> SuggestLevelUpAsync(LevelUpRequest request, CancellationToken ct = default) =>
        CompleteAsync(AiPrompts.LevelUp(request), ct);

    public Task<CharacterDraft> CreateCharacterAsync(CharacterConceptRequest request, CancellationToken ct = default) =>
        JsonAsync(AiPrompts.CreateCharacter(request), text => ParseDraft(text, request), 0.8f, ct);

    // A JSON reply, parsed; a reply that does not parse gets one more chance, with the bad reply shown back to the model.
    private async Task<T> JsonAsync<T>((string System, string User) prompt, Func<string, T> parse, float temperature, CancellationToken ct)
    {
        var messages = Messages(prompt);
        var text = await JsonReplyAsync(messages, temperature, ct);
        try
        {
            return parse(text);
        }
        catch (FormatException)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, text));
            messages.Add(new ChatMessage(ChatRole.User, "That reply could not be used. Reply again with only the JSON object, shaped exactly as asked."));
            return parse(await JsonReplyAsync(messages, temperature, ct));
        }
    }

    private async Task<string> JsonReplyAsync(List<ChatMessage> messages, float temperature, CancellationToken ct)
    {
        try
        {
            var response = await chat.GetResponseAsync(messages, new ChatOptions { ResponseFormat = ChatResponseFormat.Json, Temperature = temperature }, ct);
            return response.Text;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Not every OpenAI-compatible server accepts response_format; ask again without it.
            var response = await chat.GetResponseAsync(messages, new ChatOptions { Temperature = temperature }, ct);
            return response.Text;
        }
    }

    private static List<ChatMessage> Messages((string System, string User) prompt) => new()
    {
        new(ChatRole.System, prompt.System),
        new(ChatRole.User, prompt.User)
    };

    /// <summary>Parses the model's reply, tolerating prose or code fences around the JSON, and
    /// coerces every sheet value to the type its field declares. Values the model got wrong
    /// (an unknown reference id, a number out of range) are dropped rather than saved.</summary>
    public static CharacterDraft ParseDraft(string text, CharacterConceptRequest request)
    {
        using var doc = FieldCoercion.ParseObject(text);
        var root = doc.RootElement;
        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "Unnamed";
        var biography = root.TryGetProperty("biography", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;

        var sheet = new Dictionary<string, object>();
        if (root.TryGetProperty("sheet", out var sheetElement) && sheetElement.ValueKind == JsonValueKind.Object)
        {
            var specs = request.Attributes.Concat(request.Fields).ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
            // A model told "str: Strength" sometimes answers under "Strength": an attribute is found by its label too.
            var byLabel = request.Attributes.Where(a => !string.IsNullOrWhiteSpace(a.Label)).GroupBy(a => a.Label.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var prop in sheetElement.EnumerateObject())
            {
                if (!specs.TryGetValue(prop.Name, out var spec)) byLabel.TryGetValue(prop.Name.Trim(), out spec);
                if (spec == null || spec.Type == "Collection")
                {
                    if (prop.Name == request.HitPointsKey || prop.Name == request.HitPointsMaxKey)
                    {
                        if (FieldCoercion.TryNumber(prop.Value, out var hp)) sheet[prop.Name] = (long)Math.Max(1, hp);
                    }

                    continue;
                }

                var value = FieldCoercion.Coerce(prop.Value, spec);
                if (value != null)
                {
                    sheet[spec.Key] = value;
                }
            }
        }

        return new CharacterDraft(string.IsNullOrWhiteSpace(name) ? "Unnamed" : name, sheet, biography, ParseEquipment(root, request));
    }

    private const int MaxItems = 40;
    private const int MaxQuantity = 99;

    /// <summary>Gear for an existing character: the listed items the model picked, none when it picked nothing usable.</summary>
    public async Task<IReadOnlyList<DraftItem>> EquipAsync(EquipRequest request, CancellationToken ct = default)
    {
        var text = await JsonReplyAsync(Messages(AiPrompts.Equip(request)), 0.7f, ct);
        using var doc = FieldCoercion.ParseObject(text);
        return ParseEquipment(doc.RootElement, request.Items) ?? new List<DraftItem>();
    }

    private static List<DraftItem>? ParseEquipment(JsonElement root, CharacterConceptRequest request) =>
        request.Equipment is { Count: > 0 } allowed ? ParseEquipment(root, allowed) : null;

    /// <summary>The reply's equipment, kept to the listed items (by id or, when the model wrote one, by name; case-insensitively,
    /// as listed); a repeated item adds up.</summary>
    private static List<DraftItem>? ParseEquipment(JsonElement root, IReadOnlyList<EquipmentOption> allowed)
    {
        if (!root.TryGetProperty("equipment", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in allowed) known.TryAdd(option.Id, option.Id);
        foreach (var option in allowed) known.TryAdd(option.Name, option.Id);
        var items = new Dictionary<string, DraftItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in array.EnumerateArray())
        {
            // A bare id is a single item; anything else is the object the prompt asked for.
            var id = element.ValueKind == JsonValueKind.String ? element.GetString()
                : element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString()
                : element.ValueKind == JsonValueKind.Object && element.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString()
                : null;
            if (id == null || !known.TryGetValue(id.Trim(), out var listed))
            {
                continue;
            }

            var quantity = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("quantity", out var q) && FieldCoercion.TryNumber(q, out var n)
                ? (int)Math.Clamp(Math.Round(n), 1, MaxQuantity)
                : 1;
            var equipped = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("equipped", out var e)
                && (e.ValueKind == JsonValueKind.True || (e.ValueKind == JsonValueKind.String && bool.TryParse(e.GetString(), out var flag) && flag));
            items[listed] = items.TryGetValue(listed, out var before)
                ? new DraftItem(listed, Math.Min(before.Quantity + quantity, MaxQuantity), before.Equipped || equipped)
                : new DraftItem(listed, quantity, equipped);
            if (items.Count >= MaxItems)
            {
                break;
            }
        }

        return items.Values.ToList();
    }

    private async Task<string> CompleteAsync((string System, string User) prompt, CancellationToken ct)
    {
        var response = await chat.GetResponseAsync(Messages(prompt), new ChatOptions { Temperature = 0.7f }, ct);
        var text = response.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            throw new InvalidOperationException("The model returned an empty reply.");
        }

        return text;
    }
}
