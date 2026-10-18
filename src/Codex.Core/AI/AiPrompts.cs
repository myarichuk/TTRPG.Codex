using System.Text;

namespace Codex.Core.AI;

/// <summary>Who a generated text is for. Player-facing output must never be built from DM-only material; the caller filters, these prompts only phrase.</summary>
public enum AiAudience
{
    Dm,
    Players
}

public sealed record SessionSummaryRequest(
    string CampaignName,
    string SystemName,
    string SessionTitle,
    AiAudience Audience,
    IReadOnlyList<string> Events,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Rolls,
    string? PreviousDmSummary = null);

public sealed record RewriteRequest(
    string Subject,
    string SubjectKind,
    IReadOnlyList<string> Facts,
    string CurrentText,
    string Instruction,
    AiAudience Audience,
    string? CampaignContext = null);

public sealed record LevelUpRequest(
    string CharacterName,
    string SystemName,
    string? ClassName,
    string? SubclassName,
    int CurrentLevel,
    int NextLevel,
    IReadOnlyList<string> SheetFacts,
    IReadOnlyList<string> NextLevelFeatures,
    IReadOnlyList<string> SubclassFeatures,
    IReadOnlyList<string> AvailableSubclasses,
    string? Biography);

public sealed record CharacterFieldSpec(string Key, string Label, string Type, IReadOnlyList<string>? Options, double? Min, double? Max);

public sealed record CharacterConceptRequest(
    string Concept,
    string Kind,
    string SystemName,
    IReadOnlyList<CharacterFieldSpec> Attributes,
    IReadOnlyList<CharacterFieldSpec> Fields,
    string? HitPointsKey,
    string? HitPointsMaxKey,
    string? CampaignContext = null);

/// <summary>Pure prompt builders: no network, no state, unit-testable.</summary>
public static class AiPrompts
{
    public const string Style = "Write in plain, concrete English. Use the markdown subset: **bold**, *italic*, \"- \" bullets, and short headings on their own line. No preamble, no closing remarks, no mention of being an AI.";

    public static (string System, string User) SessionSummary(SessionSummaryRequest r)
    {
        var system = r.Audience == AiAudience.Players
            ? "You are the chronicler of a tabletop role-playing campaign. Write the recap the players will read: an in-world account of what the party did and learned this session, in past tense, two to four short paragraphs, followed by a bullet list titled \"Loose threads\" with open questions the party is aware of. Only use what the log says; never invent events, names or outcomes, and never reveal anything the log marks as secret or private. " + Style
            : "You are the campaign assistant of a tabletop role-playing game master. Write the DM's private session summary with these headings: \"What happened\" (bullets), \"NPCs and places\" (bullets), \"Secrets still hidden\" (what the players do not yet know, from the secret or private material), \"Loose threads\" and \"Hooks for next session\" (two or three concrete suggestions). Only use what the log says; mark anything you infer with \"(inference)\". " + Style;

        var sb = new StringBuilder();
        sb.AppendLine($"Campaign: {r.CampaignName} ({r.SystemName})");
        sb.AppendLine($"Session: {(string.IsNullOrWhiteSpace(r.SessionTitle) ? "untitled" : r.SessionTitle)}");
        if (!string.IsNullOrWhiteSpace(r.PreviousDmSummary))
        {
            sb.AppendLine().AppendLine("Existing DM summary (revise it rather than starting over):").AppendLine(r.PreviousDmSummary);
        }

        AppendList(sb, "Event log", r.Events);
        AppendList(sb, "Table notes", r.Notes);
        AppendList(sb, "Dice rolls", r.Rolls);
        if (r.Events.Count == 0 && r.Notes.Count == 0 && r.Rolls.Count == 0)
        {
            sb.AppendLine().AppendLine("The log is empty. Say so in one sentence and stop.");
        }

        return (system, sb.ToString());
    }

