namespace Codex.Persistence;

/// <summary>
/// Prepared encounters (ENCOUNTER_BUILDER_DESIGN.md 3): GM prep, kept apart from <see cref="IEncounterRepository"/> on purpose, because that one is read by the
/// campaign export, the book and the table's "restore the open fight". Every call that takes a <see cref="CampaignAccess"/> refuses anyone who is not a GM of the
/// campaign as it is stored now (the owner and co-GMs alike; a party preview is a player's access), checks that the document is the access's own campaign's,
/// and, for a write, refuses an access whose game system is missing. The repository only stores: the counts and places on a plan are checked by the service.
/// </summary>
public interface IPreparedEncounterRepository
{
    /// <summary>One plan of the access's campaign, or null when there is none, it is another campaign's, or the caller is not a GM.</summary>
    Task<PreparedEncounterDocument?> GetAsync(string id, CampaignAccess access);

    /// <summary>Every plan of the access's campaign, the most recently changed first. Empty for anyone but a GM of it.</summary>
    Task<IReadOnlyList<PreparedEncounterDocument>> ListAsync(CampaignAccess access);

    /// <summary>
    /// Saves a plan; false when refused (not a GM, a party preview, the system missing, a plan of another campaign, or an id that is not stored). A plan with no
    /// id is new: it gets <c>preparedencounters/&lt;guid&gt;</c> and the access's user as creator. Every save stamps <c>UpdatedAt</c>. A saved plan keeps its stored
    /// run list and creator whatever the copy carries, and only <see cref="MarkRunAsync"/> sets <see cref="PreparedStatus.Ran"/>: a plan with stored runs stays
    /// Ran whatever the copy says, one with none is never Ran (a copy saying Ran becomes Ready). So an editor needs no run list, and an edit made on a stale
    /// copy never forgets a fight. On success the passed document holds what was stored.
    /// </summary>
    Task<bool> SaveAsync(PreparedEncounterDocument document, CampaignAccess access);

    /// <summary>Deletes a plan; false when refused or not found.</summary>
    Task<bool> DeleteAsync(string id, CampaignAccess access);

    Task DeleteAllForCampaignAsync(string campaignId);

    /// <summary>
    /// Records that the plan was started as the live encounter <paramref name="encounterId"/>: appends it to the run list (once) and sets <see cref="PreparedStatus.Ran"/>,
    /// changing nothing else but <c>UpdatedAt</c>, in one load-and-save under optimistic concurrency, so a GM editing the plan meanwhile is not overwritten.
    /// A write like the others: false when refused (not a GM, a party preview, the system missing) or when there is no such plan in the access's campaign.
    /// </summary>
    Task<bool> MarkRunAsync(string id, string encounterId, CampaignAccess access);

    /// <summary>Every plan of a campaign, for the campaign export. No access check: the export service has already required the campaign's GM.</summary>
    Task<IReadOnlyList<PreparedEncounterDocument>> GetAllForCampaignAsync(string campaignId);

    /// <summary>Writes a plan from a campaign import over what is stored under its id. No access check: the import has already required the campaign's GM. Writes nothing for a plan whose id is stored as
    /// anything but this campaign's plan, or is not stored and is not a plan id (the prefix, or a bare GUID).</summary>
    Task RestoreAsync(PreparedEncounterDocument document);
}
