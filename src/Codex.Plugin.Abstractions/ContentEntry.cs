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
