using System.Globalization;
using Codex.Core.WorldBuilder;
using Codex.Localization;
using Codex.Persistence;

namespace Codex.Web.Components.Campaign;

/// <summary>How the world builder shows what the model did: a line per tool call (an icon, what it did, how it came out, how long
/// it took), and the cost of a turn. One place, so the live turn and the stored replies read the same.</summary>
public static class WorldChatView
{
    private static readonly Dictionary<string, (string Icon, string Key)> Tools = new(StringComparer.Ordinal)
    {
        ["search_world"] = ("search", "WorldChat.Tool.Search"),
        ["get_page"] = ("file-text", "WorldChat.Tool.Page"),
        ["list_places"] = ("map-pin", "WorldChat.Tool.Places"),
        ["search_library"] = ("book-marked", "WorldChat.Tool.Library"),
        ["get_entry"] = ("scroll-text", "WorldChat.Tool.Entry"),
        ["homebrew_fields"] = ("list", "WorldChat.Tool.Fields"),
        ["list_tags"] = ("tags", "WorldChat.Tool.Tags"),
        ["add_entries"] = ("plus", "WorldChat.Tool.Add"),
        ["propose_changes"] = ("clipboard-check", "WorldChat.Tool.Propose")
    };

    public static string Icon(WorldChatStep step) => Tools.TryGetValue(step.Tool, out var tool) ? tool.Icon : "zap";

    /// <summary>"Searched the campaign for “salt”": what the call did, in the DM's language.</summary>
    public static string Label(Localizer l, WorldChatStep step)
    {
        if (!Tools.TryGetValue(step.Tool, out var tool)) return step.Tool;
        return step.Count is { } count ? l[tool.Key, count] : l[tool.Key, step.Subject ?? "…"];
    }

    /// <summary>How the call came out, in a word or two: still running, failed, a proposal refused with so many things to fix or
    /// taken, or how many lines a lookup returned.</summary>
    public static string Outcome(Localizer l, WorldChatStep step)
    {
        if (step.Status == WorldChatStepStatus.Running) return l["WorldChat.Step.Running"];
        if (step.Status == WorldChatStepStatus.Failed) return l["WorldChat.Step.Failed"];
        var result = step.Result ?? string.Empty;
        if (step.Tool is "propose_changes" or "add_entries")
        {
            return step.Rejected ? l["WorldChat.Step.Rejected", Math.Max(1, result.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal)))] : l["WorldChat.Step.Ready"];
        }

        return l["WorldChat.Step.Lines", result.Length == 0 ? 0 : result.Split('\n').Length];
    }

    public static string Tone(WorldChatStep step) => step.Status switch
    {
        WorldChatStepStatus.Running => "running",
        WorldChatStepStatus.Failed => "failed",
        _ => step.Rejected ? "rejected" : "done"
    };

    /// <summary>"0.4 s", "12 s", "2 min 5 s".</summary>
    public static string Duration(Localizer l, long milliseconds)
    {
        if (milliseconds < 10_000) return l["WorldChat.Seconds", (Math.Round(milliseconds / 100.0) / 10).ToString("0.0", CultureInfo.CurrentCulture)];
        var seconds = (long)Math.Round(milliseconds / 1000.0);
        return seconds < 60 ? l["WorldChat.Seconds", seconds.ToString(CultureInfo.CurrentCulture)] : l["WorldChat.Minutes", seconds / 60, seconds % 60];
    }

    /// <summary>The line under a reply: the model, how many calls it took, how long, and the tokens it used when the server said.</summary>
    public static IEnumerable<string> Usage(Localizer l, WorldChatUsage usage)
    {
        if (!string.IsNullOrWhiteSpace(usage.Model)) yield return usage.Model;
        if (usage.Rounds > 0) yield return l["WorldChat.Usage.Calls", usage.Rounds];
        yield return Duration(l, usage.DurationMs);
        if (usage.InputTokens is > 0 || usage.OutputTokens is > 0)
        {
            yield return l["WorldChat.Usage.Tokens", (usage.InputTokens ?? 0).ToString("N0", CultureInfo.CurrentCulture), (usage.OutputTokens ?? 0).ToString("N0", CultureInfo.CurrentCulture)];
        }
    }

    /// <summary>A step as raw JSON: everything that went over the wire and what the app knows about the call, for the DM who wants
    /// to see exactly what the model asked for and got.</summary>
    public static string Raw(WorldChatStep step, bool withResult = true)
    {
        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["tool"] = step.Tool,
            ["status"] = step.Status.ToString().ToLowerInvariant() + (step.Rejected ? " (rejected)" : ""),
            ["startedAt"] = step.StartedAt.ToString("o", CultureInfo.InvariantCulture),
            ["durationMs"] = step.DurationMs,
            ["arguments"] = Parse(step.Arguments),
            ["error"] = step.Error
        };
        if (withResult) node["result"] = step.Result;
        return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        static System.Text.Json.Nodes.JsonNode? Parse(string json)
        {
            try
            {
                return System.Text.Json.Nodes.JsonNode.Parse(json);
            }
            catch (System.Text.Json.JsonException)
            {
                return json;
            }
        }
    }

    /// <summary>Where a link from an applied proposal goes.</summary>
    public static string Href(string basePath, WorldChatLink link) => link.Kind switch
    {
        "page" or "change-page" => $"{basePath}/wiki/{link.Id}",
        "actor" or "change-actor" => $"{basePath}/actors/{link.Id}",
        "place" or "change-place" => $"{basePath}/world#place-{link.Id}",
        "homebrew" or "change-homebrew" => $"{basePath}/library/{link.Id}",
        "bestiary" => $"{basePath}/bestiary",
        "tags" => $"{basePath}/library",
        _ => $"{basePath}/lore"
    };

    public static string LinkIcon(WorldChatLink link) => link.Kind switch
    {
        "page" => "book-open",
        "actor" => "user",
        "place" => "map-pin",
        "homebrew" => "book-marked",
        "bestiary" => "paw-print",
        "lore" => "key-round",
        "tags" => "tags",
        _ => "edit-3"
    };

    /// <summary>The kinds in the order a proposal lists them: changes to what exists, then the world from the ground up, then rules.</summary>
    public static readonly string[] KindOrder =
        { DraftKinds.Edit, DraftKinds.Tags, DraftKinds.Place, DraftKinds.Npc, DraftKinds.Wiki, DraftKinds.Fact, DraftKinds.Homebrew, DraftKinds.Bestiary };

    public static string KindIcon(WorldDraft draft) => draft.Kind switch
    {
        DraftKinds.Place => "map-pin",
        DraftKinds.Npc => "user",
        DraftKinds.Wiki => draft.PageKind switch { "faction" => "crown", "quest" => "compass", _ => "book-open" },
        DraftKinds.Fact => "key-round",
        DraftKinds.Homebrew => draft.Bestiary ? "paw-print" : "book-marked",
        DraftKinds.Edit => "edit-3",
        DraftKinds.Tags => "tags",
        DraftKinds.Bestiary => "paw-print",
        _ => "circle"
    };
}
