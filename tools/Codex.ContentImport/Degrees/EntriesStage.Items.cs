using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

public static partial class EntriesStage
{
    /// <summary>The description of one item: the name its paragraph opens with (bold, then a colon) and the paragraphs.</summary>
    internal sealed record ItemDescription(string Name, List<string> Paragraphs);

    /// <summary>
    /// A label of a picture on a page the row names in <see cref="TakeRow.ArtPages"/>: a display block, a body block of up to five words with no full stop, or a heading in the block-header
    /// font of up to four words that opens with a digit (a distance on a diagram).
    /// </summary>
    internal static bool IsArtPageLabel(TakeRow row, Block block)
    {
        // A page where a sidebar's lines run on past the gutter into the column of the page's own text: its body-font blocks are the sidebar's (the page's own text is in the rules font).
        if (row.SidebarSpill.Contains(block.Page) && block.Role == "body")
        {
            return true;
        }

        if (!row.ArtPages.Contains(block.Page))
        {
            return false;
        }

        var text = string.Join(' ', block.Lines);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return block.Role == "display"
            || (block.Role == "body" && words <= 5 && !text.Contains('.'))
            || (block.Role == "block-header" && words <= 4 && text.Length > 0 && char.IsDigit(text[0]));
    }

    /// <summary>A body block set entirely in capitals is the label under a picture (an item's name over its drawing), not text.</summary>
    internal static bool IsArtLabel(Block block) =>
        block.Role == "body" && block.Lines.All(l => l.Any(char.IsLetter) && !l.Any(char.IsLower));

    /// <summary>
    /// The descriptions in a range: each is a paragraph that opens with a bold name and a colon, and goes on until the next one. The text before the first (a sentence that says what follows) is read past.
    /// Stat tables are not text. With <see cref="TakeRow.ArtLabels"/> the captions of the page's art are skipped, and counted.
    /// </summary>
    internal static (List<ItemDescription> Descriptions, int ArtLabels, int LeadLines) ReadDescriptions(List<Block> slice, TakeRow row, HashSet<string> compounds)
    {
        var read = ReadDescriptionsAndLead(slice, row, compounds);
        return (read.Descriptions, read.ArtLabels, read.LeadLines);
    }

    /// <summary>
    /// <see cref="ReadDescriptions"/>, and the lead as paragraphs too: the text before the first description, which a gear row may make a rule of (<see cref="TakeRow.LeadRules"/>).
    /// </summary>
    internal static (List<ItemDescription> Descriptions, int ArtLabels, int LeadLines, List<string> LeadParagraphs) ReadDescriptionsAndLead(List<Block> slice, TakeRow row, HashSet<string> compounds)
    {
        var found = new List<(string Name, List<StringBuilder> Paragraphs)>();
        var leadText = new List<StringBuilder> { new() };
        int art = 0, lead = 0;
        var previousShort = true;
        foreach (var block in slice)
        {
            if (block.Role == StatTables.Role)
            {
                continue;
            }

            if (row.ArtLabels && IsArtLabel(block))
            {
                art++;
                continue;
            }

            for (var i = 0; i < block.Lines.Count; i++)
            {
                var info = block.Info?[i] ?? new LineInfo(block.Left, block.Right, string.Empty);
                var text = block.Lines[i];
                var label = info.Label;
                var opens = label.Length > 0 && text.StartsWith(label, StringComparison.Ordinal) && (label.EndsWith(':') || text[label.Length..].TrimStart().StartsWith(':'));
                if (opens)
                {
                    var after = text[label.Length..].TrimStart();
                    if (after.StartsWith(':'))
                    {
                        after = after[1..].TrimStart();
                    }

                    found.Add((Clean(label).TrimEnd(':').Trim(), new List<StringBuilder> { new() }));
                    text = after;
                }
                else if (found.Count == 0)
                {
                    lead++;
                    if (previousShort && info.Left - block.Left > Indent && leadText[^1].Length > 0)
                    {
                        leadText.Add(new StringBuilder());
                    }

                    JoinLine(leadText[^1], text, compounds);
                    previousShort = info.Right < block.Right - ShortLine;
                    continue;
                }
                else if (previousShort && info.Left - block.Left > Indent && found[^1].Paragraphs[^1].Length > 0)
                {
                    found[^1].Paragraphs.Add(new StringBuilder());
                }

                JoinLine(found[^1].Paragraphs[^1], text, compounds);
                previousShort = info.Right < block.Right - ShortLine;
            }
        }

        return (found.Select(f => new ItemDescription(f.Name, f.Paragraphs.Select(p => Clean(p.ToString())).Where(p => p.Length > 0).ToList())).ToList(), art, lead,
            leadText.Select(p => Clean(p.ToString())).Where(p => p.Length > 0).ToList());
    }

