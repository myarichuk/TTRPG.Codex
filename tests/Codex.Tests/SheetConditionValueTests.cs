using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>C1 of docs/SHEET_CONDITIONS_DESIGN.md: valued conditions and spell effects parse and are checked at load, Exhaustion's value
/// comes from <c>vitals.exhaustion</c>, and <see cref="SheetConditions.Of"/> says which conditions hold.</summary>
public class SheetConditionValueTests
{
    private const string Frightened = """
        conditionEffects:
          Frightened:
            value: { max: 4, decrease: { on: turnEnd, by: 1 } }
            modifiers:
              - { target: attack, amount: "-value" }
        """;

    private static SystemDefinition Parse(string body, string head = "") =>
        SystemDefinitionLoader.Parse("id: cond-test\nname: Cond test\n" + head + "\n" + body);

    private static IReadOnlyList<string> Errors(string body, string head = "") =>
        SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => Parse(body, head)));

    [Fact]
    public void AValue_ParsesKeyMaxAndDecrease()
    {
        var value = Parse("""
            conditionEffects:
              Frightened:
                value: { key: fear, max: "level / 2", decrease: { on: turnStart, by: 2 } }
            """, "attributes: [{ key: level }]").ConditionEffects["Frightened"].Value!;

        Assert.Equal("fear", value.Key);
        Assert.Equal("level / 2", value.Max);
        Assert.Equal("turnStart", value.Decrease!.On);
        Assert.Equal(2, value.Decrease.By);
    }

    [Fact]
    public void TheKey_DefaultsToConditionAndTheSlug_AndByDefaultsToOne()
    {
        var system = Parse(Frightened + "\n  \"Half Cover\": { value: { max: 3 } }");

        var valued = SheetConditions.Valued(system).ToList();

        Assert.Equal(new[] { "condition_frightened", "condition_half_cover" }, valued.Select(v => v.Key));
        Assert.Null(system.ConditionEffects["Frightened"].Value!.Key);
        Assert.Equal(1, system.ConditionEffects["Frightened"].Value!.Decrease!.By);
        Assert.Null(system.ConditionEffects["Half Cover"].Value!.Decrease);
    }

    [Fact]
    public void AClashingKey_IsRefused()
    {
        Assert.Contains(Errors("""
            conditionEffects:
              Frightened: { value: { key: hp } }
            """, "resources: [{ key: hp }]"), e => e.Contains("conditionEffects.Frightened.value") && e.Contains("clashes with the resource 'hp'"));
        Assert.Contains(Errors("""
            conditionEffects:
              Frightened: { value: { key: stress } }
            """, "sheetKinds:\n  - kind: hero\n    tracks: [{ key: stress, max: 9 }]"), e => e.Contains("clashes with the track 'stress'"));
        Assert.Contains(Errors("""
            conditionEffects:
              Frightened: { value: { key: fear } }
              Sickened: { value: { key: fear } }
            """), e => e.Contains("also the value of 'Frightened'"));
    }

    [Fact]
    public void ABadMax_IsRefused()
    {
        var errors = Errors("conditionEffects:\n  Frightened: { value: { max: \"2 +\" } }");

        Assert.Contains(errors, e => e.Contains("conditionEffects.Frightened.value.max '2 +' cannot be read"));
    }

    [Fact]
    public void AnUnknownDecreaseMoment_IsRefused_AndARestOrADeclaredMomentIsKnown()
    {
        var errors = Errors("conditionEffects:\n  Frightened: { value: { decrease: { on: nap } } }");
        Assert.Contains(errors, e => e.Contains("decrease.on 'nap'"));

        var known = Parse("""
            conditionEffects:
              Frightened: { value: { decrease: { on: Long Rest } } }
              Sickened: { value: { decrease: { on: downtime } } }
            """, "moments: [{ key: downtime }]\nvitals:\n  rests: [{ name: Long Rest }]");
        Assert.Equal(2, known.ConditionEffects.Count);
    }

    [Theory]
    [InlineData("sheetRules:\n  derived: [{ key: value, label: V, formula: \"1\" }]")]
    [InlineData("actorFields: [{ key: value, label: V, type: Number }]")]
    [InlineData("sheetKinds:\n  - kind: hero\n    values: [{ key: value, formula: \"1\" }]")]
    public void ASystemWithSomethingNamedValue_IsRefused(string head)
    {
        Assert.Contains(Errors("", head), e => e.Contains("'value'") && e.Contains("the condition's own value"));
    }

    [Fact]
    public void Exhaustion_GetsItsKeyAndMaximumFromVitals()
    {
        const string vitals = "vitals:\n  exhaustion: { key: Exhaustion, maxLevel: 6, condition: Exhaustion }";
        var system = Parse("""
            conditionEffects:
              Exhaustion:
                modifiers:
                  - { target: attack, amount: "-2 * value" }
            """, vitals);

        var value = system.ConditionEffects["Exhaustion"].Value!;
        Assert.Equal("Exhaustion", value.Key);
        Assert.Equal("6", value.Max);
        Assert.Empty(SystemDefinitionLoader.Validate(system)); // checking a parsed system again does not take the folded value for a written one
        Assert.Equal(("Exhaustion", "Exhaustion"), (SheetConditions.Valued(system).Single().Condition, SheetConditions.Valued(system).Single().Key));

        // srd5e declares the same vitals, and its own Exhaustion entry writes only the rest decrease: the key and maximum are folded in from them.
        var srd5e = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        Assert.Equal("Exhaustion", srd5e.Vitals!.Exhaustion!.Key);
        Assert.Equal(6, srd5e.Vitals.Exhaustion.MaxLevel);
        var srdValue = srd5e.ConditionEffects["Exhaustion"].Value!;
        Assert.Equal(("Exhaustion", "6"), (srdValue.Key, srdValue.Max));
    }

    private const string ExhaustionVitals = "vitals:\n  exhaustion: { key: Exhaustion, maxLevel: 6, condition: Exhaustion }\n  rests: [{ name: Long Rest }]";

    [Theory]
    [InlineData("{ key: Lvl }")]
    [InlineData("{ max: 6 }")]
    [InlineData("{ key: Lvl, max: 6, decrease: { on: Long Rest } }")]
    public void AKeyOrMaxWrittenOnTheExhaustionEffect_IsRefused(string value)
    {
        var errors = Errors($"conditionEffects:\n  Exhaustion: {{ value: {value} }}", ExhaustionVitals);

        Assert.Contains(errors, e => e.Contains("come from vitals.exhaustion") && e.Contains("write only decrease"));
    }

    [Fact]
    public void ADecreaseAloneOnTheExhaustionEffect_IsAccepted_AndFolded()
    {
        var system = Parse("conditionEffects:\n  Exhaustion: { value: { decrease: { on: Long Rest, by: 1 } } }", ExhaustionVitals);

        var value = system.ConditionEffects["Exhaustion"].Value!;
        Assert.Equal("Exhaustion", value.Key);
        Assert.Equal("6", value.Max);
        Assert.Equal("Long Rest", value.Decrease!.On);
        Assert.Equal(1, value.Decrease.By);
        Assert.Empty(SystemDefinitionLoader.Validate(system));

        // A decrease at a moment the system does not know is still refused there.
        Assert.Contains(Errors("conditionEffects:\n  Exhaustion: { value: { decrease: { on: nap } } }", ExhaustionVitals), e => e.Contains("decrease.on 'nap'"));
    }

    [Fact]
    public void AnEffectsList_ParsesIsNameableFromConditionEffects_AndIsLocalized()
    {
        var system = Parse("""
            effects: [Bless, Guided]
            conditionEffects:
              Bless:
                modifiers:
                  - { target: attack, amount: 1d4 }
              Guided:
                modifiers:
                  - { target: check, amount: 1d4, optIn: { label: Guidance, ends: true } }
            """, "conditions: [Poisoned]");
        system.Translations["ru"] = new SystemTranslation { Conditions = { ["Bless"] = "Благословение", ["Poisoned"] = "Отравлен" } };

        Assert.Equal(new[] { "Bless", "Guided" }, system.Effects);
        Assert.Equal(new[] { "Poisoned" }, system.Conditions);
        Assert.Equal(2, system.ConditionEffects.Count);
        Assert.Equal("Благословение", system.Localize("ru").DisplayCondition("ru", "Bless"));
        Assert.Equal("Guided", system.Localize("ru").DisplayCondition("ru", "Guided"));
    }

    [Fact]
    public void ANameInBothLists_IsRefused()
    {
        Assert.Contains(Errors("effects: [Bless]", "conditions: [Bless]"), e => e.Contains("both a condition and an effect"));
    }

    [Fact]
    public void Of_GivesTheSheetsConditions_PlusAValuedOneAboveZero()
    {
        var system = Parse(Frightened);
        var sheet = new Dictionary<string, object>
        {
            [SheetModel.ConditionsKey] = new List<object> { "Hasted" },
            ["condition_frightened"] = 2L
        };

        Assert.Equal(new[] { "Hasted", "Frightened" }, SheetConditions.Of(system, sheet));

        sheet["condition_frightened"] = 0L;
        Assert.Equal(new[] { "Hasted" }, SheetConditions.Of(system, sheet));

        sheet.Remove("condition_frightened");
        Assert.Equal(new[] { "Hasted" }, SheetConditions.Of(system, sheet));

        // Listed on the sheet as well as valued: said once.
        sheet[SheetModel.ConditionsKey] = new List<object> { "frightened" };
        sheet["condition_frightened"] = 1L;
        Assert.Single(SheetConditions.Of(system, sheet));
    }
}
