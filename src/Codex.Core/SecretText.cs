using System.Text;
using System.Text.RegularExpressions;

namespace Codex.Core;

/// <summary>Who may read a block of a text.</summary>
public enum SecretAudience
{
    /// <summary>Text outside any block: everyone who may read the text.</summary>
    Everyone,

    /// <summary><c>:::dm</c>: the DM only.</summary>
    Dm,

    /// <summary><c>:::party</c>: a DM block the DM has revealed. Players read it as ordinary text; the DM sees it marked.</summary>
    Party,

    /// <summary><c>:::players</c>: what the players are told, written beside the truth. Players read it as ordinary text.</summary>
    Players
}

/// <summary>A run of lines of a text and who may read it. <paramref name="Block"/> is the 0-based number of the block
/// (counting every opening line in the text), or -1 for text outside any block.</summary>
public sealed record SecretSegment(SecretAudience Audience, string Text, int Block);

/// <summary>
/// The secret blocks of a DM's text (a wiki page, a place's description, a character's history):
/// <code>
/// :::dm
/// The lighthouse keeper runs the smugglers.
/// :::
/// </code>
/// A line <c>:::dm</c>, <c>:::party</c> or <c>:::players</c> opens a block and a line <c>:::</c> closes it. Blocks nest, and a
/// block inside a DM block stays the DM's whatever it says. Everything here fails closed, because the cost of a mistake is a
/// spoiler: a block that is never closed runs to the end of the text, an opening word this version does not know (a typo, or
/// <c>:::for</c>, which is reserved for revealing to single characters) is the DM's, and markers count inside code fences too.
/// Players are given <see cref="ForPlayers"/>, which the repositories apply before a text leaves the database for a player.
/// </summary>
public static class SecretText
{
    // ":::" alone closes; ":::word …" opens. Leading spaces are allowed so an indented marker is not mistaken for text.
    private static readonly Regex Marker = new(@"^\s*:::(?:\s*(?<word>[^\s:]\S*)(?<rest>.*))?\s*$", RegexOptions.Compiled);

    /// <summary>Whether the text holds any block marker at all (cheap check before parsing).</summary>
    public static bool HasMarkers(string? text) => !string.IsNullOrEmpty(text) && text.Contains(":::", StringComparison.Ordinal);

    /// <summary>Whether a player would read less of the text than the DM: it holds a DM block (revealed or not, a block is still
    /// worth the DM's attention only when it hides something).</summary>
    public static bool HasSecrets(string? text) => Parse(text).Any(s => s.Audience == SecretAudience.Dm);

    /// <summary>The text cut into runs of lines by who may read them, markers left out, in order. Adjacent lines with the
    /// same audience and block stay one segment.</summary>
    public static IReadOnlyList<SecretSegment> Parse(string? text)
    {
        var segments = new List<SecretSegment>();
        if (string.IsNullOrEmpty(text))
        {
            return segments;
        }

        if (!HasMarkers(text))
        {
            segments.Add(new SecretSegment(SecretAudience.Everyone, text, -1));
            return segments;
        }

        // The open blocks, innermost last: their audience and number.
        var open = new List<(SecretAudience Audience, int Block)>();
        var blocks = 0;
        var current = new StringBuilder();
        (SecretAudience Audience, int Block) currentKey = (SecretAudience.Everyone, -1);

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = Marker.Match(line);
            if (!m.Success)
            {
                current.Append(line).Append('\n');
                continue;
            }

            Flush();
            if (m.Groups["word"].Success)
            {
                var audience = AudienceOf(m.Groups["word"].Value);
                // Inside a DM block everything stays the DM's: a ":::party" pasted into a secret does not reveal it.
                if (open.Any(o => o.Audience == SecretAudience.Dm))
                {
                    audience = SecretAudience.Dm;
                }

                open.Add((audience, blocks++));
            }
            else if (open.Count > 0)
            {
                open.RemoveAt(open.Count - 1);
            }

            // A stray ":::" with nothing open is dropped, like any marker line.
            currentKey = open.Count == 0 ? (SecretAudience.Everyone, -1) : open[^1];
        }

        Flush();
        return segments;

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            var chunk = current.ToString().TrimEnd('\n');
            current.Clear();
            if (segments.Count > 0 && segments[^1].Audience == currentKey.Audience && segments[^1].Block == currentKey.Block)
            {
                segments[^1] = segments[^1] with { Text = segments[^1].Text + "\n" + chunk };
            }
            else
            {
                segments.Add(new SecretSegment(currentKey.Audience, chunk, currentKey.Block));
            }
        }
    }

    /// <summary>The text a player reads: DM blocks gone, revealed and player blocks as ordinary text, no marker lines.
    /// A text without markers comes back as it is.</summary>
    public static string ForPlayers(string? text)
    {
        if (!HasMarkers(text))
        {
            return text ?? string.Empty;
        }

        // Blank lines between the kept runs, so a paragraph before a removed block and one after it do not run together.
        return string.Join("\n\n", Parse(text).Where(s => s.Audience != SecretAudience.Dm).Select(s => s.Text.Trim('\n')).Where(t => t.Trim().Length > 0));
    }

    /// <summary>The whole text with the marker lines left out: for a one-line summary the DM reads.</summary>
    public static string WithoutMarkers(string? text)
    {
        if (!HasMarkers(text))
        {
            return text ?? string.Empty;
        }

        return string.Join("\n\n", Parse(text).Select(s => s.Text.Trim('\n')).Where(t => t.Trim().Length > 0));
    }

    /// <summary>
    /// What to store when a player saves a text the DM may have put secrets in (a player character's history): the player's
    /// <paramref name="edited"/> text, with the DM blocks of <paramref name="stored"/> kept at its end. Nothing the player never
    /// saw is lost, and nothing is revealed. A player cannot write DM blocks, so any in <paramref name="edited"/> are dropped
    /// first: that also makes this idempotent, which matters because a live table applies a save twice (to its cached copy,
    /// then that copy to the stored one).
    /// </summary>
    public static string? KeepSecrets(string? stored, string? edited)
    {
        var secrets = SecretBlocks(stored);
        edited = HasMarkers(edited) ? ForPlayers(edited) : edited;
        if (secrets.Count == 0)
        {
            return edited;
        }

        var kept = string.Join("\n\n", secrets.Select(s => $":::dm\n{s}\n:::"));
        return string.IsNullOrWhiteSpace(edited) ? kept : edited.TrimEnd() + "\n\n" + kept;
    }

    /// <summary>The text of each DM block, in order (nested DM blocks come back as their own entries).</summary>
    public static IReadOnlyList<string> SecretBlocks(string? text) =>
        Parse(text).Where(s => s.Audience == SecretAudience.Dm).GroupBy(s => s.Block).Select(g => string.Join("\n", g.Select(s => s.Text))).ToList();

    private static SecretAudience AudienceOf(string word) => word.ToLowerInvariant() switch
    {
        "party" or "revealed" => SecretAudience.Party,
        "players" or "player" => SecretAudience.Players,
        _ => SecretAudience.Dm
    };
}
