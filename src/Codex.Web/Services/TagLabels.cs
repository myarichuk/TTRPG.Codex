using System.Text.RegularExpressions;
using Codex.Localization;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>
/// An entry's tag as the reader should see it. Tags stay as the pack wrote them (search and filters match them), so only
/// the label is translated: through the type's own option labels, then the system's value translations, then the two
/// patterns the SRD importer writes ("level-3", "cr-1/4"). An unknown tag is shown as written.
/// </summary>
public static partial class TagLabels
{
    /// <param name="type">The entry's type as the reader sees it (a localized copy carries the option labels).</param>
    /// <param name="system">The campaign's system as the rules know it; its translations hold the value labels.</param>
    public static string Show(string tag, ContentTypeDefinition? type, SystemDefinition? system, Localizer l)
    {
        foreach (var field in type?.Fields ?? new List<FieldDefinition>())
        {
            var shown = field.Display(tag);
            if (shown != tag) return shown;
        }

        if (system?.DisplayOption(l.Culture.Name, string.Empty, tag) is { } value && value != tag) return value;

        var pattern = Pattern().Match(tag);
        if (!pattern.Success) return tag;
        return pattern.Groups[1].Value.Equals("level", StringComparison.OrdinalIgnoreCase)
            ? l["Library.Detail.Level", pattern.Groups[2].Value]
            : l["Library.Tag.Challenge", pattern.Groups[2].Value];
    }

    [GeneratedRegex(@"^(level|cr)-(\d+(?:/\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
