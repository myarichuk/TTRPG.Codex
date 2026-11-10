using System.Text.Json;

namespace Codex.Persistence;

/// <summary>
/// A whole campaign as one versioned JSON document (4.4): every aggregate scoped to the
/// campaign, so export-then-import restores the table exactly. <see cref="Format"/> is the
/// compatibility gate - import rejects anything it doesn't recognize by name.
/// </summary>
public class CampaignBundle
{
    public const string CurrentFormat = "codex-campaign/1";

    public string Format { get; set; } = CurrentFormat;
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public CampaignDocument? Campaign { get; set; }
    public List<ActorDocument> Actors { get; set; } = new();
    public List<SessionDocument> Sessions { get; set; } = new();
    public List<EncounterDocument> Encounters { get; set; } = new();
    public List<FactDocument> Facts { get; set; } = new();
    public List<NoteDocument> Notes { get; set; } = new();
    public List<RegionDocument> Regions { get; set; } = new();
    public List<HomebrewEntryDocument> Homebrew { get; set; } = new();

    /// <summary>World wiki pages. Player journals are deliberately absent: they are private to each player.</summary>
    public List<WikiPageDocument> Wiki { get; set; } = new();

    /// <summary>The DM's templates and their versions of the built-in ones; null in bundles from before there were any.</summary>
    public CampaignTemplatesDocument? Templates { get; set; }

    /// <summary>The campaign's clocks and tables; null in bundles from before there were any.</summary>
    public CampaignObjectsDocument? Objects { get; set; }
}

