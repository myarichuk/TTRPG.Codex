using System.IO.Compression;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The real four-degrees system's first shipped script (<c>rules.js#onTurnStart</c>, the recovery check of a dying creature) and the data beside it
/// (knocked out and dead, hero points), and the path the script travels: loaded from the folder, fingerprinted, packed, reloaded, copied into the
/// translations. Rules read from the pack's <c>dying-rule</c>, <c>death-rule</c>, <c>getting-knocked-out</c> and <c>hero-points</c>.
/// </summary>
public sealed class FourDegreesRulesScriptTests : IDisposable
{
    private const string Id = "four-degrees";
    private static readonly string Folder = Path.Combine(TestPaths.ContentRoot, "systems", Id);
    private static readonly Lazy<SystemDefinition> Loaded = new(() => SystemDefinitionLoader.Load(Path.Combine(Folder, SystemDefinitionLoader.FileName)));

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "codex-fd-rules-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static SystemDefinition System => Loaded.Value;

    private static Dictionary<string, object> Sheet(int dying = 0, int doomed = 0, int hp = 0, int wounded = 0, params string[] conditions)
    {
        var sheet = new Dictionary<string, object> { ["HP"] = (long)hp, ["HP_Max"] = 20L };
        if (dying > 0) sheet["condition_dying"] = (long)dying;
        if (doomed > 0) sheet["condition_doomed"] = (long)doomed;
        if (wounded > 0) sheet["condition_wounded"] = (long)wounded;
        if (conditions.Length > 0) sheet[SheetModel.ConditionsKey] = conditions.Cast<object>().ToList();
        return sheet;
    }

    private static List<string> Said(IEnumerable<SheetIntent> intents) =>
        intents.Select(i => i.Kind + (i.Fields.Count == 0 ? "" : " " + string.Join(" ", i.Fields.Select(f => $"{f.Key}={Show(f.Value)}")))).ToList();

    private static string Show(object? value) => value switch
    {
        IDictionary<string, object?> map => "{" + string.Join(",", map.Select(p => $"{p.Key}:{Show(p.Value)}")) + "}",
        System.Collections.IEnumerable list and not string => "[" + string.Join(",", list.Cast<object?>().Select(Show)) + "]",
        double number => number.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? ""
    };

    private static IReadOnlyList<SheetIntent> TurnStart(SystemDefinition system, Dictionary<string, object> sheet, IDiceSource dice) =>
        SheetEvents.Handle(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("turnStart"), dice);

    // ---- Loading ----

    [Fact]
    public void TheSystemLoadsRulesJs_WithNoScriptProblem()
    {
        Assert.Contains("rules.js", System.Scripts.Keys);
        Assert.Empty(SystemScripts.Problems(System));
        Assert.Equal(new[] { "rules.js" }, SystemScripts.FilesNamed(System));
        Assert.Empty(SheetEvents.Problems(System));
    }

    [Fact]
    public void TheFingerprint_NamesRulesJs_AndTheScriptIsWhatTheFileSays()
    {
        Assert.Contains("rules.js", System.Fingerprint!.Files.Keys);
        Assert.Equal(SystemFingerprint.Hash(Path.Combine(Folder, "rules.js")), System.Fingerprint.Files["rules.js"]);
    }

    [Fact]
    public void TheCommittedTypings_AreWhatTheCheckWrites()
    {
        // Regenerate with: dotnet run --project tools/Codex.SystemCheck -- content/systems/four-degrees --types
        Assert.Equal(ScriptTypings.For(System), File.ReadAllText(Path.Combine(Folder, ScriptTypings.FileName)));
    }

    // ---- The recovery check ----

    [Theory]
    [InlineData(1, 20, "condition remove=Dying|condition add=Wounded by=1")] // critical success: out of dying, wounded
    [InlineData(1, 11, "condition remove=Dying|condition add=Wounded by=1")] // success: 0
    [InlineData(1, 10, "condition add=Dying value=2")] // failure
    [InlineData(1, 1, "condition add=Dying value=3")] // critical failure
    [InlineData(3, 13, "condition add=Dying value=2")] // success lowers by 1 (DC 13)
    public void RecoveryCheck_MovesDyingByTheDegree(int dying, int face, string expected)
    {
        var dice = new SequenceDiceSource(new[] { face });

        var intents = TurnStart(System, Sheet(dying), dice);

        var said = Said(intents);
        var degree = expected.StartsWith("condition add=Dying value=3", StringComparison.Ordinal) ? "Critical failure"
            : expected.StartsWith("condition add=Dying value=2", StringComparison.Ordinal) && dying == 1 ? "Failure"
            : expected.StartsWith("condition add=Dying value=2", StringComparison.Ordinal) ? "Success"
            : face == 20 ? "Critical success" : "Success";
        Assert.Equal($"log key=Recovery check args=[DC {10 + dying},{degree}]", said[0]);
        Assert.Equal(expected, string.Join("|", said.Skip(1)));
        Assert.Equal(1, dice.Used);
    }

