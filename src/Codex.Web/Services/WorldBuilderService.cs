using System.Globalization;
using Codex.Core;
using Codex.Core.AI;
using Codex.Core.WorldBuilder;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>What saving a world-builder bundle stored.</summary>
public sealed record WorldBuildResult(IReadOnlyList<HomebrewEntryDocument> Entries, IReadOnlyList<WikiPageDocument> Pages);

/// <summary>
/// The world builder (docs/WORLD_BUILDER.md): turns a recipe's answers into an outline of drafts, writes each draft in full,
/// and saves the ones the DM keeps. The model only drafts; every write goes through the repositories with the DM's access,
/// and what it saves starts hidden from players (homebrew unpublished, wiki pages DM-only). DM only, on the DM's own key.
/// </summary>
public sealed class WorldBuilderService(AiProviderService providers, ISystemCatalog catalog, CampaignLibrary library, IHomebrewRepository homebrew, IWikiRepository wiki)
{
    /// <summary>The language the model writes in: the one the DM reads the app in.</summary>
    private static string Lang => SupportedCultures.Normalize(CultureInfo.CurrentUICulture.Name);

    private const int ContextCap = 40;

    public IReadOnlyList<WorldRecipe> Recipes => WorldRecipes.All;

    /// <summary>Asks for the recipe's drafts. <paramref name="answers"/> is keyed by question key; an empty answer is left to the model.</summary>
    public async Task<WorldBundle> OutlineAsync(CampaignDocument campaign, CampaignAccess access, WorldRecipe recipe, IReadOnlyDictionary<string, string?> answers, CancellationToken ct = default)
    {
        DmOnly(access);
        var assistant = await providers.AssistantForAsync(access.UserId);
        var system = catalog.GetDefinition(campaign.SystemId);
        var types = await HomebrewTypesAsync(campaign, access, system);
        var pages = (await wiki.GetVisibleForCampaignAsync(access)).ToList();
        var taken = (await homebrew.GetVisibleForCampaignAsync(access)).Select(h => h.Name)
            .Concat(pages.Select(p => p.Title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ContextCap * 2)
            .ToList();

        var kinds = recipe.Drafts.Select(k => k switch
        {
            DraftKinds.Homebrew => "homebrew: a Library entry of one of the content types",
            DraftKinds.Wiki => "wiki: a page of the campaign's world wiki",
            _ => k
        }).ToList();

        var request = new WorldOutlineRequest(
            recipe.Prompt,
            catalog.GetDisplayName(campaign.SystemId),
            Answers(recipe, answers, system),
            kinds,
            recipe.Drafts.Contains(DraftKinds.Homebrew) ? types.Select(t => $"{t.Type} ({t.Label})").ToList() : Array.Empty<string>(),
            pages.Take(ContextCap).Select(p => $"{WorldBundle.ExistingPrefix}{p.Slug} ({p.Title})").ToList(),
            taken,
            CampaignContext(campaign),
            Lang);

        var rules = new BundleRules(recipe.Drafts, TypeSpellings(types, campaign.SystemId), pages.Select(p => p.Slug).ToList());
        return await Unusable(() => assistant.OutlineWorldAsync(request, rules, ct));
    }

    /// <summary>Writes one draft of <paramref name="bundle"/> in full: a wiki page's text, or a homebrew entry's description, tags and fields.</summary>
    public async Task ExpandAsync(CampaignDocument campaign, CampaignAccess access, WorldRecipe recipe, IReadOnlyDictionary<string, string?> answers, WorldBundle bundle, WorldDraft draft, CancellationToken ct = default)
    {
        DmOnly(access);
        var assistant = await providers.AssistantForAsync(access.UserId);
        var system = catalog.GetDefinition(campaign.SystemId);
        var related = bundle.Drafts.Where(d => d != draft && d.Selected)
            .Select(d => $"{d.Name} ({(d.Kind == DraftKinds.Homebrew ? d.Type : "wiki page")}): {d.Pitch}")
            .ToList();
        var pages = (await wiki.GetVisibleForCampaignAsync(access)).Take(ContextCap).Select(p => p.Title).ToList();

        if (draft.Kind == DraftKinds.Wiki)
        {
            var request = new WorldExpandRequest(draft.Name, draft.Pitch, null, catalog.GetDisplayName(campaign.SystemId), Array.Empty<FieldSpec>(),
                Answers(recipe, answers, system), related, pages, CampaignContext(campaign), Lang);
            await Unusable(async () =>
            {
                await assistant.ExpandPageAsync(draft, request, ct);
                return true;
            });
            return;
        }

        // The prompt names the type and its fields as system.yaml does, whatever language the DM reads.
        var type = system?.Canonical.FindType(draft.Type ?? string.Empty)
                   ?? new ContentTypeDefinition { Type = draft.Type ?? string.Empty, Label = CampaignLibrary.Humanize(draft.Type ?? "entry") };
        var fields = await FieldSpecsAsync(campaign, access, system, type);
        var homebrewRequest = new WorldExpandRequest(draft.Name, draft.Pitch, type.Label, catalog.GetDisplayName(campaign.SystemId), fields,
            Answers(recipe, answers, system), related, pages, CampaignContext(campaign), Lang);
        var culture = Lang;
        await Unusable(async () =>
        {
            await assistant.ExpandHomebrewAsync(draft, homebrewRequest, system == null ? null : (key, value) => system.CanonicalOption(culture, key, value), ct);
            return true;
        });
    }

    /// <summary>Saves the selected drafts: wiki pages first (DM-only), so their slugs are settled before links are rewritten,
    /// then homebrew entries (unpublished). A draft that was never written in full is saved with its pitch as its text.</summary>
    public async Task<WorldBuildResult> SaveAsync(CampaignDocument campaign, CampaignAccess access, WorldRecipe recipe, WorldBundle bundle)
    {
        DmOnly(access);
        var pages = new List<WikiPageDocument>();
        foreach (var draft in bundle.Drafts.Where(d => d.Selected && d.Kind == DraftKinds.Wiki))
        {
            var page = new WikiPageDocument
            {
                Title = draft.Name.Trim(),
                Body = WorldBundleParser.ResolveLinks(draft.Body ?? draft.Pitch, bundle),
                Visibility = WikiVisibility.DmOnly,
                Tags = new List<string> { recipe.Key },
                AuthorUserId = access.UserId
            };
            if (!await wiki.TrySaveAsync(page, access))
            {
                throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            }

            pages.Add(page);
        }

        var entries = new List<HomebrewEntryDocument>();
        var existing = (await homebrew.GetVisibleForCampaignAsync(access)).ToList();
        foreach (var draft in bundle.Drafts.Where(d => d.Selected && d.Kind == DraftKinds.Homebrew && !string.IsNullOrEmpty(d.Type)))
        {
            var entry = new HomebrewEntryDocument
            {
                CampaignId = campaign.Id,
                SystemId = campaign.SystemId,
                Type = draft.Type!,
                Name = draft.Name.Trim(),
                EntryId = UniqueEntryId(existing.Concat(entries), draft.Type!, draft.Name),
                Description = WorldBundleParser.ResolveLinks(draft.Body ?? draft.Pitch, bundle, keepLinks: false),
                Tags = new List<string>(draft.Tags),
                Properties = new Dictionary<string, object>(draft.Properties),
                IsPublished = false,
                AuthorUserId = access.UserId
            };
            if (!await homebrew.TrySaveAsync(entry, access))
            {
                throw new LocalizedException("WorldBuilder.Error.OnlyDm");
            }

            entries.Add(entry);
        }

        return new WorldBuildResult(entries, pages);
    }

    /// <summary>The types a homebrew draft may have, as the DM reads them: the system's declared types (and any extra ones the Library has).</summary>
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

    /// <summary>The fields the expand call fills: the type's editor fields, with the options a reference may pick and the
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

    /// <summary>The answers as the model reads them: the English question, then the option key, the type's English label, yes/no or the text.</summary>
    public static List<WorldAnswer> Answers(WorldRecipe recipe, IReadOnlyDictionary<string, string?> answers, SystemDefinition? system)
    {
        var result = new List<WorldAnswer>();
        foreach (var question in recipe.Questions)
        {
            var answer = answers.TryGetValue(question.Key, out var given) ? given?.Trim() : null;
            if (string.IsNullOrEmpty(answer))
            {
                if (question.Required) throw new LocalizedException("WorldBuilder.Error.Required");
                result.Add(new WorldAnswer(question.Ask, question.Surprise ? "surprise me" : "no preference"));
                continue;
            }

            result.Add(new WorldAnswer(question.Ask, question.Type switch
            {
                QuestionTypes.Toggle => answer == "true" ? "yes" : "no",
                QuestionTypes.ContentType => $"{system?.Canonical.FindType(answer)?.Label ?? CampaignLibrary.Humanize(answer)} (type id: {answer})",
                _ => answer
            }));
        }

        return result;
    }

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
        $"{campaign.Name}{(string.IsNullOrWhiteSpace(campaign.Description) ? "" : ": " + TextFormatting.Summary(campaign.Description, 300))}";

    private static void DmOnly(CampaignAccess access)
    {
        if (!access.IsDm) throw new LocalizedException("WorldBuilder.Error.OnlyDm");
    }

    // A reply that would not parse even after the retry is the model's fault, not the DM's: say so in their language.
    private static async Task<T> Unusable<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (FormatException)
        {
            throw new LocalizedException("WorldBuilder.Error.Unusable");
        }
    }
}
