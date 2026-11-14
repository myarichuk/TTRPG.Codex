using Codex.Core;
using Codex.Core.Rules;

namespace Codex.Persistence.Runtime;

/// <summary>Resolves a linked actor's sheet from a set of loaded actor documents (docs/CREW_LINKS_DESIGN.md): the sheet page's linked actors,
/// or a table card's campaign actors when no table is live. An actor not in the set resolves to null, a known, empty link.</summary>
public sealed class ActorLinkedSheets : ILinkedSheets
{
    private readonly Dictionary<string, ActorDocument> _actors;

    public ActorLinkedSheets(IEnumerable<ActorDocument> actors) => _actors = actors.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object>? Sheet(string actorId) => _actors.GetValueOrDefault(actorId)?.Sheet;

    /// <summary>The ids of the actors a sheet's <c>link__*</c> keys name.</summary>
    public static IReadOnlyList<string> LinkedIds(IReadOnlyDictionary<string, object> sheet) =>
        sheet.Keys.Where(k => k.StartsWith(SheetLinks.KeyPrefix, StringComparison.Ordinal))
            .Select(k => YamlValues.GetString(sheet, k))
            .Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToList();
}
