using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;

namespace Codex.Web.Services;

/// <summary>Something a vault character uses that a campaign does not have, by name.</summary>
/// <param name="Carried">The entry itself, when the character carries it (<see cref="CarriedEntry"/>): the GM can then add it.</param>
public sealed record MissingEntry(SheetReference Reference, string Name, CarriedEntry? Carried = null);

/// <summary>What a GM does with an entry a character brings.</summary>
public enum BringKind
{
    /// <summary>Add it to the campaign's Library.</summary>
    Add,
    /// <summary>Point the sheet at an entry the campaign already has (<see cref="BringChoice.UseId"/>).</summary>
    UseOther,
    /// <summary>Take it off the sheet.</summary>
    LeaveOut
}

public sealed record BringChoice(string Type, string EntryId, BringKind Kind, string? UseId = null);

/// <summary>A change the GM made to one of the character's picks in the review: <paramref name="Key"/> is a choice's sheet key or a
/// pick list's key, <paramref name="Picks"/> what it holds now, <paramref name="Label"/> what to call it and
/// <paramref name="Summary"/> the picks by name, for the note the player reads.</summary>
public sealed record ReviewEdit(string Key, string Label, IReadOnlyList<string> Picks, string Summary, string Was = "");

/// <summary>A request's sheet after the GM's review, the carried entries to add to the Library, and what to tell the player.</summary>
public sealed record ReviewResult(Dictionary<string, object> Sheet, List<CarriedEntry> Add, List<JoinNote> Notes);

