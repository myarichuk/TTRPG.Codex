using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codex.Core.Consistency;

/// <summary>
/// The model's view of a list of issues (<c>list_issues</c>): snake_case JSON, one issue to a line, in the order given (callers pass
/// <see cref="Issue.Order"/>). Pure: the same issues give the same text, byte for byte, so a repeated call changes nothing the model
/// reads. Long lists are cut at <see cref="MaxShown"/> with a closing line that says how many were left out.
/// </summary>
public static class IssueListing
{
    /// <summary>The most issues one listing shows. The rest are counted, and a narrower scope or severity reaches them.</summary>
    public const int MaxShown = 50;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>The names a severity filter accepts, most severe first.</summary>
    public static readonly IReadOnlyList<string> SeverityNames = new[] { "error", "warning", "info" };

    /// <summary>Reads the <c>severity</c> argument: empty is no filter. Otherwise the least severe level to list, so <c>warning</c> gives
    /// errors and warnings. False for any other word.</summary>
    public static bool TryParseSeverity(string? text, out IssueSeverity? severity)
    {
        severity = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        switch (text.Trim().ToLowerInvariant())
        {
            case "error": severity = IssueSeverity.Error; return true;
            case "warning": severity = IssueSeverity.Warning; return true;
            case "info": severity = IssueSeverity.Info; return true;
            default: return false;
        }
    }

    public static string SeverityName(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => "error",
        IssueSeverity.Warning => "warning",
        _ => "info"
    };

    public static string FixViaName(FixVia fixVia) => fixVia switch
    {
        FixVia.Page => "page",
        FixVia.Place => "place",
        FixVia.Fact => "fact",
        FixVia.ActorBiography => "actor-biography",
        FixVia.Sheet => "sheet",
        FixVia.Homebrew => "homebrew",
        _ => "report-only"
    };

    /// <summary>One issue as the model reads it.</summary>
    public static string One(Issue issue) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["id"] = issue.Id,
        ["check"] = issue.Check,
        ["severity"] = SeverityName(issue.Severity),
        ["entity_kind"] = issue.EntityKind,
        ["entity_id"] = issue.EntityId,
        ["field"] = issue.Field,
        ["code"] = issue.Code,
        ["args"] = issue.Args,
        ["message"] = issue.Message,
        ["fix_hint"] = issue.FixHint,
        ["bundle"] = issue.Bundle,
        ["fix_via"] = FixViaName(issue.FixVia)
    }, Json);

    /// <summary>The listing: a JSON array with one issue to a line, then, when some were cut, a line saying how many more there are.</summary>
    public static string Render(IReadOnlyList<Issue> issues)
    {
        if (issues.Count == 0) return "[]\n(no open issues match)";
        var shown = issues.Take(MaxShown).Select(One).ToList();
        var text = "[\n  " + string.Join(",\n  ", shown) + "\n]";
        return issues.Count > shown.Count
            ? text + $"\n({issues.Count - shown.Count} more not shown, of {issues.Count}; narrow with scope or severity to see them)"
            : text;
    }
}