    /// <summary>
    /// The entries of an <c>items</c> row. Each description is joined to its row of a stat table by name, and a row with no description, a description with no row, or two of either with one name fails the run
    /// unless the row's <see cref="TakeRow.Exclude"/> names it (and says why in its countsDiffer). The statistics are the table's cells, the text is the description.
    /// </summary>
    private static List<ParsedEntry> ParseItems(TakenRange range, List<Block> slice, BlocksResult blocks, IReadOnlyList<SpineNode> spine, string book, HashSet<string> compounds)
    {
        var row = range.Row;
        var rows = ItemRows.Read(blocks, spine, row);
        var (descriptions, _, _) = ReadDescriptions(slice, row, compounds);
        var excluded = row.Exclude.Select(ItemRows.Key).ToHashSet(StringComparer.Ordinal);
        var unmatched = excluded.Where(k => !rows.Any(r => ItemRows.Key(r.Name) == k) && !descriptions.Any(d => ItemRows.Key(d.Name) == k)).ToList();
        if (unmatched.Count > 0)
        {
            throw new DegreesException($"Chapter {row.Chapter}: {unmatched.Count} excluded name(s) match no table row and no description.");
        }

        var kept = rows.Where(r => !excluded.Contains(ItemRows.Key(r.Name))).ToList();
        var described = descriptions.Where(d => !excluded.Contains(ItemRows.Key(d.Name))).ToList();
        if (kept.GroupBy(r => ItemRows.Key(r.Name)).Any(g => g.Count() > 1) || described.GroupBy(d => ItemRows.Key(d.Name)).Any(g => g.Count() > 1))
        {
            throw new DegreesException($"Chapter {row.Chapter}: a name is repeated among the table rows or among the descriptions.");
        }

        var rowKeys = kept.Select(r => ItemRows.Key(r.Name)).ToHashSet(StringComparer.Ordinal);
        var descriptionKeys = described.Select(d => ItemRows.Key(d.Name)).ToHashSet(StringComparer.Ordinal);
        var undescribed = row.Undescribed.Select(ItemRows.Key).ToHashSet(StringComparer.Ordinal);
        var ruleKeys = row.RuleDescriptions.Select(ItemRows.Key).ToHashSet(StringComparer.Ordinal);
        if (undescribed.Any(k => !rowKeys.Contains(k) || descriptionKeys.Contains(k)) || ruleKeys.Any(k => !descriptionKeys.Contains(k) || rowKeys.Contains(k)))
        {
            throw new DegreesException($"Chapter {row.Chapter}: an undescribed name has a description or no row, or a name given as a rule description has no description or has a row.");
        }

        var noDescription = kept.Where(r => !descriptionKeys.Contains(ItemRows.Key(r.Name)) && !undescribed.Contains(ItemRows.Key(r.Name))).Select(r => r.Name).ToList();
        var noRow = described.Where(d => !rowKeys.Contains(ItemRows.Key(d.Name)) && !ruleKeys.Contains(ItemRows.Key(d.Name))).Select(d => d.Name).ToList();
        if (noDescription.Count > 0 || noRow.Count > 0)
        {
            throw new DegreesException($"Chapter {row.Chapter}: {kept.Count} table rows and {described.Count} descriptions; {noDescription.Count} row(s) have no description ({string.Join("; ", noDescription.Take(8))}) and {noRow.Count} description(s) have no row ({string.Join("; ", noRow.Take(8))}).");
        }

        var result = new List<ParsedEntry>();
        foreach (var item in kept)
        {
            var description = described.FirstOrDefault(d => ItemRows.Key(d.Name) == ItemRows.Key(item.Name)) ?? new ItemDescription(item.Name, new List<string>());
            var entry = NewEntry(range, book, description.Name, compounds);
            entry.Level = item.Level;
            switch (row.ItemKind)
            {
                case "armor":
                    ArmorFields(entry, item);
                    break;
                case "shield":
                    ShieldFields(entry, item);
                    break;
                default:
                    WeaponFields(entry, item);
                    break;
            }

            foreach (var p in description.Paragraphs)
            {
                entry.Parts.Add(new LabelledText(string.Empty, p));
                entry.Paragraphs.Add(p);
            }

            var traitCell = item.Cells.GetValueOrDefault("Armor Traits") ?? item.Cells.GetValueOrDefault("Weapon Traits");
            if (TraitCell(traitCell) is { Count: > 0 } traits)
            {
                var printed = string.Join(", ", traits).ToLowerInvariant();
                printed = Regex.Replace(printed, @"\b([bps])\b", m => m.Value switch { "b" => "bludgeoning", "p" => "piercing", _ => "slashing" });
                var part = new LabelledText("Traits", printed);
                entry.Parts.Add(part);
                entry.Fields.Add(part);
            }

            foreach (var note in item.Notes)
            {
                var part = new LabelledText("Note", Clean(note));
                entry.Parts.Add(part);
                entry.Fields.Add(part);
            }

            result.Add(entry);
        }

        if (row.RuleDescriptions.Count > 0)
        {
            // Descriptions with no row of their own (ammunition): one rule, a labelled paragraph each, in the book's order.
            var rule = NewEntry(range, book, row.RuleName!, compounds, "rule", "rules", row.RuleCategory);
            foreach (var d in described.Where(d => ruleKeys.Contains(ItemRows.Key(d.Name))))
            {
                var part = new LabelledText(d.Name, string.Join(' ', d.Paragraphs));
                rule.Parts.Add(part);
                rule.Fields.Add(part);
            }

            result.Add(rule);
        }

        return result;
    }

