using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Codex.Core.AI;

/// <summary>A generated character: the sheet values keyed like the actor schema, plus prose.</summary>
public sealed record CharacterDraft(string Name, Dictionary<string, object> Sheet, string? Biography);

/// <summary>
/// The AI features, each one prompt in, one text out. Nothing here decides what the model may
/// see: callers build the request from already-filtered material (see <c>SessionDigest</c>).
/// </summary>
public sealed class AiAssistant(IChatClient chat)
{
    private static readonly Regex JsonObject = new(@"\{[\s\S]*\}", RegexOptions.Compiled);

    public Task<string> SummarizeSessionAsync(SessionSummaryRequest request, CancellationToken ct = default) =>
        CompleteAsync(AiPrompts.SessionSummary(request), ct);

    public Task<string> RewriteAsync(RewriteRequest request, CancellationToken ct = default) =>
        CompleteAsync(AiPrompts.Rewrite(request), ct);

    public Task<string> DescribeAsync(DescribeRequest request, CancellationToken ct = default) =>
        CompleteAsync(AiPrompts.Describe(request), ct);

    public Task<string> SuggestLevelUpAsync(LevelUpRequest request, CancellationToken ct = default) =>
        CompleteAsync(AiPrompts.LevelUp(request), ct);

    public async Task<CharacterDraft> CreateCharacterAsync(CharacterConceptRequest request, CancellationToken ct = default)
    {
        var (system, user) = AiPrompts.CreateCharacter(request);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, user)
        };

        string text;
        try
        {
            var response = await chat.GetResponseAsync(messages, new ChatOptions { ResponseFormat = ChatResponseFormat.Json, Temperature = 0.8f }, ct);
            text = response.Text;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Not every OpenAI-compatible server accepts response_format; ask again without it.
            var response = await chat.GetResponseAsync(messages, new ChatOptions { Temperature = 0.8f }, ct);
            text = response.Text;
        }

        return ParseDraft(text, request);
    }

    /// <summary>Parses the model's reply, tolerating prose or code fences around the JSON, and
    /// coerces every sheet value to the type its field declares. Values the model got wrong
    /// (an unknown reference id, a number out of range) are dropped rather than saved.</summary>
    public static CharacterDraft ParseDraft(string text, CharacterConceptRequest request)
    {
        var match = JsonObject.Match(text ?? string.Empty);
        if (!match.Success)
        {
            throw new FormatException("The model did not return a JSON object.");
        }

        using var doc = JsonDocument.Parse(match.Value);
        var root = doc.RootElement;
        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "Unnamed";
        var biography = root.TryGetProperty("biography", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;

        var sheet = new Dictionary<string, object>();
        if (root.TryGetProperty("sheet", out var sheetElement) && sheetElement.ValueKind == JsonValueKind.Object)
        {
            var specs = request.Attributes.Concat(request.Fields).ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var prop in sheetElement.EnumerateObject())
            {
                if (!specs.TryGetValue(prop.Name, out var spec))
                {
                    if (prop.Name == request.HitPointsKey || prop.Name == request.HitPointsMaxKey)
                    {
                        if (TryNumber(prop.Value, out var hp)) sheet[prop.Name] = (long)Math.Max(1, hp);
                    }

                    continue;
                }

                var value = Coerce(prop.Value, spec);
                if (value != null)
                {
                    sheet[spec.Key] = value;
                }
            }
        }

        return new CharacterDraft(string.IsNullOrWhiteSpace(name) ? "Unnamed" : name, sheet, biography);
    }

    private static object? Coerce(JsonElement value, CharacterFieldSpec spec)
    {
        switch (spec.Type)
        {
            case "Number":
                if (!TryNumber(value, out var number)) return null;
                if (spec.Min.HasValue) number = Math.Max(spec.Min.Value, number);
                if (spec.Max.HasValue) number = Math.Min(spec.Max.Value, number);
                return (long)Math.Round(number);
            case "Boolean":
                return value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : null,
                    _ => null
                };
            case "TagList":
                if (value.ValueKind == JsonValueKind.Array)
                {
                    return value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => (object)e.GetString()!).ToList();
                }

                return value.ValueKind == JsonValueKind.String ? value.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => (object)s).ToList() : null;
            case "Dropdown":
            case "EntityReference":
                if (value.ValueKind != JsonValueKind.String) return null;
                var chosen = value.GetString()!.Trim();
                if (spec.Options == null || spec.Options.Count == 0) return chosen;
                // Options are "id" or "id (label)"; accept either the id or the label, case-insensitively.
                foreach (var option in spec.Options)
                {
                    var id = option.Split(" (")[0].Trim();
                    var label = option.Contains(" (") ? option[(option.IndexOf(" (", StringComparison.Ordinal) + 2)..].TrimEnd(')') : id;
                    if (string.Equals(id, chosen, StringComparison.OrdinalIgnoreCase) || string.Equals(label, chosen, StringComparison.OrdinalIgnoreCase))
                    {
                        return id;
                    }
                }

                return null;
            default:
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                    _ => null
                };
        }
    }

    private static bool TryNumber(JsonElement value, out double number)
    {
        number = 0;
        if (value.ValueKind == JsonValueKind.Number)
        {
            number = value.GetDouble();
            return true;
        }

        return value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    private async Task<string> CompleteAsync((string System, string User) prompt, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, prompt.System),
            new(ChatRole.User, prompt.User)
        };
        var response = await chat.GetResponseAsync(messages, new ChatOptions { Temperature = 0.7f }, ct);
        var text = response.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            throw new InvalidOperationException("The model returned an empty reply.");
        }

        return text;
    }
}
