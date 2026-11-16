using Codex.Localization;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Components.Shared;

/// <summary>The words for the degrees of a check (<c>criticalSuccess</c>, <c>success</c>, <c>failure</c>, <c>criticalFailure</c>), in the reader's language.</summary>
public static class DegreeWords
{
    /// <summary>The word for an outcome of the system's check roll (<c>dice.checkRoll</c>); null when the system has none, or the outcome is not one of the four.
    /// Never a colour or a bare key: a degree is always said in words.</summary>
    public static string? Of(Localizer l, SystemDefinition? system, string? outcome)
    {
        if (outcome == null || system?.Dice.CheckRoll == null) return null;
        // The system's own word for it, in the reader's language, comes first; the app's four words are what is left.
        if (system.DisplayOption(l.Culture.Name, string.Empty, outcome) is { } own && own != outcome) return own;
        var key = "Actor.Degree." + outcome;
        return l.Has(key) ? l[key] : null;
    }
}
