using Codex.Core;
using Codex.Core.Rules;

namespace Codex.Persistence.Runtime;

/// <summary>Resolves a linked actor's sheet from a set of loaded actor documents (docs/CREW_LINKS_DESIGN.md): the sheet page's linked actors,
/// or a table card's campaign actors when no table is live. A retired actor (in the trash) gives no sheet and is <see cref="LinkedActorStatus.Deleted"/>;
/// an actor not in the set gives none and is <see cref="LinkedActorStatus.NotFound"/>. Either way the link is unknown, so what it made is kept.</summary>
public sealed class ActorLinkedSheets : ILinkedSheets
{
    private readonly Dictionary<string, ActorDocument> _actors;

    public ActorLinkedSheets(IEnumerable<ActorDocument> actors) => _actors = actors.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object>? Sheet(string actorId) =>
        _actors.GetValueOrDefault(actorId) is { Retired: false } actor ? actor.Sheet : null;

    public LinkedActorStatus Status(string actorId) =>
        !_actors.TryGetValue(actorId, out var actor) ? LinkedActorStatus.NotFound : actor.Retired ? LinkedActorStatus.Deleted : LinkedActorStatus.Available;

    /// <summary>The ids of the actors a sheet's <c>link__*</c> keys name.</summary>
    public static IReadOnlyList<string> LinkedIds(IReadOnlyDictionary<string, object> sheet) =>
        sheet.Keys.Where(k => k.StartsWith(SheetLinks.KeyPrefix, StringComparison.Ordinal))
            .Select(k => YamlValues.GetString(sheet, k))
            .Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToList();
}