/// <summary>
/// The vault (docs/SERVER_ACCESS_AND_VAULT.md 8) and bringing its characters into campaigns (7): what is missing first, a
/// request the GM accepts, and updating the vault from the campaign's copy with only what the player owns.
/// </summary>
public sealed class VaultService(
    IVaultRepository vault,
    IActorRepository actors,
    ISystemCatalog catalog,
    CampaignLibrary library,
    PlayedCharacters played,
    CampaignRuntimeManager runtime,
    ComponentRegistry components,
    CastService cast)
{
    /// <summary>A new character in <paramref name="userId"/>'s vault on the system's starting sheet (the default attribute method),
    /// or from an AI <paramref name="draft"/> made against the server's packs (<see cref="CampaignFor"/>) with its history.</summary>
    public Task<ActorDocument> CreateAsync(string userId, string systemId, string name, CharacterDraft? draft = null)
    {
        var system = catalog.GetDefinition(systemId);
        return vault.CreateAsync(userId, systemId, draft?.Name ?? name, actor =>
        {
            cast.NewSheet(actor, systemId, system, draft);
            if (draft != null)
            {
                actor.Biography = draft.Biography;
                actor.Carried = draft.Proposed.Select(CarriedEntry.From).ToList();
            }
            ActorSheetSync.ApplySheetToState(actor, system, components);
        });
    }

    /// <summary>What a vault character is built against: the server's packs for its system, no campaign's homebrew.</summary>
    public static CampaignDocument CampaignFor(string systemId) => new() { Id = string.Empty, SystemId = systemId };

    /// <summary>What <paramref name="character"/> uses that the campaign does not have, named as the vault's Library names it.
    /// Empty when the campaign's system is not the character's: that is refused before anything is listed.</summary>
    public async Task<IReadOnlyList<MissingEntry>> MissingAsync(ActorDocument character, CampaignDocument campaign, CampaignAccess access)
    {
        if (catalog.GetDefinition(campaign.SystemId) is not { } system || !SameSystem(character, campaign)) return Array.Empty<MissingEntry>();
        var inCampaign = await CachedEntryResolver.LoadAsync(library, campaign, access, system);
        var inVault = await ResolverAsync(character, system);
        var missing = SheetReferences.Missing(system, character.Sheet, inCampaign, inVault);
        if (missing.Count == 0) return Array.Empty<MissingEntry>();

        return missing.Select(r => new MissingEntry(r, r.Types.Select(t => inVault.Resolve(t, r.Id)?.Name).FirstOrDefault(n => n != null) ?? r.Id,
            character.Carried.FirstOrDefault(c => r.Types.Any(t => c.Is(t, r.Id))))).ToList();
    }

    /// <summary>What a vault character's sheet is read against: the server's packs for its system and the entries it carries.</summary>
    public Task<CachedEntryResolver> ResolverAsync(ActorDocument character, SystemDefinition system) =>
        CachedEntryResolver.LoadAsync(library, CampaignFor(system.Id), CampaignAccess.Vault(character.OwnerUserId ?? string.Empty), system,
            character.Carried.Select(c => c.ToEntry(system.Id)));

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
            AskedFor = missing.Except(dropped).Where(m => m.Carried == null).Select(m => m.Name).ToList(),
            Brings = missing.Except(dropped).Select(m => m.Carried).OfType<CarriedEntry>().DistinctBy(c => (c.Type, c.EntryId)).ToList()
        });
    }

    /// <summary>A GM accepts a request. A new character joins the cast; an update goes over the campaign's copy through the
    /// live table when there is one, keeping the history's secret blocks and the table's own state.</summary>
    /// <param name="choices">What the GM decided about each entry the character brings; one with no choice is added.</param>
    public async Task<bool> AcceptAsync(string requestId, CampaignAccess access, SystemDefinition? system, IReadOnlyList<BringChoice>? choices = null,
        IReadOnlyList<ReviewEdit>? edits = null, CampaignDocument? campaign = null)
    {
        Dictionary<string, object>? sheet = null;
        List<CarriedEntry>? add = null;
        var notes = new List<JoinNote>();
        if (system != null && (await vault.GetRequestsAsync(access)).FirstOrDefault(r => r.Id == requestId) is { } request
            && (request.Brings.Count > 0 || edits is { Count: > 0 }))
        {
            // The sheet is read as it would stand with everything it brings added, as the GM saw it in the review.
            var resolver = campaign == null ? null : await CachedEntryResolver.LoadAsync(library, campaign, access, system, request.Brings.Select(b => b.ToEntry(system.Id)));
            var reviewed = Review(system, request, choices ?? Array.Empty<BringChoice>(), edits ?? Array.Empty<ReviewEdit>(), resolver);
            (sheet, add, notes) = (reviewed.Sheet, reviewed.Add, reviewed.Notes);
        }

        if (await vault.AcceptAsync(requestId, access, sheet, add, notes) is not { } accepted) return false;
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
        copy.JoinNotes = notes;
        ActorSheetSync.ApplySheetToState(copy, system, components);
        return await runtime.TrySaveActorAsync(copy, before, access, system);
    }

    /// <summary>A request's sheet after the GM's review, and the carried entries to add to the Library: one left out leaves the
    /// sheet, one swapped has the sheet name the campaign's own entry instead, the rest are added.</summary>
    public static (Dictionary<string, object> Sheet, List<CarriedEntry> Add) Reviewed(SystemDefinition system, CharacterRequestDocument request, IReadOnlyList<BringChoice> choices)
    {
        var reviewed = Review(system, request, choices, Array.Empty<ReviewEdit>(), null);
        return (reviewed.Sheet, reviewed.Add);
    }

    /// <summary>The whole of a review: the GM's edits to the character's picks applied first (choices through the engine, so a row pick
    /// settles its counters, and list picks as given), then what each carried entry is to become (<see cref="Reviewed"/>), with a
    /// note for each thing the player should be told. Only the request's own sheet changes: the vault character is left as it is.
    /// <paramref name="resolver"/> is the campaign's Library with the carried entries added; without one, edits to choices are not applied
    /// and a swap is named by its id.</summary>
    public static ReviewResult Review(SystemDefinition system, CharacterRequestDocument request, IReadOnlyList<BringChoice> choices,
        IReadOnlyList<ReviewEdit> edits, IEntryResolver? resolver)
    {
        var sheet = new Dictionary<string, object>(request.Sheet);
        var notes = new List<JoinNote>();
        var engine = resolver == null ? null : new SheetRuleEngine(system, resolver);
        foreach (var edit in edits)
        {
            if (edit.Key.StartsWith(SheetRuleEngine.ChoicePrefix, StringComparison.Ordinal))
            {
                if (engine == null) continue;
                engine.ChangeChoice(sheet, edit.Key, edit.Picks);
            }
            else if (system.SheetRules?.Lists.Any(l => l.Key == edit.Key) == true)
            {
                sheet[edit.Key] = edit.Picks.Cast<object>().ToList();
            }
            else
            {
                continue;
            }

            notes.Add(new JoinNote(JoinNote.Edited, edit.Label, edit.Summary, edit.Was));
        }

        var add = new List<CarriedEntry>();
        foreach (var carried in request.Brings)
        {
            var choice = choices.FirstOrDefault(c => carried.Is(c.Type, c.EntryId));
            switch (choice?.Kind ?? BringKind.Add)
            {
                case BringKind.LeaveOut:
                    sheet = SheetReferences.Without(system, sheet, SheetReferences.Of(system, sheet, resolver).Where(r => r.Types.Any(t => carried.Is(t, r.Id))));
                    notes.Add(new JoinNote(JoinNote.LeftOut, carried.Name, Type: carried.Type, Id: carried.EntryId));
                    break;
                case BringKind.UseOther when !string.IsNullOrWhiteSpace(choice!.UseId):
                    sheet = SheetReferences.Repointed(system, sheet, carried.Type, carried.EntryId, choice.UseId, resolver);
                    notes.Add(new JoinNote(JoinNote.Swapped, carried.Name, resolver?.Resolve(carried.Type, choice.UseId)?.Name ?? choice.UseId, Type: carried.Type, Id: carried.EntryId, ToId: choice.UseId));
                    break;
                default:
                    add.Add(carried);
                    break;
            }
        }

        return new ReviewResult(sheet, add, notes);
    }

    /// <summary>Updates the vault character the caller's campaign copy came from, with what of it is theirs (<see cref="PlayedCharacters.AsTheirsAsync"/>).</summary>
    public async Task<bool> UpdateVaultAsync(string actorId, CampaignDocument campaign, CampaignAccess access)
    {
        var asPlayer = access.AsPlayer();
        if (await actors.GetVisibleAsync(actorId, asPlayer) is not { } copy || copy.OwnerUserId != access.UserId || copy.VaultSourceId is not { } sourceId)
        {
            return false;
        }

        var theirs = (await played.AsTheirsAsync(campaign, asPlayer, await actors.GetVisibleForCampaignAsync(asPlayer), [copy])).Single();
        return await vault.UpdateFromCopyAsync(sourceId, access.UserId, theirs.Name, theirs.Sheet, theirs.Biography);
    }
}
