using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>A problem the readout has to say, as a resource key and its arguments, so the page words it in the reader's language and a test can name it.</summary>
public sealed record DifficultyIssue(string Key, params object[] Args);

/// <summary>One line of a lineup as the rating needs it. <see cref="Value"/> is the number the system's field gives for one creature of the line (what it is worth for shape
/// <c>sum</c>, its level for <c>byLevel</c>), or null when it has none; <see cref="Problem"/> says why, when the reader of the lineup knows. A hazard line is not counted.</summary>
public sealed record RatedLine(string Name, int Count, double? Value, DifficultyIssue? Problem = null, bool Hazard = false);

/// <summary>A line that did not count, and why.</summary>
public sealed record NotCountedLine(string Name, DifficultyIssue Reason);

/// <summary>A band and what the party's budget for it comes to.</summary>
public sealed record BandBudget(string Key, string Label, long Budget);

/// <summary>Where the total landed: <see cref="Nothing"/> when it is zero, <see cref="In"/> a band, <see cref="Above"/> the last band's budget (for <c>atMost</c>) or <see cref="Below"/>
/// the first band's (for <c>atLeast</c>).</summary>
public enum BandPosition { Nothing, In, Above, Below }

/// <summary>What the readout says. When <see cref="Unavailable"/> is set there is no readout and everything else is empty: the reason says why.</summary>
public sealed record DifficultyRating
{
    public DifficultyIssue? Unavailable { get; init; }
    public double Total { get; init; }
    public string? Unit { get; init; }
    public IReadOnlyList<BandBudget> Budgets { get; init; } = Array.Empty<BandBudget>();
    public BandPosition Position { get; init; }

    /// <summary>The band the total is in; for <see cref="BandPosition.Above"/> the last band, for <see cref="BandPosition.Below"/> the first; null for <see cref="BandPosition.Nothing"/>.</summary>
    public BandBudget? Band { get; init; }

    public bool AtMost { get; init; } = true;

    /// <summary>True for shape <c>byLevel</c>, whose party has one level (the one most characters have) rather than a row for each.</summary>
    public bool ByLevel { get; init; }
    public int PartySize { get; init; }
    public int MinLevel { get; init; }
    public int MaxLevel { get; init; }

    /// <summary>The party's level: the one level, or for shape <c>byLevel</c> the level most characters have (the lower when tied).</summary>
    public int PartyLevel { get; init; }

    public bool LevelsVary => MinLevel != MaxLevel;

    /// <summary>The lines that could not be valued, each with its reason. They do not count; the band is given from the rest.</summary>
    public IReadOnlyList<NotCountedLine> NotCounted { get; init; } = Array.Empty<NotCountedLine>();

    /// <summary>The names of hazard lines, which are not counted.</summary>
    public IReadOnlyList<string> Hazards { get; init; } = Array.Empty<string>();

    public bool Available => Unavailable == null;

    public static DifficultyRating None(DifficultyIssue reason) => new() { Unavailable = reason };
}

