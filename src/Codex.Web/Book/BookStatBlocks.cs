using System.Globalization;
using System.Text;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Book;

/// <summary>
/// The numbers of the campaign's own homebrew, written by code from the system's description of the entry's type (docs/BOOK_EXPORT.md 46), never
/// by a model: a creature (a type the system marks <c>actorBlueprint</c>) as Homebrewery's stat block, anything else as a table of its fields.
/// Nothing here knows a game: which fields there are, what they are called, which are the scores and how a score's modifier is worked out all come
/// from <c>system.yaml</c>, so a homebrew system gets its own stat blocks. A type the system does not describe gets no block.
/// <para>Each block carries an id of its own (<c>#sb-KEY</c>, <c>#pt-KEY</c>, KEY the entry's slot key), which is what makes an AI edit keep it
/// back as a piece the program owns (<see cref="Codex.Core.Book.BookDraftEdits"/>): the numbers are never sent to a model. Every value passes
/// through the draft writer's text path (mentions as the edition names them, secret blocks as the edition may read them) and is then flattened so it
/// cannot close the block, start a table row or open another block.</para>
/// </summary>
public static class BookStatBlocks
{
    public const string Creature = "monster";
    public const string Table = "properties";

    /// <summary>The block for <paramref name="entry"/>, or null when the system does not describe its type or nothing in it has a value.</summary>
    public static string? For(HomebrewEntryDocument entry, SystemDefinition? system, string language, string key, Func<string, string, string?> mention, bool dm)
    {
        if (system?.Localize(language) is not { } shown) return null;
        var type = shown.EntityTypes.FirstOrDefault(t => string.Equals(t.Type, entry.Type, StringComparison.OrdinalIgnoreCase));
        if (type == null) return null;
        var words = Codex.Localization.Localizer.For(language);
        // A hazard is the GM's: its numbers are the traps and dangers the table runs, and the party edition holds no hazard at all.
        if (type.Hazard != null) return dm ? HazardBlock(entry, type, shown, words, key, mention) : null;
        string? Value(FieldDefinition field)
        {
            if (!entry.Properties.TryGetValue(field.Key, out var raw) || raw == null) return null;
            var text = raw switch
            {
                bool b => b ? words["Library.Detail.Yes"] : words["Library.Detail.No"],
                string s => field.Display(s),
                IConvertible c => Convert.ToString(c, CultureInfo.InvariantCulture),
                IEnumerable<object> list when list.All(o => o is IConvertible) => string.Join(", ", list.Select(o => field.Display(Convert.ToString(o, CultureInfo.InvariantCulture) ?? ""))),
                _ => null
            };
            return string.IsNullOrWhiteSpace(text) ? null : Inline(text, mention, dm);
        }

        var scores = type.ActorBlueprint ? Scores(type, shown, entry) : [];
        var scoreKeys = scores.Select(s => s.Field).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var summary = type.ActorBlueprint ? type.SummaryFields.ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        // Plain fields, in the order the system gives them, grouped by the category it puts them in. Long text is the entry's own prose, a
        // reference is an id; neither is a number to print.
        var plain = type.Fields
            .Where(f => f.Type is not (FieldType.Collection or FieldType.LongText or FieldType.EntityReference or FieldType.ImagePicker))
            .Where(f => !scoreKeys.Contains(f.Key) && !summary.Contains(f.Key))
            .Select(f => (Field: f, Value: Value(f)))
            .Where(p => p.Value != null)
            .ToList();
        var rows = type.Fields.Where(f => f.Type == FieldType.Collection)
            .Select(f => (f.Label, Rows: Rows(entry, f.Key, mention, dm)))
            .Where(c => c.Rows.Count > 0)
            .ToList();

        var sb = new StringBuilder();
        if (type.ActorBlueprint)
        {
            sb.Append("{{").Append(Creature).Append(",frame,#sb-").Append(key).Append('\n');
            sb.Append("## ").Append(BookCompiler.Escape(entry.Name)).Append('\n');
            var line = type.SummaryFields.Select(k => type.Fields.FirstOrDefault(f => string.Equals(f.Key, k, StringComparison.OrdinalIgnoreCase)))
                .OfType<FieldDefinition>().Select(Value).OfType<string>().ToList();
            if (line.Count > 0) sb.Append('*').Append(string.Join(", ", line)).Append("*\n");
            foreach (var group in plain.GroupBy(p => p.Field.Category ?? string.Empty))
            {
                sb.Append("___\n");
                foreach (var (field, value) in group) sb.Append("**").Append(BookCompiler.Escape(field.Label)).Append("** ").Append(value).Append("  \n");
            }

            if (scores.Count > 0)
            {
                sb.Append("___\n|").Append(string.Join("|", scores.Select(s => BookCompiler.Escape(s.Label)))).Append("|\n|")
                    .Append(string.Join("|", scores.Select(_ => ":---:"))).Append("|\n|")
                    .Append(string.Join("|", scores.Select(s => s.Text))).Append("|\n");
            }
        }
        else
        {
            if (plain.Count == 0 && rows.Count == 0) return null;
            sb.Append("{{").Append(Table).Append(",#pt-").Append(key).Append('\n');
            if (plain.Count > 0)
            {
                sb.Append("| ").Append(BookCompiler.Escape(type.Label)).Append(" | |\n|:---|:---|\n");
                foreach (var (field, value) in plain) sb.Append("| ").Append(BookCompiler.Escape(field.Label)).Append(" | ").Append(value).Append(" |\n");
            }
        }

        foreach (var (label, list) in rows)
        {
            sb.Append(type.ActorBlueprint ? "### " : "\n##### ").Append(BookCompiler.Escape(label)).Append('\n');
            foreach (var (name, text) in list)
            {
                sb.Append("***").Append(name).Append(".*** ").Append(text).Append("\n\n");
            }
        }

        return sb.ToString().TrimEnd('\n') + "\n}}";
    }

