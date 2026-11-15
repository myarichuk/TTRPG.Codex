using Codex.ContentImport;
using Codex.Core;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The srd5e hazard type, its scaling, the eight traps from the reviewed data file and the two homebrew hazards.</summary>
public class Srd5eHazardsTests : IAsyncLifetime
{
    private static readonly string[] TrapIds =
    [
        "collapsing-roof", "falling-net", "fire-casting-statue", "hidden-pit", "poisoned-darts", "poisoned-needle", "rolling-stone", "spiked-pit"
    ];

    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public Srd5eHazardsTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private SystemDefinition System => _loader.GetDefinition("srd5e")!;

    private IEnumerable<ContentEntry> Hazards(params string[] packs) => _registry.GetEntries("srd5e", "hazard", new HashSet<string>(packs));

    [Fact]
    public void TheHazardType_IsDeclaredAndIsNotACreature()
    {
        var type = System.FindType("hazard")!;

        Assert.NotNull(type.Hazard);
        Assert.False(type.ActorBlueprint);
        Assert.False(type.PlayerFacing);
        Assert.Equal("detect", type.Hazard!.Notice!.Routes);
        Assert.Equal("disarm", type.Hazard.Disable!.Routes);
        Assert.True(type.Hazard.Notice.Passive);
        Assert.Equal("damage", type.Hazard.Effect!.Damage);
        Assert.Equal(FieldType.Collection, type.Fields.Single(f => f.Key == "damage").Type);
    }

    private ContentTypeDefinition HazardType => System.FindType("hazard")!;

    [Fact]
    public void TheScaling_HasRowsWithoutSeverity_AndANuisanceAndADeadlyRowAtEachBand()
    {
        var rows = System.Hazards!.Scaling;
        var plain = rows.Where(r => !r.ContainsKey("severity")).ToList();

        // Collapsing Roof's progression, a Deadly trap with a save, stays as the rows for a hazard with no severity.
        Assert.Equal(new[] { "1", "5", "11", "17" }, plain.Select(r => r["level"]));
        Assert.Equal(new[] { "2d10", "4d10", "10d10", "18d10" }, plain.Select(r => r["damage"]));
        Assert.Equal(new[] { 13, 15, 17, 19 }, plain.Select(r => int.Parse(r["saveDc"])));

        // The two severities come from the SRD's own traps: the tightest range holding every trap of that severity at that band.
        Assert.Equal(new[] { "Nuisance", "Deadly" }, HazardNumbers.Severities(System));
        string[] nuisanceDc = ["10-11", "12-13", "14-15", "15-17"], nuisanceDamage = ["3.5-5.5", "10.5-11", "21-22", "42-55"];
        string[] deadlyDc = ["13-15", "13-15", "13-17", "13-19"], deadlyDamage = ["7-12.5", "14-24", "28-57", "49-100.5"];
        foreach (var (severity, dc, damage) in new[] { ("Nuisance", nuisanceDc, nuisanceDamage), ("Deadly", deadlyDc, deadlyDamage) })
        {
            var own = rows.Where(r => r.GetValueOrDefault("severity") == severity).ToList();
            Assert.Equal(new[] { "1", "5", "11", "17" }, own.Select(r => r["level"]));
            Assert.Equal(dc, own.Select(r => r["saveDc"]));
            Assert.Equal(damage, own.Select(r => r["damage"]));
            // Neither bound falls as the level rises.
            foreach (var column in new[] { "saveDc", "damage" })
            {
                var bounds = own.Select(r => HazardNumbers.Bounds(r[column])!.Value).ToList();
                Assert.Equal(bounds.Select(b => b.Low).OrderBy(v => v), bounds.Select(b => b.Low));
                Assert.Equal(bounds.Select(b => b.High).OrderBy(v => v), bounds.Select(b => b.High));
            }
        }
    }

    [Fact]
    public void EveryShippedTrap_AtEveryBand_GivesNoScalingWarning()
    {
        var traps = Hazards("srd-5.2.1").ToList();
        Assert.Equal(8, traps.Count);
        foreach (var trap in traps)
        {
            Assert.True(!HazardNumbers.ScalingProblems(trap, HazardType, System).Any(), $"{trap.Id}: " + string.Join(" | ", HazardNumbers.ScalingProblems(trap, HazardType, System)));
        }
    }

