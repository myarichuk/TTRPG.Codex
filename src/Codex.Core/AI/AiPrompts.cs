using System.Text;
using Codex.Core.WorldBuilder;

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
    string? PreviousDmSummary = null,
    IReadOnlyList<string>? Quotes = null,
    string? Language = null);

public sealed record RewriteRequest(
    string Subject,
    string SubjectKind,
    IReadOnlyList<string> Facts,
    string CurrentText,
    string Instruction,
    AiAudience Audience,
    string? CampaignContext = null,
    string? Language = null);

/// <summary>A description for a Library entry (a homebrew creature, spell, item) or a place on the world map.</summary>
public sealed record DescribeRequest(
    string Subject,
    string SubjectKind,
    string SystemName,
    IReadOnlyList<string> Facts,
    string CurrentText,
    string Instruction,
    AiAudience Audience,
    string? CampaignContext = null,
    string? Language = null);

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
    string? Biography,
    string? Language = null,
    IReadOnlyList<string>? Choices = null,
    IReadOnlyList<string>? SheetChanges = null);

/// <summary>A field the model fills, described the way a prompt lists it. <paramref name="Hint"/> is the field's own help text.</summary>
public sealed record FieldSpec(string Key, string Label, string Type, IReadOnlyList<string>? Options, double? Min, double? Max, string? Hint = null);

/// <summary>A world-builder question as the model reads it, with the DM's answer ("surprise me" when left to the model).</summary>
public sealed record WorldAnswer(string Question, string Answer);

/// <summary>The outline call of a world-builder recipe: propose the drafts, by name and one-line pitch.</summary>
/// <param name="Kinds">Draft kinds allowed, as "kind: what it is".</param>
/// <param name="HomebrewTypes">Content types a homebrew draft may have, as "id (label)".</param>
/// <param name="ExistingPages">Wiki pages already written, as "existing:slug (title)", which drafts may link to.</param>
/// <param name="TakenNames">Names the campaign already uses, so the model does not repeat them.</param>
/// <param name="Current">For a change to an outline: its drafts, one JSON object each in the reply's shape (no texts).</param>
/// <param name="Instruction">For a change to an outline: what the game master asked to change.</param>
/// <param name="CreatureStats">The system has creatures with stat blocks, so a person may name the one that plays like them.</param>
/// <param name="Creature">What the system calls such a creature (<see cref="Codex.Plugin.Abstractions.AiVocabulary.Creature"/>).</param>
/// <param name="CreatureExamples">Plain roles to show the model, as the system words them; null for the neutral few.</param>
public sealed record WorldOutlineRequest(
    string Brief,
    string SystemName,
    IReadOnlyList<WorldAnswer> Answers,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> HomebrewTypes,
    IReadOnlyList<string> ExistingPages,
    IReadOnlyList<string> TakenNames,
    string? CampaignContext = null,
    string? Language = null,
    IReadOnlyList<string>? Current = null,
    string? Instruction = null,
    bool CreatureStats = false,
    string Creature = "creature",
    IReadOnlyList<string>? CreatureExamples = null);

/// <summary>The expand call for one draft of an outline: write it in full.</summary>
/// <param name="TypeLabel">For a homebrew draft, its content type as system.yaml names it ("Spell").</param>
/// <param name="Fields">For a homebrew draft, the fields to fill.</param>
/// <param name="Related">The bundle's other drafts, as "Name (kind): pitch".</param>
/// <param name="Kind">The draft's kind (<see cref="DraftKinds"/>), which picks how a page or a secret is written.</param>
/// <param name="Guidance">The recipe's own instructions for this kind (<see cref="WorldRecipe.Write"/>), if any.</param>
/// <param name="Instruction">A change the game master asked for, made to <paramref name="CurrentText"/>.</param>
/// <param name="CurrentText">The draft as written so far: a page's text, or a homebrew entry as its reply JSON.</param>
public sealed record WorldExpandRequest(
    string Name,
    string Pitch,
    string? TypeLabel,
    string SystemName,
    IReadOnlyList<FieldSpec> Fields,
    IReadOnlyList<WorldAnswer> Answers,
    IReadOnlyList<string> Related,
    IReadOnlyList<string> ExistingPages,
    string? CampaignContext = null,
    string? Language = null,
    string Kind = DraftKinds.Wiki,
    string? Guidance = null,
    string? Instruction = null,
    string? CurrentText = null);

