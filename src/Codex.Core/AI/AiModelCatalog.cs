using System.Reflection;
using System.Text.Json;

namespace Codex.Core.AI;

/// <summary>Which settings list a catalog entry belongs to: text models and image models are different lists on every service.</summary>
public enum AiModelPurpose { Text, Image }

/// <summary>A model the settings page offers as a suggestion. <see cref="Label"/> is a friendly name; the <see cref="Id"/> is what is sent to the service.</summary>
public sealed record AiModelOption(string Id, string? Label = null);

/// <summary>
/// The model ids the settings page suggests, so nobody has to look up the exact string a service wants. A suggestion only:
/// the field still takes any text, because a local server's models and OpenRouter's hundreds are not a list we can ship.
/// <para>
/// The bundled list is <c>AI/ai-models.json</c>, embedded in the assembly. A file of the same shape beside the server's data
/// folder (<see cref="FileName"/>, or <c>Codex:AiModelsFile</c>) is merged over it: for each purpose and service it names, its
/// list replaces the bundled one (an empty list switches the suggestions for that service off), and everything else keeps
/// the bundled list. That file is hand-edited, so a mistake in it never stops the server: the bundled list is used and
/// <see cref="Problem"/> says what was wrong.
/// </para>
/// Shape: <c>{ "text": { "anthropic": [ { "id": "claude-sonnet-5-5", "label": "Claude Sonnet 5.5" } ] }, "image": { … } }</c>,
/// where the service is a preset's kind (<c>AIClientFactory.Presets</c>, <c>AiImageGenerator.Presets</c>) and the label is optional.
/// </summary>
public sealed class AiModelCatalog
{
    public const string FileName = "ai-models.json";
    private const string ResourceName = "Codex.Core.AI.ai-models.json";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly Dictionary<(AiModelPurpose, string), IReadOnlyList<AiModelOption>> lists;

    private AiModelCatalog(Dictionary<(AiModelPurpose, string), IReadOnlyList<AiModelOption>> lists, string? problem)
    {
        this.lists = lists;
        Problem = problem;
    }

    /// <summary>What was wrong with the override file, when it could not be used; null when there is none or it was read.</summary>
    public string? Problem { get; }

    /// <summary>The suggestions for a service, in the order the file gives them; empty for a service with none (a custom server).</summary>
    public IReadOnlyList<AiModelOption> For(AiModelPurpose purpose, string? kind) =>
        lists.TryGetValue((purpose, Normal(kind)), out var list) ? list : [];

    /// <summary>The list shipped with the app.</summary>
    public static AiModelCatalog Bundled() => new(ReadBundled(), null);

    /// <summary>The bundled list with <paramref name="overridePath"/> merged over it, if that file exists and is usable.</summary>
    public static AiModelCatalog Load(string? overridePath)
    {
        var merged = ReadBundled();
        if (string.IsNullOrWhiteSpace(overridePath) || !File.Exists(overridePath))
        {
            return new AiModelCatalog(merged, null);
        }

        try
        {
            var fromFile = Parse(File.ReadAllText(overridePath));
            foreach (var (key, list) in fromFile)
            {
                merged[key] = list;
            }

            return new AiModelCatalog(merged, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Not the half-read file: either all of the override is used or none of it.
            return new AiModelCatalog(ReadBundled(), $"{overridePath} was not used: {ex.Message}");
        }
    }

    private static Dictionary<(AiModelPurpose, string), IReadOnlyList<AiModelOption>> ReadBundled()
    {
        using var stream = typeof(AiModelCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Reads the file's text. Entries without an id are skipped and a repeated id keeps its first place; anything
    /// not in the documented shape is an <see cref="InvalidDataException"/>, so the caller can tell the user what to fix.</summary>
    internal static Dictionary<(AiModelPurpose, string), IReadOnlyList<AiModelOption>> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, Lenient);
        var result = new Dictionary<(AiModelPurpose, string), IReadOnlyList<AiModelOption>>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("the file must be an object with \"text\" and \"image\" in it");
        }

        foreach (var section in doc.RootElement.EnumerateObject())
        {
            if (!Enum.TryParse<AiModelPurpose>(section.Name, ignoreCase: true, out var purpose) || section.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"\"{section.Name}\" is not \"text\" or \"image\", or is not an object of services");
            }

            foreach (var service in section.Value.EnumerateObject())
            {
                if (service.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"{section.Name}.{service.Name} must be a list of models");
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var options = new List<AiModelOption>();
                foreach (var item in service.Value.EnumerateArray())
                {
                    // A bare string is the id with no label, which is all most entries need.
                    var id = item.ValueKind == JsonValueKind.String ? item.GetString()
                        : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString()
                        : throw new InvalidDataException($"{section.Name}.{service.Name} has an entry that is neither a string nor an object with an \"id\"");
                    id = id?.Trim();
                    if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                    var label = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String
                        ? labelProp.GetString()?.Trim()
                        : null;
                    options.Add(new AiModelOption(id, string.IsNullOrEmpty(label) ? null : label));
                }

                result[(purpose, Normal(service.Name))] = options;
            }
        }

        return result;
    }

    private static string Normal(string? kind) => (kind ?? string.Empty).Trim().ToLowerInvariant();
}
