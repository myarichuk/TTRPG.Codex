using System.Text.Json;
using Codex.Core.WorldBuilder;
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
        var text = await JsonReplyAsync(Messages(AiPrompts.CreateCharacter(request)), 0.8f, ct);
        return ParseDraft(text, request);
    }

    /// <summary>The drafts a world-builder recipe proposes, checked against <paramref name="rules"/>.</summary>
    public Task<WorldBundle> OutlineWorldAsync(WorldOutlineRequest request, BundleRules rules, CancellationToken ct = default) =>
        JsonAsync(AiPrompts.WorldOutline(request), text => WorldBundleParser.ParseOutline(text, rules), 0.9f, ct);

    /// <summary>The outline <paramref name="previous"/> with the change in <paramref name="request"/> made, its drafts kept where the reply keeps them.</summary>
    public Task<WorldBundle> ReviseWorldAsync(WorldOutlineRequest request, BundleRules rules, WorldBundle previous, CancellationToken ct = default) =>
        JsonAsync(AiPrompts.WorldOutline(request), text => WorldBundleParser.ParseRevision(text, rules, previous), 0.8f, ct);

    /// <summary>Writes a homebrew draft in full: its description, tags and fields.</summary>
    /// <param name="canonical">Maps a value the model wrote in the reader's language back to the stored one (field key, value).</param>
    public Task ExpandHomebrewAsync(WorldDraft draft, WorldExpandRequest request, Func<string, string, string>? canonical = null, CancellationToken ct = default) =>
        JsonAsync(AiPrompts.WorldExpandHomebrew(request), text =>
        {
            WorldBundleParser.ApplyHomebrew(draft, text, request.Fields, canonical);
            return true;
        }, 0.8f, ct);

    /// <summary>Writes a wiki draft's page.</summary>
    public async Task ExpandPageAsync(WorldDraft draft, WorldExpandRequest request, CancellationToken ct = default) =>
        WorldBundleParser.ApplyPage(draft, await CompleteAsync(AiPrompts.WorldExpandPage(request), ct));

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
            foreach (var prop in sheetElement.EnumerateObject())
            {
                if (!specs.TryGetValue(prop.Name, out var spec) || spec.Type == "Collection")
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

        return new CharacterDraft(string.IsNullOrWhiteSpace(name) ? "Unnamed" : name, sheet, biography);
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