    public const string HazardId = "hz";

    /// <summary>
    /// A hazard as a stat block, in the GM's edition only (docs/HAZARDS_DESIGN.md 7): written by code from the entry's numbers and the type's hazard block, the same reading the
    /// table uses (<see cref="HazardNumbers"/>), in plain lines that need no layout of their own, so it reads the same left to right and right to left. Its words (what a field is
    /// called, the condition names) are the system's in the book's language; the few it adds itself are the book's own strings. Nothing the entry does not say is printed.
    /// </summary>
    private static string? HazardBlock(HomebrewEntryDocument homebrew, ContentTypeDefinition type, SystemDefinition system, Codex.Localization.Localizer words, string key,
        Func<string, string, string?> mention)
    {
        var hazard = type.Hazard!;
        var content = homebrew.ToEntry();
        var numbers = HazardNumbers.For(content, type, null);
        FieldDefinition? Def(string? field) => string.IsNullOrEmpty(field) ? null : type.Fields.FirstOrDefault(f => f.Key == field);
        string Label(string? field) => BookCompiler.Escape(Def(field)?.Label ?? field ?? string.Empty);
        string I(string text) => Inline(text, mention, dm: true);
        string? Scalar(string? field)
        {
            if (string.IsNullOrEmpty(field) || !numbers.TryGetValue(field, out var raw) || raw == null) return null;
            var text = raw switch
            {
                bool b => b ? words["Library.Detail.Yes"] : words["Library.Detail.No"],
                string s => Def(field)?.Display(s) ?? s,
                IConvertible c => Convert.ToString(c, CultureInfo.InvariantCulture),
                IEnumerable<object> list when list.All(o => o is IConvertible) => string.Join(", ", list.Select(o => Def(field)?.Display(Convert.ToString(o, CultureInfo.InvariantCulture) ?? "") ?? Convert.ToString(o, CultureInfo.InvariantCulture))),
                _ => null
            };
            return string.IsNullOrWhiteSpace(text) ? null : I(text);
        }

        string RouteText(HazardRoute route)
        {
            var parts = new List<string>();
            if (route.Action is { } action) parts.Add(I(action));
            // The check as the book's language names it: the skill or attribute the stored key, label or canonical label stands for, else the words as written.
            if (route.Check is { } check)
            {
                var named = I(ActorChecks.Resolve(system, check)?.Label ?? check);
                parts.Add(route.Dc is { } dc ? words["Campaign.Book.Hazard.Check", named, dc] : named);
            }
            else if (route.Dc is { } bare) parts.Add(words["Campaign.Book.Hazard.Dc", bare]);
            if (route.Needs is { } needs) parts.Add(words["Campaign.Book.Hazard.Needs", I(needs)]);
            if (route.After is { } after) parts.Add(words["Campaign.Book.Hazard.After", I(HazardNumbers.NoticeRoutes(numbers, hazard).FirstOrDefault(r => r.Id == after)?.Shown ?? after)]);
            if (route.Successes > 1) parts.Add(words["Campaign.Book.Hazard.Successes", route.Successes]);
            if (route.TriggersOn.Any(w => string.Equals(w, "failure", StringComparison.OrdinalIgnoreCase))) parts.Add(words["Campaign.Book.Hazard.SetsOff"]);
            else if (route.TriggersOn.Any(w => string.Equals(w, HazardRoutes.CriticalFailure, StringComparison.OrdinalIgnoreCase))) parts.Add(words["Campaign.Book.Hazard.SetsOffCritical"]);
            if (route.Passive == false) parts.Add(words["Campaign.Book.Hazard.NoPassive"]);
            if (route.Rank is { } rank) parts.Add(words["Campaign.Book.Hazard.Rank", I(rank)]);
            if (route.Text is { } text) parts.Add(I(text));
            return string.Join("; ", parts);
        }

        string Dice(HazardDamagePart part)
        {
            var text = I(part.Type is { } kind ? part.Dice + " " + kind : part.Dice);
            return part.Count is { } count ? words["Campaign.Book.Hazard.Times", text, I(count)] : text;
        }

        var sb = new StringBuilder();
        sb.Append("{{").Append(Creature).Append(",frame,#").Append(HazardId).Append('-').Append(key).Append('\n');
        sb.Append("## ").Append(BookCompiler.Escape(homebrew.Name)).Append('\n');
        var summary = new List<string> { BookCompiler.Escape(type.Label) };
        if (Scalar(hazard.Level) is { } level) summary.Add(Label(hazard.Level) + " " + level);
        if (Scalar(hazard.Severity) is { } severity) summary.Add(severity);
        if (HazardNumbers.IsComplex(content, type)) summary.Add(Label(hazard.Complex));
        sb.Append('*').Append(string.Join(", ", summary)).Append("*\n");

        // The lines: what sets it off and how long, then its effect (save, attack, damage, conditions), then every other plain number it has.
        var lines = new List<(string Label, string Value)>();
        void Line(string? field)
        {
            if (Scalar(field) is { } value) lines.Add((Label(field), value));
        }

        Line(hazard.Trigger);
        Line(hazard.Duration);
        var effect = hazard.Effect;
        Line(effect?.Attack);
        var save = new List<string>();
        if (Scalar(effect?.Save) is { } attribute) save.Add(attribute);
        if (numbers.TryGetValue(effect?.SaveDc ?? string.Empty, out var dcRaw) && int.TryParse(Convert.ToString(dcRaw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var saveDc)) save.Add(words["Campaign.Book.Hazard.Dc", saveDc]);
        if (Scalar(effect?.Save) != null && Scalar(effect?.OnSave) is { } onSave)
        {
            save.Add(string.Equals(onSave, "half", StringComparison.OrdinalIgnoreCase) ? words["Campaign.Book.Hazard.HalfOnSave"] : words["Campaign.Book.Hazard.NoneOnSave"]);
        }

        if (save.Count > 0) lines.Add((Label(effect?.Save ?? effect?.SaveDc), string.Join(", ", save)));
        var damage = HazardNumbers.Damage(numbers, hazard);
        if (damage.Count > 0) lines.Add((Label(effect?.Damage), string.Join("; ", damage.Select(Dice))));
        var conditions = HazardNumbers.Conditions(numbers, hazard);
        if (conditions.Count > 0)
        {
            lines.Add((Label(effect?.Conditions), string.Join("; ", conditions.Select(c => I(system.DisplayCondition(c.Name)) + (c.Lasts is { } lasts ? " (" + I(lasts) + ")" : string.Empty)))));
        }

        var taken = new HashSet<string?>(StringComparer.Ordinal)
        {
            hazard.Level, hazard.Severity, hazard.Complex, hazard.Trigger, hazard.Duration, effect?.Attack, effect?.Save, effect?.SaveDc, effect?.OnSave, effect?.Damage, effect?.Conditions,
            effect?.Text, hazard.Reset, hazard.Routine, hazard.HigherLevels, hazard.Methods, hazard.Notice?.Routes, hazard.Disable?.Routes
        };
        foreach (var field in type.Fields.Where(f => !taken.Contains(f.Key) && f.Type is not (FieldType.Collection or FieldType.LongText or FieldType.EntityReference or FieldType.ImagePicker))) Line(field.Key);
        if (lines.Count > 0)
        {
            sb.Append("___\n");
            foreach (var (label, value) in lines) sb.Append("**").Append(label).Append("** ").Append(value).Append("  \n");
        }

        if (Scalar(effect?.Text) is { } effectText) sb.Append("___\n").Append(effectText).Append('\n');
        if (Scalar(hazard.Reset) is { } reset) sb.Append("___\n**").Append(Label(hazard.Reset)).Append("** ").Append(reset).Append("\n\n");

        void Routes(string? field, IReadOnlyList<HazardRoute> routes)
        {
            if (routes.Count == 0) return;
            sb.Append("### ").Append(Label(field)).Append('\n');
            foreach (var route in routes) sb.Append("***").Append(I(route.Shown)).Append(".*** ").Append(RouteText(route)).Append("\n\n");
        }

        Routes(hazard.Notice?.Routes, HazardNumbers.NoticeRoutes(numbers, hazard));
        Routes(hazard.Disable?.Routes, HazardNumbers.DisableRoutes(numbers, hazard));
        Routes(hazard.Methods, HazardNumbers.Methods(numbers, hazard));

        var routine = HazardNumbers.Routine(numbers, hazard);
        if (routine.Count > 0)
        {
            sb.Append("### ").Append(Label(hazard.Routine)).Append('\n');
            foreach (var step in routine)
            {
                sb.Append("***").Append(I(step.Name)).Append(".*** ").Append(I(step.Description)).Append(step.Strikes ? " (" + words["Campaign.Book.Hazard.Strikes"] + ")" : string.Empty).Append("\n\n");
            }
        }

        // The bands: each its levels, then only what it changes, in the field's own words.
        var bands = HazardNumbers.EditableRows(hazard.HigherLevels is { } bandField ? homebrew.Properties.GetValueOrDefault(bandField) : null);
        if (bands.Count > 0)
        {
            sb.Append("### ").Append(Label(hazard.HigherLevels)).Append('\n');
            foreach (var band in bands)
            {
                var changed = new List<string>();
                foreach (var (field, raw) in band.Where(p => p.Key != "levels"))
                {
                    var text = field == effect?.Damage ? string.Join("; ", HazardNumbers.Damage(new Dictionary<string, object> { [field] = raw }, hazard).Select(Dice))
                        : field == hazard.Methods ? string.Join("; ", HazardNumbers.Methods(new Dictionary<string, object> { [field] = raw }, hazard).Select(m => I(m.Shown)))
                        : raw is IConvertible c ? I(Def(field)?.Display(Convert.ToString(c, CultureInfo.InvariantCulture) ?? string.Empty) ?? Convert.ToString(c, CultureInfo.InvariantCulture) ?? string.Empty)
                        : string.Empty;
                    if (text.Length > 0) changed.Add(Label(field) + " " + text);
                }

                var levels = band.TryGetValue("levels", out var l) ? Convert.ToString(l, CultureInfo.InvariantCulture) : null;
                if (string.IsNullOrWhiteSpace(levels) && changed.Count == 0) continue;
                sb.Append("***").Append(I(levels ?? string.Empty)).Append(".*** ").Append(string.Join("; ", changed)).Append("\n\n");
            }
        }

        // A block with nothing but its title says nothing.
        return sb.ToString().Count(c => c == '\n') <= 2 ? null : sb.ToString().TrimEnd('\n') + "\n}}";
    }

    // The scores' row: each attribute the type maps to a field of its own, under the system's abbreviation, with the system's modifier.
    private static List<(string Field, string Label, string Text)> Scores(ContentTypeDefinition type, SystemDefinition system, HomebrewEntryDocument entry)
    {
        var scores = new List<(string, string, string)>();
        foreach (var attribute in system.Attributes)
        {
            var field = type.AttributeProperties.FirstOrDefault(p => string.Equals(p.Value, attribute.Key, StringComparison.OrdinalIgnoreCase)).Key;
            if (field == null || !entry.Properties.TryGetValue(field, out var raw) || !int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var score))
            {
                continue;
            }

            var modifier = attribute.ModifierFor(score) is { } m ? " (" + (m >= 0 ? "+" + m : "−" + -m) + ")" : string.Empty;
            // A modifier-scale attribute is its modifier: print it once, signed.
            scores.Add((field, attribute.Abbreviation ?? attribute.Label, attribute.IsModifier ? (score >= 0 ? "+" : "−") + Math.Abs(score).ToString(CultureInfo.InvariantCulture) : score.ToString(CultureInfo.InvariantCulture) + modifier));
        }

        return scores;
    }