    [Theory]
    [InlineData(2, 0, 2, true)] // dying 2, a critical failure: 4, dead
    [InlineData(2, 0, 10, false)] // a failure: 3
    [InlineData(3, 0, 10, true)] // dying 3, a failure: 4, dead
    [InlineData(2, 1, 10, true)] // doomed 1: dead at 3
    [InlineData(1, 3, 10, true)] // doomed 3: dead at 1, and a failure makes 2
    [InlineData(1, 3, 15, true)] // dying 1 is already the death value with doomed 3: no check can save it
    [InlineData(3, 1, 20, true)] // already at the death value (dying 3, doomed 1): dies, whatever the die would say
    public void RecoveryCheck_AtTheDeathValue_Kills_AndLessWhenDoomed(int dying, int doomed, int face, bool dies)
    {
        var intents = TurnStart(System, Sheet(dying, doomed), new SequenceDiceSource(new[] { face }));

        var kinds = intents.Select(i => i.Kind).ToList();
        Assert.Equal(dies, kinds.Contains("kill"));
        if (dies) Assert.Equal(new[] { "condition", "condition", "kill" }, kinds.Where(k => k != "log")); // a check is logged when one was rolled
        if (dies) Assert.All(intents.Where(i => i.Kind == "condition"), i => Assert.NotNull(i.Fields.GetValueOrDefault("remove")));
    }

    [Fact]
    public void DoomedAtFour_KillsADyingCreatureAtOnce_WithNoCheck()
    {
        var dice = new SequenceDiceSource(new[] { 20 });

        var intents = TurnStart(System, Sheet(dying: 1, doomed: 4), dice);

        Assert.Equal(new[] { "condition", "condition", "kill" }, intents.Select(i => i.Kind));
        Assert.Equal(0, dice.Used);
    }

    [Fact]
    public void ADyingCreature_RaisedByAFailedCheck_StaysDyingAtTheNewValue()
    {
        var intents = TurnStart(System, Sheet(dying: 1, doomed: 0, wounded: 2), new SequenceDiceSource(new[] { 5 }));

        Assert.Equal(new[] { "log", "condition" }, intents.Select(i => i.Kind));
        Assert.Equal(2d, YamlValues.ToNumber(intents[1].Fields["value"]));
    }

    [Fact]
    public void NotDying_RollsNothing_AndReturnsNoIntent()
    {
        var dice = new SequenceDiceSource(new[] { 10 });

        Assert.Empty(TurnStart(System, Sheet(hp: 12), dice));
        Assert.Empty(TurnStart(System, Sheet(dying: 0, doomed: 2, wounded: 1), dice));
        Assert.Equal(0, dice.Used);
    }

    [Fact]
    public void Dead_RollsNothing()
    {
        var dice = new SequenceDiceSource(new[] { 10 });

        Assert.Empty(TurnStart(System, Sheet(dying: 3, conditions: "Dead"), dice));
        Assert.Equal(0, dice.Used);
    }

    [Fact]
    public void DyingAtOneHitPointOrMore_EndsWithoutACheck_AndWoundedRises()
    {
        var dice = new SequenceDiceSource(new[] { 1 });

        var intents = TurnStart(System, Sheet(dying: 2, hp: 1), dice);

        Assert.Equal(new[] { "condition remove=Dying", "condition add=Wounded by=1" }, Said(intents));
        Assert.Equal(0, dice.Used);
    }

