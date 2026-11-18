using System.Globalization;
using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>One row of a spellcaster's "spells per day" table: the level, the cantrips, and the slots of each rank (index 0 is the 1st rank, none is 0).</summary>
public sealed record SpellLevelRow(int Level, int Cantrips, IReadOnlyList<int> Slots)
{
    /// <summary>The highest rank the class has a slot for at this level (0 when it has none).</summary>
    public int MaxRank => Slots.Select((n, i) => n > 0 ? i + 1 : 0).DefaultIfEmpty(0).Max();

    /// <summary>The slots of each rank from the 1st up to the highest, as the sheet's slot pools read them: <c>3/3/2</c>.</summary>
    public string SlotString => string.Join('/', Slots.Take(MaxRank));

    /// <summary>One more spell for each slot (a repertoire gains a spell of the slot's rank, a prepared caster fills each slot).</summary>
    public int SpellCount => Slots.Sum();
}

/// <summary>What a spellcaster's class text says of its casting: the tradition, how it casts, the focus pool it starts with.</summary>
public sealed record SpellcastingFacts(string Tradition, string Casting, int FocusPool);

public static partial class EntriesStage
{
    /// <summary>The ranks the table prints: the 1st to the 10th (the 10th is a slot of a class feature).</summary>
    private const int TableRanks = 10;

    private static readonly Regex SpellRowLine = new(@"^(?:\d{1,2}[*†]*|[—–-])(?:\s+(?:\d{1,2}[*†]*|[—–-]))*$", RegexOptions.CultureInvariant);

    private static readonly Regex SpellHeaderLine = new(@"^(?:Your Spell|Rank|Level Cantrips(?:\s+\d{1,2}(?:st|nd|rd|th))+|\d{1,2}(?:st|nd|rd|th)(?:\s+\d{1,2}(?:st|nd|rd|th))*)$", RegexOptions.CultureInvariant);

    private static readonly Regex SpellRankRowName = new(@"^\d{1,2}(?:st|nd|rd|th)-rank spell$", RegexOptions.CultureInvariant);

    private static readonly Regex TraditionSentence = new(@"can cast spells of the (?<t>arcane|divine|occult|primal) tradition", RegexOptions.CultureInvariant);

    // The attribute the text names as the key attribute where it speaks of the spell attack, and the attribute its modifier is said to be: both must be the class's.
    private static readonly Regex SpellAttributeSentence = new(@"key attribute is (?<a>[A-Z][a-z]+), your spell attack", RegexOptions.CultureInvariant);

    private static readonly Regex SpellAttributeUse = new(@"\buse your (?<a>[A-Z][a-z]+) modifier", RegexOptions.CultureInvariant);

    private static readonly Regex FirstLevelSpells = new(
        @"(?<spells>one|two|three|four|five|six) 1st[-‑]rank (?:[a-z]+ )?spells(?: of your choice)? and (?<cantrips>one|two|three|four|five|six|seven) (?:[a-z]+ )?cantrips", RegexOptions.CultureInvariant);

    private static readonly Regex FocusPoolSentence = new(@"[Yy]ou start with a focus pool of (?<n>\d+) Focus Points?", RegexOptions.CultureInvariant);

    private static readonly string[] NumberWords = { "zero", "one", "two", "three", "four", "five", "six", "seven" };

    private static int NumberWord(string word) => Array.IndexOf(NumberWords, word);

    private static string[] SpellLines(Block block) => block.Lines.Select(l => SpanText.Normalize(l).Trim()).Where(l => l.Length > 0).ToArray();

    /// <summary>
    /// Whether a body block belongs to the spells-per-day table: lines of numbers and dashes (rows), the column labels, and at the end of the block the table's notes (text). A block of notes alone
    /// belongs to it only when it goes on from a table block just before it (its first word is lowercase: the note was cut at the foot of a column).
    /// </summary>
    public static bool IsSpellTableBlock(Block block, bool followsTable)
    {
        var lines = SpellLines(block);
        if (lines.Length == 0)
        {
            return false;
        }

        var tableLines = 0;
        var seenText = false;
        foreach (var line in lines)
        {
            if (SpellRowLine.IsMatch(line) || SpellHeaderLine.IsMatch(line))
            {
                if (seenText)
                {
                    return false; // a row after a note is no table
                }

                tableLines++;
            }
            else
            {
                seenText = true;
            }
        }

        return tableLines > 0 || (followsTable && lines.Length <= 3 && char.IsLower(lines[0][0]));
    }