    [Fact]
    public void TheShippedTraps_WouldWarn_AgainstTheRowsWithoutSeverity()
    {
        // The guard that the severity is really read: the same traps with the severity taken out are read against Collapsing Roof's rows, and some leave them.
        var type = HazardType;
        var severity = type.Hazard!.Severity!;
        var bandField = type.Hazard.HigherLevels!;
        ContentEntry Without(ContentEntry trap)
        {
            var properties = new Dictionary<string, object>(trap.Properties);
            properties.Remove(severity);
            if (properties.TryGetValue(bandField, out var raw))
            {
                properties[bandField] = HazardNumbers.EditableRows(raw).Select(r => { r.Remove(severity); return (object)r; }).ToList();
            }

            return new ContentEntry { Id = trap.Id, Type = trap.Type, Name = trap.Name, Properties = properties };
        }

        var warned = Hazards("srd-5.2.1").ToDictionary(t => t.Id, t => HazardNumbers.ScalingProblems(Without(t), type, System).ToList());
        Assert.NotEmpty(warned["poisoned-darts"]);
        Assert.NotEmpty(warned["spiked-pit"]);
        Assert.NotEmpty(warned["hidden-pit"]);
        Assert.Contains(warned["fire-casting-statue"], m => m.Contains("Save DC 15 is low"));
    }

    [Fact]
    public void EachRowsBounds_AreSetByAnSrdTrap()
    {
        // No row was widened by hand: each bound is the number some shipped trap has at that band.
        var type = HazardType;
        var measured = new Dictionary<(string, string, string), List<double>>();
        void Add((string, string, string) key, double value)
        {
            if (!measured.TryGetValue(key, out var list)) measured[key] = list = [];
            list.Add(value);
        }

        void Note(ContentEntry trap, string levels, string? band)
        {
            var numbers = HazardNumbers.For(trap, type, band);
            var severity = Convert.ToString(numbers["severity"]) ?? string.Empty;
            if (numbers.TryGetValue("saveDc", out var dc) && double.TryParse(Convert.ToString(dc, global::System.Globalization.CultureInfo.InvariantCulture), out var d)) Add((severity, levels, "saveDc"), d);
            var average = HazardNumbers.Damage(numbers, type.Hazard!).Sum(p => HazardNumbers.Bounds(p.Dice)!.Value.Low * (p.Count == null ? 1 : HazardNumbers.Bounds(p.Count)!.Value.Low));
            if (average > 0) Add((severity, levels, "damage"), average);
        }

        foreach (var trap in Hazards("srd-5.2.1"))
        {
            Note(trap, Convert.ToString(trap.Properties["level"]) ?? string.Empty, null);
            foreach (var band in HazardNumbers.Rows(trap, type)) Note(trap, band, band);
        }

        var rows = System.Hazards!.Scaling.Where(r => r.ContainsKey("severity")).ToList();
        foreach (var row in rows)
        {
            var band = row["level"] switch { "1" => "1-4", "5" => "5-10", "11" => "11-16", _ => "17-20" };
            foreach (var column in new[] { "saveDc", "damage" })
            {
                var bounds = HazardNumbers.Bounds(row[column])!.Value;
                var seen = measured.GetValueOrDefault((row["severity"], band, column)) ?? new List<double>();
                Assert.True(seen.Any(v => Math.Abs(v - bounds.Low) < 0.001), $"{row["severity"]} {band} {column}: no trap has the low bound {bounds.Low}.");
                Assert.True(seen.Any(v => Math.Abs(v - bounds.High) < 0.001), $"{row["severity"]} {band} {column}: no trap has the high bound {bounds.High}.");

                // And no row is narrower than the traps need: the row is exactly the tightest range that holds every trap of that severity at that band.
                Assert.True(seen.Count == 0 || Math.Abs(seen.Min() - bounds.Low) < 0.001, $"{row["severity"]} {band} {column}: the lowest trap is {seen.Min()}, the row starts at {bounds.Low}.");
                Assert.True(seen.Count == 0 || Math.Abs(seen.Max() - bounds.High) < 0.001, $"{row["severity"]} {band} {column}: the highest trap is {seen.Max()}, the row ends at {bounds.High}.");
            }
        }
    }