    private static List<(string Name, string Text)> Rows(HomebrewEntryDocument entry, string key, Func<string, string, string?> mention, bool dm)
    {
        if (!entry.Properties.TryGetValue(key, out var raw) || raw is not IEnumerable<object> items) return [];
        var rows = new List<(string, string)>();
        foreach (var item in items.OfType<IDictionary<string, object>>())
        {
            var name = item.TryGetValue("name", out var n) && n is IConvertible ? Convert.ToString(n, CultureInfo.InvariantCulture) : null;
            var text = item.TryGetValue("description", out var d) && d is IConvertible ? Convert.ToString(d, CultureInfo.InvariantCulture) : null;
            var shownText = string.IsNullOrWhiteSpace(text) ? string.Empty : Inline(text, mention, dm);
            if (string.IsNullOrWhiteSpace(name) && shownText.Length == 0) continue;
            rows.Add((string.IsNullOrWhiteSpace(name) ? string.Empty : Inline(name, mention, dm), shownText));
        }

        return rows;
    }

    /// <summary>
    /// A value as one line of the block: through the draft writer's text path (which escapes what it writes of a mention, and HTML), then on one
    /// line, with every brace and bar escaped, so nothing in it can end the block, open another, or make a table cell of its own. Emphasis stays.
    /// </summary>
    public static string Inline(string text, Func<string, string, string?> mention, bool dm)
    {
        var converted = BookDraftWriter.Text(text, mention, dm);
        var line = string.Join(' ', converted.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        var sb = new StringBuilder(line.Length + 8);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            // A mark the writer already escaped stays as it is.
            if (c == '\\' && i + 1 < line.Length)
            {
                sb.Append(c).Append(line[++i]);
                continue;
            }

            if (c is '{' or '}' or '|') sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }
}
