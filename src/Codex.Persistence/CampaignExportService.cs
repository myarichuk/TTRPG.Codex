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
    IWikiRepository? wiki = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<CampaignBundle?> ExportAsync(CampaignAccess access)
    {
        if (!access.IsDm)
        {
            return null;
        }

        var campaign = await campaigns.GetAsync(access.CampaignId);
        if (campaign == null)
        {
            return null;
        }

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
            Wiki = wiki == null ? new List<WikiPageDocument>() : (await wiki.GetAllForCampaignAsync(access.CampaignId)).ToList()
        };
    }

    public async Task<bool> ImportAsync(CampaignBundle? bundle, string requestingUserId)
    {
        if (bundle?.Campaign == null || bundle.Format != CampaignBundle.CurrentFormat)
        {
            return false;
        }

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
            NormalizeState(actor);
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
            await facts.SaveAsync(fact, dmAccess);
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
            entry.Properties = NormalizeMap(entry.Properties);
            entry.HasArt = false;
            await homebrew.SaveAsync(entry);
        }

        if (wiki != null)
        {
            foreach (var page in bundle.Wiki.Where(p => p.CampaignId == campaign.Id))
            {
                await wiki.SaveAsync(page);
            }
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

            foreach (var actor in bundle.Actors)
            {
                NormalizeState(actor);
            }

            foreach (var entry in bundle.Homebrew)
            {
                entry.Properties = NormalizeMap(entry.Properties);
            }

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
    /// plain CLR values (string, long, double, bool, lists, nested dictionaries) on the way in.
    /// </summary>
    private static void NormalizeState(ActorDocument actor)
    {
        actor.State = NormalizeMap(actor.State);
        actor.Sheet = NormalizeMap(actor.Sheet);
    }

    private static Dictionary<string, object> NormalizeMap(Dictionary<string, object> map)
    {
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