    [Fact]
    public void TheSrdPack_HasTheEightTraps_AndEachNamesItsParts()
    {
        var traps = Hazards("srd-5.2.1").ToList();

        Assert.Equal(TrapIds.OrderBy(i => i), traps.Select(t => t.Id).OrderBy(i => i));
        foreach (var trap in traps)
        {
            Assert.Equal("trap", trap.Properties["kind"]);
            foreach (var key in new[] { "severity", "level", "trigger", "duration", "detect", "effect" })
            {
                Assert.True(trap.Properties.ContainsKey(key), $"{trap.Id} has no {key}.");
            }

            Assert.True(trap.Properties.ContainsKey("damage") || trap.Properties.ContainsKey("conditions"), $"{trap.Id} does neither damage nor a condition.");
            // A save needs its DC; the two pits have neither, and a trap is otherwise inert after it goes off, so only two say how they reset.
            Assert.Equal(trap.Properties.ContainsKey("saveAttribute"), trap.Properties.ContainsKey("saveDc"));
            if (trap.Properties.ContainsKey("saveDc")) Assert.IsType<long>(trap.Properties["saveDc"]);
        }

        Assert.Equal(new[] { "fire-casting-statue", "poisoned-darts" }, traps.Where(t => t.Properties.ContainsKey("reset")).Select(t => t.Id).Order());
        Assert.Equal(new[] { "hidden-pit", "spiked-pit" }, traps.Where(t => !t.Properties.ContainsKey("saveAttribute") && t.Properties.ContainsKey("damage")).Select(t => t.Id).Order());
    }

    [Fact]
    public void EveryTrapButTheRollingStone_ScalesThroughTheThreeHigherBands_AndTheStoneChangesSeverityInOne()
    {
        foreach (var trap in Hazards("srd-5.2.1"))
        {
            var rows = trap.Properties.TryGetValue("higherLevels", out var value) ? value as IList<object> : null;
            Assert.NotNull(rows);
            var levels = rows!.Cast<IDictionary<string, object>>().Select(r => (string)r["levels"]);
            Assert.Equal(trap.Id == "rolling-stone" ? new[] { "17-20" } : new[] { "5-10", "11-16", "17-20" }, levels);
        }
    }

    [Fact]
    public void RollingStone_IsDeadlyAtTheBase_AndANuisanceInItsLastBand()
    {
        var type = System.FindType("hazard")!;
        var stone = Hazards("srd-5.2.1").Single(h => h.Id == "rolling-stone");

        Assert.Equal("Deadly", HazardNumbers.For(stone, type, null)["severity"]);
        var last = HazardNumbers.For(stone, type, "17-20");
        Assert.Equal("Nuisance", last["severity"]);
        Assert.Equal(15L, Convert.ToInt64(last["saveDc"]));
        Assert.True(stone.Properties["complex"] is true);
        Assert.Equal(8L, stone.Properties["initiative"]);
        Assert.Equal(17L, stone.Properties["armorClass"]);
        Assert.Equal(new[] { "Poison", "Psychic" }, ((IEnumerable<object>)stone.Properties["immunities"]).Select(i => i.ToString()));
    }

    [Fact]
    public void SpikedPit_HasTwoDamageParts_AndNoSave()
    {
        var type = System.FindType("hazard")!;
        var pit = Hazards("srd-5.2.1").Single(h => h.Id == "spiked-pit");
        var hazard = type.Hazard!;

        Assert.False(pit.Properties.ContainsKey("saveAttribute"));
        Assert.False(pit.Properties.ContainsKey("saveDc"));
        Assert.Equal(new[] { new HazardDamagePart("1d6", "bludgeoning", null), new HazardDamagePart("2d8", "piercing", null) }, HazardNumbers.Damage(HazardNumbers.For(pit, type, null), hazard));
        var deep = HazardNumbers.For(pit, type, "17-20");
        Assert.Equal(new[] { new HazardDamagePart("12d6", "bludgeoning", null), new HazardDamagePart("13d8", "piercing", null) }, HazardNumbers.Damage(deep, hazard));
        Assert.Equal("120 feet", deep["depth"]);
        Assert.False(deep.ContainsKey("saveDc"));
    }

    [Fact]
    public void TheStatue_HasTwoDisarmRoutes_AndReadingTheGlyphIsADetectRoute()
    {
        var type = System.FindType("hazard")!;
        var statue = Hazards("srd-5.2.1").Single(h => h.Id == "fire-casting-statue");
        var numbers = HazardNumbers.For(statue, type, null);

        var disarm = HazardNumbers.DisableRoutes(numbers, type.Hazard!);
        var notice = HazardNumbers.NoticeRoutes(numbers, type.Hazard!);

        Assert.Equal(new[] { "deface", "wedge" }, disarm.Select(r => r.Id));
        Assert.Equal(new[] { "glyph", "plate" }, disarm.Select(r => r.After));
        Assert.Equal(new[] { "aura", "glyph", "meaning", "plate" }, notice.Select(r => r.Id));
        var read = notice.Single(r => r.Id == "meaning");
        Assert.Equal(("Arcana", 15, "Study", "glyph"), (read.Check, read.Dc, read.Action, read.After));
        var aura = notice.Single(r => r.Id == "aura");
        Assert.False(aura.HasCheck);
        Assert.Equal("A Detect Magic spell", aura.Needs);
        Assert.Equal(new int?[] { 10, 15, 15 }, notice.Where(r => r.HasCheck).Select(r => r.Dc));
        Assert.Equal("30-foot Cone", HazardNumbers.For(statue, type, "5-10")["area"]);
        Assert.Equal(15L, Convert.ToInt64(HazardNumbers.For(statue, type, "17-20")["saveDc"]));
    }