/// <summary>The character the model is asked to make from a concept.</summary>
/// <param name="Equipment">The items a sheet may carry, or null when the system has no inventory (the reply then has no equipment).</param>
/// <param name="OneArmorAndShield">The rules let a character wear one suit of body armor and carry one shield, no more.</param>
public sealed record CharacterConceptRequest(
    string Concept,
    string Kind,
    string SystemName,
    IReadOnlyList<FieldSpec> Attributes,
    IReadOnlyList<FieldSpec> Fields,
    string? HitPointsKey,
    string? HitPointsMaxKey,
    string? CampaignContext = null,
    string? Language = null,
    string? HitPointsLabel = null,
    string? HitPointsBasis = null,
    IReadOnlyList<EquipmentOption>? Equipment = null,
    bool OneArmorAndShield = false);

/// <summary>An item a character may carry: its Library id and the name a model knows it by.</summary>
public sealed record EquipmentOption(string Id, string Name);

/// <summary>Gear for a character who already exists: one made by hand or from a stat block, or one whose model left the gear out.</summary>
/// <param name="Character">Who they are, in a line or a paragraph.</param>
public sealed record EquipRequest(string Character, string SystemName, IReadOnlyList<EquipmentOption> Items, bool OneArmorAndShield);

/// <summary>Pure prompt builders: no network, no state, unit-testable.</summary>
public static class AiPrompts
{
    public const string Style = "Write in plain, concrete English. Use the markdown subset: **bold**, *italic*, \"- \" bullets, and short headings on their own line. No preamble, no closing remarks, no mention of being an AI.";

    /// <summary>The language a text is written in: a tag ("ru", "he-IL") or null for English.</summary>
    public static string LanguageName(string? language) =>
        (language ?? "en").Split('-', '_')[0].ToLowerInvariant() switch
        {
            "ru" => "Russian",
            "he" or "iw" => "Hebrew",
            _ => "English"
        };

    /// <summary><see cref="Style"/> for the requested language. English is byte-for-byte the constant; any other
    /// language is told to write everything in it, headings included, since the prompts name their headings in English.</summary>
    public static string StyleFor(string? language)
    {
        var name = LanguageName(language);
        return name == "English"
            ? Style
            : Style.Replace("plain, concrete English", $"plain, concrete {name}")
                + $" Write the entire reply in {name}, and translate every heading you are asked for into {name}.";
    }

