using System.Globalization;
using Codex.Core.Consistency;
using Codex.Localization;

namespace Codex.Web.Services;

/// <summary>
/// An issue's sentence in the reader's language: made at read time from the issue's <see cref="Issue.Code"/> and <see cref="Issue.Args"/>, never
/// stored. The model keeps getting the English <see cref="Issue.Message"/>; only the app's screens go through here. Where the app has no text for
/// the code (a game system's own check) or the text cannot be filled from the arguments, the English message stands.
/// </summary>
public static class IssueText
{
    public static string Message(Localizer l, Issue issue)
    {
        if (!l.Has(issue.Code)) return issue.Message;
        try
        {
            // A number is passed as a number, so a plural in the sentence ("2 other entries") picks its form.
            var text = l.Get(issue.Code, issue.Args.Select(Arg).ToArray());
            // A blank left unfilled (fewer arguments than the text has places) is not a sentence to show.
            return text.Length == 0 || text == issue.Code || UnfilledBlank.IsMatch(text) ? issue.Message : text;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
        {
            return issue.Message;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex UnfilledBlank = new(@"\{\d+[,}]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static object Arg(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && text.Length < 10 && (text.Length == 1 || text[0] != '0') ? n : text;

    /// <summary>The localisation key of a severity's heading.</summary>
    public static string SeverityKey(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => "Campaign.Issues.Severity.Error",
        IssueSeverity.Warning => "Campaign.Issues.Severity.Warning",
        _ => "Campaign.Issues.Severity.Info"
    };

    /// <summary>The kinds the panel filters by, in the order it lists them.</summary>
    public static readonly IReadOnlyList<string> Kinds = new[] { "page", "actor", "place", "fact", "homebrew", "campaign" };
}

/// <summary>
/// What the last apply of a campaign's world builder did to its issues, kept in memory for the Issues panel to show (the DM's own screen; never stored,
/// never sent to anyone else). One entry per campaign: the latest apply that had a re-check.
/// </summary>
public sealed class IssueRecheckLog
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ApplyRecheck> latest = new(StringComparer.Ordinal);

    public void Record(string campaignId, ApplyRecheck recheck) => latest[campaignId] = recheck;

    public ApplyRecheck? Latest(string campaignId) => latest.TryGetValue(campaignId, out var r) ? r : null;

    public void Clear(string campaignId) => latest.TryRemove(campaignId, out _);
}
