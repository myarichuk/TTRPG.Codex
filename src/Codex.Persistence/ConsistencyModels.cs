using Codex.Core;
using Codex.Core.Consistency;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Raven.Client.Exceptions;

namespace Codex.Persistence;

/// <summary>An issue the DM set aside on purpose, and who and when: a red link that is meant to stay red, a page left a stub.</summary>
public class IssueDismissal
{
    /// <summary>The issue's id (<see cref="Issue.Id"/>): what the checker derives from the entity, the check and the field.</summary>
    public string IssueId { get; set; } = string.Empty;

    public string DismissedBy { get; set; } = string.Empty;
    public DateTime DismissedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Why, in the DM's words; optional.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// The issues of a campaign the DM dismissed (docs/WORLD_BUILDER.md, consistency checker). A document of its own, one per campaign: the
/// checker's results are derived from the campaign's documents and never stored, so the only thing it keeps is the DM's decisions.
/// An id whose problem has gone stays here, harmlessly, and applies again if the same problem comes back.
/// </summary>
public class ConsistencyDismissalsDocument
{
    public static string IdFor(string campaignId) => "ConsistencyDismissals/" + campaignId;

    public string Id { get; set; } = string.Empty;
    public string CampaignId { get; set; } = string.Empty;
    public List<IssueDismissal> Dismissed { get; set; } = new();

    /// <summary>The document as a campaign import may keep it: what the file holds, with the limits the repository applies to a dismissal made in the app. Ids that are
    /// empty or too long are dropped, an id given twice keeps its first entry, a note is cut. An id whose problem is gone is kept: it applies again if the same
    /// problem comes back, as in the app.</summary>
    public ConsistencyDismissalsDocument Sanitized(string campaignId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<IssueDismissal>();
        foreach (var d in Dismissed ?? new List<IssueDismissal>())
        {
            var id = (d?.IssueId ?? string.Empty).Trim();
            if (id.Length == 0 || id.Length > RavenConsistencyDismissalRepository.MaxIdLength || !seen.Add(id)) continue;
            var note = string.IsNullOrWhiteSpace(d!.Note) ? null : d.Note.Trim()[..Math.Min(d.Note.Trim().Length, RavenConsistencyDismissalRepository.MaxNoteLength)];
            kept.Add(new IssueDismissal { IssueId = id, DismissedBy = RavenConsistencyDismissalRepository.CutBy(d.DismissedBy), DismissedAt = d.DismissedAt, Note = note });
        }

        // A file may hold any number: the most recent ones (the list is in the order they were made) are kept, so the document stays what the app could have made.
        if (kept.Count > RavenConsistencyDismissalRepository.MaxEntries) kept = kept.GetRange(kept.Count - RavenConsistencyDismissalRepository.MaxEntries, RavenConsistencyDismissalRepository.MaxEntries);
        return new ConsistencyDismissalsDocument { Id = IdFor(campaignId), CampaignId = campaignId, Dismissed = kept };
    }
}

public interface IConsistencyDismissalRepository
{
    /// <summary>What the DM dismissed, in the order they did. Empty for anyone but the campaign's DM.</summary>
    Task<IReadOnlyList<IssueDismissal>> ListAsync(CampaignAccess access);

    /// <summary>Dismisses an issue. DM only; false when refused. Dismissing one already dismissed changes nothing.</summary>
    Task<bool> DismissAsync(CampaignAccess access, string issueId, string? note);

    /// <summary>Takes an issue off the dismissed list. DM only; false when refused or it was not dismissed.</summary>
    Task<bool> RestoreAsync(CampaignAccess access, string issueId);

    Task DeleteAllForCampaignAsync(string campaignId);

    /// <summary>The campaign's whole dismissals document, for the campaign export; null when the DM never dismissed anything. No access check: the
    /// caller (the export service) has already required the campaign's DM.</summary>
    Task<ConsistencyDismissalsDocument?> GetDocumentAsync(string campaignId);

    /// <summary>Writes the document over what is stored (a campaign import). The caller has already required the campaign's DM.</summary>
    Task SaveDocumentAsync(ConsistencyDismissalsDocument document);
}

public sealed class RavenConsistencyDismissalRepository(RavenDbService dbService) : IConsistencyDismissalRepository
{
    private const int MaxAttempts = 8;
    public const int MaxIdLength = 400;
    public const int MaxNoteLength = 500;