    [Fact]
    public void TheCheckApplied_ToTheSheet_WritesDyingAndWounded_WithinTheirMaximum()
    {
        var sheet = Sheet(dying: 1);
        SheetEvents.Raise(System, NullEntryResolver.Instance, sheet, SheetEvent.Of("turnStart"), dice: new SequenceDiceSource(new[] { 1 }));
        Assert.Equal(3L, sheet["condition_dying"]);

        SheetEvents.Raise(System, NullEntryResolver.Instance, sheet, SheetEvent.Of("turnStart"), dice: new SequenceDiceSource(new[] { 20 }));
        Assert.Equal(1L, sheet["condition_dying"]); // a critical success lowers it by 2
        var result = SheetEvents.Raise(System, NullEntryResolver.Instance, sheet, SheetEvent.Of("turnStart"), dice: new SequenceDiceSource(new[] { 20 }));

        Assert.Equal(0L, sheet["condition_dying"]);
        Assert.Equal(1L, sheet["condition_wounded"]);
        Assert.Contains("condition_wounded", result.Applied.Changed);
    }

    // ---- Damage, healing and heroic recovery (the scripts' intents; the table's own rules are in FourDegreesDyingTableTests) ----

    private static IReadOnlyList<SheetIntent> Damaged(SystemDefinition system, Dictionary<string, object> sheet, double dealt, double after, double max = 20, bool critical = false, string? save = null, bool died = false)
    {
        var fields = new List<(string, object?)> { ("dealt", dealt), ("after", after), ("max", max), ("critical", critical), ("died", died) };
        if (save != null) fields.Add(("save", save));
        return SheetEvents.Handle(system, NullEntryResolver.Instance, sheet, SheetEvent.Of("damaged", fields.ToArray()));
    }

    [Theory]
    [InlineData(0, 0, 0, false, null, "condition add=Dying value=1")] // knocked out
    [InlineData(0, 0, 0, true, null, "condition add=Dying value=2")] // a critical hit
    [InlineData(0, 0, 0, false, "criticalFailure", "condition add=Dying value=2")] // its own critical failure
    [InlineData(1, 0, 0, false, null, "condition add=Dying value=2")] // wounded 1 is added
    [InlineData(2, 0, 0, true, null, "condition remove=Dying|condition remove=Doomed|kill")] // a critical on wounded 2 is dying 4
    [InlineData(0, 2, 0, false, null, "condition add=Dying value=3")] // already dying 2: one more
    [InlineData(0, 2, 0, true, null, "condition remove=Dying|condition remove=Doomed|kill")] // a critical on dying 2 is 4
    [InlineData(0, 0, 5, false, null, "")] // still on its feet
    public void Damaged_KnocksOutOrRaisesTheDyingValue(int wounded, int dying, int after, bool critical, string? save, string expected)
    {
        var intents = Damaged(System, Sheet(dying: dying, wounded: wounded, hp: after), dealt: 4, after: after, critical: critical, save: save);

        Assert.Equal(expected, string.Join("|", Said(intents)));
    }

    [Fact]
    public void Damaged_DoomedLowersWhereItKills_AMassiveBlowKills_AndTheDeadAreLeftAlone()
    {
        Assert.Contains("kill", Damaged(System, Sheet(dying: 1, doomed: 2, hp: 0), 2, 0).Select(i => i.Kind)); // 1 + 1 = 2 = 4 - 2
        Assert.DoesNotContain("kill", Damaged(System, Sheet(hp: 0), 39, 0).Select(i => i.Kind)); // 2 x 20 less 1 is no massive blow
        var massive = Damaged(System, Sheet(hp: 5), 40, 0);
        Assert.Equal(new[] { "log", "condition", "condition", "kill" }, massive.Select(i => i.Kind));
        Assert.Empty(Damaged(System, Sheet(hp: 0, conditions: "Dead"), 40, 0));
        Assert.Empty(Damaged(System, Sheet(hp: 0), 40, 0, died: true));
    }

    [Fact]
    public void Healed_ToOneHitPointWhileDying_LosesTheCondition_AndNothingOtherwise()
    {
        var healed = SheetEvents.Handle(System, NullEntryResolver.Instance, Sheet(dying: 2, hp: 3), SheetEvent.Of("healed", ("after", 3d)));
        var well = SheetEvents.Handle(System, NullEntryResolver.Instance, Sheet(hp: 3), SheetEvent.Of("healed", ("after", 3d)));

        Assert.Equal(new[] { "condition remove=Dying", "condition add=Wounded by=1" }, Said(healed));
        Assert.Empty(well);
    }

