using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The vault (docs/SERVER_ACCESS_AND_VAULT.md 8) and bringing its characters into campaigns (7).</summary>
public class VaultTests : IClassFixture<RavenDbFixture>
{
    private readonly RavenDbService _db;
    private readonly ActorRepository _actors;
    private readonly RavenVaultRepository _vault;
    private readonly CampaignRepository _campaigns;

    public VaultTests(RavenDbFixture fixture)
    {
        _db = new RavenDbService(fixture.DbPath, fixture.DbName, runInMemory: true);
        _actors = new ActorRepository(_db);
        _vault = new RavenVaultRepository(_db);
        _campaigns = new CampaignRepository(_db, new NoOpSystemCatalog(), _actors, new RavenSessionRepository(_db), new RavenNoteRepository(_db),
            new RegionRepository(_db), new FactRepository(_db), new RavenHomebrewRepository(_db), new RavenEncounterRepository(_db), vaultRepository: _vault);
    }

    private async Task<CampaignDocument> CampaignAsync(string owner, string system = "srd5e", params string[] players)
    {
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = owner, Name = "Camp", SystemId = system };
        campaign.Members.AddRange(players.Select(p => new CampaignMember { UserId = p, Role = CampaignRole.Player }));
        await _campaigns.SaveAsync(campaign);
        return campaign;
    }

    private static string User() => "user-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task WhatACharacterCarries_IsReviewedWithTheRequest_AndOnlyWhatTheGmTakesReachesTheLibrary_Async()
    {
        var (gm, player) = (User(), User());
        var campaign = await CampaignAsync(gm, "srd5e", player);
        var (asGm, asPlayer) = (new CampaignAccess(campaign.Id, gm, CampaignRole.DM), new CampaignAccess(campaign.Id, player, CampaignRole.Player));
        var homebrew = new RavenHomebrewRepository(_db);
        var character = await _vault.CreateAsync(player, "srd5e", "Ossic", actor =>
        {
            actor.Sheet["class"] = "warlock";
            actor.Sheet["subclass"] = "the-drowned-page";
            actor.Carried.Add(new CarriedEntry { Type = "subclass", EntryId = "the-drowned-page", Name = "The Drowned Page", Description = "A book that whispers.\n:::dm\nnot a player's to write\n:::", Properties = { ["class"] = "warlock" } });
            actor.Carried.Add(new CarriedEntry { Type = "feat", EntryId = "frog-licker", Name = "Frog Licker" });
        });

        // The request brings what the character really carries, whatever the caller sends, and never a GM's block.
        var request = (await _vault.AskAsync(asPlayer, new CharacterRequestDocument
        {
            VaultActorId = character.Id, Name = "Ossic", Sheet = character.Sheet,
            Brings = { new CarriedEntry { Type = "subclass", EntryId = "the-drowned-page", Name = "Forged", Description = "Forged text" }, new CarriedEntry { Type = "spell", EntryId = "made-up" } }
        }))!;
        var brought = Assert.Single(request.Brings);
        Assert.Equal(("The Drowned Page", "A book that whispers."), (brought.Name, brought.Description));
        Assert.Empty(await homebrew.GetVisibleForCampaignAsync(asGm)); // nothing joins the Library while it waits

        var accepted = await _vault.AcceptAsync(request.Id, asGm, add: request.Brings);

        Assert.Equal("the-drowned-page", accepted!.Value.Added!.Sheet["subclass"]);
        var entry = Assert.Single(await homebrew.GetVisibleForCampaignAsync(asPlayer)); // published: its player sees it
        Assert.Equal(("subclass", "the-drowned-page", "The Drowned Page", player), (entry.Type, entry.EntryId, entry.Name, entry.AuthorUserId));
        Assert.Equal("warlock", entry.Properties["class"]?.ToString());

        // Left out by the GM: the sheet comes in as reviewed, and nothing is added.
        var again = (await _vault.AskAsync(asPlayer, new CharacterRequestDocument { VaultActorId = character.Id, Name = "Ossic II", Sheet = character.Sheet, Brings = { new CarriedEntry { Type = "feat", EntryId = "frog-licker" } } }))!;
        var without = new Dictionary<string, object>(again.Sheet);
        without.Remove("subclass");
        var second = await _vault.AcceptAsync(again.Id, asGm, without, new List<CarriedEntry>());
        Assert.False(second!.Value.Added!.Sheet.ContainsKey("subclass"));
        Assert.Single(await homebrew.GetVisibleForCampaignAsync(asGm));
    }

    [Fact]
    public async Task ANewVaultCharacter_StartsFromWhatItIsGiven_ButStaysInItsOwnersVault_Async()
    {
        var owner = User();
        var created = await _vault.CreateAsync(owner, "srd5e", "", actor =>
        {
            // A draft fills the sheet and the history; it cannot make the character someone else's or put it in a campaign.
            actor.Name = "Mira";
            actor.Sheet["species"] = "elf";
            actor.Biography = "Sailed with the Red Gull.";
            actor.CampaignId = "campaigns/elsewhere";
            actor.OwnerUserId = "someone-else";
            actor.VaultSourceId = "vault-x";
            actor.Kind = ActorKind.Monster;
        });

        var stored = (await _vault.GetAsync(created.Id, owner))!;
        Assert.Equal(("Mira", "elf", "Sailed with the Red Gull."), (stored.Name, stored.Sheet["species"]?.ToString(), stored.Biography));
        Assert.True(stored.IsVault);
        Assert.Equal((owner, "srd5e", (string?)null, ActorKind.PlayerCharacter), (stored.OwnerUserId, stored.SystemId, stored.VaultSourceId, stored.Kind));
        Assert.Null(await _actors.GetVisibleAsync(created.Id, new CampaignAccess("campaigns/elsewhere", "someone-else", CampaignRole.DM)));

        // With no name at all it still gets one.
        Assert.Equal("New character", (await _vault.CreateAsync(owner, "srd5e", " ")).Name);
    }

    [Fact]
    public async Task AVaultCharacter_IsItsOwnersAlone_AndNoCampaignAccessReachesIt_Async()
    {
        var owner = User();
        var other = User();
        var character = await _vault.CreateAsync(owner, "srd5e", "Aria");

        Assert.Equal(new[] { character.Id }, (await _vault.GetAllAsync(owner)).Select(a => a.Id));
        Assert.Empty(await _vault.GetAllAsync(other));
        Assert.Null(await _vault.GetAsync(character.Id, other));

        var theirs = (await _vault.GetAsync(character.Id, owner))!;
        theirs.Name = "Aria the Bold";
        Assert.False(await _vault.SaveAsync(theirs, other));
        Assert.True(await _vault.SaveAsync(theirs, owner));
        Assert.False(await _vault.DeleteAsync(character.Id, other));

        // Every campaign-side read and write refuses it, whatever campaign the access names, an empty one included.
        var campaign = await CampaignAsync(owner);
        var dm = new CampaignAccess(campaign.Id, owner, CampaignRole.DM);
        var none = CampaignAccess.Vault(owner);
        Assert.Null(await _actors.GetVisibleAsync(character.Id, dm));
        Assert.Null(await _actors.GetVisibleAsync(character.Id, none));
        Assert.DoesNotContain(await _actors.GetVisibleForCampaignAsync(none), a => a.Id == character.Id);
        Assert.False(await _actors.TrySaveAsync(theirs, none));
        Assert.False(await _actors.TryDeleteAsync(character.Id, dm));
        Assert.Null(await _actors.TryRetireAsync(character.Id, dm));
        Assert.DoesNotContain(await _actors.GetRecentForOwnerAsync(owner, 10, sortByCreated: true), a => a.Id == character.Id);

        // "Delete everything of no campaign" would be every vault.
        await _actors.DeleteAllForCampaignAsync(string.Empty);
        Assert.NotNull(await _vault.GetAsync(character.Id, owner));

        // A copy saved twice from one load is refused the second time, as a campaign sheet is.
        var stale = (await _vault.GetAsync(character.Id, owner))!;
        var fresh = (await _vault.GetAsync(character.Id, owner))!;
        fresh.Name = "Fresh";
        Assert.True(await _vault.SaveAsync(fresh, owner));
        stale.Name = "Stale";
        await Assert.ThrowsAsync<Codex.Localization.LocalizedException>(() => _vault.SaveAsync(stale, owner));
    }

    [Fact]
    public async Task ACharacterJoinsACampaign_OnlyWhenAGmAccepts_AsACopyPlayedByWhoeverAsked_Async()
    {
        var gm = User();
        var player = User();
        var stranger = User();
        var campaign = await CampaignAsync(gm, "srd5e", player);
        var character = await _vault.CreateAsync(player, "srd5e", "Bram");
        await _vault.UpdateFromCopyAsync(character.Id, player, "Bram", new Dictionary<string, object> { ["level"] = 3L }, "Sailed\n\n:::dm\ntypo\n:::\n");
        var asPlayer = new CampaignAccess(campaign.Id, player, CampaignRole.Player);
        var asGm = new CampaignAccess(campaign.Id, gm, CampaignRole.DM);

        // Refused: not in the campaign, not theirs, another system.
        Assert.Null(await _vault.AskAsync(new CampaignAccess(campaign.Id, stranger, CampaignRole.Player), new CharacterRequestDocument { VaultActorId = character.Id }));
        Assert.Null(await _vault.AskAsync(asGm, new CharacterRequestDocument { VaultActorId = character.Id }));
        var otherSystem = await CampaignAsync(gm, "other", player);
        Assert.Null(await _vault.AskAsync(new CampaignAccess(otherSystem.Id, player, CampaignRole.Player), new CharacterRequestDocument { VaultActorId = character.Id }));

        var vaultCopy = (await _vault.GetAsync(character.Id, player))!;
        var request = await _vault.AskAsync(asPlayer, new CharacterRequestDocument
        {
            VaultActorId = character.Id, Name = vaultCopy.Name, Sheet = vaultCopy.Sheet, Biography = "Sailed\n\n:::dm\nmade up\n:::\n", AskedFor = { "Pirate" }
        });
        Assert.NotNull(request);
        // A player cannot write secret blocks, so any typed in the vault come in as the text a player reads.
        Assert.Equal("Sailed", request!.Biography);
        // Asking again replaces the first request.
        request = await _vault.AskAsync(asPlayer, new CharacterRequestDocument { VaultActorId = character.Id, Name = "Bram", Sheet = vaultCopy.Sheet, Biography = vaultCopy.Biography });
        Assert.Single(await _vault.GetRequestsAsync(asGm));

        // Waiting, it is in no campaign list, and only the GM and the asker see the request.
        Assert.DoesNotContain(await _actors.GetVisibleForCampaignAsync(asGm), a => a.Name == "Bram");
        var otherPlayer = User();
        await _campaigns.UpdateAsync(campaign.Id, c => c.Members.Add(new CampaignMember { UserId = otherPlayer, Role = CampaignRole.Player }));
        Assert.Empty(await _vault.GetRequestsAsync(new CampaignAccess(campaign.Id, otherPlayer, CampaignRole.Player)));
        Assert.Single(await _vault.GetRequestsAsync(asPlayer));

        // A player cannot accept, even their own.
        Assert.Null(await _vault.AcceptAsync(request.Id, asPlayer));
        var accepted = await _vault.AcceptAsync(request.Id, asGm);
        var added = accepted?.Added;
        Assert.NotNull(added);
        Assert.Null(await _vault.AcceptAsync(request.Id, asGm));
        Assert.Empty(await _vault.GetRequestsAsync(asGm));

        var inCampaign = (await _actors.GetVisibleAsync(added!.Id, asGm))!;
        Assert.Equal(ActorKind.PlayerCharacter, inCampaign.Kind);
        Assert.Equal(player, inCampaign.OwnerUserId);
        Assert.Equal(character.Id, inCampaign.VaultSourceId);
        Assert.Equal(campaign.Id, inCampaign.CampaignId);
        Assert.Equal(3L, Convert.ToInt64(inCampaign.Sheet["level"]));
        // The vault keeps its own character.
        Assert.NotNull(await _vault.GetAsync(character.Id, player));
    }

    [Fact]
    public async Task UpdatingFromTheCampaignCopy_TouchesOnlyTheOwnersVaultCharacter_AndAnUpdateRequestNeedsTheirOwnCopy_Async()
    {
        var gm = User();
        var player = User();
        var campaign = await CampaignAsync(gm, "srd5e", player);
        var character = await _vault.CreateAsync(player, "srd5e", "Cass");
        var asPlayer = new CampaignAccess(campaign.Id, player, CampaignRole.Player);
        var asGm = new CampaignAccess(campaign.Id, gm, CampaignRole.DM);

        Assert.False(await _vault.UpdateFromCopyAsync(character.Id, gm, "Taken", new(), null));
        Assert.True(await _vault.UpdateFromCopyAsync(character.Id, player, "Cass II", new(), "Grew up\n:::dm\nsecret\n:::\n"));
        var stored = (await _vault.GetAsync(character.Id, player))!;
        Assert.Equal("Cass II", stored.Name);
        Assert.DoesNotContain("secret", stored.Biography);

        var request = await _vault.AskAsync(asPlayer, new CharacterRequestDocument { VaultActorId = character.Id, Name = "Cass" });
        var copy = (await _vault.AcceptAsync(request!.Id, asGm))!.Value.Added!;

        // An update must name the asker's own copy of this very character.
        var npc = new ActorDocument { Id = Guid.NewGuid().ToString(), CampaignId = campaign.Id, Name = "Guard", Kind = ActorKind.NonPlayerCharacter };
        await _actors.SaveAsync(npc);
        Assert.Null(await _vault.AskAsync(asPlayer, new CharacterRequestDocument { VaultActorId = character.Id, ReplacesActorId = npc.Id }));
        var update = await _vault.AskAsync(asPlayer, new CharacterRequestDocument { VaultActorId = character.Id, ReplacesActorId = copy.Id, Name = "Cass II" });
        Assert.NotNull(update);
        var applied = await _vault.AcceptAsync(update!.Id, asGm);
        Assert.Null(applied!.Value.Added);
        Assert.Equal(copy.Id, applied.Value.Request.ReplacesActorId);
    }

    [Fact]
    public async Task LeavingOrDeletingTheCampaign_TakesItsRequestsAway_Async()
    {
        var gm = User();
        var player = User();
        var campaign = await CampaignAsync(gm, "srd5e", player);
        var character = await _vault.CreateAsync(player, "srd5e", "Dax");
        var asGm = new CampaignAccess(campaign.Id, gm, CampaignRole.DM);
        await _vault.AskAsync(new CampaignAccess(campaign.Id, player, CampaignRole.Player), new CharacterRequestDocument { VaultActorId = character.Id, Name = "Dax" });

        Assert.Equal(CampaignLeaveResult.Left, await _campaigns.LeaveAsync(campaign.Id, player));
        Assert.Empty(await _vault.GetRequestsAsync(asGm));

        var second = await CampaignAsync(gm, "srd5e", player);
        await _vault.AskAsync(new CampaignAccess(second.Id, player, CampaignRole.Player), new CharacterRequestDocument { VaultActorId = character.Id, Name = "Dax" });
        Assert.Equal(CampaignDeleteResult.Deleted, await _campaigns.DeleteAsync(second.Id, gm));
        using var session = _db.Store.OpenAsyncSession();
        Assert.Empty(await Raven.Client.Documents.LinqExtensions.ToListAsync(session.Query<CharacterRequestDocument>().Where(r => r.CampaignId == second.Id)));
        // The vault character itself is untouched.
        Assert.NotNull(await _vault.GetAsync(character.Id, player));
    }

    [Fact]
    public void WhatASheetUses_IsReadFromTheSystemsRules_AndLeavingSomethingOutTakesOnlyThat()
    {
        var system = new SystemDefinition
        {
            ActorFields =
            {
                new FieldDefinition("class", "Class", FieldType.EntityReference, targetEntityType: "class"),
                new FieldDefinition("subclass", "Subclass", FieldType.EntityReference, targetEntityType: "subclass"),
                new FieldDefinition("species", "Species", FieldType.EntityReference, targetEntityType: "species"),
            },
            SheetRules = new SheetRules
            {
                Lists = { new ListRule { Key = "feats", TargetEntityType = "feat" } },
                Inventory = new InventoryRules { Key = "gear", Types = { "equipment", "weapon" } },
                Multiclass = new MulticlassRules { Key = "multiclass", ClassField = "class", SubclassField = "subclass" }
            }
        };
        var sheet = new Dictionary<string, object>
        {
            ["class"] = "fighter", ["species"] = "elf", ["level"] = 5L,
            ["feats"] = new List<object> { "alert", "homebrew-sea-legs|str:1" },
            ["gear"] = new List<object> { "rope|qty:2", "cutlass|equipped:1" },
            ["multiclass"] = new List<object> { "wizard:2:bladesinger", "rogue:1" }
        };

        var uses = SheetReferences.Of(system, sheet);
        Assert.Contains(uses, r => r.FieldKey == "feats" && r.Id == "homebrew-sea-legs");
        Assert.Contains(uses, r => r.FieldKey == "multiclass" && r.Id == "bladesinger" && r.Types.Single() == "subclass");
        Assert.Contains(uses, r => r.FieldKey == "gear" && r.Id == "cutlass" && r.Types.Contains("weapon"));

        var known = new[] { ("class", "fighter"), ("species", "elf"), ("feat", "alert"), ("equipment", "rope"), ("weapon", "cutlass"), ("class", "wizard"), ("class", "rogue") };
        var missing = SheetReferences.Missing(system, sheet, new Resolver(known));
        Assert.Equal(new[] { "homebrew-sea-legs", "bladesinger" }, missing.Select(m => m.Id).OrderByDescending(id => id.Length));

        var without = SheetReferences.Without(system, sheet, missing);
        Assert.Equal(new[] { "alert" }, SheetRuleEngine.RawList(without, "feats"));
        Assert.Equal(new[] { "wizard:2", "rogue:1" }, SheetRuleEngine.RawList(without, "multiclass"));
        Assert.Equal(2, SheetRuleEngine.RawList(without, "gear").Count);
        Assert.Equal("fighter", without["class"]);
        Assert.Equal(5L, without["level"]);
        // Leaving out the class itself clears the field and nothing else.
        var noClass = SheetReferences.Without(system, sheet, uses.Where(u => u.FieldKey == "class"));
        Assert.False(noClass.ContainsKey("class"));
        Assert.Equal("elf", noClass["species"]);
        Assert.Empty(SheetReferences.Missing(system, without, new Resolver(known)));
    }

    private sealed class Resolver((string Type, string Id)[] known) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => known.Contains((type, id)) ? new ContentEntry { Type = type, Id = id, Name = id } : null;
        public IEnumerable<ContentEntry> Candidates(string type) => Enumerable.Empty<ContentEntry>();
    }
}
