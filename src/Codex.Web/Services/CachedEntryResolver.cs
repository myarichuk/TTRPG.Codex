using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>
/// An <see cref="IEntryResolver"/> for the sheet rule engine, pre-loaded with every entry (pack and
/// homebrew) of the content types a sheet can reference (its fields, lists and inventory, and a sheet kind's lists and slot tracks), so evaluation is synchronous and cheap
/// while the player edits.
/// </summary>
public sealed class CachedEntryResolver : IEntryResolver
{
    private readonly Dictionary<string, List<ContentEntry>> _byType = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Type, string Id), ContentEntry> _byId = new();

    /// <param name="extra">Entries beside the Library's: the ones a vault character carries. A Library entry of the same type
    /// and id stands.</param>
    public static async Task<CachedEntryResolver> LoadAsync(CampaignLibrary library, CampaignDocument campaign, CampaignAccess access, SystemDefinition system, IEnumerable<ContentEntry>? extra = null)
    {
        var resolver = new CachedEntryResolver();
        var types = system.ActorFields.Where(f => f.Type == FieldType.EntityReference && f.TargetEntityType != null).Select(f => f.TargetEntityType!)
            .Concat(system.SheetRules?.Lists.Select(l => l.TargetEntityType) ?? Enumerable.Empty<string>())
            .Concat(system.SheetRules?.Inventory?.Types ?? Enumerable.Empty<string>())
            // A sheet kind's pick lists, and what its slot tracks may hold (a 3.5 spell).
            .Concat(system.SheetKinds.SelectMany(k => k.Lists.Select(l => l.Type ?? "")))
            .Concat(system.SheetKinds.SelectMany(k => k.Tracks.Select(t => t.Holds?.Type ?? "")).Where(t => !t.Equals("text", StringComparison.OrdinalIgnoreCase)))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var type in types)
        {
            var items = await library.OptionsAsync(campaign, access, type);
            var list = new List<ContentEntry>();
            foreach (var item in items)
            {
                list.Add(item.Entry);
                resolver._byId[(type.ToLowerInvariant(), item.Entry.Id)] = item.Entry;
            }

            resolver._byType[type] = list;
        }

        foreach (var entry in extra ?? Enumerable.Empty<ContentEntry>())
        {
            if (!resolver._byId.TryAdd((entry.Type.ToLowerInvariant(), entry.Id), entry)) continue;
            if (!resolver._byType.TryGetValue(entry.Type, out var ofType)) resolver._byType[entry.Type] = ofType = new List<ContentEntry>();
            ofType.Add(entry);
        }

        return resolver;
    }

    public ContentEntry? Resolve(string type, string id) =>
        _byId.TryGetValue((type.ToLowerInvariant(), id), out var entry) ? entry : null;

    public IEnumerable<ContentEntry> Candidates(string type) =>
        _byType.TryGetValue(type, out var list) ? list : Enumerable.Empty<ContentEntry>();
}