    public static (string System, string User) SessionSummary(SessionSummaryRequest r)
    {
        var system = r.Audience == AiAudience.Players
            ? "You are the chronicler of a tabletop role-playing campaign. Write the recap the players will read: an in-world account of what the party did and learned this session, in past tense, two to four short paragraphs, followed by a bullet list titled \"Loose threads\" with open questions the party is aware of. Only use what the log says; never invent events, names or outcomes, and never reveal anything the log marks as secret or private. " + StyleFor(r.Language)
            : "You are the campaign assistant of a tabletop role-playing game master. Write the DM's private session summary with these headings: \"What happened\" (bullets), \"NPCs and places\" (bullets), \"Secrets still hidden\" (what the players do not yet know, from the secret or private material), \"Loose threads\" and \"Hooks for next session\" (two or three concrete suggestions). Only use what the log says; mark anything you infer with \"(inference)\". " + StyleFor(r.Language);

        var sb = new StringBuilder();
        sb.AppendLine($"Campaign: {r.CampaignName} ({r.SystemName})");
        sb.AppendLine($"Session: {(string.IsNullOrWhiteSpace(r.SessionTitle) ? "untitled" : r.SessionTitle)}");
        if (!string.IsNullOrWhiteSpace(r.PreviousDmSummary))
        {
            sb.AppendLine().AppendLine("Existing DM summary (revise it rather than starting over):").AppendLine(r.PreviousDmSummary);
        }

        AppendList(sb, "Event log", r.Events);
        AppendList(sb, "Table notes ([Clue], [Loot], [Ruling] and [Follow-up] say what a note records; \"(Round 3 · Grik's turn)\" says when in a fight it was written)", r.Notes);
        AppendList(sb, "Dice rolls", r.Rolls);
        AppendList(sb, "Things said at the table (quote the lines that matter word for word; never put words in anyone's mouth)", r.Quotes ?? Array.Empty<string>());
        if (r.Events.Count == 0 && r.Notes.Count == 0 && r.Rolls.Count == 0 && (r.Quotes?.Count ?? 0) == 0)
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
        var system = $"You are a writing assistant for a tabletop role-playing campaign. You rewrite or write the history, personality and background of a {r.SubjectKind}. Keep every fact given; do not contradict the sheet; keep names as spelled. {audience} Reply with the finished text only. {StyleFor(r.Language)}";

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

    public static (string System, string User) Describe(DescribeRequest r)
    {
        var audience = r.Audience == AiAudience.Players
            ? "Players will read this, so describe only what someone standing there would see, hear and learn; no secrets, no game-master asides."
            : "The text is for the game master's eyes and may include hooks and secrets.";
        var system = $"You are a writing assistant for a {r.SystemName} tabletop campaign. You write the description of a {r.SubjectKind}: vivid, concrete and usable at the table, two short paragraphs unless told otherwise. Keep every fact given and never contradict the numbers or names. {audience} Reply with the finished text only. {StyleFor(r.Language)}";

        var sb = new StringBuilder();
        sb.AppendLine($"Subject: {r.Subject} ({r.SubjectKind})");
        if (!string.IsNullOrWhiteSpace(r.CampaignContext))
        {
            sb.AppendLine($"Campaign: {r.CampaignContext}");
        }

        AppendList(sb, "Facts", r.Facts);
        sb.AppendLine().AppendLine("Instruction: " + (string.IsNullOrWhiteSpace(r.Instruction)
            ? (string.IsNullOrWhiteSpace(r.CurrentText) ? "Write the description from the facts." : "Polish and enrich the current text; keep what it says.")
            : r.Instruction.Trim()));
        sb.AppendLine().AppendLine("Current text:").AppendLine(string.IsNullOrWhiteSpace(r.CurrentText) ? "(none yet)" : r.CurrentText);
        return (system, sb.ToString());
    }

    /// <summary>A text-to-image prompt: the subject, its visual description, and a house style that keeps lettering and frames out.</summary>
    public static string Image(string subject, string subjectKind, string? visual, ArtShape shape)
    {
        // A square is a character's avatar, shown small and cropped: the face has to carry it.
        var shot = shape switch
        {
            ArtShape.Wide => "Wide establishing view",
            ArtShape.Square => "Head-and-shoulders portrait, face clearly visible,",
            _ => "Full-figure portrait"
        };
        var details = string.IsNullOrWhiteSpace(visual) ? string.Empty : " " + visual.Trim().TrimEnd('.') + ".";
        return $"{shot} of {subject}, a {subjectKind}.{details} Painterly fantasy illustration, dramatic natural light, rich muted colours. No text, no lettering, no border, no watermark.";
    }

    public static (string System, string User) LevelUp(LevelUpRequest r)
    {
        var system = $"You are a rules-savvy assistant for the {r.SystemName} tabletop game. A character is advancing from level {r.CurrentLevel} to {r.NextLevel}. Using only the features listed (they are the rules text), write: a heading \"What you gain at level {r.NextLevel}\" with bullets; a heading \"Choices to make\" listing every decision the features require{ListOf(r.Choices)}; a heading \"Suggested picks\" with one recommendation per choice and a one-line reason that fits the character's sheet and story; and a heading \"Sheet changes\" with the concrete numbers to update{ListOf(r.SheetChanges)}. If the features list is empty, say the Library has no entry for this level and give general advice only. {StyleFor(r.Language)}";

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
        // The gear is part of the one shape the reply follows: asked for as an afterthought, models tend to leave it out.
        var gear = r.Equipment is { Count: > 0 };
        var shape = gear
            ? $"{{\"name\": string, \"sheet\": {{field key: value}}, \"biography\": string, {EquipmentShape}}}"
            : "{\"name\": string, \"sheet\": {field key: value}, \"biography\": string}";
        var system = $"You create {r.Kind} characters for the {r.SystemName} tabletop game. Reply with ONE JSON object and nothing else, shaped exactly like {shape}. Every sheet value must respect the field specification: numbers within min and max, dropdowns and references chosen from the listed options by their id, booleans as true/false, tag lists as arrays of strings, long text as a string. Omit a field rather than guessing an id that is not listed. The biography is two or three paragraphs of plain prose with a hook the game master can use." + CharacterLanguageNote(r.Language);

        if (gear)
        {
            system += " " + EquipmentRules(r.OneArmorAndShield);
        }

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
            var basis = string.IsNullOrWhiteSpace(r.HitPointsBasis) ? "the rest of the sheet" : r.HitPointsBasis.Trim();
            sb.AppendLine().AppendLine($"{(string.IsNullOrWhiteSpace(r.HitPointsLabel) ? "Starting resource" : r.HitPointsLabel.Trim())}: set \"{r.HitPointsKey}\"{(r.HitPointsMaxKey != null ? $" and \"{r.HitPointsMaxKey}\"" : "")} to a number appropriate for {basis}.");
        }

