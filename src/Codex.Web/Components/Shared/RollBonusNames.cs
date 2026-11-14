using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Components.Shared;

/// <summary>
/// What a roll bonus is called in the reader's language. The engine works on the entries' own (English) text, so a bonus's <c>Source</c> (the feature
/// that gives it) and <c>Label</c> (what its tick box says) are English; this says them in the viewer's culture and never touches an id.
/// A source is the translated name of the entry, or of the row of an entry's <c>features</c>, that has that English name (the packs' own translations).
/// A label is the system's translation of that text (<c>values</c> in <c>system.&lt;locale&gt;.yaml</c>), or the translated source when the label is just the
/// feature's name. Anything not translated stays as it is, and English is unchanged.
/// </summary>
public sealed class RollBonusNames
{
    /// <summary>Kept per loaded definition: a reload of the systems makes new definitions (and their localized copies) along with the packs, so an index is
    /// never read past the content it was built from, though the registry object itself is refilled in place.</summary>
    private static readonly ConditionalWeakTable<SystemDefinition, ConcurrentDictionary<string, IReadOnlyDictionary<string, string>>> Indexes = new();

    /// <summary>Names as written: nothing is translated.</summary>
    public static RollBonusNames English { get; } = new(null, null);

    private readonly SystemDefinition? _definition;
    private readonly IReadOnlyDictionary<string, string>? _sources;

    private readonly string? _culture;

    private RollBonusNames(SystemDefinition? definition, IReadOnlyDictionary<string, string>? sources, string? culture = null)
    {
        _definition = definition;
        _sources = sources;
        _culture = culture;
    }

    /// <summary>The system's translation of a word (<c>values</c>), read from either copy of the definition; the word itself when there is none.</summary>
    private string Value(string text) => _definition?.DisplayOption(_culture, string.Empty, text) ?? text;

    /// <param name="definition">The system as the reader reads it (<see cref="SystemDefinition.Localize"/>), which holds the translated labels.</param>
    public static RollBonusNames For(IContentRegistry? registry, SystemDefinition? definition, string? culture)
    {
        if (definition == null) return English;
        if (registry == null || string.IsNullOrWhiteSpace(culture) || !registry.IsLocalized(culture)) return new RollBonusNames(definition, null);
        var byCulture = Indexes.GetOrCreateValue(definition);
        var index = byCulture.GetOrAdd(culture, c => Index(registry, definition.Id, c));
        return new RollBonusNames(definition, index, culture);
    }

    /// <summary>The name of the feature a bonus comes from, as the reader reads it.</summary>
    public string Source(string source) => Translate(source);

    /// <summary>The whole text translated as one name, else its parts: a feature that offers options is named <c>Deadly (Crew): Prowl</c>, and a part that has no
    /// translation stays as it is.</summary>
    private string Translate(string text) => Exact(text) ?? Composed(text) ?? text;

    private string? Exact(string text)
    {
        if (_sources != null && _sources.TryGetValue(text, out var shown)) return shown;
        var word = Value(text);
        return string.Equals(word, text, StringComparison.Ordinal) ? null : word;
    }

    private string? Composed(string text)
    {
        if (text.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0) return Join(text[..colon], ": ", text[(colon + 2)..], string.Empty);
        if (text.EndsWith(')') && text.LastIndexOf(" (", StringComparison.Ordinal) is var open and > 0) return Join(text[..open], " (", text[(open + 2)..^1], ")");
        return null;
    }

    private string? Join(string left, string glue, string right, string close)
    {
        var first = Translate(left);
        var second = Translate(right);
        return first == left && second == right ? null : first + glue + second + close;
    }

    /// <summary>What a tick box or a counted bonus says, as the reader reads it. <paramref name="source"/> is the feature it came from.</summary>
    public string Label(string label, string source)
    {
        var shown = Value(label);
        if (!string.Equals(shown, label, StringComparison.Ordinal)) return shown;
        return string.Equals(label, source, StringComparison.OrdinalIgnoreCase) ? Source(source) : label;
    }

    private static Dictionary<string, string> Index(IContentRegistry registry, string systemId, string culture)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in registry.GetEntryTypes(systemId).ToList())
        {
            foreach (var entry in registry.GetEntries(systemId, type).ToList())
            {
                var shown = registry.Localize(entry, culture);
                if (ReferenceEquals(shown, entry)) continue; // not translated
                Add(names, entry.Name, shown.Name);
                if (Rows(entry) is not { } original || Rows(shown) is not { } translated || original.Count != translated.Count) continue;
                for (var i = 0; i < original.Count; i++) Add(names, original[i], translated[i]);
            }
        }

        return names;
    }

    private static void Add(Dictionary<string, string> names, string? english, string? translated)
    {
        if (string.IsNullOrWhiteSpace(english) || string.IsNullOrWhiteSpace(translated) || english == translated) return;
        names.TryAdd(english, translated);
    }

    /// <summary>The names of an entry's <c>features</c> rows, in order; null when it has none or they are not rows with names.</summary>
    private static List<string?>? Rows(ContentEntry entry)
    {
        if (!entry.Properties.TryGetValue("features", out var value) || value is string || value is not IEnumerable<object> rows) return null;
        var names = new List<string?>();
        foreach (var row in rows)
        {
            if (row is not IDictionary<string, object> map) return null;
            names.Add(map.TryGetValue("name", out var name) ? name as string : null);
        }

        return names;
    }
}