    [Fact]
    public void RollingStone_DealsNoHalfDamage_KnocksProne_AndHasItsStatBlock()
    {
        var type = System.FindType("hazard")!;
        var stone = Hazards("srd-5.2.1").Single(h => h.Id == "rolling-stone");
        var numbers = HazardNumbers.For(stone, type, null);

        Assert.Equal("none", stone.Properties["onSave"]);
        Assert.Equal(new[] { new HazardCondition("Prone", null) }, HazardNumbers.Conditions(numbers, type.Hazard!));
        Assert.Equal(new[] { new HazardDamagePart("10d10", "bludgeoning", null) }, HazardNumbers.Damage(numbers, type.Hazard!));
        Assert.Equal((100L, 10L), (stone.Properties["hitPoints"], stone.Properties["damageThreshold"]));
    }

    [Fact]
    public void TheWireTraps_HaveATextOnlyDisarm_AndTheRoofSavesAtDc13_AndTheNetRestrains()
    {
        var type = System.FindType("hazard")!;
        foreach (var id in new[] { "collapsing-roof", "falling-net" })
        {
            var trap = Hazards("srd-5.2.1").Single(h => h.Id == id);
            var cut = HazardNumbers.DisableRoutes(HazardNumbers.For(trap, type, null), type.Hazard!).Single();
            Assert.False(cut.HasCheck);
            Assert.Null(cut.Needs);
            Assert.False(string.IsNullOrEmpty(cut.Text));
            Assert.Equal("wire", cut.After);
            Assert.Equal(("Perception", 11), (HazardNumbers.NoticeRoutes(HazardNumbers.For(trap, type, null), type.Hazard!).Single().Check, HazardNumbers.NoticeRoutes(HazardNumbers.For(trap, type, null), type.Hazard!).Single().Dc));
        }

        Assert.Equal(13L, Hazards("srd-5.2.1").Single(h => h.Id == "collapsing-roof").Properties["saveDc"]);
        var net = Hazards("srd-5.2.1").Single(h => h.Id == "falling-net");
        Assert.Equal(new[] { new HazardCondition("Restrained", "until it escapes") }, HazardNumbers.Conditions(HazardNumbers.For(net, type, null), type.Hazard!));
    }

    [Fact]
    public void TheProblemChecks_ReadBandRows_AndAreaPerEntry()
    {
        var type = System.FindType("hazard")!;
        var entry = Traps.Read("""
            - id: t
              name: T
              properties:
                saveAttribute: Dexterity
                onSave: halve
                damage: 4d10
                conditions: [Prone]
                detect: Perception 15
                disarm:
                  - { id: a, name: Loop, after: b, text: x }
                  - { id: b, name: Loop2, after: a, text: x }
                  - { id: c, name: Self, after: c, text: x }
                higherLevels:
                  - levels: 5-10
                    methods:
                      - { name: Escape, dc: 12 }
                      - { name: Slow, check: Athletics, dc: twelve }
                    damage:
                      - { dice: 4d1O, type: fire }
            """).Single();

        var problems = HazardNumbers.Problems(entry, type, System).ToList();

        Assert.Contains(problems, p => p.Contains("band 5-10") && p.Contains("'Escape'") && p.Contains("dc and no check"));
        Assert.Contains(problems, p => p.Contains("band 5-10") && p.Contains("'Slow'") && p.Contains("not a whole number"));
        Assert.Contains(problems, p => p.Contains("band 5-10") && p.Contains("'4d1O'"));
        Assert.Contains(problems, p => p.Contains("damage field") && p.Contains("one value"));
        Assert.Contains(problems, p => p.Contains("detect field") && p.Contains("one value"));
        Assert.Contains(problems, p => p.Contains("condition list has 'Prone'"));
        Assert.Contains(problems, p => p.Contains("onSave is 'halve'"));
        Assert.Contains(problems, p => p.Contains("save and no save DC"));
    }

