namespace Codex.Web.Components.Shared;

/// <summary>A model the picker suggests. <paramref name="Label"/> is a friendly name (the id is what is saved), <paramref name="Detail"/>
/// a short line to tell models apart (size, price), and <paramref name="Tools"/> marks one that can call tools.</summary>
public sealed record ModelOption(string Id, string? Label = null, string? Detail = null, bool Tools = false);

/// <summary>Narrows the picker's options as the user types.</summary>
public static class ModelFilter
{
    /// <summary>The options matching <paramref name="query"/>, at most <paramref name="limit"/> of them, and how many matched in all.
    /// Every word typed must appear in the id or the name, in any case, so "claude son" finds "anthropic/claude-sonnet-5-5";
    /// a model whose id or name starts with the first word comes before one that merely contains it. With nothing typed
    /// everything matches, in the order given.</summary>
    public static IReadOnlyList<ModelOption> Apply(IReadOnlyList<ModelOption> options, string? query, int limit, out int total)
    {
        var words = (query ?? string.Empty).Split([' ', '\t', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            total = options.Count;
            return options.Count <= limit ? options : [.. options.Take(limit)];
        }

        bool Has(ModelOption o, string word) =>
            o.Id.Contains(word, StringComparison.OrdinalIgnoreCase) || (o.Label?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false);

        var matches = options.Where(o => words.All(w => Has(o, w))).ToList();
        total = matches.Count;
        var first = words[0];
        return [.. matches
            .OrderBy(o => o.Id.StartsWith(first, StringComparison.OrdinalIgnoreCase) || (o.Label?.StartsWith(first, StringComparison.OrdinalIgnoreCase) ?? false) ? 0 : 1)
            .Take(limit)];
    }
}
