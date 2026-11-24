using System.Globalization;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>
/// The helper the combat console and a prepared encounter's start share (ENCOUNTER_BUILDER_DESIGN.md 4.2). Each case asserts that <see cref="TableInitiative"/> gives
/// exactly what the console's own code gave before the helper existed; <see cref="Old"/> is that code, copied, and is the definition of "the console's old numbers".
/// </summary>
public class TableInitiativeTests
{
    private static readonly SystemDefinition Srd = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
    private static readonly SystemDefinition FourDegrees = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "four-degrees", "system.yaml"));

    /// <summary>What the console did before: the same bodies, over a stored sheet.</summary>
    private static class Old
    {
        public static int Bonus(SystemDefinition? definition, SheetRuleEngine? engine, Dictionary<string, object> liveSheet)
        {
            if (engine == null || definition?.Dice.InitiativeBonus is not { } key) return 0;
            try
            {
                var evaluation = engine.EvaluateLive(liveSheet, null);
                return evaluation.Derived.TryGetValue(key, out var value) ? Convert.ToInt32(value) : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static string Text(int bonus) => bonus == 0 ? "" : bonus > 0 ? $" + {bonus}" : $" - {-bonus}";

        public static int TieBreak(SystemDefinition? definition, Dictionary<string, object> sheet)
        {
            if (definition?.Dice.InitiativeTieBreak is not { } key) return 0;
            return YamlValues.GetInt(sheet, key) ?? definition.Attributes.FirstOrDefault(a => a.Key == key)?.Default ?? 0;
        }

        public static string Expression(string die, int bonus) => die + (bonus == 0 ? "" : bonus > 0 ? $"+{bonus}" : bonus.ToString());
    }

    private static Dictionary<string, object> Sheet(long? dex) => dex is { } d ? new Dictionary<string, object> { ["dex"] = d } : new Dictionary<string, object>();

    private static T Invariant<T>(Func<T> work)
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return work(); }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void Bonus_IsTheSystemsDerivedField_AsTheConsoleWorkedItOut()
    {
        var engine = new SheetRuleEngine(Srd, NullEntryResolver.Instance);
        var bonuses = new Dictionary<long, int>();
        foreach (var dex in new long[] { 14, 8, 10 })
        {
            var oracle = Invariant(() => Old.Bonus(Srd, engine, Sheet(dex)));
            var actual = TableInitiative.Bonus(Srd, engine, () => Sheet(dex));

            Assert.Equal(oracle, actual);
            Assert.Equal(Invariant(() => Old.Expression("1d20", oracle)), Invariant(() => TableInitiative.Expression("1d20", actual)));
            Assert.Equal(Old.Text(oracle), TableInitiative.BonusText(actual));
            bonuses[dex] = actual;
        }

        // Both could answer 0 (the rules failing on a bare resolver) and pin nothing: they do not.
        Assert.NotEqual(0, bonuses[14]);
        Assert.NotEqual(bonuses[14], bonuses[8]);
    }

    [Fact]
    public void TieBreak_ReadsTheStoredAttribute_ThenItsDefault()
    {
        Assert.Equal(13, TableInitiative.TieBreak(Srd, Sheet(13)));
        Assert.Equal(Old.TieBreak(Srd, Sheet(13)), TableInitiative.TieBreak(Srd, Sheet(13)));

        var fallback = TableInitiative.TieBreak(Srd, Sheet(null));
        Assert.Equal(Srd.Attributes.First(a => a.Key == "dex").Default, fallback);
        Assert.Equal(Old.TieBreak(Srd, Sheet(null)), fallback);
    }

    [Fact]
    public void ASystemThatNamesNoBonusOrTieBreak_RollsTheBareDie()
    {
        var engine = new SheetRuleEngine(FourDegrees, NullEntryResolver.Instance);

        Assert.Equal(0, TableInitiative.Bonus(FourDegrees, engine, () => Sheet(14)));
        Assert.Equal(0, TableInitiative.TieBreak(FourDegrees, Sheet(14)));
        Assert.Equal("1d20", TableInitiative.Expression(TableInitiative.Die(FourDegrees), 0));
        Assert.Equal("1d20", TableInitiative.Die(null));
    }

    [Fact]
    public void NoEngine_OrRulesThatFail_GiveZero()
    {
        var asked = false;
        Assert.Equal(0, TableInitiative.Bonus(Srd, null, () => { asked = true; return Sheet(14); }));
        Assert.False(asked); // no engine: the sheet is not even built, as the console never built it

        // A sheet that cannot be laid out (or linked) gives no bonus, as it did inside the console's own try.
        var engine = new SheetRuleEngine(Srd, NullEntryResolver.Instance);
        Assert.Equal(0, TableInitiative.Bonus(Srd, engine, () => throw new InvalidOperationException("broken sheet")));
        Assert.Equal(0, TableInitiative.Bonus(Srd, engine, () => Sheet(14), () => throw new InvalidOperationException("broken link")));

        // A bonus field the rules do not derive.
        var odd = new SystemDefinition { Id = "odd", Name = "Odd", Dice = new DiceConventions { InitiativeBonus = "nowhere-to-be-found" } };
        Assert.Equal(0, TableInitiative.Bonus(odd, new SheetRuleEngine(Srd, NullEntryResolver.Instance), () => Sheet(14)));
    }

    [Fact]
    public void Expression_KeepsAdvantageModes()
    {
        Assert.Equal("adv+2", TableInitiative.Expression("adv", 2));
        Assert.Equal("dis-1", TableInitiative.Expression("dis", -1));
        Assert.Equal("1d20", TableInitiative.Expression("1d20", 0));
        Assert.Equal(" + 2", TableInitiative.BonusText(2));
        Assert.Equal(" - 1", TableInitiative.BonusText(-1));
        Assert.Equal("", TableInitiative.BonusText(0));
    }

    [Fact]
    public void ANegativeBonus_IsWrittenTheSameInEveryCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            // The Hebrew culture's negative sign carries a direction mark under ICU; the expression must not.
            CultureInfo.CurrentCulture = new CultureInfo("he-IL");
            Assert.Equal("1d20-1", TableInitiative.Expression("1d20", -1));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
