using Codex.Core;
using Codex.Localization;
using Raven.Client.Exceptions;

namespace Codex.Persistence;

/// <summary>One of the DM's text templates (see <see cref="LiveTemplate"/>). A built-in one's <see cref="Id"/> starts with
/// <see cref="BuiltInTemplates.Prefix"/>; stored with that id, it is the DM's own version of the built-in one.</summary>
public class TextTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Typed before Tab, it puts the template in. Empty: only the <c>/</c> menu and a new page offer it.</summary>
    public string Abbreviation { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>The kind of wiki page this is the outline of: a new page of that kind can start from it, a page of that kind
    /// can take the sections it lacks, and the AI writes such pages to it. Null for a snippet.</summary>
    public WikiPageKind? PageKind { get; set; }

    /// <summary>A built-in template the DM turned off. Only meaningful on a built-in one's id.</summary>
    public bool Hidden { get; set; }

    public bool IsBuiltIn => Id.StartsWith(BuiltInTemplates.Prefix, StringComparison.Ordinal);

    public TextTemplate Copy() => (TextTemplate)MemberwiseClone();
}

/// <summary>A campaign's templates: the DM's own and their versions of the built-in ones. One document per campaign.</summary>
public class CampaignTemplatesDocument
{
    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public List<TextTemplate> Templates { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static string IdFor(string campaignId) => $"CampaignTemplates/{campaignId}";
}

public enum TemplateSave
{
    Done,

    /// <summary>Not the DM's, or the template is not well formed (no name, a bad abbreviation, too long).</summary>
    Refused,

    /// <summary>Another template the campaign uses already has the abbreviation.</summary>
    Taken
}

public interface ITemplateRepository
{
    /// <summary>The templates the DM works with: the built-in ones in <paramref name="l"/>'s language, each replaced by the DM's
    /// version where there is one, then the DM's own; turned-off built-in ones are included, marked <see cref="TextTemplate.Hidden"/>.
    /// Empty for anyone but the DM: a template may hold the DM's secrets.</summary>
    Task<IReadOnlyList<TextTemplate>> GetAsync(CampaignAccess access, Localizer l);

    /// <summary>Saves a template (a new one when its id is empty, which is then assigned). DM only.</summary>
    Task<TemplateSave> TrySaveAsync(TextTemplate template, CampaignAccess access, Localizer l);

    /// <summary>Deletes one of the DM's templates, or puts a built-in one back as it ships (<see cref="TemplateSave.Taken"/> when
    /// that would bring back an abbreviation another template now uses). DM only.</summary>
    Task<TemplateSave> TryRemoveAsync(string id, CampaignAccess access, Localizer l);

    /// <summary>Export only.</summary>
    Task<CampaignTemplatesDocument?> GetDocumentAsync(string campaignId);

    /// <summary>Import only.</summary>
    Task SaveDocumentAsync(CampaignTemplatesDocument document);

    Task DeleteAllForCampaignAsync(string campaignId);
}

/// <summary>The templates every campaign starts with, in the reader's language: an outline for each kind of wiki page and a few
/// snippets. Their text lives in the Template area of the resources (<c>Template.{key}.Name</c>, <c>.Body</c>).</summary>
public static class BuiltInTemplates
{
    public const string Prefix = "builtin:";

    private static readonly (string Key, string Abbreviation, WikiPageKind? Kind)[] All =
    {
        ("Person", "", WikiPageKind.Person),
        ("Place", "", WikiPageKind.Place),
        ("Faction", "", WikiPageKind.Faction),
        ("Quest", "", WikiPageKind.Quest),
        ("Npc", "npc", null),
        ("Encounter", "enc", null),
        ("Secret", "dmb", null),
        ("Truth", "dmp", null)
    };

    public static IReadOnlyList<TextTemplate> For(Localizer l) =>
        All.Select(t => new TextTemplate
        {
            Id = Prefix + t.Key.ToLowerInvariant(),
            Name = l[$"Template.{t.Key}.Name"],
            Abbreviation = t.Abbreviation,
            Body = l[$"Template.{t.Key}.Body"],
            PageKind = t.Kind
        }).ToList();

    /// <summary>The built-in templates with the DM's versions put in place, then the DM's own.</summary>
    public static IReadOnlyList<TextTemplate> Merge(IReadOnlyList<TextTemplate> builtIn, IEnumerable<TextTemplate> stored)
    {
        var mine = stored.ToList();
        var byId = mine.Where(t => t.IsBuiltIn).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.Last());
        return builtIn.Select(b => byId.TryGetValue(b.Id, out var changed) ? changed.Copy() : b.Copy())
            .Concat(mine.Where(t => !t.IsBuiltIn).Select(t => t.Copy()))
            .ToList();
    }
}

/// <summary>What the editor and the wiki ask of a campaign's templates.</summary>
public static class TemplateSet
{
    /// <summary>The templates in use (turned-off built-in ones left out).</summary>
    public static IEnumerable<TextTemplate> InUse(IEnumerable<TextTemplate> templates) => templates.Where(t => !t.Hidden);