/// <summary>
/// Campaign export/import (4.4). Export is DM-only and returns null for anyone else - a bundle
/// contains DM secrets by design. Import restores every document under its stored id (upsert),
/// so it doubles as the restore path for a deleted campaign; the caller must be the campaign's
/// owner or a DM member of the stored campaign (the bundle's own membership counts only when no
/// such campaign exists), and every document must belong to the bundled campaign.
/// </summary>
public class CampaignExportService(
    ICampaignRepository campaigns,
    IActorRepository actors,
    ISessionRepository sessions,
    IEncounterRepository encounters,
    IFactRepository facts,
    INoteRepository notes,
    IRegionRepository regions,
    IHomebrewRepository homebrew,
    IWikiRepository? wiki = null,
    ITemplateRepository? templates = null,
    ICampaignObjectsRepository? objects = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<CampaignBundle?> ExportAsync(CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return null;
        }

        // The role as the campaign stores it now, not as the page that asked loaded it: a GM made a player since gets nothing (D15).
        var campaign = await campaigns.GetAsync(access.CampaignId);
        if (campaign == null || CampaignAccessResolver.RoleOf(campaign, access.UserId) != CampaignRole.DM)
        {
            return null;
        }

        // Whoever holds the file must not be able to join the table with it (D13).
        campaign.InviteCode = string.Empty;
        campaign.InviteExpiresAt = null;
        campaign.InviteSingleUse = false;
        campaign.InviteUsedBy = null;

        return new CampaignBundle
        {
            ExportedAt = DateTime.UtcNow,
            Campaign = campaign,
            Actors = (await actors.GetVisibleForCampaignAsync(access)).ToList(),
            Sessions = (await sessions.GetAllForCampaignAsync(access.CampaignId)).ToList(),
            Encounters = (await encounters.GetAllForCampaignAsync(access.CampaignId)).ToList(),
            Facts = (await facts.GetVisibleForCampaignAsync(access, new HashSet<string>())).ToList(),
            Notes = (await notes.GetAllForCampaignAsync(access.CampaignId)).ToList(),
            Regions = (await regions.GetVisibleForCampaignAsync(access)).ToList(),
            Homebrew = (await homebrew.GetAllForCampaignAsync(access.CampaignId)).ToList(),
            Wiki = wiki == null ? new List<WikiPageDocument>() : (await wiki.GetAllForCampaignAsync(access.CampaignId)).ToList(),
            Templates = templates == null ? null : await templates.GetDocumentAsync(access.CampaignId),
            Objects = objects == null ? null : await objects.GetDocumentAsync(access.CampaignId)
        };
    }

    /// <summary>The bundle of any campaign, as its owner would export it: for the server's own backups (D11) only.</summary>
    internal Task<CampaignBundle?> ExportForBackupAsync(CampaignDocument campaign) =>
        ExportAsync(new CampaignAccess(campaign.Id, campaign.OwnerId, CampaignRole.DM));

    /// <summary>
    /// Restores the bundle as a campaign of its own (D13): every document gets a new id and every reference to one (ids in
    /// other documents, mentions in text) follows, so it can never land on a campaign that exists, whatever the file holds.
    /// The caller must be a DM in the file and becomes the new campaign's owner; the invite link is new. Returns the new
    /// campaign's id, or null when refused.
    /// </summary>
    public async Task<string?> ImportAsNewAsync(CampaignBundle? bundle, string requestingUserId)
    {
        if (bundle?.Campaign == null || bundle.Format != CampaignBundle.CurrentFormat || !IsDm(bundle.Campaign, requestingUserId))
        {
            return null;
        }

        var copy = Deserialize(WithNewIds(Serialize(bundle), IdsIn(bundle)));
        if (copy?.Campaign is not { } campaign)
        {
            return null;
        }

        campaign.OwnerId = requestingUserId;
        campaign.Members.RemoveAll(m => m.UserId == requestingUserId);
        campaign.InviteCode = string.Empty;
        campaign.InviteExpiresAt = null;
        campaign.InviteSingleUse = false;
        campaign.InviteUsedBy = null;
        return await ImportAsync(copy, requestingUserId) ? campaign.Id : null;
    }

    private static IEnumerable<string> IdsIn(CampaignBundle bundle) =>
        new[] { bundle.Campaign!.Id }
            .Concat(bundle.Actors.Select(d => d.Id))
            .Concat(bundle.Sessions.Select(d => d.Id))
            .Concat(bundle.Encounters.Select(d => d.Id))
            .Concat(bundle.Facts.Select(d => d.Id))
            .Concat(bundle.Notes.Select(d => d.Id))
            .Concat(bundle.Regions.Select(d => d.Id))
            .Concat(bundle.Homebrew.Select(d => d.Id))
            .Concat(bundle.Wiki.Select(d => d.Id))
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal);

    /// <summary>Replaces each id wherever it appears whole in <paramref name="json"/>: as a value, inside another id
    /// (<c>CampaignTemplates/{campaignId}</c>), in a target (<c>session:{id}</c>) or a mention (<c>[[actor:{id}|Name]]</c>).
    /// "Whole" means not inside a longer run of id characters, so <c>Notes/1-A</c> does not touch <c>Notes/11-A</c>.</summary>
    internal static string WithNewIds(string json, IEnumerable<string> ids)
    {
        var map = ids.ToDictionary(id => id, _ => Guid.NewGuid().ToString(), StringComparer.Ordinal);
        if (map.Count == 0)
        {
            return json;
        }

        var pattern = @"(?<![A-Za-z0-9_-])(?:" + string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(System.Text.RegularExpressions.Regex.Escape)) + @")(?![A-Za-z0-9_-])";
        return System.Text.RegularExpressions.Regex.Replace(json, pattern, m => map[m.Value]);
    }

    /// <summary>
    /// Merges the bundle into the campaign it came from (or, when that campaign no longer exists, restores it under its own
    /// ids): every document in the file is written over the stored one, and documents the file does not have stay.
    /// </summary>
    public async Task<bool> ImportAsync(CampaignBundle? bundle, string requestingUserId)
    {
        if (bundle?.Campaign == null || bundle.Format != CampaignBundle.CurrentFormat)
        {
            return false;
        }

        NormalizeMaps(bundle);
        var campaign = bundle.Campaign;

        // Authorise against what is stored, never against what the file claims: otherwise a DM of
        // any campaign could craft a bundle naming someone else's campaign id and make themselves
        // its owner. Only when no such campaign exists (restoring a deleted one) is there nothing
        // to resolve against, and then the bundle's own owner/DM list is the best evidence there is.
        var stored = await campaigns.GetAsync(campaign.Id);
        if (stored != null)
        {
            if (!IsDm(stored, requestingUserId))
            {
                return false;
            }

            // Content comes from the file; who may touch the campaign, and the live pointers the
            // runtime and invite flow own, stay as stored.
            campaign.OwnerId = stored.OwnerId;
            campaign.Members = stored.Members;
            campaign.InviteCode = stored.InviteCode;
            campaign.CurrentSessionId = stored.CurrentSessionId;
        }
        else if (!IsDm(campaign, requestingUserId))
        {
            return false;
        }

        // Mint the DM access the guarded saves require from the verified check above.
        var dmAccess = new CampaignAccess(campaign.Id, requestingUserId, CampaignRole.DM);

        // Images are attachments and don't travel in the bundle; flags claiming them would only show broken images.
        campaign.HasCover = false;
        campaign.PackArt.Clear();
        await campaigns.SaveAsync(campaign);
        foreach (var actor in bundle.Actors.Where(a => a.CampaignId == campaign.Id))
        {
            actor.HasPortrait = false;
            await actors.SaveAsync(actor);
        }

        foreach (var session in bundle.Sessions.Where(s => s.CampaignId == campaign.Id))
        {
            await sessions.SaveAsync(session);
        }

        foreach (var encounter in bundle.Encounters.Where(e => e.CampaignId == campaign.Id))
        {
            await encounters.SaveAsync(encounter);
        }

        foreach (var fact in bundle.Facts.Where(f => f.CampaignId == campaign.Id))
        {
            await facts.RestoreAsync(fact);
        }

        foreach (var note in bundle.Notes.Where(n => n.CampaignId == campaign.Id))
        {
            await notes.CreateNoteAsync(note);
        }

        foreach (var region in bundle.Regions.Where(r => r.CampaignId == campaign.Id))
        {
            region.Locations.ForEach(l => l.HasArt = false);
            await regions.SaveAsync(region, dmAccess);
        }

        foreach (var entry in bundle.Homebrew.Where(h => h.CampaignId == campaign.Id))
        {
            entry.HasArt = false;
            await homebrew.SaveAsync(entry);
        }

        if (wiki != null)
        {
            foreach (var page in bundle.Wiki.Where(p => p.CampaignId == campaign.Id))
            {
                page.HasArt = false;
                await wiki.SaveAsync(page);
            }
        }

        if (templates != null && bundle.Templates is { } bundled && bundled.CampaignId == campaign.Id)
        {
            await templates.SaveDocumentAsync(bundled);
        }

        if (objects != null && bundle.Objects is { } kept && kept.CampaignId == campaign.Id)
        {
            await objects.SaveDocumentAsync(kept);
        }

        return true;
    }

    private static bool IsDm(CampaignDocument campaign, string userId) =>
        campaign.OwnerId == userId
        || campaign.Members.Any(m => m.UserId == userId && m.Role == CampaignRole.DM);

    public static string Serialize(CampaignBundle bundle) =>
        JsonSerializer.Serialize(bundle, JsonOptions);

    public static CampaignBundle? Deserialize(string json)
    {
        try
        {
            var bundle = JsonSerializer.Deserialize<CampaignBundle>(json, JsonOptions);
            if (bundle == null)
            {
                return null;
            }

            NormalizeMaps(bundle);
            return bundle;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// System.Text.Json deserializes <c>Dictionary&lt;string, object&gt;</c> values as
    /// <see cref="JsonElement"/>; RavenDB would persist those as garbage, so convert back to
    /// plain CLR values (string, long, double, bool, lists, nested dictionaries) on the way in. Every
    /// such map in a bundle, not only the sheets (D13): a pack place's <c>Metadata["blueprintId"]</c>
    /// stored as garbage never matched its blueprint again.
    /// </summary>
    internal static void NormalizeMaps(CampaignBundle bundle)
    {
        if (bundle.Campaign != null)
        {
            bundle.Campaign.WorldState = NormalizeMap(bundle.Campaign.WorldState);
        }

        foreach (var actor in bundle.Actors)
        {
            actor.State = NormalizeMap(actor.State);
            actor.Sheet = NormalizeMap(actor.Sheet);
        }

        foreach (var encounter in bundle.Encounters)
        {
            encounter.ActiveEffects = NormalizeMap(encounter.ActiveEffects);
        }

        foreach (var fact in bundle.Facts)
        {
            fact.Metadata = NormalizeMap(fact.Metadata);
        }

        foreach (var location in bundle.Regions.SelectMany(r => r.Locations))
        {
            location.Metadata = NormalizeMap(location.Metadata);
        }

        foreach (var entry in bundle.Homebrew)
        {
            entry.Properties = NormalizeMap(entry.Properties);
        }
    }

    internal static Dictionary<string, object> NormalizeMap(Dictionary<string, object>? map)
    {
        map ??= new Dictionary<string, object>();
        foreach (var key in map.Keys.ToList())
        {
            if (map[key] is JsonElement element)
            {
                map[key] = ToClr(element);
            }
        }

        return map;
    }

    private static object ToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        // NB: the (object) cast pins the ternary's type to object - without it, long and
        // double unify to double and every whole number comes back as e.g. 10.0.
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(ToClr).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToClr(p.Value)),
        _ => ""
    };
}