    /// <summary>The most dismissals one campaign keeps. A campaign has far fewer issues than this; the limit stops a crafted import, or a runaway, from making the
    /// document (read on every check) as large as the upload allows. A DM at the limit restores some first; an import keeps the most recent ones.</summary>
    public const int MaxEntries = 2000;

    /// <summary>The longest <see cref="IssueDismissal.DismissedBy"/> kept (a user id).</summary>
    public const int MaxDismissedByLength = 100;

    internal static string CutBy(string? by) => (by ?? string.Empty) is { Length: > MaxDismissedByLength } over ? over[..MaxDismissedByLength] : by ?? string.Empty;

    public async Task<IReadOnlyList<IssueDismissal>> ListAsync(CampaignAccess access)
    {
        if (!access.IsDm || string.IsNullOrEmpty(access.CampaignId)) return Array.Empty<IssueDismissal>();
        using var session = dbService.Store.OpenAsyncSession();
        var document = await session.LoadAsync<ConsistencyDismissalsDocument>(ConsistencyDismissalsDocument.IdFor(access.CampaignId));
        return document?.Dismissed.ToList() ?? new List<IssueDismissal>();
    }

    public async Task<bool> DismissAsync(CampaignAccess access, string issueId, string? note)
    {
        issueId = (issueId ?? string.Empty).Trim();
        if (issueId.Length == 0 || issueId.Length > MaxIdLength) return false;
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { IsDm: true, SystemMissing: false } current) return false;
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, MaxNoteLength)];

        return await ChangeAsync(current.CampaignId, dismissed =>
        {
            if (dismissed.Any(d => d.IssueId == issueId)) return false;
            // Refused in words, not as "no longer open": the DM has to restore some before setting more aside.
            if (dismissed.Count >= MaxEntries) throw new Codex.Localization.LocalizedException("Campaign.Issues.DismissalsFull", MaxEntries);
            dismissed.Add(new IssueDismissal { IssueId = issueId, DismissedBy = CutBy(current.UserId), DismissedAt = DateTime.UtcNow, Note = trimmed });
            return true;
        });
    }

    public async Task<bool> RestoreAsync(CampaignAccess access, string issueId)
    {
        if (await CampaignAccessResolver.NowAsync(dbService.Store, access) is not { IsDm: true, SystemMissing: false } current) return false;
        return await ChangeAsync(current.CampaignId, dismissed => dismissed.RemoveAll(d => d.IssueId == issueId) > 0);
    }

    // Load (or start) the document, change it, save under optimistic concurrency; again from a fresh load when another writer got in between.
    private async Task<bool> ChangeAsync(string campaignId, Func<List<IssueDismissal>, bool> change)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = dbService.Store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            var id = ConsistencyDismissalsDocument.IdFor(campaignId);
            var document = await session.LoadAsync<ConsistencyDismissalsDocument>(id);
            var isNew = document == null;
            document ??= new ConsistencyDismissalsDocument { Id = id, CampaignId = campaignId };
            if (!change(document.Dismissed)) return false;
            if (isNew) await session.StoreAsync(document, string.Empty, id);
            try
            {
                await session.SaveChangesAsync();
                return true;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
                // Saved by someone else between the load and the save (or started by them): go again.
            }
        }
    }

    public async Task DeleteAllForCampaignAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        session.Delete(ConsistencyDismissalsDocument.IdFor(campaignId));
        await session.SaveChangesAsync();
    }

    public async Task<ConsistencyDismissalsDocument?> GetDocumentAsync(string campaignId)
    {
        using var session = dbService.Store.OpenAsyncSession();
        return await session.LoadAsync<ConsistencyDismissalsDocument>(ConsistencyDismissalsDocument.IdFor(campaignId));
    }

    public async Task SaveDocumentAsync(ConsistencyDismissalsDocument document)
    {
        using var session = dbService.Store.OpenAsyncSession();
        // An upsert: what the file holds replaces what is stored, as every other document of an import does.
        await session.StoreAsync(document, ConsistencyDismissalsDocument.IdFor(document.CampaignId));
        await session.SaveChangesAsync();
    }
}