    [Fact]
    public void HeroicRecovery_SpendsAllThePoints_AndLosesDyingWithNoWounded_OrSaysThereAreNone()
    {
        var sheet = Sheet(dying: 3, wounded: 1);
        sheet["heroPoints"] = 2L;
        var spent = SheetEvents.Handle(System, NullEntryResolver.Instance, sheet, SheetEvent.Of("heroicRecovery"));
        var none = SheetEvents.Handle(System, NullEntryResolver.Instance, Sheet(dying: 3), SheetEvent.Of("heroicRecovery"));
        var notDying = SheetEvents.Handle(System, NullEntryResolver.Instance, Sheet(), SheetEvent.Of("heroicRecovery"));

        Assert.Equal(new[] { "set track=heroPoints to=0", "condition remove=Dying", "log key=Heroic recovery" }, Said(spent));
        Assert.Equal(new[] { "log key=No hero points" }, Said(none));
        Assert.Empty(notDying);
    }

    [Fact]
    public void TheTranslationCopies_GiveTheSameIntentsForDamage()
    {
        var en = Said(Damaged(System, Sheet(hp: 0), 4, 0));

        Assert.Equal(en, Said(Damaged(System.Localize("ru"), Sheet(hp: 0), 4, 0)));
        Assert.Equal(en, Said(Damaged(System.Localize("he"), Sheet(hp: 0), 4, 0)));
    }

    // ---- Knocked out and dead ----

    private static VitalsView Creature(int hp, bool player) =>
        new(new Dictionary<string, int> { ["HP"] = hp, ["HP_Max"] = 20 }, Array.Empty<string>(), player, new Dictionary<string, object>());

    [Fact]
    public void APlayerCharacterBroughtToZero_IsKnockedOutNotDead_AndAMassiveBlowDoesNotKillIt()
    {
        var down = VitalsHandlers.Damage(System, Creature(5, true), "HP", 9, null, false);
        var massive = VitalsHandlers.Damage(System, Creature(5, true), "HP", 60, null, true);

        Assert.True(down.Report.WentDown);
        Assert.False(down.Report.Died);
        Assert.Contains(down.Intents, i => i.Kind == "condition" && i.Fields.GetValueOrDefault("add")?.ToString() == "Unconscious");
        Assert.DoesNotContain(down.Intents.Concat(massive.Intents), i => i.Kind == "kill");
        Assert.False(massive.Report.Died);
    }

    [Fact]
    public void ACreatureThatIsNotAPlayerCharacter_DiesAtZero()
    {
        var result = VitalsHandlers.Damage(System, Creature(5, false), "HP", 6, null, false);

        Assert.True(result.Report.Died);
        Assert.Contains(result.Intents, i => i.Kind == "kill");
    }

    [Fact]
    public void HealingFromZero_WakesAKnockedOutCharacter()
    {
        var view = new VitalsView(new Dictionary<string, int> { ["HP"] = 0, ["HP_Max"] = 20 }, new[] { "Unconscious" }, true, new Dictionary<string, object>());

        var healed = VitalsHandlers.Heal(System, view, "HP", 4);

        Assert.True(healed.Report.Revived);
        Assert.Contains(healed.Intents, i => i.Kind == "condition" && i.Fields.GetValueOrDefault("remove")?.ToString() == "Unconscious");
    }

    // ---- Hero points ----

    [Theory]
    [InlineData(null, 1L)]
    [InlineData(0L, 1L)]
    [InlineData(3L, 1L)]
    public void SessionStart_SetsHeroPointsToOne_FromNothingZeroOrFull(long? from, long expected)
    {
        var sheet = new Dictionary<string, object> { ["kind"] = "character" };
        if (from is { } held) sheet["heroPoints"] = held;

        SheetEvents.Raise(System, NullEntryResolver.Instance, sheet, SheetEvent.Of("sessionStart"));

        Assert.Equal(expected, sheet["heroPoints"]);
    }

    [Fact]
    public void HeroPoints_AreClampedAtThree_AndTheMomentNamesThem_AndTheLayoutHasTheTrack()
    {
        var sheet = new Dictionary<string, object> { ["kind"] = "character", ["heroPoints"] = 2L };

        SheetEvents.Raise(System, NullEntryResolver.Instance, sheet, SheetEvent.Of("adjust", ("track", "heroPoints"), ("by", 5L)));
        var effects = SystemMoments.Effects(System, sheet, "sessionStart");

        Assert.Equal(3L, sheet["heroPoints"]);
        Assert.Equal(new[] { "Hero Points" }, effects.Tracks);
        var track = Assert.Single(System.SheetKinds.Single(k => k.Kind == "character").Tracks, t => t.Key == "heroPoints");
        Assert.Equal("boxes", track.Display);
        Assert.Equal("3", track.Max?.ToString());
    }

