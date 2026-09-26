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
}

/// <summary>
/// Campaign export/import (4.4). Export is DM-only and returns null for anyone else - a bundle
/// contains DM secrets by design. Import restores every document under its stored id (upsert),
/// so it doubles as the restore path for a deleted campaign; the caller must be the campaign's
/// owner or a DM member recorded in the bundle itself, and every document must belong to the
/// bundled campaign, so a crafted file can't write into another campaign.
/// </summary>
public class CampaignExportService(
    ICampaignRepository campaigns,
    IActorRepository actors,
    ISessionRepository sessions,
    IEncounterRepository encounters,
    IFactRepository facts,
    INoteRepository notes,
    IRegionRepository regions)
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
            Regions = (await regions.GetVisibleForCampaignAsync(access)).ToList()
        };
    }

    public async Task<bool> ImportAsync(CampaignBundle? bundle, string requestingUserId)
    {
        if (bundle?.Campaign == null || bundle.Format != CampaignBundle.CurrentFormat)
        {
            return false;
        }

        var campaign = bundle.Campaign;
        var allowed = campaign.OwnerId == requestingUserId
            || campaign.Members.Any(m => m.UserId == requestingUserId && m.Role == CampaignRole.DM);
        if (!allowed)
        {
            return false;
        }

        // The bundle is trusted about its own membership (it is the restore path for a deleted
        // campaign, where there is nothing to resolve against), so mint the DM access the
        // guarded saves require from the verified owner/member check above.
        var dmAccess = new CampaignAccess(campaign.Id, requestingUserId, CampaignRole.DM);

        await campaigns.SaveAsync(campaign);
        foreach (var actor in bundle.Actors.Where(a => a.CampaignId == campaign.Id))
        {
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
            await regions.SaveAsync(region, dmAccess);
        }

        return true;
    }

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
        foreach (var key in actor.State.Keys.ToList())
        {
            if (actor.State[key] is JsonElement element)
            {
                actor.State[key] = ToClr(element);
            }
        }
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
