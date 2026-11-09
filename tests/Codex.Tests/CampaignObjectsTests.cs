using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The campaign's own objects (<see cref="CampaignObjects"/>, overhaul step 7). The crew fixture is the spec; these pin
/// the edges it leaves implicit: bounds, what a full clock raises once, what names nothing, edits, and who sees what.</summary>
public class CampaignObjectsTests
{
    private static SystemDefinition System(string events = "{}") => SystemDefinitionLoader.Parse($$"""
        id: campaign-test
        name: Campaign test
        sheetKinds:
          - kind: hero
            label: Hero
            tracks:
              - { key: stress, max: 6 }
        campaign:
          clocks:
            segments: [4, 6, 8]
            visibility: [gm, players]
          tables:
            - key: factions
              label: Factions
              itemFields: [{ key: status, type: Number, min: -3, max: 3, default: 0 }, { key: notes, type: Text }]
            - key: secrets
              visibility: gm
              itemFields: [{ key: weight, type: Number, default: 1 }]
        events: {{events}}
        """);

    private static Dictionary<string, object> State() => new()
    {
        ["clocks"] = new List<object>
        {
            new Dictionary<string, object> { ["name"] = "Alarm", ["segments"] = 6L, ["filled"] = 4L, ["visibility"] = "gm" },
            new Dictionary<string, object> { ["name"] = "Ritual", ["segments"] = 4L, ["filled"] = 0L, ["visibility"] = "players" }
        },
        ["factions"] = new List<object> { new Dictionary<string, object> { ["name"] = "The Watch", ["status"] = -1L } },
        ["secrets"] = new List<object> { new Dictionary<string, object> { ["name"] = "Hidden" } }
    };

