using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>One-time upgrades of stored documents (<see cref="DatabaseUpgrades"/>).</summary>
[Collection("RavenDb")]
public class DatabaseUpgradesTests(RavenDbFixture fixture) : IClassFixture<RavenDbFixture>
{
    private static readonly SystemDefinition Capped = SystemDefinitionLoader.Parse("""
        id: capped
        name: Capped
        attributes:
          - { key: str, label: Strength, default: 10, modifier: half-minus-five }
          - { key: dex, label: Dexterity, default: 10, modifier: half-minus-five }
        entityTypes:
          - { type: feat, label: Feat }
        sheetRules:
          attributes:
            methods: [point-buy, manual]
            default: point-buy
            max: 20
            pointBuy: { budget: 27, min: 8, max: 15 }
            increases: { label: Score increases, fromList: feats }
          lists:
            - { key: feats, label: Feats, targetEntityType: feat }
        """);

    private RavenDbService Database() =>
        new(fixture.DbPath, "upgrades-" + Guid.NewGuid().ToString("N")[..8], runInMemory: true);

    private static ActorDocument Creature(string campaignId, string name, Dictionary<string, object> sheet) => new()
    {
        Id = "actors/" + Guid.NewGuid().ToString("N"),
        CampaignId = campaignId,
        Kind = ActorKind.Monster,
        BlueprintId = "pack:" + name.ToLowerInvariant(),
        Name = name,
        Sheet = sheet
    };

    private static Dictionary<string, object> Manual(long str, long dex) => new()
    {
        [SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod,
        [SheetRuleEngine.BasePrefix + "str"] = str,
        [SheetRuleEngine.BasePrefix + "dex"] = dex,
        ["str"] = str,
        ["dex"] = dex
    };

    private static void Save(RavenDbService db, params object[] documents)
    {
        using var session = db.Store.OpenSession();
        foreach (var document in documents) session.Store(document);
        session.SaveChanges();
    }

    private static string? MethodOf(RavenDbService db, string id)
    {
        using var session = db.Store.OpenSession();
        return YamlValues.GetString(session.Load<ActorDocument>(id).Sheet, SheetRuleEngine.MethodKey);
    }

    private static SystemDefinition? Systems(string id) => id == Capped.Id ? Capped : null;

    /// <summary>A pit fiend imported before the fix (manual, Strength 26 over the cap of 20) becomes as printed, and its
    /// scores stay what they were. A creature the DM has since given an increase keeps manual, since as printed it would
    /// lose it; the log says which and why. A sheet not made from a stat block is not touched.</summary>
    [Fact]
    public void ACreatureFromBeforeTheFix_IsPrinted_UnlessThatWouldChangeItsScores()
    {
        using var db = Database();
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = "dm", Name = "C", SystemId = Capped.Id };
        var fiend = Creature(campaign.Id, "Pit fiend", Manual(26, 14));
        var raised = Manual(18, 12);
        raised["feats"] = new List<object> { SheetRuleEngine.FormatPick("asi", new Dictionary<string, int> { ["str"] = 2 }) };
        var ogre = Creature(campaign.Id, "Ogre", raised);
        var hero = new ActorDocument { Id = "actors/hero", CampaignId = campaign.Id, Kind = ActorKind.PlayerCharacter, Name = "Hero", Sheet = Manual(15, 14) };
        Save(db, campaign, fiend, ogre, hero);

        var log = DatabaseUpgrades.Run(db.Store, Systems);

        Assert.Equal(SheetRuleEngine.PrintedMethod, MethodOf(db, fiend.Id));
        Assert.Equal(SheetRuleEngine.ManualMethod, MethodOf(db, ogre.Id));
        Assert.Equal(SheetRuleEngine.ManualMethod, MethodOf(db, hero.Id));
        Assert.Contains(log, l => l.Contains("'Ogre'") && l.Contains("str 20 → 18"));
        Assert.Contains(log, l => l.Contains("1 creature sheet(s) switched"));

        using var session = db.Store.OpenSession();
        var sheet = session.Load<ActorDocument>(fiend.Id).Sheet;
        var evaluation = new SheetRuleEngine(Capped, NullEntryResolver.Instance).Recompute(new Dictionary<string, object>(sheet));
        Assert.DoesNotContain(evaluation.Violations, v => v.Key == "Actor.Rule.ScoreMax");
        Assert.Equal(26, YamlValues.GetInt(sheet, "str"));
    }

    /// <summary>Once done it is recorded, so a DM who sets a creature back to manual afterwards is not overruled on the next
    /// start.</summary>
    [Fact]
    public void TheUpgrade_RunsOnce_SoADmsLaterChoiceOfManualStands()
    {
        using var db = Database();
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = "dm", Name = "C", SystemId = Capped.Id };
        var fiend = Creature(campaign.Id, "Pit fiend", Manual(26, 14));
        Save(db, campaign, fiend);
        DatabaseUpgrades.Run(db.Store, Systems);

        using (var session = db.Store.OpenSession())
        {
            session.Load<ActorDocument>(fiend.Id).Sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
            session.SaveChanges();
        }

        Assert.Empty(DatabaseUpgrades.Run(db.Store, Systems));
        Assert.Equal(SheetRuleEngine.ManualMethod, MethodOf(db, fiend.Id));
        using var check = db.Store.OpenSession();
        Assert.Contains(DatabaseUpgrades.PrintedStatBlocks, check.Load<DatabaseUpgrades.Ledger>(DatabaseUpgrades.LedgerId).Applied);
    }

    /// <summary>A creature in a campaign whose system is not loaded cannot be judged: it is left alone and the upgrade is
    /// not recorded, so it runs again once the system is back.</summary>
    [Fact]
    public void ACreatureWhoseSystemIsMissing_WaitsForIt()
    {
        using var db = Database();
        var campaign = new CampaignDocument { Id = Guid.NewGuid().ToString(), OwnerId = "dm", Name = "C", SystemId = Capped.Id };
        var fiend = Creature(campaign.Id, "Pit fiend", Manual(26, 14));
        Save(db, campaign, fiend);

        var log = DatabaseUpgrades.Run(db.Store, _ => null);
        Assert.Contains(log, l => l.Contains("waits for system 'capped'"));
        Assert.Equal(SheetRuleEngine.ManualMethod, MethodOf(db, fiend.Id));

        DatabaseUpgrades.Run(db.Store, Systems);
        Assert.Equal(SheetRuleEngine.PrintedMethod, MethodOf(db, fiend.Id));
    }
}
