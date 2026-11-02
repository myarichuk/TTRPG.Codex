using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The rules that were English or SRD words in the engine are the system's data now: how hit points are averaged,
/// the armor training that covers all armor, the plural a weapon's name drops, the attribute armor adds, and which fields
/// mean the level and class. Each test runs the same sheet with the setting on and off.</summary>
public class SystemWordsTests
{
    private const string Yaml = """
        id: demo
        name: Demo
        attributes:
          - { key: agi, label: Agility, abbreviation: AGI, default: 10, modifier: half-minus-five }
          - { key: con, label: Constitution, abbreviation: CON, default: 10, modifier: half-minus-five }
        resources:
          - { key: HP, label: Hit Points, maxKey: HP_Max, default: 10, isPrimary: true }
        actorFields:
          - { key: rank, label: Rank, type: Number, defaultValue: 1 }
          - { key: calling, label: Calling, type: EntityReference, targetEntityType: calling }
          - { key: armorClass, label: Armor Class, type: Number, defaultValue: 10 }
        entityTypes:
          - { type: calling, label: Calling }
          - { type: equipment, label: Equipment }
        fieldRoles: { level: rank, class: calling, summary: [rank, calling] }
        sheetRules:
          derived:
            - { key: edge, label: Edge, formula: "2 + (rank - 1) / 4" }
            - { key: quickness, label: Quickness, formula: "mod(agi) + edge" }
          hitPoints: { attribute: con, dieProperty: hitDie, default: average, classField: calling, levelField: rank }
          armorClass: { unarmored: "10 + mod(agi)", dexAttribute: agi }
          inventory: { types: [equipment] }
          proficiencies:
            classField: calling
            armorTraining: { heavy: Heavy Armor }
            weaponTag: weapons
        """;

    private sealed class Resolver : IEntryResolver
    {
        private readonly List<ContentEntry> _entries = new();

        public Resolver()
        {
            Add("calling", "warden", "Warden", p =>
            {
                p["hitDie"] = "d10";
                p["armorProficiencies"] = new List<object> { "Every armor" };
                p["weaponProficiencies"] = new List<object> { "Spears" };
            });
            Add("equipment", "mail", "Mail", p => { p["armorType"] = "heavy"; p["armorBase"] = 16L; p["armorDexCap"] = 2L; });
            Add("equipment", "spear", "Spear", _ => { }, "weapons");
        }

        private void Add(string type, string id, string name, Action<Dictionary<string, object>> fill, params string[] tags)
        {
            var entry = new ContentEntry { Type = type, Id = id, Name = name, SystemId = "demo", PackId = "p", Tags = tags.ToList() };
            fill(entry.Properties);
            _entries.Add(entry);
        }

        public ContentEntry? Resolve(string type, string id) => _entries.FirstOrDefault(e => e.Type == type && e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => _entries.Where(e => e.Type == type);
    }

    private static SystemDefinition System(Func<string, string>? change = null) => SystemDefinitionLoader.Parse((change ?? (y => y))(Yaml));

    private static SheetEvaluation Evaluate(SystemDefinition system, int rank = 1, int agi = 10, params string[] inventory)
    {
        var engine = new SheetRuleEngine(system, new Resolver());
        var sheet = new Dictionary<string, object> { ["calling"] = "warden", ["rank"] = (long)rank, ["agi"] = (long)agi, ["con"] = 10L, ["hpMode"] = "average" };
        if (inventory.Length > 0) sheet["inventory"] = inventory.Cast<object>().ToList();
        return engine.Evaluate(sheet);
    }

    [Fact]
    public void TheDemoSystem_IsValid() => Assert.Empty(SystemDefinitionLoader.Validate(System()));

    [Fact]
    public void HitPoints_ByDefault_TheFirstLevelTakesTheWholeDie_AndLaterOnesHalfPlusOne() =>
        Assert.Equal(10 + 6 + 6, Evaluate(System(), rank: 3).HitPoints!.Max);

    [Fact]
    public void HitPoints_TheSystemCanAverageTheFirstLevelToo_AndTakeHalfTheDie()
    {
        var system = System(y => y.Replace("default: average, classField", "default: average, firstLevel: average, average: half, classField"));
        Assert.Equal(5 + 5 + 5, Evaluate(system, rank: 3).HitPoints!.Max);
    }

    [Theory]
    [InlineData(HitPointRules.HalfPlusOne, 10, 6)]
    [InlineData(HitPointRules.HalfPlusOne, 8, 5)]
    [InlineData(HitPointRules.Half, 10, 5)]
    [InlineData(HitPointRules.Half, 12, 6)]
    public void AverageOf_FollowsTheRule(string rule, int die, int average) =>
        Assert.Equal(average, new HitPointRules { Average = rule }.AverageOf(die));

    [Fact]
    public void Validation_RejectsAnUnknownFirstLevelOrAverage()
    {
        var problems = Assert.Throws<InvalidDataException>(() => System(y => y.Replace("default: average, classField", "default: average, firstLevel: best, average: median, classField"))).Message;
        Assert.Contains("best", problems);
        Assert.Contains("median", problems);
    }

    [Fact]
    public void AllArmor_OnlyCoversEveryArmor_WhenTheSystemNamesTheTraining()
    {
        static bool Warned(SheetEvaluation e) => e.Warnings.Any(w => w.Key == "Actor.Rule.ArmorUntrained");

        Assert.True(Warned(Evaluate(System(), inventory: "mail|equipped:1")));
        var named = System(y => y.Replace("weaponTag: weapons", "weaponTag: weapons\n    allArmor: Every armor"));
        Assert.False(Warned(Evaluate(named, inventory: "mail|equipped:1")));
    }