    public static (string System, string User) Rewrite(RewriteRequest r)
    {
        var audience = r.Audience == AiAudience.Players
            ? "The text will be read by the players, so keep anything the facts mark as secret or DM-only out of it."
            : "The text is for the game master's eyes.";
        var system = $"You are a writing assistant for a tabletop role-playing campaign. You rewrite or write the history, personality and background of a {r.SubjectKind}. Keep every fact given; do not contradict the sheet; keep names as spelled. {audience} Reply with the finished text only. {Style}";

        var sb = new StringBuilder();
        sb.AppendLine($"Subject: {r.Subject} ({r.SubjectKind})");
        if (!string.IsNullOrWhiteSpace(r.CampaignContext))
        {
            sb.AppendLine($"Campaign: {r.CampaignContext}");
        }

        AppendList(sb, "Facts from the sheet", r.Facts);
        sb.AppendLine().AppendLine("Instruction: " + (string.IsNullOrWhiteSpace(r.Instruction) ? "Polish and expand the current text into two or three paragraphs." : r.Instruction.Trim()));
        sb.AppendLine().AppendLine("Current text:").AppendLine(string.IsNullOrWhiteSpace(r.CurrentText) ? "(none yet - write it from the facts)" : r.CurrentText);
        return (system, sb.ToString());
    }

    public static (string System, string User) LevelUp(LevelUpRequest r)
    {
        var system = $"You are a rules-savvy assistant for the {r.SystemName} tabletop game. A character is advancing from level {r.CurrentLevel} to {r.NextLevel}. Using only the features listed (they are the rules text), write: a heading \"What you gain at level {r.NextLevel}\" with bullets; a heading \"Choices to make\" listing every decision the features require (subclass, feats, spells, fighting styles...); a heading \"Suggested picks\" with one recommendation per choice and a one-line reason that fits the character's sheet and story; and a heading \"Sheet changes\" with the concrete numbers to update (hit points, proficiency bonus, new resources). If the features list is empty, say the Library has no entry for this class level and give general advice only. {Style}";

        var sb = new StringBuilder();
        sb.AppendLine($"Character: {r.CharacterName}");
        sb.AppendLine($"Class: {r.ClassName ?? "unknown"}{(r.SubclassName != null ? $" / {r.SubclassName}" : "")}");
        AppendList(sb, "Sheet", r.SheetFacts);
        if (!string.IsNullOrWhiteSpace(r.Biography))
        {
            sb.AppendLine().AppendLine("Story so far:").AppendLine(r.Biography.Length > 1500 ? r.Biography[..1500] + "…" : r.Biography);
        }

        AppendList(sb, $"Class features gained at level {r.NextLevel}", r.NextLevelFeatures);
        AppendList(sb, $"Subclass features gained at level {r.NextLevel}", r.SubclassFeatures);
        if (r.AvailableSubclasses.Count > 0)
        {
            AppendList(sb, "Subclasses available", r.AvailableSubclasses);
        }

        return (system, sb.ToString());
    }

    public static (string System, string User) CreateCharacter(CharacterConceptRequest r)
    {
        var system = $"You create {r.Kind} characters for the {r.SystemName} tabletop game. Reply with ONE JSON object and nothing else, shaped exactly like {{\"name\": string, \"sheet\": {{field key: value}}, \"biography\": string}}. Every sheet value must respect the field specification: numbers within min and max, dropdowns and references chosen from the listed options by their id, booleans as true/false, tag lists as arrays of strings, long text as a string. Omit a field rather than guessing an id that is not listed. The biography is two or three paragraphs of plain prose with a hook the game master can use.";

        var sb = new StringBuilder();
        sb.AppendLine("Concept: " + r.Concept.Trim());
        if (!string.IsNullOrWhiteSpace(r.CampaignContext))
        {
            sb.AppendLine("Campaign: " + r.CampaignContext);
        }

        sb.AppendLine().AppendLine("Attributes (key: label, min-max):");
        foreach (var a in r.Attributes)
        {
            sb.AppendLine($"- {a.Key}: {a.Label}, {a.Min?.ToString() ?? "?"}-{a.Max?.ToString() ?? "?"}");
        }

        sb.AppendLine().AppendLine("Fields (key: label, type, options):");
        foreach (var f in r.Fields)
        {
            var options = f.Options == null ? "" : f.Options.Count == 0 ? ", options: none loaded (omit)" : ", options: " + string.Join(" | ", f.Options);
            sb.AppendLine($"- {f.Key}: {f.Label}, {f.Type}{options}");
        }

        if (r.HitPointsKey != null)
        {
            sb.AppendLine().AppendLine($"Hit points: set \"{r.HitPointsKey}\"{(r.HitPointsMaxKey != null ? $" and \"{r.HitPointsMaxKey}\"" : "")} to a number appropriate for the level, class and constitution.");
        }

        return (system, sb.ToString());
    }

    private static void AppendList(StringBuilder sb, string heading, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        sb.AppendLine().AppendLine(heading + ":");
        foreach (var line in lines)
        {
            sb.AppendLine("- " + line);
        }
    }
}