    [Fact]
    public void TheRollingStone_HasExactlyOneStrikingRow_Crush_AndNoOtherRoutineRowStrikes()
    {
        var type = System.FindType("hazard")!;
        var stone = Hazards("srd-5.2.1").Single(h => h.Id == "rolling-stone");

        var routine = HazardNumbers.Routine(HazardNumbers.For(stone, type, null), type.Hazard!);

        Assert.Equal(new[] { "Crush" }, routine.Where(s => s.Strikes).Select(s => s.Name));
        Assert.True(HazardNumbers.HasEffect(HazardNumbers.For(stone, type, null), type.Hazard!));
        Assert.Empty(HazardNumbers.Problems(stone, type, System));
        // The Flooding Room's Surge has its own numbers (half on a failure, none on a success), so none of its rows applies the main effect.
        var room = Hazards("codex-hazards").Single(h => h.Id == "homebrew-flooding-room");
        Assert.DoesNotContain(HazardNumbers.Routine(HazardNumbers.For(room, type, null), type.Hazard!), s => s.Strikes);
    }

    [Fact]
    public void AStrikesKey_MustBeTrueOrFalse_OnOneRowAtMost_AndOnlyOnAHazardWithAnEffect()
    {
        var type = System.FindType("hazard")!;
        var withEffect = Traps.Read("""
            - id: a
              name: A
              properties:
                saveAttribute: Dexterity
                saveDc: 12
                damage:
                  - { dice: 2d6, type: fire }
                routine:
                  - { name: One, strikes: maybe }
                  - { name: Two, strikes: true }
                  - { name: Three, strikes: true }
            """).Single();
        var noEffect = Traps.Read("""
            - id: b
              name: B
              properties:
                routine:
                  - { name: Wave, strikes: true }
                  - { name: Idle, strikes: false }
            """).Single();

        var problems = HazardNumbers.Problems(withEffect, type, System).ToList();
        var bare = HazardNumbers.Problems(noEffect, type, System).ToList();

        Assert.Contains(problems, p => p.Contains("'One'") && p.Contains("true or false"));
        Assert.Contains(problems, p => p.Contains("more than one routine row"));
        Assert.DoesNotContain(problems, p => p.Contains("no effect"));
        Assert.Contains(bare, p => p.Contains("'Wave'") && p.Contains("has no effect"));
        Assert.DoesNotContain(bare, p => p.Contains("'Idle'"));
        // The effect is read from the entry's own values: the type always has the field names, an entry with no save, attack or damage has no effect.
        Assert.False(HazardNumbers.HasEffect(HazardNumbers.For(noEffect, type, null), type.Hazard!));
    }

    [Fact]
    public void ASaveDcWithNoSave_AndAnOnSaveWithNoSave_AreReported()
    {
        var type = System.FindType("hazard")!;
        var entry = Traps.Read("""
            - id: t
              name: T
              properties:
                saveDc: 12
                onSave: half
            """).Single();

        var problems = HazardNumbers.Problems(entry, type, System).ToList();

        Assert.Contains(problems, p => p.Contains("save DC and no save"));
        Assert.Contains(problems, p => p.Contains("onSave and no save"));
    }

    [Fact]
    public void ADetectRoute_CannotWaitForItself_OrInACircle()
    {
        var type = System.FindType("hazard")!;
        var entry = Traps.Read("""
            - id: t
              name: T
              properties:
                detect:
                  - { id: a, name: A, after: b, text: x }
                  - { id: b, name: B, after: a, text: x }
                  - { id: c, name: C, after: c, text: x }
            """).Single();

        var problems = HazardNumbers.Problems(entry, type, System).ToList();

        Assert.Contains(problems, p => p.Contains("'c' comes after itself") || p.Contains("'C' comes after itself"));
        Assert.Contains(problems, p => p.Contains("waits, through other routes, for itself"));
    }

    [Fact]
    public void ARouteWithNoCheck_IsDoneByWhatItNeeds()
    {
        var type = System.FindType("hazard")!;
        var pit = Hazards("srd-5.2.1").Single(h => h.Id == "hidden-pit");

        var wedge = HazardNumbers.DisableRoutes(HazardNumbers.For(pit, type, null), type.Hazard!).First();
        var escape = HazardNumbers.Methods(HazardNumbers.For(pit, type, null), type.Hazard!).Single();

        Assert.False(wedge.HasCheck);
        Assert.Null(wedge.Dc);
        Assert.Equal("An Iron Spike or a similar object", wedge.Needs);
        Assert.Equal("lid", wedge.After);
        Assert.False(escape.HasCheck);
        Assert.Contains("Climb Speed", escape.Needs);
        Assert.Equal("Wedge the lid", wedge.Shown);
    }