    [Fact]
    public void AWeaponMatchesItsPluralTraining_OnlyWithTheSystemsPluralEnding()
    {
        static bool Warned(SheetEvaluation e) => e.Warnings.Any(w => w.Key == "Actor.Rule.WeaponUntrained");

        Assert.True(Warned(Evaluate(System(), inventory: "spear|equipped:1")));
        var plural = System(y => y.Replace("weaponTag: weapons", "weaponTag: weapons\n    pluralSuffix: s"));
        Assert.False(Warned(Evaluate(plural, inventory: "spear|equipped:1")));
    }

    [Fact]
    public void ArmorClass_NamesTheAttributeArmorAdds_AsTheSystemCallsIt()
    {
        var line = Evaluate(System(), agi: 14, inventory: "mail|equipped:1").ArmorClass!.Parts.Single(l => l.Kind == "dex");
        Assert.Equal("Agility", line.Label);
        Assert.Equal(2, line.Value);
    }

    [Fact]
    public void FieldRoles_MustNameActorFields()
    {
        var problems = Assert.Throws<InvalidDataException>(() => System(y => y.Replace("summary: [rank, calling]", "summary: [rank, species]"))).Message;
        Assert.Contains("species", problems);
    }

    [Fact]
    public void FieldRoles_AreReadFromTheYaml_AndAreEmptyWhenLeftOut()
    {
        var roles = System().FieldRoles;
        Assert.Equal("rank", roles.Level);
        Assert.Equal("calling", roles.Class);
        Assert.Null(roles.Speed);
        Assert.Equal(new[] { "rank", "calling" }, roles.Summary);
        Assert.Null(System(y => y.Replace("fieldRoles: { level: rank, class: calling, summary: [rank, calling] }\n", "")).FieldRoles.Level);
    }

    [Fact]
    public void TheShippedSystem_NamesItsRolesAndWords()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        Assert.Empty(SystemDefinitionLoader.Validate(system));
        Assert.Equal("level", system.FieldRoles.Level);
        Assert.Equal("speed", system.FieldRoles.Speed);
        Assert.Equal("All armor", system.SheetRules!.Proficiencies!.AllArmor);
        Assert.Equal("s", system.SheetRules.Proficiencies.PluralSuffix);
        Assert.Equal("spell slot", system.SheetRules.SlotPools.Single(p => p.Key == "slots").Unit);
        Assert.Equal("תא לחשים", system.Localize("he").DisplayValue("spell slot"));
        Assert.Equal("Ячейки договора", system.Localize("ru").DisplayValue("Pact slots"));
    }

    [Fact]
    public void ADerivedValuesSum_ShowsTheNumbersItsFormulaRead()
    {
        var evaluation = Evaluate(System(), rank: 5, agi: 15);
        string Working(string key, string formula) =>
            FormulaWorking.Write(formula, evaluation.DerivedInputs[key], k => k == "agi" ? "AGI" : null);

        Assert.Equal(3L, evaluation.Derived["edge"]);
        Assert.Equal("2 + (5 - 1) / 4", Working("edge", "2 + (rank - 1) / 4"));
        Assert.Equal("AGI +2 + 3", Working("quickness", "mod(agi) + edge"));
        Assert.Equal("AGI -1", FormulaWorking.Write("mod(agi)", new Dictionary<string, double> { ["agi"] = 8 }, _ => "AGI"));
    }

    [Fact]
    public void AnAttributeTheSystemGainedLater_ReachesASheetMadeBefore_AtItsDefault()
    {
        // Made when the system had only Agility; Constitution was added to system.yaml since (F24).
        var sheet = new Dictionary<string, object> { ["base_agi"] = 14L, ["agi"] = 14L };
        var system = System(y => y.Replace("  derived:", "  attributes: { methods: [manual] }\n  derived:"));
        new SheetRuleEngine(system, new Resolver()).ApplyAttributeTotals(sheet);

        Assert.Equal(10L, sheet["base_con"]);
        Assert.Equal(10L, sheet["con"]);
        Assert.Equal(14L, sheet["agi"]);

        // A sheet that never kept base scores is left as it is.
        var manual = new Dictionary<string, object> { ["agi"] = 12L };
        new SheetRuleEngine(system, new Resolver()).ApplyAttributeTotals(manual);
        Assert.False(manual.ContainsKey("con"));
    }

    [Fact]
    public void TheShippedSystem_NamesThePlatesShortWords_InEachLanguage()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems", "srd5e", "system.yaml"));
        FieldDefinition Field(SystemDefinition s, string key) => s.ActorFields.Single(f => f.Key == key);

        Assert.Equal("AC", Field(system, "armorClass").Abbreviation);
        Assert.Equal("ft", Field(system, "speed").Unit);
        Assert.Equal("КБ", Field(system.Localize("ru"), "armorClass").Abbreviation);
        Assert.Equal("фт", Field(system.Localize("ru"), "speed").Unit);
        Assert.Equal("רגל", Field(system.Localize("he"), "speed").Unit);
        // Hebrew names Armor Class but gives no short form, so the plate shows the whole label.
        Assert.Null(Field(system.Localize("he"), "armorClass").Abbreviation);
    }
}