        AppendItems(sb, r.Equipment);

        return (system, sb.ToString());
    }

    private const string EquipmentShape = "\"equipment\": [{\"id\": string, \"quantity\": number, \"equipped\": boolean}]";

    private static string EquipmentRules(bool oneArmorAndShield) =>
        "\"equipment\" is the gear the character carries and is never empty; each \"id\" is one of the listed item ids (the part before the colon)." +
        " Pick what a person like this would plausibly own and use: their weapons, protection, tools and everyday gear (usually 3 to 10 entries), with sensible quantities, and an extraordinary item only when the concept calls for one." +
        " Mark as equipped what they wield or wear" + (oneArmorAndShield ? " (at most one suit of body armor and one shield)" : "") + ".";

    // "id: name", so the model can recognise an item it would otherwise have to guess from a slug.
    private static void AppendItems(StringBuilder sb, IReadOnlyList<EquipmentOption>? items)
    {
        if (items is not { Count: > 0 }) return;
        sb.AppendLine().AppendLine("Items (id: name; equipment may use only these ids):");
        foreach (var item in items)
        {
            sb.AppendLine(string.Equals(item.Id, item.Name, StringComparison.OrdinalIgnoreCase) ? $"- {item.Id}" : $"- {item.Id}: {item.Name}");
        }
    }

    public static (string System, string User) Equip(EquipRequest r)
    {
        var system = $"You equip characters for the {r.SystemName} tabletop game. Reply with ONE JSON object and nothing else, shaped exactly like {{{EquipmentShape}}}. " + EquipmentRules(r.OneArmorAndShield);
        var sb = new StringBuilder();
        sb.AppendLine("Character: " + r.Character.Trim());
        AppendItems(sb, r.Items);
        return (system, sb.ToString());
    }

    public static (string System, string User) WorldOutline(WorldOutlineRequest r)
    {
        var name = LanguageName(r.Language);
        var language = name == "English"
            ? ""
            : $" Write the names and pitches in {name}; keep every JSON key, kind, type id, ref and existing key exactly as listed.";
        var system = $"You help the game master of a {r.SystemName} tabletop campaign build their world. {r.Brief.Trim()} " +
                     "Reply with ONE JSON object and nothing else, shaped exactly like {\"drafts\": [{\"ref\": \"d1\", \"kind\": string, \"type\": string, \"name\": string, \"pitch\": string, \"links\": [string]}]}. " +
                     "\"kind\" is one of the listed kinds; \"type\" is only for kind \"homebrew\" and is one of the listed content type ids. " +
                     "\"ref\" is a short id (\"d1\", \"d2\") that other drafts use in \"links\"; a link may also be one of the listed \"existing:\" keys. " +
                     "The pitch is one sentence the game master can judge the idea by. " +
                     "Invent original names; never use the names of published settings, products, or of characters, places, deities or creatures that belong to a company, and do not reuse the names already taken. " +
                     "Where an answer is \"surprise me\", decide it yourself." + language;
        if (r.CreatureStats && r.Kinds.Any(k => k.StartsWith(DraftKinds.Npc + ":", StringComparison.Ordinal)))
        {
            // The stat block's name is looked up in the Library, so it must be the rules' own English name, not an invented one.
            var examples = r.CreatureExamples is { Count: > 0 } ? r.CreatureExamples : DefaultCreatureExamples;
            system += $" A \"npc\" draft may also have \"stats\": the English name of a generic {r.Creature} from the {r.SystemName} rules whose game statistics suit the person " +
                      $"(such as {string.Join(", ", examples.Take(examples.Count - 1))}{(examples.Count > 1 ? " or " : "")}{examples[^1]}), exactly as the rules name it; leave it out when none fits.";
        }

        var change = Instruction(r.Instruction);
        if (change != null)
        {
            system += " The game master already has an outline, listed as JSON, and asks for a change to it. Reply with the whole revised outline in the same shape: " +
                      "keep every draft the change does not touch as it is, with its ref; keep the ref of a draft you change; give new drafts new refs; " +
                      "leave out the drafts the change removes.";
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(r.CampaignContext))
        {
            sb.AppendLine("Campaign: " + r.CampaignContext);
        }

        AppendList(sb, "The game master's answers", r.Answers.Select(a => $"{a.Question} {a.Answer}").ToList());
        AppendList(sb, "Kinds", r.Kinds);
        AppendList(sb, "Content type ids (label)", r.HomebrewTypes);
        AppendList(sb, "Existing wiki pages (key (title))", r.ExistingPages);
        AppendList(sb, "Names already taken", r.TakenNames);
        if (change != null)
        {
            // The change comes last, so it wins over everything above.
            AppendList(sb, "The current outline", r.Current ?? Array.Empty<string>());
            sb.AppendLine().AppendLine("The change the game master asks for: " + change);
        }

        return (system, sb.ToString());
    }

    public static (string System, string User) WorldExpandHomebrew(WorldExpandRequest r)
    {
        var name = LanguageName(r.Language);
        var language = name == "English"
            ? ""
            : $" Write the description, the long text fields and the names and descriptions of collection rows in {name}. Keep every JSON key, field key and option exactly as listed, and write short stat fields (casting time, range, duration, size, creature type, speed, senses, damage types) in English, worded as the official rules word them; the app translates them for display.";
        var system = $"You write homebrew content for the {r.SystemName} tabletop game: a {r.TypeLabel ?? "entry"} called “{r.Name}”. " +
                     "Reply with ONE JSON object and nothing else, shaped exactly like {\"description\": string, \"tags\": [string], \"properties\": {field key: value}}. " +
                     "The description is the entry's full text as the rules print it: what it does, with every number and rule a table needs, in the markdown subset (**bold**, *italic*, \"- \" bullets). " +
                     "Fill every field that applies, following the field specification: numbers as numbers within min and max, dropdowns as one of the listed options, booleans as true/false, tag lists as arrays of strings, collections as arrays of objects with \"name\" and \"description\", text as a string. Leave out fields that do not apply. " +
                     "Keep the numbers in line with official material of the same power. Tags are two to five short lower-case words. Use original names only." + GuidanceNote(r.Guidance) + language;
        var change = Instruction(r.Instruction);
        if (change != null)
        {
            system += " The game master asks for a change to the entry as written so far: make it, keep everything the change does not touch, " +
                      "and reply with the whole entry, every field included, not only what changed.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Entry: {r.Name} ({r.TypeLabel ?? "entry"})");
        sb.AppendLine("Pitch: " + r.Pitch);
        if (!string.IsNullOrWhiteSpace(r.CampaignContext))
        {
            sb.AppendLine("Campaign: " + r.CampaignContext);
        }

        AppendList(sb, "The game master's answers", r.Answers.Select(a => $"{a.Question} {a.Answer}").ToList());
        AppendList(sb, "Drafted alongside it", r.Related);
        sb.AppendLine().AppendLine("Fields (key: label, type, options or range; help):");
        foreach (var f in r.Fields)
        {
            var detail = f.Options is { Count: > 0 } ? ", options: " + string.Join(" | ", f.Options)
                : f.Min.HasValue || f.Max.HasValue ? $", {f.Min?.ToString() ?? "?"}-{f.Max?.ToString() ?? "?"}"
                : "";
            var hint = string.IsNullOrWhiteSpace(f.Hint) ? "" : "; " + f.Hint.Trim();
            sb.AppendLine($"- {f.Key}: {f.Label}, {f.Type}{detail}{hint}");
        }

        AppendChange(sb, change, r.CurrentText);
        return (system, sb.ToString());
    }

    /// <summary>A page (kind "wiki"), a person's page (kind "npc"), or a secret's details (kind "fact"), as plain reply text.</summary>
    public static (string System, string User) WorldExpandPage(WorldExpandRequest r)
    {
        var shape = r.Kind switch
        {
            DraftKinds.Npc =>
                "a page about one person, for the game master's eyes. Write it in short sections with headings: who they are and their role, how they look and sound, what they want, " +
                "what they hide, and how the party might meet them. Keep it concrete and usable at the table. ",
            DraftKinds.Place =>
                "a page about one place, for the game master's eyes. Write it in short sections with headings: what it looks, sounds and smells like, who is found there, " +
                "what the party can do or learn there, and what it hides. Keep it concrete and usable at the table. ",
            DraftKinds.Fact =>
                "the details of one secret the party can uncover, for the game master's eyes. Write two to four plain sentences: what is true, who knows it, and how the party could learn it. " +
                "No headings, lists or links. ",
            _ =>
                "a page of the world wiki, for the game master's eyes. Write it in three to six short sections with headings: concrete, usable at the table, with hooks and secrets the game master can use. "
        };
        var links = r.Kind == DraftKinds.Fact
            ? ""
            : "Where they fit, link to the other drafts and existing pages listed by writing their names in double brackets, exactly as spelled ([[Name]]). ";
        // The recipe's instructions, the game master's change and, for a secret, "no headings" come last, so they win over the
        // general shape and style; a secret is saved as plain sentences, whatever the change asks.
        var change = Instruction(r.Instruction);
        var system = $"You write {shape}This is for a {r.SystemName} tabletop campaign. " + links +
                     "Use original names only. Reply with the text only. " + StyleFor(r.Language) + GuidanceNote(r.Guidance) +
                     (change == null ? "" : " The game master asks for a change to the text as written so far: make it, keep everything the change does not touch, and reply with the whole revised text.") +
                     (r.Kind == DraftKinds.Fact ? " This reply has no headings, lists or links: plain sentences only." : "");

        var sb = new StringBuilder();
        sb.AppendLine((r.Kind == DraftKinds.Fact ? "Secret: " : "Page: ") + r.Name);
        sb.AppendLine("Pitch: " + r.Pitch);
        if (!string.IsNullOrWhiteSpace(r.CampaignContext))
        {
            sb.AppendLine("Campaign: " + r.CampaignContext);
        }

        AppendList(sb, "The game master's answers", r.Answers.Select(a => $"{a.Question} {a.Answer}").ToList());
        AppendList(sb, "Drafted alongside it", r.Related);
        AppendList(sb, "Existing wiki pages", r.ExistingPages);
        AppendChange(sb, change, r.CurrentText);
        return (system, sb.ToString());
    }

    /// <summary>Roles named when a system lists none of its own: plain jobs, not any game's creature names.</summary>
    private static readonly string[] DefaultCreatureExamples = { "a guard", "a priest", "a spy", "a bandit leader" };

    // " (a, b...)" for the system's own list; nothing when it has none, so the prompt names no decision or number the game does not have.
    private static string ListOf(IReadOnlyList<string>? items) =>
        items is not { Count: > 0 }
            ? ""
            : $" ({string.Join(", ", items)}...)";

    /// <summary>The longest change a game master may ask for in one go.</summary>
    public const int MaxInstruction = 1000;

    // The game master's change, trimmed and capped, or null when there is none.
    private static string? Instruction(string? instruction)
    {
        var text = instruction?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length <= MaxInstruction ? text : text[..MaxInstruction];
    }

    // The text so far and the change asked of it, last in the message, so the change wins over the context above.
    private static void AppendChange(StringBuilder sb, string? change, string? currentText)
    {
        if (change == null) return;
        sb.AppendLine().AppendLine("Current text:").AppendLine(string.IsNullOrWhiteSpace(currentText) ? "(none yet - write it with the change)" : currentText.Trim());
        sb.AppendLine().AppendLine("The change the game master asks for: " + change);
    }

    private static string GuidanceNote(string? guidance) =>
        string.IsNullOrWhiteSpace(guidance)
            ? ""
            : " Where the following differs from the above, follow it: " + string.Join(' ', guidance.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string CharacterLanguageNote(string? language)
    {
        var name = LanguageName(language);
        return name == "English"
            ? ""
            : $" Write the name and the biography in {name}; keep every JSON key, field key and option id exactly as listed.";
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