    private static string Dash(string? cell) => cell == null || cell.Trim() is "" or "—" or "–" or "-" ? string.Empty : cell.Trim();

    /// <summary>The two shapes of a numeric cell: a signed number, with a distance in feet after it or a second bonus (the tower shield's pair, of which the first is taken). Anything else fails.</summary>
    private static readonly Regex LeadingNumber = new(@"^(?<n>[+-]?\d+)(?:/[+-]?\d+|\s*ft\.?)?$", RegexOptions.CultureInvariant);

    /// <summary>The whole number a cell starts with ("+1", "–5 ft."), or null for a dash. A cell that is neither fails.</summary>
    public static int? Signed(string? cell, string what)
    {
        var text = Dash(cell).Replace('–', '-').Replace('−', '-');
        if (text.Length == 0)
        {
            return null;
        }

        var m = LeadingNumber.Match(text);
        return m.Success ? int.Parse(m.Groups["n"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : throw new DegreesException($"A {what} cell is not a number.");
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static void Raw(ParsedEntry entry, string key, string raw) => entry.Raw.Add(new LabelledText(key, raw));

    private static void RawText(ParsedEntry entry, string key, string? cell)
    {
        if (Dash(cell).Length > 0)
        {
            Raw(entry, key, PackEmitter.Scalar(Clean(Dash(cell)), 6));
        }
    }

    private static void RawNumber(ParsedEntry entry, string key, string? cell, string what)
    {
        if (Signed(cell, what) is { } n)
        {
            Raw(entry, key, Num(n));
        }
    }

    /// <summary>The words of a trait cell: one per trait as printed, parameters and all. A dash is none.</summary>
    internal static List<string> TraitCell(string? cell) =>
        Dash(cell).Length == 0 ? new List<string>() : Dash(cell).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// A trait's key from the trait as printed: its words in lowercase up to the first one that is a parameter (a die, a number of feet, a damage letter), joined with hyphens
    /// (<c>thrown 10 ft.</c> is <c>thrown</c>, <c>two-hand d8</c> is <c>two-hand</c>).
    /// </summary>
    public static string TraitKey(string printed)
    {
        // "Attached to shield" is the attached trait with the kind of item it goes on as its parameter.
        if (printed.StartsWith("attached to ", StringComparison.OrdinalIgnoreCase))
        {
            return "attached";
        }

        var words = printed.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeWhile(w => Regex.IsMatch(w, "^[a-z][a-z-]*$") && w.Length > 1 && w != "ft").ToList();
        return words.Count == 0 ? throw new DegreesException("A trait of a table row has no name before its parameter.") : string.Join('-', words);
    }

    private static void Traits(ParsedEntry entry, ItemRow item, string column)
    {
        foreach (var t in TraitCell(item.Cells.GetValueOrDefault(column)))
        {
            var key = TraitKey(t);
            if (!entry.Traits.Contains(key))
            {
                entry.Traits.Add(key);
            }
        }

        if (item.Uncommon && !entry.Traits.Contains("uncommon"))
        {
            entry.Traits.Add("uncommon");
        }
    }

    private static void ArmorFields(ParsedEntry entry, ItemRow item)
    {
        var bonus = Signed(item.Cells["AC Bonus"], "armor class bonus") ?? throw new DegreesException("An armor row has no armor class bonus.");
        // The armor's base armor class is 10 plus its printed bonus, as the homebrew armor and the sheet's armor rule have it (docs/CONTENT_PACKS.md, Armor class).
        Raw(entry, "armorBase", Num(10 + bonus));
        RawNumber(entry, "armorDexCap", item.Cells["Dex Cap"], "Dex Cap");
        Raw(entry, "armorCategory", PackEmitter.Scalar(item.Category, 6));
        RawNumber(entry, "checkPenalty", item.Cells["Check Penalty"], "check penalty");
        RawNumber(entry, "speedPenalty", item.Cells["Speed Penalty"], "speed penalty");
        if (item.Cells.TryGetValue("Strength", out var strength))
        {
            RawNumber(entry, "strength", strength, "Strength");
        }

        RawText(entry, "bulk", item.Cells["Bulk"]);
        RawText(entry, "group", item.Cells["Group"].ToLowerInvariant());
        RawText(entry, "price", item.Cells["Price"]);
        Traits(entry, item, "Armor Traits");
    }

    private static void ShieldFields(ParsedEntry entry, ItemRow item)
    {
        Raw(entry, "armorType", PackEmitter.Scalar("shield", 6));
        // A tower shield prints two bonuses (+2 raised, +4 with Take Cover); the first is what raising it gives, and the other is in the note.
        Raw(entry, "armorClassBonus", Num(Signed(item.Cells["AC Bonus"], "shield bonus") ?? throw new DegreesException("A shield row has no armor class bonus.")));
        RawNumber(entry, "speedPenalty", item.Cells["Speed Penalty"], "speed penalty");
        RawNumber(entry, "hardness", item.Cells["Hardness"], "hardness");
        var hp = Regex.Match(item.Cells["HP (BT)"], @"^(?<hp>\d+) \((?<bt>\d+)\)$");
        if (!hp.Success)
        {
            throw new DegreesException("A shield's hit points are not in the shape of a number and a bracketed number.");
        }

        Raw(entry, "hp", hp.Groups["hp"].Value);
        Raw(entry, "brokenThreshold", hp.Groups["bt"].Value);
        RawText(entry, "bulk", item.Cells["Bulk"]);
        RawText(entry, "price", item.Cells["Price"]);
    }

    private static readonly Regex DamageCell = new(@"^(?<dice>\d+d\d+|\d+) (?<type>[BPS])$", RegexOptions.CultureInvariant);

    private static void WeaponFields(ParsedEntry entry, ItemRow item)
    {
        var damage = DamageCell.Match(item.Cells["Damage"]);
        if (!damage.Success)
        {
            throw new DegreesException("A weapon's damage cell is not a die and a damage letter.");
        }

        var dice = damage.Groups["dice"].Value;
        Raw(entry, "weaponCategory", PackEmitter.Scalar(item.Category, 6));
        Raw(entry, "damage", PackEmitter.Scalar(dice, 6));
        Raw(entry, "damageType", PackEmitter.Scalar(damage.Groups["type"].Value switch { "B" => "bludgeoning", "P" => "piercing", _ => "slashing" }, 6));
        if (item.Attack == "ranged")
        {
            RawNumber(entry, "range", item.Cells["Range"], "range");
            RawNumber(entry, "reload", item.Cells["Reload"], "reload");
        }

        RawText(entry, "hands", item.Cells["Hands"]);
        RawText(entry, "bulk", item.Cells["Bulk"]);
        RawText(entry, "group", item.Cells["Group"].ToLowerInvariant());
        RawText(entry, "price", item.Cells["Price"]);
        if (item.Ammunition != null)
        {
            Raw(entry, "ammunition", PackEmitter.Scalar(item.Ammunition, 6));
        }

        Traits(entry, item, "Weapon Traits");

        // The abilities of a Strike, as the book's attack rules print them (Weapons, Attack Rolls): a melee attack roll uses Strength, or Dexterity for a weapon with the finesse trait, and a ranged one uses Dexterity.
        var abilities = item.Attack == "ranged" ? "dex" : entry.Traits.Contains("finesse") ? "str, dex" : "str";
        Raw(entry, "use", $"{{ attack: {item.Attack}, abilities: [{abilities}], damage: {dice} }}");
        entry.Tags.Add("weapons");
    }
}