    /// <summary>The outline of pages of <paramref name="kind"/>: the DM's own first, then their version of the built-in one.</summary>
    public static TextTemplate? ForKind(IEnumerable<TextTemplate> templates, WikiPageKind kind) =>
        InUse(templates).Where(t => t.PageKind == kind).OrderBy(t => t.IsBuiltIn).FirstOrDefault();
}

public class RavenTemplateRepository(RavenDbService dbService) : ITemplateRepository
{
    public async Task<IReadOnlyList<TextTemplate>> GetAsync(CampaignAccess access, Localizer l)
    {
        if (!access.IsDm)
        {
            return Array.Empty<TextTemplate>();
        }

        var document = await GetDocumentAsync(access.CampaignId);
        return BuiltInTemplates.Merge(BuiltInTemplates.For(l), document?.Templates ?? new List<TextTemplate>());
    }

    public async Task<TemplateSave> TrySaveAsync(TextTemplate template, CampaignAccess access, Localizer l)
    {
        var name = template.Name.Trim();
        var abbreviation = template.Abbreviation.Trim();
        if (!access.IsDm || name.Length == 0 || name.Length > LiveTemplate.MaxName || template.Body.Length > LiveTemplate.MaxBody
            || !LiveTemplate.IsValidAbbreviation(abbreviation))
        {
            return TemplateSave.Refused;
        }

        var builtIn = BuiltInTemplates.For(l);
        if (template.IsBuiltIn && builtIn.All(b => b.Id != template.Id))
        {
            return TemplateSave.Refused;
        }

        var saved = new TextTemplate
        {
            Id = string.IsNullOrEmpty(template.Id) ? Guid.NewGuid().ToString("N") : template.Id,
            Name = name,
            Abbreviation = abbreviation,
            Body = template.Body.Replace("\r\n", "\n"),
            PageKind = template.PageKind,
            Hidden = template.IsBuiltIn && template.Hidden
        };

        var result = TemplateSave.Refused;
        void Apply(CampaignTemplatesDocument document)
        {
            // Against the templates as stored now, so two tabs saving at once cannot both take one abbreviation.
            var others = BuiltInTemplates.Merge(builtIn, document.Templates).Where(t => t.Id != saved.Id && !t.Hidden);
            if (abbreviation.Length > 0 && others.Any(t => string.Equals(t.Abbreviation, abbreviation, StringComparison.OrdinalIgnoreCase)))
            {
                result = TemplateSave.Taken;
                return;
            }

            document.Templates.RemoveAll(t => t.Id == saved.Id);
            document.Templates.Add(saved);
            document.UpdatedAt = DateTime.UtcNow;
            result = TemplateSave.Done;
        }

        var id = CampaignTemplatesDocument.IdFor(access.CampaignId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await RavenUpdate.RunAsync<CampaignTemplatesDocument>(dbService, id, Apply) != null)
            {
                if (result == TemplateSave.Done) template.Id = saved.Id;
                return result;
            }

            // The campaign's first template: create the document, unless another save just did.
            var document = new CampaignTemplatesDocument { Id = id, CampaignId = access.CampaignId };
            Apply(document);
            if (result != TemplateSave.Done)
            {
                return result;
            }

            using var session = dbService.Store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            // An empty change vector: stored only if no document has that id yet.
            await session.StoreAsync(document, string.Empty, id);
            try
            {
                await session.SaveChangesAsync();
                template.Id = saved.Id;
                return TemplateSave.Done;
            }
            catch (ConcurrencyException)
            {
            }
        }

        return TemplateSave.Refused;
    }

    public async Task<TemplateSave> TryRemoveAsync(string id, CampaignAccess access, Localizer l)
    {
        if (!access.IsDm)
        {
            return TemplateSave.Refused;
        }

        var shipped = BuiltInTemplates.For(l).FirstOrDefault(t => t.Id == id);
        var result = TemplateSave.Refused;
        await RavenUpdate.RunAsync<CampaignTemplatesDocument>(dbService, CampaignTemplatesDocument.IdFor(access.CampaignId), document =>
        {
            result = TemplateSave.Refused;
            if (shipped is { Abbreviation.Length: > 0 }
                && document.Templates.Any(t => t.Id != id && !t.Hidden && string.Equals(t.Abbreviation, shipped.Abbreviation, StringComparison.OrdinalIgnoreCase)))
            {
                result = TemplateSave.Taken;
                return;
            }

            if (document.Templates.RemoveAll(t => t.Id == id) > 0)
            {
                document.UpdatedAt = DateTime.UtcNow;
                result = TemplateSave.Done;
            }
        });
        return result;
    }

    public async Task<CampaignTemplatesDocument?> GetDocumentAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<CampaignTemplatesDocument>(CampaignTemplatesDocument.IdFor(campaignId));
    }

    public async Task SaveDocumentAsync(CampaignTemplatesDocument document)
    {
        document.Id = CampaignTemplatesDocument.IdFor(document.CampaignId);
        using var session = dbService.Store.OpenAsyncSession();
        await session.StoreAsync(document, document.Id);
        await session.SaveChangesAsync();
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        session.Delete(CampaignTemplatesDocument.IdFor(campaignId));
        await session.SaveChangesAsync();
    }
}
