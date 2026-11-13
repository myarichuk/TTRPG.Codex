using Codex.Core;
using Codex.Core.Rules;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Codex.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The GM's join review: edits to the character's picks, the entries the campaign has that a carried one may be swapped for
/// (only what the sheet's rules allow in that slot), and the notes the player reads. Against the real SRD pack.</summary>
public class JoinReviewEditTests : IAsyncLifetime, IClassFixture<RavenDbFixture>
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;
    private readonly RavenDbService _db;

    public JoinReviewEditTests(RavenDbFixture fixture)
    {
        _db = fixture.Open(fixture.DbName);
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private SystemDefinition System => _loader.GetDefinition("srd5e")!;

    private sealed class WithCarried(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }

    private static ContentEntry Carried(string type, string id, string name, string? cls = null)
    {
        var entry = new ContentEntry { Type = type, Id = id, Name = name, SystemId = "srd5e", PackId = "homebrew" };
        if (cls != null) entry.Properties["class"] = cls;
        return entry;
    }

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) Warlock(ContentEntry subclass)
    {
        var engine = new SheetRuleEngine(System, new WithCarried(_registry, subclass));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "warlock";
        sheet["subclass"] = subclass.Id;
        sheet["level"] = 5L;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        return (engine, sheet);
    }

    // ---- "Use one the campaign has" lists only what the rules allow in that slot ----

    [Fact]
    public void ASwapForACarriedSubclass_IsOnlyOfferedTheSubclassesOfItsClass()
    {
        var drowned = Carried("subclass", "the-drowned-page", "The Drowned Page", "warlock");
        var (engine, sheet) = Warlock(drowned);
        var evaluation = engine.Evaluate(sheet);

        var ours = SheetReferences.Replacements(System, sheet, evaluation, new WithCarried(_registry), "subclass", drowned.Id);

        Assert.NotEmpty(ours);
        Assert.All(ours, e => Assert.Equal("warlock", e.Properties["class"]?.ToString()));
        Assert.Contains(ours, e => e.Id == "fiend-patron");
        var everySubclass = _registry.GetEntries("srd5e", "subclass").Count();
        Assert.True(ours.Count < everySubclass, "every subclass of every class was offered");
    }

    [Fact]
    public void ASwapForAFeat_IsOnlyOfferedWhatTheFeatListAllows_AndNothingWhenTheSheetNoLongerUsesIt()
    {
        var frog = Carried("feat", "frog-licker", "Frog Licker");
        var (engine, sheet) = Warlock(Carried("subclass", "fiend-patron", "Fiend"));
        sheet.Remove("subclass");
        sheet["feats"] = new List<object> { frog.Id };
        var withFeat = new SheetRuleEngine(System, new WithCarried(_registry, frog));
        var evaluation = withFeat.Evaluate(sheet);

        var ours = SheetReferences.Replacements(System, sheet, evaluation, new WithCarried(_registry), "feat", frog.Id);

        var allowed = evaluation.ListOptions["feats"].Select(o => o.Id).ToHashSet();
        Assert.NotEmpty(ours);
        Assert.All(ours, e => Assert.Contains(e.Id, allowed));
        Assert.DoesNotContain(ours, e => e.Id == frog.Id);

        sheet["feats"] = new List<object>();
        Assert.Empty(SheetReferences.Replacements(System, sheet, withFeat.Evaluate(sheet), new WithCarried(_registry), "feat", frog.Id));
    }

    // ---- The GM's edits to the picks, applied to the copy ----

    private (CharacterRequestDocument Request, SheetRuleEngine Engine) DragonbornRequest()
    {
        var resolver = new WithCarried(_registry);
        var engine = new SheetRuleEngine(System, resolver);
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "fighter";
        sheet["species"] = "dragonborn";
        sheet["level"] = 1L;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        engine.ChangeChoice(sheet, "choice__dragonborn__lineage", new[] { "Draconic Ancestor: Red" });
        sheet["feats"] = new List<object> { "alert" };
        return (new CharacterRequestDocument { Name = "Skra", Sheet = sheet }, engine);
    }

    [Fact]
    public void AnEditedChoiceAndAnEditedListPick_ReachTheCopy_NotTheRequestItCameFrom_AndAreNoted()
    {
        var (request, _) = DragonbornRequest();
        var edits = new[]
        {
            new ReviewEdit("choice__dragonborn__lineage", "Lineage", new[] { "Draconic Ancestor: Blue" }, "Draconic Ancestor: Blue"),
            new ReviewEdit("feats", "Feats", new[] { "tough" }, "Tough")
        };

        var result = VaultService.Review(System, request, Array.Empty<BringChoice>(), edits, new WithCarried(_registry));

        Assert.Equal(new[] { "Draconic Ancestor: Blue" }, SheetRuleEngine.RawList(result.Sheet, "choice__dragonborn__lineage"));
        Assert.Equal(new[] { "tough" }, SheetRuleEngine.RawList(result.Sheet, "feats"));
        Assert.Equal(new[] { "Draconic Ancestor: Red" }, SheetRuleEngine.RawList(request.Sheet, "choice__dragonborn__lineage"));
        Assert.Equal(new[] { "alert" }, SheetRuleEngine.RawList(request.Sheet, "feats"));
        Assert.Equal(new[] { new JoinNote(JoinNote.Edited, "Lineage", "Draconic Ancestor: Blue"), new JoinNote(JoinNote.Edited, "Feats", "Tough") }, result.Notes);
    }

    [Fact]
    public void AnEditNamingNothingTheSheetHas_IsIgnored_AndWithNoRulesAChoiceIsNotApplied()
    {
        var (request, _) = DragonbornRequest();
        var stray = new ReviewEdit("notes", "Notes", new[] { "x" }, "x");
        var choice = new ReviewEdit("choice__dragonborn__lineage", "Lineage", new[] { "Draconic Ancestor: Blue" }, "Blue");

        var ignored = VaultService.Review(System, request, Array.Empty<BringChoice>(), new[] { stray }, new WithCarried(_registry));
        var unread = VaultService.Review(System, request, Array.Empty<BringChoice>(), new[] { choice }, null);

        Assert.Empty(ignored.Notes);
        Assert.Empty(unread.Notes);
        Assert.Equal(new[] { "Draconic Ancestor: Red" }, SheetRuleEngine.RawList(unread.Sheet, "choice__dragonborn__lineage"));
    }

    [Fact]
    public void WhatTheGmLeftOutOrSwapped_IsNotedForThePlayer_ByTheNameOfWhatItBecame()
    {
        var drowned = Carried("subclass", "the-drowned-page", "The Drowned Page", "warlock");
        var (_, sheet) = Warlock(drowned);
        sheet["feats"] = new List<object> { "frog-licker", "alert" };
        var request = new CharacterRequestDocument
        {
            Sheet = sheet,
            Brings =
            {
                new CarriedEntry { Type = "subclass", EntryId = drowned.Id, Name = drowned.Name },
                new CarriedEntry { Type = "feat", EntryId = "frog-licker", Name = "Frog Licker" }
            }
        };

        var result = VaultService.Review(System, request, new[]
        {
            new BringChoice("subclass", drowned.Id, BringKind.UseOther, "fiend-patron"),
            new BringChoice("feat", "frog-licker", BringKind.LeaveOut)
        }, Array.Empty<ReviewEdit>(), new WithCarried(_registry));

        var fiend = _registry.GetEntries("srd5e", "subclass").First(e => e.Id == "fiend-patron");
        Assert.Equal(new[]
        {
            new JoinNote(JoinNote.Swapped, "The Drowned Page", fiend.Name, Type: "subclass", Id: drowned.Id, ToId: "fiend-patron"),
            new JoinNote(JoinNote.LeftOut, "Frog Licker", Type: "feat", Id: "frog-licker")
        }, result.Notes);
        Assert.Empty(result.Add);
    }

    // ---- The player is told ----

    [Fact]
    public async Task TheNotes_AreKeptOnTheAcceptedCharacter_ForItsPlayer_Async()
    {
        var (gm, player) = ("gm-" + Guid.NewGuid().ToString("N"), "player-" + Guid.NewGuid().ToString("N"));
        var campaigns = new CampaignRepository(_db, new NoOpSystemCatalog(), new ActorRepository(_db), new RavenSessionRepository(_db), new RavenNoteRepository(_db),
            new RegionRepository(_db), new FactRepository(_db), new RavenHomebrewRepository(_db), new RavenEncounterRepository(_db), vaultRepository: new RavenVaultRepository(_db));
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = gm, Name = "Camp", SystemId = "srd5e" };
        campaign.Members.Add(new CampaignMember { UserId = player, Role = CampaignRole.Player });
        await campaigns.SaveAsync(campaign);
        var vault = new RavenVaultRepository(_db);
        var (asGm, asPlayer) = (new CampaignAccess(campaign.Id, gm, CampaignRole.DM), new CampaignAccess(campaign.Id, player, CampaignRole.Player));
        var character = await vault.CreateAsync(player, "srd5e", "Skra", actor => actor.Sheet["class"] = "fighter");
        var request = (await vault.AskAsync(asPlayer, new CharacterRequestDocument { VaultActorId = character.Id, Name = "Skra", Sheet = character.Sheet }))!;
        var notes = new[] { new JoinNote(JoinNote.Edited, "Feats", "Tough"), new JoinNote(JoinNote.LeftOut, "Frog Licker") };

        var accepted = await vault.AcceptAsync(request.Id, asGm, notes: notes);

        Assert.Equal(notes, accepted!.Value.Added!.JoinNotes);
        var stored = await new ActorRepository(_db).GetVisibleAsync(accepted.Value.Added.Id, asPlayer);
        Assert.Equal(notes, stored!.JoinNotes);
        Assert.Empty((await vault.GetAsync(character.Id, player))!.JoinNotes); // the vault character is as it was
        Assert.Equal(character.Sheet.Keys.OrderBy(k => k), (await vault.GetAsync(character.Id, player))!.Sheet.Keys.OrderBy(k => k));

        // Only the character's owner may dismiss the notes: the GM, another player and the vault's id do not.
        var actors = new ActorRepository(_db);
        Assert.False(await vault.DismissJoinNotesAsync(accepted.Value.Added.Id, gm));
        Assert.False(await vault.DismissJoinNotesAsync(accepted.Value.Added.Id, "someone-else"));
        Assert.False(await vault.DismissJoinNotesAsync(character.Id, player));
        Assert.Equal(notes, (await actors.GetVisibleAsync(accepted.Value.Added.Id, asPlayer))!.JoinNotes);
        Assert.True(await vault.DismissJoinNotesAsync(accepted.Value.Added.Id, player));
        Assert.Empty((await actors.GetVisibleAsync(accepted.Value.Added.Id, asPlayer))!.JoinNotes); // and it stays dismissed on the next read
    }
}