    private static int CellValue(string token)
    {
        var t = token.TrimEnd('*', '†');
        return t is "—" or "–" or "-" ? 0 : int.Parse(t, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads the table's two halves into 20 rows: the first half's rows hold the level, the cantrips and the ranks the header names (1st to 4th), the second half's rows (the ranks 5th to 10th) hold no
    /// level and follow in the order of the levels. A table that does not have 20 rows in each half, or a row of another width, fails the run.
    /// </summary>
    public static List<SpellLevelRow> ParseSpellTable(List<Block> blocks, string className)
    {
        var firstHalf = new List<(int Level, int Cantrips, int[] Ranks)>();
        var secondHalf = new List<int[]>();
        var mode = 0;
        var ranks = new List<int>();
        foreach (var line in blocks.SelectMany(SpellLines))
        {
            if (line.StartsWith("Level Cantrips", StringComparison.Ordinal))
            {
                mode = 1;
                ranks = Regex.Matches(line, @"(\d{1,2})(?:st|nd|rd|th)").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
            }
            else if (line == "Rank")
            {
                mode = 2;
                ranks = new List<int>();
            }
            else if (Regex.IsMatch(line, @"^\d{1,2}(?:st|nd|rd|th)(?:\s+\d{1,2}(?:st|nd|rd|th))*$"))
            {
                ranks = Regex.Matches(line, @"(\d{1,2})(?:st|nd|rd|th)").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
            }
            else if (SpellRowLine.IsMatch(line))
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (mode == 1 && tokens.Length == 2 + ranks.Count && ranks.SequenceEqual(Enumerable.Range(1, 4)))
                {
                    firstHalf.Add((CellValue(tokens[0]), CellValue(tokens[1]), tokens.Skip(2).Select(CellValue).ToArray()));
                }
                else if (mode == 2 && tokens.Length == ranks.Count && ranks.SequenceEqual(Enumerable.Range(5, 6)))
                {
                    secondHalf.Add(tokens.Select(CellValue).ToArray());
                }
                else
                {
                    throw new DegreesException($"The spells-per-day table of the {className} has a row of {tokens.Length} cells that fits neither half of the table.");
                }
            }
        }

        if (firstHalf.Count != 20 || secondHalf.Count != 20 || firstHalf.Select(r => r.Level).SequenceEqual(Enumerable.Range(1, 20)) == false)
        {
            throw new DegreesException($"The spells-per-day table of the {className} does not hold 20 rows (levels 1 to 20) in each of its two halves ({firstHalf.Count} and {secondHalf.Count}).");
        }

        var firstRanks = Enumerable.Range(1, 4).ToArray();
        var secondRanks = Enumerable.Range(5, 6).ToArray();
        var rows = new List<SpellLevelRow>();
        for (var i = 0; i < 20; i++)
        {
            var slots = new int[TableRanks];
            for (var k = 0; k < firstRanks.Length; k++)
            {
                slots[firstRanks[k] - 1] = firstHalf[i].Ranks[k];
            }

            for (var k = 0; k < secondRanks.Length; k++)
            {
                slots[secondRanks[k] - 1] = secondHalf[i][k];
            }

            rows.Add(new SpellLevelRow(i + 1, firstHalf[i].Cantrips, slots));
        }

        return rows;
    }

    /// <summary>
    /// The advancement table's rows that say "Nth-rank spells" at a level: the level a class gets its first slot of that rank. They are no features; they are checked against the spells-per-day table
    /// (<see cref="CheckSpellRankRows"/>).
    /// </summary>
    public static bool IsSpellRankRow(string normalizedName) => SpellRankRowName.IsMatch(normalizedName);

    /// <summary>
    /// The advancement table's "Nth-rank spells" rows against the spells-per-day table: a row at a level exactly when the highest rank up to the 9th gains a new rank at that level (and it is that rank),
    /// and the 10th-rank slot, where the table has one, comes with a feature of that level that names it.
    /// </summary>
    public static void CheckSpellRankRows(List<(int Level, string Name)> table, List<SpellLevelRow> rows, IEnumerable<ClassFeature> features, string className)
    {
        int Max9(SpellLevelRow r) => r.Slots.Take(9).Select((n, i) => n > 0 ? i + 1 : 0).DefaultIfEmpty(0).Max();
        var expected = new List<(int, string)>();
        for (var i = 1; i < rows.Count; i++)
        {
            if (Max9(rows[i]) > Max9(rows[i - 1]))
            {
                expected.Add((rows[i].Level, NormalizeAdvancementName(Ordinal(Max9(rows[i])) + "-rank spells")));
            }
        }

        var actual = table.Where(t => IsSpellRankRow(t.Name)).ToList();
        if (!actual.OrderBy(t => t.Level).SequenceEqual(expected.OrderBy(t => t.Item1)))
        {
            throw new DegreesException($"The advancement table of the {className} names its new spell ranks at levels the spells-per-day table does not agree with ({actual.Count} rows against {expected.Count}).");
        }

        var tenth = rows.FirstOrDefault(r => r.Slots[9] > 0);
        if (tenth != null && !features.Any(f => f.Level == tenth.Level && Regex.IsMatch(f.Render(), @"10th[-‑]rank", RegexOptions.CultureInvariant)))
        {
            throw new DegreesException($"The spells-per-day table of the {className} has a 10th-rank slot at level {tenth.Level}, and no feature of that level names it.");
        }
    }

    private static string Ordinal(int n) => n + (n % 100 is >= 11 and <= 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>
    /// What a spellcaster's feature text says of its casting: the tradition (one of the four), whether it keeps a repertoire (spontaneous) or prepares its spells, the focus pool it starts with, and
    /// the two checks the sheet relies on: that the spell attack modifier and the spell DC use the class's key attribute, and that the text's 1st-level spell and cantrip counts are the table's.
    /// </summary>
    public static SpellcastingFacts ReadSpellcasting(IReadOnlyList<ClassFeature> features, List<SpellLevelRow> rows, string keyAttribute, string className)
    {
        var text = string.Join('\n', features.Select(f => f.Render())).Replace('‑', '-');
        var tradition = TraditionSentence.Match(text);
        if (!tradition.Success)
        {
            throw new DegreesException($"The {className} is a spellcaster whose text does not say the tradition it casts.");
        }

        var attribute = SpellAttributeSentence.Match(text);
        var used = SpellAttributeUse.Match(text, attribute.Success ? attribute.Index : 0);
        if (!attribute.Success || !used.Success || used.Groups["a"].Value != attribute.Groups["a"].Value || AttributeKeyOf(attribute.Groups["a"].Value) != keyAttribute)
        {
            throw new DegreesException($"The {className}'s spell attack modifier and spell DC are not said to use its key attribute.");
        }

        var first = FirstLevelSpells.Match(text);
        if (!first.Success || NumberWord(first.Groups["spells"].Value) != rows[0].Slots[0] || NumberWord(first.Groups["cantrips"].Value) != rows[0].Cantrips)
        {
            throw new DegreesException($"The {className}'s text does not give the 1st-level spells and cantrips the spells-per-day table gives.");
        }

        var casting = features.Any(f => f.Name.Equals("Spell Repertoire", StringComparison.OrdinalIgnoreCase)) ? "spontaneous"
            : Regex.IsMatch(text, @"you can prepare (?:one|two|three|four|five|six) 1st-rank spells", RegexOptions.CultureInvariant) ? "prepared"
            : throw new DegreesException($"The {className} neither keeps a spell repertoire nor says it prepares spells.");
        var pool = FocusPoolSentence.Match(text);
        return new SpellcastingFacts(tradition.Groups["t"].Value, casting, pool.Success ? int.Parse(pool.Groups["n"].Value, CultureInfo.InvariantCulture) : 0);
    }

    /// <summary>The class entry's spellcasting fields: the tradition, how it casts, the focus pool, and one row for each level (cantrips, slots by rank, the highest rank and the spells known or prepared).</summary>
    internal static void AddSpellcastingFields(ParsedEntry cls, SpellcastingFacts facts, List<SpellLevelRow> rows)
    {
        cls.Raw.Add(new LabelledText("tradition", Quoted(facts.Tradition)));
        cls.Raw.Add(new LabelledText("casting", Quoted(facts.Casting)));
        if (facts.FocusPool > 0)
        {
            cls.Raw.Add(new LabelledText("focusPool", facts.FocusPool.ToString(CultureInfo.InvariantCulture)));
        }

        var lines = rows.Select(r => string.Create(CultureInfo.InvariantCulture,
            $"\n      - {{ level: {r.Level}, cantrips: {r.Cantrips}, slots: \"{r.SlotString}\", maxRank: {r.MaxRank}, spells: {r.SpellCount} }}"));
        cls.Raw.Add(new LabelledText("spellcasting", string.Concat(lines)));
    }
}
