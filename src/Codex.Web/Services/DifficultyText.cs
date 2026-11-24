using Codex.Core.Rules;
using Codex.Localization;

namespace Codex.Web.Services;

/// <summary>The words of the difficulty readout (<c>Encounters.Difficulty.*</c>), in the reader's language. The list, the picker and the editor all say a band the same way.</summary>
public static class DifficultyText
{
    /// <summary>The band alone, as a list row shows it ("Moderate", "Above High"). Null when there is no readout or nothing is counted yet.</summary>
    public static string? Word(DifficultyRating? rating, Localizer text)
    {
        if (rating is not { Available: true, Band: { } band } || rating.Position == BandPosition.Nothing) return null;
        return rating.Position switch
        {
            BandPosition.Above => text["Encounters.Difficulty.Above", band.Label],
            BandPosition.Below => text["Encounters.Difficulty.Below", band.Label],
            _ => band.Label
        };
    }

    /// <summary>The one line the editor announces: "Moderate: 250 XP for 4 characters of level 3", or "Nothing counted yet".</summary>
    public static string Summary(DifficultyRating rating, Localizer text)
    {
        if (!rating.Available) return Reason(rating.Unavailable!, text);
        if (Word(rating, text) is not { } band) return text["Encounters.Difficulty.Nothing"];
        return text["Encounters.Difficulty.Summary", band, Total(rating, text), Party(rating, text)];
    }

    /// <summary>The total with its unit ("250 XP"), or the bare number when the system names none.</summary>
    public static string Total(DifficultyRating rating, Localizer text)
    {
        var number = rating.Total.ToString("0.##", text.Culture);
        return string.IsNullOrWhiteSpace(rating.Unit) ? number : text["Encounters.Difficulty.TotalUnit", number, rating.Unit];
    }

    /// <summary>"4 characters of level 3", "4 characters, levels 2 to 4", or for a table of one level most characters share, "party level 3 (levels vary)".</summary>
    public static string Party(DifficultyRating rating, Localizer text) =>
        !rating.LevelsVary ? text["Encounters.Difficulty.Party", rating.PartySize, rating.PartyLevel]
        : rating.ByLevel ? text["Encounters.Difficulty.PartyLevelVaries", rating.PartySize, rating.PartyLevel]
        : text["Encounters.Difficulty.PartyVaries", rating.PartySize, rating.MinLevel, rating.MaxLevel];

    /// <summary>The bands and what each one's budget comes to, on one line: "Low up to 200 · Moderate up to 300".</summary>
    public static string Budgets(DifficultyRating rating, Localizer text) =>
        string.Join(" · ", rating.Budgets.Select(b => text[rating.AtMost ? "Encounters.Difficulty.UpTo" : "Encounters.Difficulty.From", b.Label, b.Budget.ToString("0", text.Culture)]));

    /// <summary>A problem the rating names, in words.</summary>
    public static string Reason(DifficultyIssue issue, Localizer text) => text[issue.Key, issue.Args];
}
