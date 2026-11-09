using Codex.Plugin.Abstractions;

namespace Codex.Core.Rules;

/// <summary>
/// Applies the intents that change a sheet's tracks: <c>adjust</c> (<c>{ track, by }</c>) and <c>set</c>
/// (<c>{ track, to }</c>), each kept within the track's minimum and maximum. What happens past the edge (overflow into trauma,
/// harm pushing up) and every other intent are the table's, with event handlers (overhaul step 5); they are returned
/// unapplied.
/// </summary>
public static class SheetIntents
{
    public static IReadOnlyList<SheetIntent> Apply(SystemDefinition system, IEntryResolver resolver, Dictionary<string, object> sheet, IEnumerable<SheetIntent> intents)
    {
        var left = new List<SheetIntent>();
        foreach (var intent in intents)
        {
            if (intent.Fields.GetValueOrDefault("track")?.ToString() is not { Length: > 0 } track
                || (intent.Kind, intent.Fields.GetValueOrDefault(intent.Kind == "set" ? "to" : "by")) is not ("adjust" or "set", var amount)
                || YamlValues.ToNumber(amount) is not { } number)
            {
                left.Add(intent);
                continue;
            }

            var current = YamlValues.ToNumber(sheet.GetValueOrDefault(track)) ?? 0;
            var next = intent.Kind == "set" ? number : current + number;
            if (new SheetModel(system, resolver, sheet).TrackBounds(track) is var (min, max))
            {
                if (min is { } floor) next = Math.Max(next, floor);
                if (max is { } ceiling) next = Math.Min(next, ceiling);
            }

            sheet[track] = next == Math.Floor(next) && Math.Abs(next) < long.MaxValue ? (object)(long)next : next;
        }

        return left;
    }
}