    [Fact]
    public void ThePoisonedNeedle_FailingTheDisarmSetsItOff_AndTheDartsHitOneToThreeTimes()
    {
        var type = System.FindType("hazard")!;
        var needle = Hazards("srd-5.2.1").Single(h => h.Id == "poisoned-needle");
        var darts = Hazards("srd-5.2.1").Single(h => h.Id == "poisoned-darts");
        var hazard = type.Hazard!;

        var disarm = HazardNumbers.DisableRoutes(HazardNumbers.For(needle, type, null), hazard).Single();
        Assert.Equal(new[] { "failure" }, disarm.TriggersOn);
        Assert.Equal(("Sleight of Hand", 15), (disarm.Check, disarm.Dc));
        Assert.Equal(new[] { new HazardCondition("Poisoned", "1 hour") }, HazardNumbers.Conditions(HazardNumbers.For(needle, type, null), hazard));
        Assert.Equal("half", needle.Properties["onSave"]);

        Assert.Equal(new[] { new HazardDamagePart("1d6", "poison", "1d3") }, HazardNumbers.Damage(HazardNumbers.For(darts, type, null), hazard));
        Assert.Equal(new[] { new HazardDamagePart("7d6", "poison", "1d3") }, HazardNumbers.Damage(HazardNumbers.For(darts, type, "17-20"), hazard));
        Assert.Equal("none", darts.Properties["onSave"]);
        Assert.Equal(13L, Convert.ToInt64(HazardNumbers.For(darts, type, "17-20")["saveDc"]));
    }

    [Fact]
    public void TheFallingNet_RestatesItsEscapeDcWithEachBand()
    {
        var type = System.FindType("hazard")!;
        var net = Hazards("srd-5.2.1").Single(h => h.Id == "falling-net");

        Assert.Equal(new[] { 10, 12, 14, 16 }, new[] { null, "5-10", "11-16", "17-20" }
            .Select(band => HazardNumbers.Methods(HazardNumbers.For(net, type, band), type.Hazard!).Single(m => m.Name == "Escape").Dc!.Value));
        Assert.Equal(new[] { 13, 13, 13, 13 }, new[] { null, "5-10", "11-16", "17-20" }
            .Select(band => HazardNumbers.Methods(HazardNumbers.For(net, type, band), type.Hazard!).Single(m => m.Name == "Set the Trap").Dc!.Value));
        Assert.False(net.Properties.ContainsKey("damage"));
    }

    [Fact]
    public void TheTrapNumbers_AreWholeNumbersInsideTheCollections()
    {
        var needle = Traps.Read("""
            - id: t
              name: T
              properties:
                saveDc: 12
                initiative: 8
                complex: true
                hitPoints: 100
                damage:
                  - { dice: 1d6, type: poison, count: 1 }
                disarm:
                  - { name: Cut, check: Sleight of Hand, dc: 15, successes: 2 }
                methods:
                  - { name: Escape, check: Strength, dc: 10, text: Climb. }
                higherLevels:
                  - { levels: 5-10, saveDc: 14 }
            """).Single();

        Assert.Equal(12L, needle.Properties["saveDc"]);
        Assert.Equal(8L, needle.Properties["initiative"]);
        Assert.Equal(100L, needle.Properties["hitPoints"]);
        Assert.True(needle.Properties["complex"] is true);
        var part = (IDictionary<string, object>)((IList<object>)needle.Properties["damage"]).Single();
        Assert.Equal("1", part["count"]);
        var cut = (IDictionary<string, object>)((IList<object>)needle.Properties["disarm"]).Single();
        Assert.Equal(15L, cut["dc"]);
        Assert.Equal(2L, cut["successes"]);
        var method = (IDictionary<string, object>)((IList<object>)needle.Properties["methods"]).Single();
        Assert.Equal(10L, method["dc"]);
        var row = (IDictionary<string, object>)((IList<object>)needle.Properties["higherLevels"]).Single();
        Assert.Equal(14L, row["saveDc"]);
        Assert.Equal("5-10", row["levels"]);
    }

    [Fact]
    public void TheTwoHomebrewHazards_AreTaggedAndPrefixed_AndNeverClashWithTheSrd()
    {
        var homebrew = Hazards("codex-hazards").ToList();

        Assert.Equal(2, homebrew.Count);
        Assert.All(homebrew, h =>
        {
            Assert.StartsWith("homebrew-", h.Id);
            Assert.Contains("homebrew", h.Tags);
        });
        Assert.Empty(homebrew.Select(h => h.Id).Intersect(TrapIds));
        Assert.Contains(homebrew, h => h.Properties.TryGetValue("complex", out var complex) && complex is true);
    }