    private static SheetIntent Intent(string kind, params (string Key, object? Value)[] fields) =>
        new(kind, null, fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal));

    [Fact]
    public void AClock_FillsToItsSegments_AndSaysSoOnce()
    {
        var system = System();
        var state = State();
        var before = CampaignObjects.Copy(state);

        var result = CampaignObjects.Apply(system, state, new[] { Intent("adjust", ("clock", "alarm"), ("by", 5)) });

        Assert.Equal(6, CampaignObjects.Clocks(state)[0].Filled);
        Assert.Equal(new[] { "clocks" }, result.Changed);
        var log = Assert.Single(result.Raised);
        Assert.Equal(CampaignObjects.FullLogKey, log.Fields["key"]);
        Assert.Equal(new object?[] { "Alarm" }, (IEnumerable<object?>)log.Fields["args"]!);
        Assert.Equal(4, CampaignObjects.Clocks(before)[0].Filled);

        // Already full: it stays full, and says nothing again.
        var again = CampaignObjects.Apply(system, state, new[] { Intent("adjust", ("clock", "Alarm"), ("by", 1)) });
        Assert.Empty(again.Raised);
        Assert.Empty(again.Changed);
    }

    [Fact]
    public void AClock_EmptiesNoFurtherThanNothing_AndSetIsWithinItsSize()
    {
        var system = System();
        var state = State();
        CampaignObjects.Apply(system, state, new[] { Intent("adjust", ("clock", "Alarm"), ("by", -9)), Intent("set", ("clock", "Ritual"), ("to", 10)) });
        Assert.Equal(0, CampaignObjects.Clocks(state)[0].Filled);
        Assert.Equal(4, CampaignObjects.Clocks(state)[1].Filled);
    }

    [Fact]
    public void ARowsField_StaysWithinItsBounds()
    {
        var system = System();
        var state = State();
        var result = CampaignObjects.Apply(system, state, new[] { Intent("adjust", ("table", "factions"), ("row", "the watch"), ("field", "status"), ("by", -5)) });
        Assert.Equal(-3d, CampaignObjects.Rows(system, state, "factions")[0].Fields["status"]);
        Assert.Equal(("factions", "The Watch", "status", -1d, -3d), Assert.Single(result.Changes));

        // No field named: the table's first number field.
        CampaignObjects.Apply(system, state, new[] { Intent("set", ("table", "factions"), ("row", "The Watch"), ("to", 2)) });
        Assert.Equal(2d, CampaignObjects.Rows(system, state, "factions")[0].Fields["status"]);
    }

    [Fact]
    public void AnIntentNamingNothingTheCampaignHas_IsLeft()
    {
        var system = System();
        var state = State();
        var unknownClock = Intent("adjust", ("clock", "Nope"), ("by", 1));
        var unknownRow = Intent("adjust", ("table", "factions"), ("row", "Nobody"), ("by", 1));
        var textField = Intent("adjust", ("table", "factions"), ("row", "The Watch"), ("field", "notes"), ("by", 1));
        var result = CampaignObjects.Apply(system, state, new[] { unknownClock, unknownRow, textField, Intent("mark", ("clock", "Alarm")) });
        Assert.Equal(4, result.Left.Count);
        Assert.Empty(result.Changed);
    }

    [Fact]
    public void Problems_NameSizesFillsVisibilitiesNamesAndBounds()
    {
        var system = System();
        var state = new Dictionary<string, object>
        {
            ["clocks"] = new List<object>
            {
                new Dictionary<string, object> { ["name"] = "Odd", ["segments"] = 5L, ["filled"] = 0L, ["visibility"] = "gm" },
                new Dictionary<string, object> { ["name"] = "Over", ["segments"] = 4L, ["filled"] = 5L, ["visibility"] = "everyone" },
                new Dictionary<string, object> { ["name"] = "over", ["segments"] = 4L, ["filled"] = 0L, ["visibility"] = "gm" }
            },
            ["factions"] = new List<object> { new Dictionary<string, object> { ["name"] = "The Watch", ["status"] = 4L } }
        };

        var problems = CampaignObjects.Problems(system, state).Select(p => (p.FieldKey, p.Key)).ToList();

        Assert.Equal(new[]
        {
            ("campaign.clocks", "Actor.Rule.ClockSegments"),
            ("campaign.clocks", "Actor.Rule.ClockFilled"),
            ("campaign.clocks", "Actor.Rule.ClockVisibility"),
            ("campaign.clocks", "Actor.Rule.CampaignDuplicate"),
            ("campaign.factions", "Actor.Rule.Range")
        }, problems);
        Assert.Empty(CampaignObjects.Problems(system, State()));
    }

    [Fact]
    public void Edits_AddChangeAndRemove_WithoutTouchingTheStateTheyCameFrom()
    {
        var system = System();
        var original = State();
        var state = CampaignObjects.Copy(original);

        Assert.True(CampaignObjects.AddClock(system, state, " Escape "));
        Assert.False(CampaignObjects.AddClock(system, state, "escape"));
        var added = CampaignObjects.Clocks(state).Last();
        Assert.Equal(new CampaignClock("Escape", 4, 0, "gm"), added);

        CampaignObjects.Apply(system, state, new[] { Intent("set", ("clock", "Alarm"), ("to", 6)) });
        Assert.True(CampaignObjects.EditClock(state, "Alarm", segments: 4, visibility: "players", rename: "Siren"));
        Assert.Equal(new CampaignClock("Siren", 4, 4, "players"), CampaignObjects.Clocks(state)[0]);
        Assert.False(CampaignObjects.EditClock(state, "Siren", rename: "Ritual"));

        Assert.True(CampaignObjects.AddRow(system, state, "factions", "The Crows"));
        Assert.Equal(0d, CampaignObjects.Rows(system, state, "factions")[1].Fields["status"]);
        Assert.True(CampaignObjects.SetRowField(system, state, "factions", "The Crows", "status", "2"));
        Assert.False(CampaignObjects.SetRowField(system, state, "factions", "The Crows", "status", "lots"));
        Assert.True(CampaignObjects.SetRowField(system, state, "factions", "The Crows", "notes", " allies "));
        Assert.Equal("allies", CampaignObjects.Rows(system, state, "factions")[1].Fields["notes"]);
        Assert.True(CampaignObjects.RenameRow(state, "factions", "The Crows", "Crows"));
        Assert.True(CampaignObjects.RemoveRow(state, "factions", "The Watch"));
        Assert.True(CampaignObjects.RemoveClock(state, "Ritual"));

        Assert.Equal(State().Count, original.Count);
        Assert.Equal(new[] { "Alarm", "Ritual" }, CampaignObjects.Clocks(original).Select(c => c.Name));
        Assert.Equal(4, CampaignObjects.Clocks(original)[0].Filled);
        Assert.Equal("The Watch", Assert.Single(CampaignObjects.Rows(system, original, "factions")).Name);
    }

    [Fact]
    public void Players_SeeNoClockOrTableKeptForTheGm()
    {
        var system = System();
        var seen = CampaignObjects.Visible(system, State(), isGm: false);
        Assert.Equal(new[] { "Ritual" }, CampaignObjects.Clocks(seen).Select(c => c.Name));
        Assert.False(seen.ContainsKey("secrets"));
        Assert.True(seen.ContainsKey("factions"));
        Assert.Equal(2, CampaignObjects.Clocks(CampaignObjects.Visible(system, State(), isGm: true)).Count);
    }

    [Fact]
    public void AHandlerLine_WithATableTarget_NamesItsTable_AndTheCampaignIntentsApplyToTheCampaign()
    {
        var system = System("""
            { standing: [{ target: campaign.factions, intents: [{ adjust: { row: ev.faction, field: status, by: ev.by } }] }],
              alarm: [{ target: campaign.clocks, intents: [{ adjust: { clock: ev.clock, by: 1 } }, { adjust: { track: stress, by: 1 } }] }] }
            """);
        var sheet = new Dictionary<string, object> { ["kind"] = "hero" };
        var state = State();

        var standing = SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("standing", ("faction", "The Watch"), ("by", 2)), campaign: state);
        Assert.Equal("factions", Assert.Single(standing.Intents).Fields["table"]);
        Assert.Equal(1d, CampaignObjects.Rows(system, state, "factions")[0].Fields["status"]);
        Assert.Equal(new[] { "factions" }, standing.Campaign!.Changed);

        // On a clocks line an intent must name its clock: a track's intent there is a mistake, said rather than applied.
        var ex = Assert.Throws<SheetEventException>(() => SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("alarm", ("clock", "Alarm")), campaign: state));
        Assert.Contains("names no clock", ex.Message);
    }

    [Fact]
    public void WithoutCampaignState_TheCampaignsIntentsAreLeftForTheTable()
    {
        var system = System();
        var sheet = new Dictionary<string, object> { ["kind"] = "hero" };
        var result = SheetEvents.Raise(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("adjust", ("clock", "Alarm"), ("by", 1)));
        Assert.Null(result.Campaign);
        Assert.True(CampaignObjects.IsFor(Assert.Single(result.Applied.Left)));
        Assert.Empty(result.Applied.Changed);
    }

    [Fact]
    public void TheLoader_NamesTargetsAndTablesThatAreNotThere()
    {
        var bad = System("{ odd: [{ target: campaign.nowhere, intents: [] }], worse: [{ target: elsewhere, intents: [] }] }");
        var problems = SheetEvents.Problems(bad);
        Assert.Contains(problems, p => p.Contains("\"nowhere\""));
        Assert.Contains(problems, p => p.Contains("not \"elsewhere\""));

        var definition = SystemDefinitionLoader.Parse("""
            id: t
            name: T
            campaign:
              clocks: { segments: [1, 4], visibility: [gm, all] }
              tables: [{ key: clocks }, { key: a, visibility: party, itemFields: [{ key: name }] }, { key: a }]
            """);
        var found = CampaignObjects.DefinitionProblems(definition);
        Assert.Contains(found, p => p.Contains("not 1"));
        Assert.Contains(found, p => p.Contains("not \"all\""));
        Assert.Contains(found, p => p.Contains("cannot be called \"clocks\""));
        Assert.Contains(found, p => p.Contains("not \"party\""));
        Assert.Contains(found, p => p.Contains("a field \"name\""));
        Assert.Contains(found, p => p.Contains("defined twice"));
    }
}
