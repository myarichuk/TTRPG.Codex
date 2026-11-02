using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Codex.Core.AI;
using Codex.Core.WorldBuilder;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>What saving a world-building proposal stored.</summary>
public sealed record WorldBuildResult(
    IReadOnlyList<HomebrewEntryDocument> Entries,
    IReadOnlyList<WikiPageDocument> Pages,
    IReadOnlyList<ActorDocument> Actors,
    IReadOnlyList<FactDocument> Facts,
    IReadOnlyList<LocationDocument> Places,
    IReadOnlyList<BestiaryEntry> Shortlisted,
    IReadOnlyList<WorldEdit>? Edits = null,
    int Retagged = 0,
    int Picked = 0)
{
    /// <summary>The existing entries whose text the proposal replaced.</summary>
    public IReadOnlyList<WorldEdit> Changes => Edits ?? Array.Empty<WorldEdit>();

    /// <summary>Entries saved: <see cref="Retagged"/> counts the pages and homebrew entries whose tags changed, <see cref="Picked"/>
    /// the Library creatures put on the Bestiary by an entry of their own (a new homebrew creature put there is one entry, not two).</summary>
    public int Count => Entries.Count + Pages.Count + Actors.Count + Facts.Count + Places.Count + Changes.Count + Retagged + Picked;
}

/// <summary>An existing entry a proposal changed: what kind of text, its id (a page's slug), and its name.</summary>
/// <param name="Shared">The changed thing is one players can see (a party page, a known place or cast member, a published entry),
/// so they read the change too, all but its hidden blocks.</param>
public sealed record WorldEdit(EditTarget Target, string Id, string Name, bool Shared = false, bool Removed = false, bool Text = true);