    private static string Flat(object? value) => value switch
    {
        null => "null",
        // A whole number is a number whether the file wrote it bare (damage: 1) or the data file read it as text.
        string s => long.TryParse(s, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var n) ? "n:" + n : "s:" + s,
        bool b => "b:" + b,
        sbyte or byte or short or ushort or int or uint or long or ulong => "n:" + global::System.Convert.ToInt64(value, global::System.Globalization.CultureInfo.InvariantCulture),
        global::System.Collections.IDictionary map => "{" + string.Join(";", map.Keys.Cast<object>().Select(k => $"{k}={Flat(map[k])}").OrderBy(x => x, StringComparer.Ordinal)) + "}",
        global::System.Collections.IEnumerable list => "[" + string.Join(",", list.Cast<object?>().Select(Flat)) + "]",
        _ => "?:" + value
    };

    [Fact]
    public void TheShippedTrapFiles_AreExactlyWhatTheDataFileSays()
    {
        var shipped = Hazards("srd-5.2.1").ToDictionary(h => h.Id);
        var source = Traps.Entries();

        Assert.Equal(source.Select(e => e.Id).Order(), shipped.Keys.Order());
        foreach (var entry in source)
        {
            var file = shipped[entry.Id];
            Assert.Equal(entry.Name, file.Name);
            Assert.Equal(entry.Description, file.Description);
            Assert.Equal(entry.Properties.Keys.Order(), file.Properties.Keys.Order());
            foreach (var (key, value) in entry.Properties)
            {
                Assert.True(Flat(value) == Flat(file.Properties[key]), $"{entry.Id}.{key}: the data file says {Flat(value)}, the shipped file {Flat(file.Properties[key])}. Rewrite srd-5.2.1/hazards from Data/srd521-traps.yaml.");
            }
        }
    }

    // Checks the sheet cannot roll: the table takes a typed bonus for these (HAZARDS_DESIGN.md, H4a). Anything else must name a skill or an attribute,
    // by key or label, so a misspelling fails here. Only the homebrew hazards use these; the SRD traps ask for tools in `needs`, not as a check.
    private static readonly string[] NeedsTypedBonus = ["Thieves' Tools", "Spellcasting"];

    private static IEnumerable<string?> ChecksOf(ContentEntry hazard)
    {
        var checks = new List<string?> { Text(hazard, "saveAttribute") };
        foreach (var field in new[] { "detect", "disarm", "methods" })
        {
            checks.AddRange(Rows(hazard, field).Select(m => m.TryGetValue("check", out var c) ? c?.ToString() : null));
        }

        return checks;
    }

    [Fact]
    public void EveryCheckTheHazardsName_IsASkillOrAnAttribute_OrNeedsATypedBonus()
    {
        var unknown = new List<string>();
        foreach (var hazard in Hazards("srd-5.2.1", "codex-hazards"))
        {
            // The same lookup the table's check service uses, so a name that resolves here resolves there.
            unknown.AddRange(ChecksOf(hazard).Where(c => !string.IsNullOrEmpty(c) && Codex.Persistence.Runtime.ActorChecks.Resolve(System, c) == null && !NeedsTypedBonus.Contains(c)).Select(c => $"{hazard.Id}: {c}"));
        }

        Assert.Empty(unknown);
    }

    [Fact]
    public void TheSrdTraps_NeverAskForAToolCheck()
    {
        foreach (var trap in Hazards("srd-5.2.1"))
        {
            Assert.DoesNotContain(ChecksOf(trap), c => c != null && NeedsTypedBonus.Contains(c));
        }

        Assert.Contains(Hazards("codex-hazards").SelectMany(ChecksOf), c => c == "Thieves' Tools");
    }

    [Fact]
    public void TheSrdTrapFile_HasNoUnverifiedMarkLeft()
    {
        using var stream = typeof(Traps).Assembly.GetManifestResourceStream("srd521-traps.yaml")!;
        var text = new StreamReader(stream).ReadToEnd();

        Assert.DoesNotContain("# check", text);
    }

    private static string? Text(ContentEntry entry, string key) => entry.Properties.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static IEnumerable<IDictionary<string, object?>> Rows(ContentEntry entry, string key) =>
        entry.Properties.TryGetValue(key, out var value) && value is IEnumerable<object> rows
            ? rows.OfType<IDictionary<string, object>>().Select(r => (IDictionary<string, object?>)r.ToDictionary(p => p.Key, p => (object?)p.Value))
            : Enumerable.Empty<IDictionary<string, object?>>();