/// <summary>What the service knows about the campaign's system and packs that the documents do not say: whether the system is installed, the changes since
/// the campaign last played under it, what loading reported, and the packs the system has.</summary>
public sealed record SnapshotContext(bool SystemInstalled, IReadOnlyList<string> Changes, IReadOnlyList<string> LoadProblems, IReadOnlyList<string> KnownPacks)
{
    public static SnapshotContext Unknown { get; } = new(true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    /// <summary>The differences between two fingerprints, one line each, in the order <see cref="SystemFingerprint.ChangesSince"/> gives them.</summary>
    public static IReadOnlyList<string> Describe(IEnumerable<SystemChange> changes) => changes.Select(c => c.Kind switch
    {
        SystemChangeKind.Version => $"version {c.Before} to {c.After}",
        SystemChangeKind.Added => $"{c.File} added",
        SystemChangeKind.Removed => $"{c.File} removed",
        _ => $"{c.File} changed"
    }).ToList();
}

/// <summary>Reads the campaign's documents into the plain values the checks take (<see cref="WorldSnapshot"/>). Pure: no database, so it is
/// tested without one; the caller loads the documents with the DM's access.</summary>
public static class ConsistencySnapshots
{
    /// <param name="homebrew">The campaign's homebrew entries, or null when the scope asked for does not read them.</param>
    /// <param name="context">The system and packs as the loader knows them; null reads as an installed system with nothing to report.</param>
    public static WorldSnapshot From(
        IEnumerable<WikiPageDocument> pages,
        IEnumerable<ActorDocument> actors,
        IEnumerable<RegionDocument> regions,
        IEnumerable<FactDocument> facts,
        CampaignDocument campaign,
        SystemDefinition? system,
        IEntryResolver library,
        IEnumerable<HomebrewEntryDocument>? homebrew = null,
        SnapshotContext? context = null)
    {
        context ??= SnapshotContext.Unknown;
        return WorldSnapshot.Create(
            pages.Select(p => new PageView(p.Id, p.Slug, p.Title, p.Body ?? string.Empty, p.Visibility == WikiVisibility.Party, Blank(p.ActorId), Blank(p.PlaceId),
                p.Tags.ToList(), p.Kind.ToString().ToLowerInvariant())),
            actors.Where(a => !a.IsVault).Select(a => new ActorView(a.Id, a.Name, RoleOf(a), a.Retired, a.Biography, a.DmNotes, Blank(a.WikiPageId), a.Sheet ?? new Dictionary<string, object>(),
                Blank(a.OwnerUserId), Blank(a.BlueprintId),
                a.Hazard == null ? null : new HazardLinkView(a.Hazard.RegionId, a.Hazard.LocationId, a.Hazard.Entry, a.Hazard.IsHomebrew))),
            regions.SelectMany(r => r.Locations.Select(l => new PlaceView(l.Id, r.Id, l.Name, l.Description, Blank(l.WikiPageId), Blank(l.ParentId),
                Blank(l.Feature?.Hazard?.Entry), l.Feature?.Hazard?.IsHomebrew ?? false, Blank(l.Feature?.CreatureActorId)))),
            facts.Select(f => new FactView(f.Id, f.Summary, f.Details, f.RelatedEntityIds.ToList(), f.KnownBy.Select(k => k.EntityId).ToList())),
            system,
            library,
            SheetRuleEngine.EffectiveEnforcement(system, campaign.RulesEnforcement),
            new CampaignView(campaign.Id, campaign.OwnerId, campaign.SystemId, context.SystemInstalled, campaign.Members.Select(m => m.UserId).ToList(),
                campaign.ActivePacks.Concat(campaign.PacksOn).Concat(campaign.PacksOff).ToList(), context.KnownPacks, context.Changes, context.LoadProblems,
                campaign.Bestiary.Select(b => new BestiaryView(b.EntryType, b.EntryId, b.IsHomebrew, b.Name)).ToList()),
            homebrew?.Select(h => new HomebrewView(h.Id, h.Type, h.EntryId, h.Name, h.Tags.ToList(), h.Properties ?? new Dictionary<string, object>())),
            homebrewLoaded: homebrew != null);
    }

    // A creature taken from the bestiary, and a hazard that takes a turn, have no history to write and no kit to carry.
    public static ActorRole RoleOf(ActorDocument a) => a.Hazard != null ? ActorRole.Monster : a.Kind switch
    {
        ActorKind.PlayerCharacter => ActorRole.PlayerCharacter,
        ActorKind.NonPlayerCharacter => ActorRole.NonPlayerCharacter,
        _ => ActorRole.Monster
    };

    private static string? Blank(string? id) => string.IsNullOrWhiteSpace(id) ? null : id;
}
