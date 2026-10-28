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
    bool OneArmorAndShield = false,
    string? AttributeAdvice = null);

/// <summary>What the world-building chat's model is told about the campaign.</summary>
/// <param name="HomebrewTypes">Content types a homebrew entry may have, as "id (label)".</param>
/// <param name="Creature">What the system calls a creature with a stat block (<see cref="Codex.Plugin.Abstractions.AiVocabulary.Creature"/>).</param>
/// <param name="CreatureExamples">Plain roles to show the model, as the system words them; null for the neutral few.</param>
public sealed record WorldChatRequest(
    string SystemName,
    string? CampaignContext,
    IReadOnlyList<string> HomebrewTypes,
    string? Language = null,
    string Creature = "creature",
    IReadOnlyList<string>? CreatureExamples = null);

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

        if (r.Attributes.Count > 0)
        {
            // Left to "respect the specification", models often leave the scores out, and the sheet then keeps its starting values.
            sb.AppendLine($"Give EVERY attribute above a whole-number score in \"sheet\", under its key, chosen to suit the concept{(string.IsNullOrWhiteSpace(r.AttributeAdvice) ? "" : ": " + r.AttributeAdvice.Trim())}");
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

    /// <summary>The world-building chat: how the model works with the game master, reads the campaign through its tools, and
    /// proposes what to add with <c>propose_changes</c>, which the game master reviews before anything is saved.</summary>
    public static string WorldChat(WorldChatRequest r)
    {
        var examples = r.CreatureExamples is { Count: > 0 } ? r.CreatureExamples : DefaultCreatureExamples;
        var sb = new StringBuilder();
        sb.Append($"You help the game master of a {r.SystemName} tabletop campaign build its world, in conversation. ");
        if (!string.IsNullOrWhiteSpace(r.CampaignContext)) sb.Append($"The campaign: {r.CampaignContext.Trim()} ");
        sb.Append(
            "Talk with the game master: ask a short question when a choice is really theirs, otherwise decide and say what you chose. " +
            "Before you invent anything, look at what the campaign already has with the tools (search_world, get_page, list_places, search_library, get_entry, list_tags), " +
            "so new things fit, link to what exists, and repeat no names. " +
            "Do not narrate your lookups: call the tools without comment, then write ONE message when you are done, which is what the game master reads. " +
            "That message is never empty and never ends on a promise (\"now I'm preparing it\"): if there is more to do, do it with tool calls before you write it; if you are waiting on the game master, ask your question. " +
            "A proposal holds at most 8 entries; if the world calls for more, propose the core now and offer the rest next. " +
            "When you have something to add, call propose_changes with every related entry in ONE proposal (a settlement with its districts, people and secrets together). " +
            "Nothing is saved until the game master applies the proposal, so never say something was added or saved; say it is ready to review. " +
            "If propose_changes reports errors, fix them and call it again. After the game master applies a proposal, its entries exist and you can link to them. " +
            "To change a proposal, call propose_changes again with the whole revised proposal. " +
            "To add to a proposal that is already waiting (the game master asked for something its texts mention, or you left something out), call add_entries with ONLY the new entries: they may use the refs already in the proposal as parents and links. " +
            "When a text names a person, place or faction the world would need, link it as [[Name]]; what you did not write now, the game master can ask you for next. " +
            "To correct or extend something that already exists, add an entry of kind \"edit\" with a \"target\" (its key from search_world, get_page or list_places: existing:<slug> for a page, " +
            "existing:place:<id> for a place's short description, existing:actor:<id> for a cast member's history) and \"text\": the COMPLETE new text, which replaces the old one entirely. " +
            "Read the target with get_page first, change only what the game master asked for and keep everything else word for word, and still link with [[ref]] or [[existing:slug]]. " +
            "A page, a place's description and a cast member's history are separate texts, so change each one that should change. " +
            "People, places and items mentioned in a text appear as [[actor:ID|Name]], [[place:ID|Name]] or [[item:ID|Name]]: copy each one exactly as written, or the link is lost. " +
            "Players read a place's description and a character's history once they are revealed, so write those for players' eyes, with nothing the party must not know yet. " +
            "An edit is reviewed like any other entry, next to the current text; nothing changes until the game master applies it. " +
            "Entries: \"place\" (placeKind area, settlement, district or site; \"parent\" is the place it is inside: another entry's ref or existing:place:<id> from list_places), " +
            "\"npc\" (a person; \"cast\": true for anyone the party will talk to or fight, which also adds them to the cast with their page as their history), " +
            "\"wiki\" (pageKind lore, faction or quest), \"fact\" (a secret or rumour the party can uncover: its name is the fact in one sentence, its text who knows it and how it is learned), " +
            "\"homebrew\" (a Library entry of one of the homebrew types, such as a spell, a magic item, a piece of equipment or a creature; get its fields with homebrew_fields and fill \"properties\" with them; " +
            "for a variant of an entry the Library has, read it with get_entry and set \"basedOn\" to its type/id: it starts from that entry's fields and text, so give only the properties that differ, and the complete text if it changes), " +
            "\"tags\" (tags for things the campaign already has: \"targets\" are the keys of wiki pages, existing:<slug>, and homebrew entries, existing:homebrew:<type>/<id>; \"add\" and \"remove\" are the tags), " +
            "and \"bestiary\" (\"entry\": a Library creature, as search_library lists it, to put on the campaign's Bestiary shortlist; a new homebrew creature takes \"bestiary\": true instead). " +
            "To change a homebrew entry the campaign has (its key comes from search_library or get_entry), use an \"edit\" with target existing:homebrew:<type>/<id> and any of: \"rename\", \"text\" (the complete new rules text), \"properties\" (only the fields that change) and \"tags\" (its complete new tags). " +
            "Entries from a pack cannot be changed; make a homebrew variant of them instead. " +
            "Give every new page and homebrew entry two to five short lower-case \"tags\" (a region, a faction, a theme), reusing the ones list_tags shows: the game master finds, shares and exports homebrew by tag. " +
            "Every entry has a short \"ref\" (\"p1\") that other entries use as a parent, in \"links\", and in text as [[ref]]; link existing pages as [[existing:slug]]. " +
            "Write each page in markdown with short headings, for the game master to read, run and read aloud from at the table. " +
            "Write in full, flowing sentences, never telegraphic notes or clipped fragments (\"Tall, weathered, horse voice. Short orders, fair.\" is not a description): " +
            "a game master who has never seen this world should understand it at a glance and know what to do with it. " +
            "A page is usually 150 to 350 words; under each heading write at least two full sentences, or a list whose items are each a full sentence. " +
            "Give every entry a \"pitch\" too: one or two plain sentences saying what it is, for lists and the review card. " +
            "A place: what the party sees, hears and smells on arriving, in vivid prose; who and what is found there; what the party can do or learn there, as concrete options with what each costs or risks; " +
            "what it hides; and how it connects to the places and people around it. It is never a dead end. " +
            "A person: their part in the story; how they look and how they speak, with a line or two they might say in quotes; what they want and why; what they fear; " +
            "two things they know or remember that the party can use; how the party is likely to meet them and how they react to help, threats or lies; and one sentence on how to play them. " +
            "A faction: what it wants, how it works toward it, who leads it, what it has, who opposes it, and how the party meets it. " +
            "A quest: the hook, what is really going on, the complications along the way, the rewards, and what happens if the party ignores it. " +
            "Add secrets only where a plot lives in them, never as a quota, and put each secret in a \"fact\" linked to the entries it is about. " +
            $"A person who fights or is fought may also have \"stats\": the English name of a generic {r.Creature} from the {r.SystemName} rules whose statistics suit them " +
            $"(such as {string.Join(", ", examples.Take(examples.Count - 1))}{(examples.Count > 1 ? " or " : "")}{examples[^1]}), exactly as search_library finds it; leave it out when none fits. " +
            "Invent original names; never use the names of published settings, products, or of characters, places, deities or creatures that belong to a company. ");
        if (r.HomebrewTypes.Count > 0) sb.Append($"Homebrew types: {string.Join(", ", r.HomebrewTypes)}. ");
        var language = LanguageName(r.Language);
        sb.Append(language == "English"
            ? Style
            : $"Write your replies, names and page texts in {language}; keep every JSON key, kind, ref, id and type exactly as given. " + StyleFor(r.Language));
        return sb.ToString();
    }

    public static (string System, string User) Equip(EquipRequest r)
    {
        var system = $"You equip characters for the {r.SystemName} tabletop game. Reply with ONE JSON object and nothing else, shaped exactly like {{{EquipmentShape}}}. " + EquipmentRules(r.OneArmorAndShield);
        var sb = new StringBuilder();
        sb.AppendLine("Character: " + r.Character.Trim());
        AppendItems(sb, r.Items);
        return (system, sb.ToString());
    }

    /// <summary>Roles named when a system lists none of its own: plain jobs, not any game's creature names.</summary>
    private static readonly string[] DefaultCreatureExamples = { "a guard", "a priest", "a spy", "a bandit leader" };

    // " (a, b...)" for the system's own list; nothing when it has none, so the prompt names no decision or number the game does not have.
    private static string ListOf(IReadOnlyList<string>? items) =>
        items is not { Count: > 0 }
            ? ""
            : $" ({string.Join(", ", items)}...)";

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