    [Fact]
    public void ThePlacedNumbers_TakeTheChosenRowOverTheBase_AndNeverTheOthers()
    {
        var type = System.FindType("hazard")!;
        var pit = Hazards("srd-5.2.1").Single(h => h.Id == "spiked-pit");

        var baseNumbers = Codex.Core.Systems.HazardNumbers.For(pit, type, null);
        var midNumbers = Codex.Core.Systems.HazardNumbers.For(pit, type, "5-10");

        var first = (IDictionary<string, object>)((IList<object>)baseNumbers["damage"])[0];
        var midFirst = (IDictionary<string, object>)((IList<object>)midNumbers["damage"])[0];
        Assert.Equal("1d6", first["dice"]);
        Assert.Equal("3d6", midFirst["dice"]);
        Assert.Equal("30 feet", midNumbers["depth"]);
        Assert.Equal(pit.Properties["trigger"], midNumbers["trigger"]);
        Assert.Equal("1d6", ((IDictionary<string, object>)((IList<object>)Codex.Core.Systems.HazardNumbers.For(pit, type, "no such row")["damage"])[0])["dice"]);
        Assert.Equal(new[] { "5-10", "11-16", "17-20" }, Codex.Core.Systems.HazardNumbers.Rows(pit, type));
        Assert.Equal(new[] { "17-20" }, Codex.Core.Systems.HazardNumbers.Rows(Hazards("srd-5.2.1").Single(h => h.Id == "rolling-stone"), type));
    }

    [Fact]
    public void AScaledRowWithAMisspeltKey_OrAnUnnamedMethod_IsReported()
    {
        var type = System.FindType("hazard")!;
        var entry = Traps.Read("""
            - id: t
              name: T
              properties:
                higherLevels:
                  - { levels: 5-10, saveDC: 17 }
                  - { levels: 11-16, bogus: 1 }
                methods:
                  - { check: Strength }
            """).Single();

        var problems = Codex.Core.Systems.HazardNumbers.Problems(entry, type, System).ToList();

        Assert.Contains(problems, p => p.Contains("saveDC") && p.Contains("saveDc"));
        Assert.Contains(problems, p => p.Contains("bogus"));
        Assert.Contains(problems, p => p.Contains("no name"));
        Assert.All(Hazards("srd-5.2.1", "codex-hazards"), h => Assert.Empty(Codex.Core.Systems.HazardNumbers.Problems(h, type, System)));
    }

    [Fact]
    public void ABadRouteRow_IsReportedByWhatIsWrongWithIt()
    {
        var type = System.FindType("hazard")!;
        var entry = Traps.Read("""
            - id: t
              name: T
              properties:
                detect:
                  - { id: a, name: Plate, check: Perception, dc: 12 }
                  - { id: a, name: Again, check: Perception, dc: 12 }
                disarm:
                  - { name: Bare }
                  - { name: Dc without a check, dc: 12, needs: A spike }
                  - { name: Late, after: nowhere, text: Soon. }
                  - { name: Ranked, check: Thievery, dc: 12, rank: Trained }
                  - { name: Nobody, check: Thievery, dc: 12, successes: 0 }
                damage:
                  - { dice: lots, type: fire }
                  - { dice: 1d6, type: fire, count: many }
                conditions:
                  - { name: Slimed }
            """).Single();

        var problems = Codex.Core.Systems.HazardNumbers.Problems(entry, type, System).ToList();

        Assert.Contains(problems, p => p.Contains("repeats the id 'a'"));
        Assert.Contains(problems, p => p.Contains("'Bare' has no check"));
        Assert.Contains(problems, p => p.Contains("'Dc without a check' has a dc and no check"));
        Assert.Contains(problems, p => p.Contains("'Late' comes after 'nowhere'"));
        Assert.Contains(problems, p => p.Contains("'Ranked' names a rank"));
        Assert.Contains(problems, p => p.Contains("'Nobody' needs a whole number of successes"));
        Assert.Contains(problems, p => p.Contains("'lots'"));
        Assert.Contains(problems, p => p.Contains("count 'many'"));
        Assert.Contains(problems, p => p.Contains("'Slimed' is not one of the system's conditions"));
    }

    [Fact]
    public void TheSystemLoadsClean_WithTheHazardsAndTheNewPack()
    {
        Assert.Null(_loader.LoadException);
        Assert.Empty(_loader.LoadWarnings);
    }
}