/// <summary>
/// The world builder (docs/WORLD_BUILDER.md): what the world-building chat's tools read about the campaign, the check of a
/// proposal, and the save of the entries the DM keeps. The model only proposes; every write goes through the repositories with
/// the DM's access, and what it saves starts hidden from players (homebrew unpublished; wiki pages, secrets, cast members and
/// places DM-only). DM only.
/// </summary>
public sealed partial class WorldBuilderService(ISystemCatalog catalog, CampaignLibrary library, IHomebrewRepository homebrew, IWikiRepository wiki,
    IFactRepository facts, CastService cast, IRegionRepository regions, ICampaignRepository campaigns, IActorRepository actorRepository, AiFeatures ai,
    ITemplateRepository? templates = null)
{
    /// <summary>The language the model writes in: the one the DM reads the app in.</summary>
    private static string Lang => SupportedCultures.Normalize(CultureInfo.CurrentUICulture.Name);

    // Most lines a list tool returns, and most characters of a page get_page returns: enough to work with, not a whole wiki.
    private const int ContextCap = 40;
    private const int MaxPageText = 6000;

    private static IEnumerable<WorldDraft> Kept(WorldBundle bundle) => bundle.Drafts.Where(d => d.Selected && !string.IsNullOrWhiteSpace(d.Name));

    // Wiki tags are the DM's own words, so the ones the builder adds are written in the DM's language.
    private static string Tag(Localizer l, string key) => l.Has($"WorldBuilder.Tag.{key}") ? l[$"WorldBuilder.Tag.{key}"] : key;

    /// <summary>The types a homebrew entry may have, as the DM reads them: the system's declared types (and any extra ones the Library has).</summary>
    public Task<IReadOnlyList<ContentTypeDefinition>> TypesAsync(CampaignDocument campaign, CampaignAccess access) => library.TypesAsync(campaign, access);

    private async Task<IReadOnlyList<ContentTypeDefinition>> HomebrewTypesAsync(CampaignDocument campaign, CampaignAccess access, SystemDefinition? system)
    {
        var shown = await library.TypesAsync(campaign, access);
        return shown.Select(t => system?.Canonical.FindType(t.Type) ?? t).ToList();
    }

    // Every way the model might name a type: its key and folder, its English labels and the ones the DM reads.
    private Dictionary<string, string> TypeSpellings(IReadOnlyList<ContentTypeDefinition> types, string systemId)
    {
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localized = catalog.GetLocalizedDefinition(systemId);
        foreach (var type in types)
        {
            var shown = localized?.FindType(type.Type);
            foreach (var spelling in new[] { type.Type, type.Folder, type.Label, type.PluralLabel, shown?.Label, shown?.PluralLabel })
            {
                if (!string.IsNullOrWhiteSpace(spelling)) spellings.TryAdd(spelling.Trim(), type.Type);
            }
        }

        return spellings;
    }

    /// <summary>The fields a homebrew entry fills: the type's editor fields, with the options a reference may pick and the
    /// translated label of each dropdown option, so a value written in the DM's language still maps to the stored one.</summary>
    private async Task<List<FieldSpec>> FieldSpecsAsync(CampaignDocument campaign, CampaignAccess access, SystemDefinition? system, ContentTypeDefinition type)
    {
        var specs = new List<FieldSpec>();
        foreach (var field in type.EditorFields().Where(f => f.Type != FieldType.ImagePicker))
        {
            IReadOnlyList<string>? options = field.Type switch
            {
                FieldType.Dropdown => (field.Options ?? new List<string>())
                    .Select(o => system?.DisplayOption(Lang, field.Key, o) is { } shown && shown != o ? $"{o} ({shown})" : o)
                    .ToList(),
                FieldType.EntityReference when field.TargetEntityType != null =>
                    (await library.OptionsAsync(campaign, access, field.TargetEntityType)).Take(80).Select(i => $"{i.Entry.Id} ({i.Entry.Name})").ToList(),
                _ => null
            };
            // Image prompts feed an image model and stay English, as everywhere else.
            var hint = field.Key == "imagePrompt" ? (field.Description + " Write this one in English.").Trim() : field.Description;
            specs.Add(new FieldSpec(field.Key, field.Label, field.Type.ToString(), options, field.Min, field.Max, hint));
        }

        return specs;
    }

    /// <summary>A type as the model reads it: the label system.yaml gives it, or its key made readable when it has none.</summary>
    private static string PromptLabel(string key, ContentTypeDefinition? type) =>
        string.IsNullOrWhiteSpace(type?.Label) ? CampaignLibrary.Humanize(key) : type.Label;

    private static string KindName(PlaceKind kind) => kind == PlaceKind.Site ? "site" : kind.ToString().ToLowerInvariant();

    // "Region > Town": where a place sits.
    private static string Path(RegionDocument region, LocationDocument place) =>
        string.Join(" > ", PlaceTree.Ancestors(region.Locations, place).Select(p => p.Name).Prepend(region.Name));

    private static WikiPageDocument? PageOf(ActorDocument actor, IEnumerable<WikiPageDocument> pages) =>
        string.IsNullOrEmpty(actor.WikiPageId) ? null : pages.FirstOrDefault(p => p.Id == actor.WikiPageId);

    // The names stored mentions stand for, so the model reads "Mother Ilse", not an id.
    private static Dictionary<string, string> Names(IEnumerable<ActorDocument> actors, IEnumerable<RegionDocument> allRegions)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var actor in actors) names[$"actor:{actor.Id}"] = actor.Name;
        foreach (var place in allRegions.SelectMany(r => r.Locations)) names[$"place:{place.Id}"] = place.Name;
        return names;
    }

    private static string Cap(string text) =>
        text.Length > MaxPageText ? text[..MaxPageText] + "\n[...] (too long to show whole: it cannot be replaced by an edit)" : text;

    // A text as the model reads it to edit it: a mention stays a marker, [[actor:id|Name]], so copying a passage back keeps the link.
    private static string Editable(string? body, IReadOnlyDictionary<string, string> names) =>
        Mention.Replace(body ?? string.Empty, m => $"[[{m.Groups[1].Value}|{names.GetValueOrDefault(m.Groups[1].Value) ?? m.Groups[1].Value.Split('/').Last().Split(':').Last()}]]");

    private static readonly Regex MentionMarker = new(@"\[\[((?:actor|place|item):[^\]\s|]+)(?:\|[^\]]*)?\]\]", RegexOptions.Compiled);
    private static readonly Regex HeldMarker = new("\u0007(\\d{1,6})\u0008", RegexOptions.Compiled);

    /// <summary>The text an edit saves: links to the proposal's entries and pages resolved as for any entry, but mentions kept as the
    /// stored <c>[[actor:id]]</c> they were (the model wrote them back as <c>[[actor:id|Name]]</c>); resolving them as wiki links
    /// would turn the id into text.</summary>
    internal static string ResolveEditText(string body, WorldBundle bundle, bool keepLinks)
    {
        var held = new List<string>();
        var text = MentionMarker.Replace(body.Replace("\u0007", string.Empty).Replace("\u0008", string.Empty), m =>
        {
            held.Add($"[[{m.Groups[1].Value}]]");
            return $"\u0007{held.Count - 1}\u0008";
        });
        text = WorldBundleParser.ResolveLinks(text, bundle, keepLinks);
        return held.Count == 0 ? text : HeldMarker.Replace(text, m => held[int.Parse(m.Groups[1].Value)]);
    }

    // A homebrew entry is stamped by its last save: an edit compares fields as well as text, and every save moves it.
    private static string HomebrewStamp(HomebrewEntryDocument entry) => Stamp(entry.UpdatedAt.Ticks.ToString(CultureInfo.InvariantCulture) + "\n" + entry.Description);

    private static string Stamp(string? text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));

    private static readonly Regex Mention = new(@"\[\[((?:actor|place|item):[^\]\s]+)\]\]", RegexOptions.Compiled);

    private static string Plain(string? body, IReadOnlyDictionary<string, string> names) =>
        Mention.Replace(body ?? string.Empty, m => names.GetValueOrDefault(m.Groups[1].Value) ?? m.Groups[1].Value.Split('/').Last().Split(':').Last());

    private static string UniqueEntryId(IEnumerable<HomebrewEntryDocument> existing, string type, string name)
    {
        var taken = existing.Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase)).Select(e => e.EntryId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slug = RavenHomebrewRepository.Slugify(name);
        if (!taken.Contains(slug)) return slug;
        for (var i = 2; ; i++)
        {
            if (!taken.Contains($"{slug}-{i}")) return $"{slug}-{i}";
        }
    }

    private static string CampaignContext(CampaignDocument campaign) =>
        $"{campaign.Name}{(campaign.Blurb(300) is { Length: > 0 } blurb ? ": " + blurb : "")}";

    private static void DmOnly(CampaignAccess access)
    {
        if (!access.IsDm) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
    }
}