/// <summary>
/// The difficulty readout's arithmetic (ENCOUNTER_BUILDER_DESIGN.md 2.1 and 16), pure: no database, no language. The rules were checked by the loader
/// (<c>EncounterChecks</c>), so a budget is read here without being checked again. Every number is the system's; none is built in.
/// </summary>
public static class EncounterDifficulty
{
    public static DifficultyRating Rate(DifficultyRules rules, IReadOnlyList<int> partyLevels, IReadOnlyList<RatedLine> lines)
    {
        if (partyLevels.Count == 0) return DifficultyRating.None(new DifficultyIssue("Encounters.Difficulty.NoParty"));
        var sum = rules.Shape == DifficultyRules.ShapeSum;
        var min = partyLevels.Min();
        var max = partyLevels.Max();
        // The level most characters have; a tie takes the lower.
        var partyLevel = partyLevels.GroupBy(l => l).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        var budgets = sum ? SumBudgets(rules, partyLevels) : ByLevelBudgets(rules, partyLevels.Count);
        if (budgets.Unavailable != null) return DifficultyRating.None(budgets.Unavailable);

        var notCounted = new List<NotCountedLine>();
        var hazards = new List<string>();
        double total = 0;
        foreach (var line in lines)
        {
            if (line.Hazard)
            {
                hazards.Add(line.Name);
                continue;
            }

            if (line.Value is not { } raw)
            {
                notCounted.Add(new NotCountedLine(line.Name, line.Problem ?? new DifficultyIssue("Encounters.Difficulty.NoValue", line.Name, string.Empty)));
                continue;
            }

            if (raw < 0)
            {
                notCounted.Add(new NotCountedLine(line.Name, new DifficultyIssue("Encounters.Difficulty.Negative", line.Name)));
                continue;
            }

            if (sum)
            {
                total += raw * line.Count;
                continue;
            }

            if (raw != Math.Floor(raw))
            {
                notCounted.Add(new NotCountedLine(line.Name, new DifficultyIssue("Encounters.Difficulty.NotWhole", line.Name)));
                continue;
            }

            var delta = (int)raw - partyLevel;
            var table = rules.Values;
            if (table.Count == 0 || delta > table.Max(v => v.Delta))
            {
                notCounted.Add(new NotCountedLine(line.Name, new DifficultyIssue("Encounters.Difficulty.TooFarAbove", line.Name)));
            }
            else if (delta < table.Min(v => v.Delta))
            {
                notCounted.Add(new NotCountedLine(line.Name, new DifficultyIssue("Encounters.Difficulty.TooFarBelow", line.Name)));
            }
            else
            {
                total += (table.FirstOrDefault(v => v.Delta == delta)?.Value ?? 0) * (double)line.Count;
            }
        }

        var bands = rules.Bands.Select((b, i) => new BandBudget(b.Key, b.Label, budgets.PerBand[i])).ToList();
        var atMost = rules.Pick != DifficultyRules.PickAtLeast;
        var position = BandPosition.Nothing;
        BandBudget? band = null;
        if (total > 0)
        {
            if (atMost)
            {
                band = bands.FirstOrDefault(b => b.Budget >= total);
                position = band != null ? BandPosition.In : BandPosition.Above;
                band ??= bands[^1];
            }
            else
            {
                band = bands.LastOrDefault(b => b.Budget <= total);
                position = band != null ? BandPosition.In : BandPosition.Below;
                band ??= bands[0];
            }
        }

        return new DifficultyRating
        {
            Total = total, Unit = sum ? rules.Unit : null, Budgets = bands, Position = position, Band = band, AtMost = atMost, ByLevel = !sum,
            PartySize = partyLevels.Count, MinLevel = min, MaxLevel = max, PartyLevel = partyLevel, NotCounted = notCounted, Hazards = hazards
        };
    }

    private sealed record Budgets(long[] PerBand, DifficultyIssue? Unavailable = null);

    /// <summary>Each character adds the number of their level's row; a level below the first row or above the last gives no readout. A level between two rows takes the row below it.</summary>
    private static Budgets SumBudgets(DifficultyRules rules, IReadOnlyList<int> levels)
    {
        var rows = rules.Budget.Select(r => (Level: int.Parse(r["level"], System.Globalization.CultureInfo.InvariantCulture), Row: r)).OrderBy(r => r.Level).ToList();
        var perBand = new long[rules.Bands.Count];
        foreach (var level in levels)
        {
            if (level < rows[0].Level || level > rows[^1].Level) return new Budgets(perBand, new DifficultyIssue("Encounters.Difficulty.NoBudgetRow", level));
            var row = rows.Last(r => r.Level <= level).Row;
            for (var i = 0; i < perBand.Length; i++) perBand[i] += long.Parse(row[rules.Bands[i].Key], System.Globalization.CultureInfo.InvariantCulture);
        }

        return new Budgets(perBand);
    }

    /// <summary>Each band's budget for a party of <paramref name="size"/>: its value, plus (or minus) <c>perCharacter</c> for each character above (or below) the size it is written for, never below zero.</summary>
    private static Budgets ByLevelBudgets(DifficultyRules rules, int size)
    {
        var perBand = new long[rules.Bands.Count];
        for (var i = 0; i < perBand.Length; i++)
        {
            var value = (long)(rules.Bands[i].Value ?? 0);
            var per = (long)(rules.Bands[i].PerCharacter ?? 0);
            // The loader refuses bands that cross; a running maximum keeps them in order anyway, so a band is never below the one before it.
            perBand[i] = Math.Max(Math.Max(0, value + per * (size - (rules.PartySize ?? size))), i > 0 ? perBand[i - 1] : 0);
        }

        return new Budgets(perBand);
    }
}
