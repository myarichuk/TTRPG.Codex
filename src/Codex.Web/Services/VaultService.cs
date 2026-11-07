using Codex.Core;
using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>Something a vault character uses that a campaign does not have, by name.</summary>
public sealed record MissingEntry(SheetReference Reference, string Name);

/// <summary>
/// The vault (docs/SERVER_ACCESS_AND_VAULT.md 8) and bringing its characters into campaigns (7): what is missing first, a
/// request the GM accepts, and updating the vault from the campaign's copy with only what the player owns.
/// </summary>
public sealed class VaultService(
    IVaultRepository vault,
    IActorRepository actors,
    ISystemCatalog catalog,
    CampaignLibrary library,
    MentionSources mentionSources,
    CampaignRuntimeManager runtime,
    ComponentRegistry components)
{
    /// <summary>What a vault character is built against: the server's packs for its system, no campaign's homebrew.</summary>
    public static CampaignDocument CampaignFor(string systemId) => new() { Id = string.Empty, SystemId = systemId };

    /// <summary>What <paramref name="character"/> uses that the campaign does not have, named as the vault's Library names it.
    /// Empty when the campaign's system is not the character's: that is refused before anything is listed.</summary>
    public async Task<IReadOnlyList<MissingEntry>> MissingAsync(ActorDocument character, CampaignDocument campaign, CampaignAccess access)
    {
        if (catalog.GetDefinition(campaign.SystemId) is not { } system || !SameSystem(character, campaign)) return Array.Empty<MissingEntry>();
        var inCampaign = await CachedEntryResolver.LoadAsync(library, campaign, access, system);
        var missing = SheetReferences.Missing(system, character.Sheet, inCampaign);
        if (missing.Count == 0) return Array.Empty<MissingEntry>();

        var inVault = await CachedEntryResolver.LoadAsync(library, CampaignFor(campaign.SystemId), CampaignAccess.Vault(access.UserId), system);
        return missing.Select(r => new MissingEntry(r, r.Types.Select(t => inVault.Resolve(t, r.Id)?.Name).FirstOrDefault(n => n != null) ?? r.Id)).ToList();
    }

    public static bool SameSystem(ActorDocument character, CampaignDocument campaign) =>
        string.Equals(character.SystemId, campaign.SystemId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks the GM to bring <paramref name="character"/> in, or (with <paramref name="replacesActorId"/>) to bring its
    /// vault version over the campaign's copy. <paramref name="leaveOut"/> holds the <see cref="SheetReference.Key"/>s of the
    /// missing entries the player chose to leave out; the rest are asked for.</summary>
    public async Task<CharacterRequestDocument?> AskAsync(ActorDocument character, CampaignDocument campaign, CampaignAccess access,
        IReadOnlySet<string> leaveOut, string? replacesActorId)
    {
        if (catalog.GetDefinition(campaign.SystemId) is not { } system || !SameSystem(character, campaign)) return null;
        var missing = await MissingAsync(character, campaign, access);
        var dropped = missing.Where(m => leaveOut.Contains(m.Reference.Key)).ToList();
        return await vault.AskAsync(access, new CharacterRequestDocument
        {
            VaultActorId = character.Id,
            ReplacesActorId = replacesActorId,
            Name = character.Name,
            Sheet = SheetReferences.Without(system, character.Sheet, dropped.Select(m => m.Reference)),
            Biography = character.Biography,
            LeftOut = dropped.Select(m => m.Name).ToList(),
            AskedFor = missing.Except(dropped).Select(m => m.Name).ToList()
        });
    }

    /// <summary>A GM accepts a request. A new character joins the cast; an update goes over the campaign's copy through the
    /// live table when there is one, keeping the history's secret blocks and the table's own state.</summary>
    public async Task<bool> AcceptAsync(string requestId, CampaignAccess access, SystemDefinition? system)
    {
        if (await vault.AcceptAsync(requestId, access) is not { } accepted) return false;
        if (accepted.Added is { } added)
        {
            ActorSheetSync.ApplySheetToState(added, system, components);
            await actors.UpdateAsync(added.Id, stored => stored.State = added.State);
            return true;
        }

        if (accepted.Request.ReplacesActorId is not { } copyId || await actors.GetVisibleAsync(copyId, access) is not { } copy) return false;
        var before = new Dictionary<string, object>(copy.Sheet);
        copy.Name = accepted.Request.Name;
        copy.Sheet = new Dictionary<string, object>(accepted.Request.Sheet);
        copy.Biography = SecretText.KeepSecrets(copy.Biography, accepted.Request.Biography);
        ActorSheetSync.ApplySheetToState(copy, system, components);
        return await runtime.TrySaveActorAsync(copy, before, access, system);
    }

    /// <summary>Updates the vault character the caller's campaign copy came from, with what of it is theirs: the sheet and history
    /// as a player reads them (no GM notes, no secret or revealed blocks), mentions turned into plain names.</summary>
    public async Task<bool> UpdateVaultAsync(string actorId, CampaignDocument campaign, CampaignAccess access)
    {
        var asPlayer = access.AsPlayer();
        if (await actors.GetVisibleAsync(actorId, asPlayer) is not { } copy || copy.OwnerUserId != access.UserId || copy.VaultSourceId is not { } sourceId)
        {
            return false;
        }

        var cast = await actors.GetVisibleForCampaignAsync(asPlayer);
        var mentions = await mentionSources.DirectoryAsync(campaign, asPlayer, cast);
        var sheet = copy.Sheet.ToDictionary(kv => kv.Key, kv => kv.Value is string text ? mentions.Plain(text) : kv.Value);
        var biography = copy.Biography == null ? null : mentions.Plain(copy.Biography);
        return await vault.UpdateFromCopyAsync(sourceId, access.UserId, copy.Name, sheet, biography);
    }
}