    // ---- The path a script travels ----

    [Fact]
    public void TheTranslationCopies_ShareTheSourcesAndTheEngines_AndGiveTheSameIntents()
    {
        var ru = System.Localize("ru");
        var he = System.Localize("he");

        Assert.NotSame(System, ru);
        Assert.Same(System.Scripts, ru.Scripts);
        Assert.Same(System.Scripts, he.Scripts);
        Assert.Same(SystemScripts.For(System), SystemScripts.For(ru));
        Assert.Same(SystemScripts.For(System), SystemScripts.For(he));
        var en = Said(TurnStart(System, Sheet(dying: 1), new SequenceDiceSource(new[] { 10 })));
        Assert.Equal(en, Said(TurnStart(ru, Sheet(dying: 1), new SequenceDiceSource(new[] { 10 }))));
        Assert.Equal(en, Said(TurnStart(he, Sheet(dying: 1), new SequenceDiceSource(new[] { 10 }))));
    }

    [Fact]
    public async Task ThePackedArchive_HoldsRulesJsAndTheTypings_AndChecksCleanWithScriptsOn()
    {
        Directory.CreateDirectory(_temp);
        var zipPath = Path.Combine(_temp, "four-degrees.zip");
        await using (var output = File.Create(zipPath)) SystemArchive.Write(Folder, output);
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            Assert.Contains(names, n => n.EndsWith("rules.js", StringComparison.Ordinal));
            Assert.Contains(names, n => n.EndsWith("codex.d.ts", StringComparison.Ordinal));
        }

        await using var input = File.OpenRead(zipPath);
        var check = await SystemArchive.CheckAsync(input, Path.Combine(_temp, "unpacked"), scriptsAllowed: true);

        Assert.Empty(check.Errors);
        Assert.True(check.NamesScripts);
        Assert.True(check.Packs > 0);
        Assert.Empty(check.Warnings);
        Assert.Equal(System.Fingerprint!.Files["rules.js"], check.Fingerprint!.Files["rules.js"]);
    }

    [Fact]
    public async Task ReloadingAfterRulesJsChanges_ReportsItByName_NewDefinitionsBehaveNewly_AndOldOnesKeepTheirEngines()
    {
        var root = Path.Combine(_temp, "root");
        var copy = Path.Combine(root, Id);
        Directory.CreateDirectory(copy);
        foreach (var file in new[] { "system.yaml", "system.ru.yaml", "system.he.yaml", "rules.js" }) File.Copy(Path.Combine(Folder, file), Path.Combine(copy, file));
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(new ContentRegistry()));
        await loader.LoadAndInitializeAsync(root);
        var before = loader.GetDefinition(Id)!;
        var fingerprint = loader.GetFingerprint(Id)!;
        // Dying 2, a failure (face 10, DC 12) makes 3: alive while dead comes at 4.
        Assert.DoesNotContain("kill", TurnStart(before, Sheet(dying: 2), new SequenceDiceSource(new[] { 10 })).Select(i => i.Kind));

        var path = Path.Combine(copy, "rules.js");
        File.WriteAllText(path, File.ReadAllText(path).Replace("const DEATH = 4;", "const DEATH = 3;", StringComparison.Ordinal));
        var reload = await loader.ReloadAsync();

        Assert.Null(reload.Error);
        Assert.Equal(new[] { Id }, reload.Changed);
        var changes = loader.GetFingerprint(Id)!.ChangesSince(fingerprint);
        Assert.Equal("rules.js", Assert.Single(changes).File);
        var after = loader.GetDefinition(Id)!;
        Assert.NotSame(before.Scripts, after.Scripts);
        Assert.Contains("kill", TurnStart(after, Sheet(dying: 2), new SequenceDiceSource(new[] { 10 })).Select(i => i.Kind));
        Assert.DoesNotContain("kill", TurnStart(before, Sheet(dying: 2), new SequenceDiceSource(new[] { 10 })).Select(i => i.Kind));
    }
}
