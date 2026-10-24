using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

/// <summary>
/// One piece of reference content of any kind - a class, a feat, a spell, a monster, an item, a lore
/// entry - as a system-agnostic record. The <see cref="Type"/> names a <see cref="ContentTypeDefinition"/>
/// in the owning system (or is simply the pack folder it came from, for systems that don't declare
/// schemas), and <see cref="Properties"/> holds whatever that type's fields say. Homebrew authored in
/// the web app and SRD content imported from a pack share this exact shape, which is what lets them
/// be searched, displayed and picked together.
/// </summary>
public class ContentEntry
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string PackId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>"system:type:id" of an entry whose properties this one falls back to.</summary>
    public string? Inherits { get; set; }

    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object> Properties { get; set; } = new();

    /// <summary>Optional source reference inside the pack (page, section) for the attribution footer.</summary>
    public string? Source { get; set; }

    /// <summary>Property keys every system understands: a short piece of lore (<c>flavor</c>), where a
    /// creature lives and how it acts, and a one-sentence visual description for generating art.
    /// A <c>flavor/</c> folder in a pack fills these in on entries other packs define.</summary>
    public static readonly string[] FlavorKeys = { "flavor", "habitat", "behavior", "imagePrompt" };

    /// <summary>Set by the loader on an entry whose flavor came from another pack: that pack's id, so the
    /// Library can credit the lore to its own author and licence instead of the pack that owns the rules.</summary>
    public const string FlavorSourceKey = "flavorSource";

    /// <summary>Set by <c>IContentRegistry.Localize</c> on the display copy of a translated entry: the id of the translation pack (several
    /// ids, comma-separated, when more than one pack translated the entry), so the Library can credit the translators and show
    /// each translation's licence next to the translated text.</summary>
    public const string TranslationSourceKey = "translationSource";

    /// <summary>
    /// Copies registered flavor onto this entry. With <paramref name="overwrite"/> false (an entry the table
    /// wrote itself) anything the entry already says is kept. The source pack is credited only when the
    /// lore text itself came from the overlay.
    /// </summary>
    public void AddFlavor(IReadOnlyDictionary<string, object> flavor, bool overwrite)
    {
        var loreFromOverlay = false;
        foreach (var (name, value) in flavor)
        {
            if (name == FlavorSourceKey || (!overwrite && Properties.ContainsKey(name))) continue;
            Properties[name] = value;
            loreFromOverlay |= name == "flavor";
        }

        if (loreFromOverlay && flavor.TryGetValue(FlavorSourceKey, out var source)) Properties[FlavorSourceKey] = source;
    }

    public string FullId => $"{SystemId}:{Type}:{Id}";

    /// <summary>Fills in anything this entry left unset from <paramref name="baseEntry"/>. Lists and
    /// dictionaries are merged key-wise; a key the child sets always wins.</summary>
    public void MergeFrom(ContentEntry baseEntry)
    {
        if (string.IsNullOrEmpty(Name)) Name = baseEntry.Name;
        Description ??= baseEntry.Description;
        Source ??= baseEntry.Source;

        foreach (var tag in baseEntry.Tags)
        {
            if (!Tags.Contains(tag)) Tags.Add(tag);
        }

        foreach (var (key, value) in baseEntry.Properties)
        {
            if (!Properties.ContainsKey(key)) Properties[key] = value;
        }
    }

    public ContentEntry Clone() => new()
    {
        Id = Id,
        Type = Type,
        SystemId = SystemId,
        PackId = PackId,
        Name = Name,
        Description = Description,
        Inherits = Inherits,
        Source = Source,
        Tags = new List<string>(Tags),
        Properties = new Dictionary<string, object>(Properties)
    };
}
