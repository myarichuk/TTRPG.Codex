using Codex.Core;
using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// P11a of docs/COMPANIONS_DESIGN.md, the engine under a creature that a player owns and that is not a character: <c>sheetRules.kinds</c> (the rules
/// run on the kinds they name; another kind runs its own model), the conditions that go to the kind's values on such a sheet, <c>link()</c> reading what
/// the character rules work out, and <c>partyMember</c>. The system is a small invented one; the shipped system's own data is not read, except to check
/// that it still loads.
/// </summary>
public class CompanionKindsEngineTests
{
    private const string Yaml = """
        id: tamers
        name: Tamers
        attributes:
          - { key: str, label: Strength, abbreviation: STR, default: 10, modifier: half-minus-five }
          - { key: dex, label: Dexterity, abbreviation: DEX, default: 10, modifier: half-minus-five }
          - { key: wis, label: Wisdom, abbreviation: WIS, default: 10, modifier: half-minus-five }
        actorFields:
          - { key: level, label: Level, type: Number, defaultValue: 1 }
          - { key: class, label: Class, type: EntityReference, targetEntityType: class }
          - { key: armorClass, label: Armor Class, type: Number, defaultValue: 10 }
          - { key: ancestry, label: Ancestry, type: EntityReference, targetEntityType: ancestry }
        entityTypes:
          - { type: class, label: Class }
          - { type: ancestry, label: Ancestry }
          - { type: feat, label: Feat }
          - { type: trick, label: Trick }
        moments:
          - { key: sessionStart, label: Session start }
          - { key: dailyPrep, label: Daily preparations }
        events:
          sessionStart:
            - { intents: [{ set: { track: heroPoints, to: 1 } }] }
        conditionEffects:
          Shaken:
            value: {}
            modifiers:
              - { target: armorClass, amount: "-value", type: status }
              - { target: ac, amount: "-value", type: status }
              - { target: bite, amount: "-value", type: status }
              - { target: nothingHere, amount: "-value", type: status }
        sheetRules:
          enforcement: warn
          kinds: [character]
          constraints:
            - field: class
              where:
                - { property: ancestry, equalsField: ancestry, message: "that class belongs to another ancestry." }
          hitPoints:
            key: HP_Max
            currentKey: HP
            flatProperty: hp
            attribute: str
          attributes:
            methods: [manual, boosts]
            default: manual
            boosts:
              label: Boosts
              sources:
                - { key: lvl1, label: Level 1, level: 1, free: 2 }
          lists:
            - { key: feats, label: Feats, targetEntityType: feat }
          features:
            - { list: feats }
          skills:
            list:
              - { key: climb, label: Climb, attribute: str }
            ranks:
              - { key: trained, label: Trained, sheetKey: skillTrained, value: "level + 2" }
              - { key: expert, label: Expert, sheetKey: skillExpert, value: "level + 4" }
            saves:
              ranks:
                - { key: trained, label: Trained, sheetKey: saveTrained, value: "level + 2" }
          ranked:
            - { key: perception, label: Perception, ranksOf: skills, attribute: wis, publish: true }
          derived:
            - { key: bond, label: Bond, formula: "level * 2" }
          armorClass:
            key: armorClass
            unarmored: "10 + mod(dex)"
            dexAttribute: dex
        sheetKinds:
          - kind: character
            label: Character
            role: player
            tracks:
              - { key: heroPoints, label: Hero Points, max: 3, display: boxes }
          - kind: companion
            label: Companion
            role: player
            partyMember: false
            links:
              - { key: owner, kind: character, label: Owner }
            values:
              - { key: level, label: Level, formula: "link('owner', 'level')" }
              - { key: ac, label: AC, formula: "10 + link('owner', 'level')", modifiers: ac }
              - { key: bite, label: Bite, formula: "3 + link('owner', 'level')", modifiers: bite }
              - { key: ownerAc, label: Owner AC, formula: "link('owner', 'armorClass')" }
              - { key: ownerDexSave, label: Owner save, formula: "link('owner', 'saves.dex')" }
              - { key: ownerPerception, label: Owner Perception, formula: "link('owner', 'perception')" }
              - { key: ownerBond, label: Owner bond, formula: "link('owner', 'bond')" }
              - { key: pair, label: Pair, formula: "link('owner', 'saves.dex') + link('owner', 'skills.climb')" }
              - { key: sandwich, label: Sandwich, formula: "link('owner', 'skills.climb') + bite + link('owner', 'perception')" }
              - { key: trio, label: Trio, formula: "link('owner', 'saves.dex') + link('owner', 'skills.climb') + bite" }
            tracks:
              - { key: heroPoints, label: Hero Points, max: 3, display: boxes }
        """;

    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse(Yaml);

    private static ContentEntry Entry(string type, string id, params (string Key, object Value)[] properties)
    {
        var entry = new ContentEntry { Type = type, Id = id, Name = id };
        foreach (var (key, value) in properties) entry.Properties[key] = value;
        return entry;
    }

    /// <summary>A class, an ancestry and a feat the character rules can read; "boom" is a class whose lookup throws, and the class lookups are counted.</summary>
    private sealed class Entries : IEntryResolver
    {
        private readonly ContentEntry[] _all =
        {
            Entry("class", "scout", ("hp", 8L), ("ancestry", "elf")),
            Entry("ancestry", "dune"),
            Entry("feat", "sharp", ("modifiers", new List<object> { new Dictionary<string, object> { ["target"] = "armorClass", ["amount"] = "1", ["type"] = "item" } }))
        };

        public int ClassLookups { get; private set; }

        public ContentEntry? Resolve(string type, string id)
        {
            if (type == "class")
            {
                ClassLookups++;
                if (id == "boom") throw new InvalidOperationException("the content cannot be read");
            }

            return _all.FirstOrDefault(e => e.Type == type && e.Id == id);
        }

        public IEnumerable<ContentEntry> Candidates(string type) => _all.Where(e => e.Type == type);
    }

    private sealed class Actors(params (string Id, Dictionary<string, object> Sheet)[] sheets) : ILinkedSheets
    {
        public IReadOnlyDictionary<string, object>? Sheet(string actorId) => sheets.FirstOrDefault(s => s.Id == actorId).Sheet;
    }

    private static SheetRuleEngine Engine(SystemDefinition? system = null) => new(system ?? System, new Entries());

    private static Dictionary<string, object> Owner(string? kind = "character", long level = 5, long dex = 14, long wis = 12) =>
        Sheet(kind, ("level", level), ("str", 12L), ("dex", dex), ("wis", wis), ("acMode", "auto"), ("skillTrained", new List<object> { "climb" }));

    private static Dictionary<string, object> Sheet(string? kind, params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object>();
        if (kind != null) sheet["kind"] = kind;
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    private static Dictionary<string, object> Companion(params (string Key, object Value)[] more) =>
        Sheet("companion", more.Append(("link__owner", "owner")).ToArray());

    private static double Number(SheetEvaluation evaluation, string key) => Convert.ToDouble(evaluation.Derived[key], global::System.Globalization.CultureInfo.InvariantCulture);

    private static string[] Errors(string yaml) => SystemDefinitionLoader.ErrorsOf(Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(yaml))).ToArray();

    // ---- Load ----

    [Fact]
    public void TheFixtureLoadsClean()
    {
        Assert.Empty(SystemDefinitionLoader.Validate(System));
        Assert.Equal(new[] { "character" }, System.SheetRules!.Kinds);
        Assert.False(System.SheetKinds.Single(k => k.Kind == "companion").PartyMember);
        Assert.True(System.SheetKinds.Single(k => k.Kind == "character").PartyMember);
    }

    [Fact]
    public void SheetRulesKinds_NamingAnUnknownKind_IsRefused()
    {
        var errors = Errors(Yaml.Replace("kinds: [character]", "kinds: [character, ghost]", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("sheetRules.kinds: 'ghost' is not one of the system's sheet kinds"));
    }

    [Fact]
    public void ALinkNameTheRuleEngineKnows_Loads_AndAnUnknownOne_IsRefused()
    {
        // saves.dex, skills.climb, perception, bond (derived), armorClass (stored by the rules) and level are all in the fixture and load.
        Assert.Empty(SystemDefinitionLoader.Validate(System));
        var errors = Errors(Yaml.Replace("link('owner', 'bond')", "link('owner', 'bnod')", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("link('owner', 'bnod')") && e.Contains("always read 0"));
    }

    [Fact]
    public void ALinkNameOfAKindTheRulesDoNotApplyTo_IsNotKnownFromTheRules()
    {
        // The owner kind is not a rules kind here, so 'perception' (a rules value) is not one of its names; its own values and the system's fields are.
        var yaml = Yaml.Replace("kinds: [character]", "kinds: [companion]", StringComparison.Ordinal);
        var errors = Errors(yaml);
        Assert.Contains(errors, e => e.Contains("link('owner', 'perception')"));
        Assert.DoesNotContain(errors, e => e.Contains("link('owner', 'level')"));
    }

    [Fact]
    public void WithoutKinds_TheRulesApplyToEveryKind_AndTheCompanionsOwnArmorClassClashesWithThem()
    {
        var every = Yaml.Replace("  kinds: [character]\n", string.Empty, StringComparison.Ordinal);
        var system = SystemDefinitionLoader.Parse(every);
        Assert.Empty(system.SheetRules!.Kinds);
        Assert.True(SheetRuleEngine.AppliesTo(system, system.SheetKinds[1]));

        var clash = every.Replace("{ key: ac, label: AC,", "{ key: armorClass, label: AC,", StringComparison.Ordinal).Replace("modifiers: ac }", "modifiers: armorClass }", StringComparison.Ordinal);
        Assert.Contains(Errors(clash), e => e.Contains("'armorClass' is claimed by both sheetRules and a sheet kind"));
    }

    [Fact]
    public void TheBothClaimRule_ChecksOnlyTheKindsTheRulesApplyTo()
    {
        var clash = Yaml.Replace("{ key: ac, label: AC,", "{ key: armorClass, label: AC,", StringComparison.Ordinal).Replace("modifiers: ac }", "modifiers: armorClass }", StringComparison.Ordinal);
        Assert.Empty(SystemDefinitionLoader.Validate(SystemDefinitionLoader.Parse(clash)));

        // The same name on the kind the rules apply to is still refused.
        var onCharacter = Yaml.Replace("  - kind: character\n    label: Character\n    role: player\n",
            "  - kind: character\n    label: Character\n    role: player\n    values:\n      - { key: armorClass, formula: \"1\" }\n", StringComparison.Ordinal);
        Assert.Contains(Errors(onCharacter), e => e.Contains("'armorClass' is claimed by both sheetRules and a sheet kind"));
    }

    // ---- The shipped system ----

    private sealed class Shipped : IAsyncLifetime
    {
        public SystemDefinition? System { get; private set; }

        public async Task InitializeAsync()
        {
            var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(new ContentRegistry()));
            await loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
            System = loader.GetDefinition("four-degrees");
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task FourDegrees_LoadsWithItsRulesLimitedToTheCharacter_AndAHypotheticalCompanionValueNamedAc()
    {
        var shipped = new Shipped();
        await shipped.InitializeAsync();
        var system = shipped.System!;
        Assert.Equal(new[] { "character" }, system.SheetRules!.Kinds);
        Assert.Empty(SystemDefinitionLoader.Validate(system));

        system.SheetKinds.Add(new SheetKindDefinition
        {
            Kind = "companion",
            PartyMember = false,
            Values = { new ValueDefinition { Key = "ac", Formula = "10", Modifiers = "ac" }, new ValueDefinition { Key = "armorClass", Formula = "10", Modifiers = "armorClass" } }
        });
        Assert.Empty(SystemDefinitionLoader.Validate(system)); // frightened aims at armorClass; the companion's own is no clash

        system.SheetRules.Kinds.Clear();
        Assert.Contains(SystemDefinitionLoader.Validate(system), e => e.Contains("'armorClass' is claimed by both sheetRules and a sheet kind"));
    }

    // ---- Gap 1: the character rules on the kinds they name ----

    [Fact]
    public void ASheetWithNoKindKey_IsTheFirstKind_AndStillGetsTheCharacterRules()
    {
        var stored = Owner(kind: null);
        Assert.True(SheetRuleEngine.AppliesTo(System, stored));

        var evaluation = Engine().Recompute(stored);

        Assert.Equal(10, Number(evaluation, "bond"));
        Assert.Equal(12L, Convert.ToInt64(stored["armorClass"]));
        Assert.NotNull(evaluation.Model);
        Assert.Contains("climb", evaluation.Skills.Select(s => s.Key));
    }

    [Fact]
    public void ACharacterSheetIsUnchanged_ByTheKindsList()
    {
        var withKinds = Owner();
        var withoutKinds = Owner();
        var every = SystemDefinitionLoader.Parse(Yaml.Replace("  kinds: [character]\n", string.Empty, StringComparison.Ordinal));

        var a = Engine().Recompute(withKinds);
        var b = Engine(every).Recompute(withoutKinds);

        Assert.Equal(b.Skills.Select(s => (s.Key, s.Bonus)), a.Skills.Select(s => (s.Key, s.Bonus)));
        Assert.Equal(b.SavingThrows.Select(s => (s.Key, s.Bonus)), a.SavingThrows.Select(s => (s.Key, s.Bonus)));
        Assert.Equal(b.Derived.OrderBy(d => d.Key).Select(d => (d.Key, d.Value)), a.Derived.OrderBy(d => d.Key).Select(d => (d.Key, d.Value)));
        Assert.Equal(withoutKinds.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.ToString())), withKinds.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.ToString())));
    }

    public void ACompanionSheet_GetsNoAncestryClassBoostsOrCharacterHitPoints_NoDerivedValues_AndKeepsItsOwnModel()
    {
        // The same keys on both: a class and an ancestry that clash under the constraint, boosts picked, a feat that gives armor class, and a level.
        (string, object)[] build =
        {
            ("level", 3L), ("class", "scout"), ("ancestry", "dune"), (SheetRuleEngine.MethodKey, "boosts"),
            ("boost__lvl1__m1", new[] { "str" }), ("boost__lvl1__m2", new[] { "dex" }), ("feats", new List<object> { "sharp" })
        };
        var character = Sheet("character", build);
        var characterEvaluation = Engine().Recompute(character);
        // Without the kinds list the character would be the only proof the fixture is a build: it has hit points, boosted attributes, a violation and the feat's number.
        Assert.True(Convert.ToInt64(character["HP_Max"]) > 0);
        Assert.True(character.ContainsKey("str") && character.ContainsKey("dex"));
        Assert.NotEmpty(characterEvaluation.Violations);
        var withoutFeat = Engine().Recompute(Sheet("character", build.Where(b => b.Item1 != "feats").ToArray()));
        Assert.Equal(withoutFeat.ArmorClass!.Total + 1, characterEvaluation.ArmorClass!.Total);

        var sheet = Companion(build);
        var before = sheet.ToDictionary(p => p.Key, p => p.Value);

        var evaluation = Engine().Recompute(sheet, new Actors(("owner", Owner())));

        Assert.Empty(evaluation.Skills);
        Assert.Empty(evaluation.SavingThrows);
        Assert.Empty(evaluation.Classes);
        Assert.Null(evaluation.HitPoints);
        Assert.Null(evaluation.ArmorClass);
        Assert.Empty(evaluation.Violations); // the class/ancestry constraint is the character's
        Assert.DoesNotContain("bond", evaluation.Derived.Keys);
        Assert.DoesNotContain("perception", evaluation.Derived.Keys);
        Assert.False(sheet.ContainsKey("armorClass") || sheet.ContainsKey("HP_Max") || sheet.ContainsKey("HP") || sheet.ContainsKey("str") || sheet.ContainsKey("dex"));
        Assert.Equal(15, Number(evaluation, "ac")); // the kind's own values still work out, with no feat's bonus
        Assert.Equal(before.Keys.Order(), sheet.Keys.Where(k => !k.EndsWith("_Max", StringComparison.Ordinal)).Order());
        Assert.Equal("boosts", sheet[SheetRuleEngine.MethodKey]); // left as the sheet stored it, not reset by InitializeSheet or the attribute pass
    }

    [Fact]
    public void WhateverTheRulesWouldAddToAnActorFieldOrList_IsNotDoneToACompanion()
    {
        var sheet = Companion();
        var engine = Engine();
        engine.InitializeSheet(sheet);
        engine.ApplyAttributeTotals(sheet);
        engine.ApplyGranted(sheet);
        engine.ApplyInherited(sheet);
        Assert.False(sheet.ContainsKey(SheetRuleEngine.MethodKey));
        Assert.False(sheet.ContainsKey("acMode"));
        Assert.False(sheet.ContainsKey("dex"));
    }

    // ---- Conditions ----

    [Fact]
    public void ACondition_OnACompanion_GoesToTheKindsValuesOnce_AndToTheCharacterThroughTheRules()
    {
        var owners = new Actors(("owner", Owner()));
        var companion = Companion(("condition_shaken", 2L));
        var plain = Companion();

        var shaken = Engine().Evaluate(companion, owners);
        var calm = Engine().Evaluate(plain, owners);
        Assert.Equal(Number(calm, "ac") - 2, Number(shaken, "ac"));
        Assert.Equal(Number(calm, "bite") - 2, Number(shaken, "bite"));

        // A character holding the same condition: the rules' armor class takes it, once.
        var character = Owner();
        var characterPlain = Engine().Recompute(Owner());
        character["condition_shaken"] = 2L;
        var characterShaken = Engine().Recompute(character);
        Assert.Equal(Convert.ToInt64(characterPlain.ArmorClass!.Total) - 2, Convert.ToInt64(characterShaken.ArmorClass!.Total));
    }

    [Fact]
    public void AConditionModifierWhoseTargetTheKindLacks_ReachesNothing_AndDoesNotThrow()
    {
        var companion = Companion(("condition_shaken", 3L)); // Shaken also aims at armorClass and nothingHere, which the companion has not
        var evaluation = Engine().Evaluate(companion, new Actors(("owner", Owner())));
        Assert.Equal(12, Number(evaluation, "ac")); // only the targets it has are lowered: 15 - 3
        Assert.Empty(evaluation.Violations);
        Assert.Equal(Array.Empty<PrintedNumber>(), Engine().Printed(companion));
        Assert.Equal(RollSiteBonus.None, Engine().ConditionBonus(companion, "armorClass"));
    }

    // ---- Gap 2: link() reads what the rules work out ----

    [Fact]
    public void LinkReadsTheSavesSkillsPerceptionAndDerivedValuesOfACharacter_ByTheNamesTheEngineGives()
    {
        var owner = Owner();
        var worked = Engine().Recompute(owner);
        var model = new SheetModel(System, new Entries(), Companion(), new Actors(("owner", owner))).Evaluate();

        Assert.Equal(worked.SavingThrows.Single(s => s.Key == "dex").Bonus, Convert.ToDouble(model.Values["ownerDexSave"]));
        Assert.Equal(Number(worked, "perception"), Convert.ToDouble(model.Values["ownerPerception"]));
        Assert.Equal(Number(worked, "bond"), Convert.ToDouble(model.Values["ownerBond"]));
        Assert.Equal(Convert.ToDouble(owner["armorClass"]), Convert.ToDouble(model.Values["ownerAc"])); // stored by the rules
        Assert.Equal(5.0, Convert.ToDouble(model.Values["level"])); // a stored key still reads as before
        Assert.Equal(worked.WorkedOut()["skills.climb"], worked.Skills.Single(s => s.Key == "climb").Bonus);
        Assert.Contains("saves.dex", worked.WorkedOut().Keys);
    }

    [Fact]
    public void TwoLinkReadsInOneFormula_ThenAKindValue_AreAllCorrect()
    {
        var owner = Owner();
        var worked = Engine().Recompute(owner);
        var model = new SheetModel(System, new Entries(), Companion(), new Actors(("owner", owner))).Evaluate();

        var save = worked.SavingThrows.Single(s => s.Key == "dex").Bonus;
        var climb = worked.Skills.Single(s => s.Key == "climb").Bonus;
        Assert.Equal(save + climb, Convert.ToDouble(model.Values["pair"]));
        // link(skills.climb) + bite (a kind value, read after the first link) + link(perception): the model the formula functions read is the companion's each time.
        Assert.Equal(climb + Convert.ToDouble(model.Values["bite"]) + Number(worked, "perception"), Convert.ToDouble(model.Values["sandwich"]));
        Assert.Equal(8, Convert.ToDouble(model.Values["bite"]));
    }

    public void TheNestedPass_RunsMidFormula_AndTheOuterModelIsTheOneAKindValueReadsAfterIt()
    {
        var owner = Owner();
        var worked = Engine().Recompute(owner);
        var save = worked.SavingThrows.Single(s => s.Key == "dex").Bonus;
        var climb = worked.Skills.Single(s => s.Key == "climb").Bonus;

        // A fresh model whose first read is the formula: two link() reads (the first runs the owner's pass in the middle of the formula) and then a kind value,
        // which must read the companion's model, not the owner's.
        var model = new SheetModel(System, new Entries(), Companion(), new Actors(("owner", owner)));
        Assert.Equal(save + climb + 8, SheetFormulas.Number(model.Path("trio")));
        Assert.Throws<FormulaException>(() => SheetModel.Need()); // nothing is being worked out out here, and the nested pass left none behind

        // And by path afterwards: the companion's own armor class value, and a formula that reads a link.
        Assert.Equal(15.0, SheetFormulas.Number(model.Path("ac")));
        Assert.Equal(save + climb, SheetFormulas.Number(model.Path("pair")));
    }

    [Fact]
    public void WhenTheOwnersRulesThrowInTheNestedPass_TheOuterModelIsPutBack_TheLinkReadsZero_AndTheSheetWarns()
    {
        var owner = Owner();
        owner["class"] = "boom"; // the content lookup throws inside the owner's rule pass
        var owners = new Actors(("owner", owner));

        var model = new SheetModel(System, new Entries(), Companion(), owners);
        // trio = saves.dex (0, the rules failed) + skills.climb (0) + bite (3 + the owner's stored level, read by the plain model): the kind value after the throw is the companion's.
        Assert.Equal(8.0, SheetFormulas.Number(model.Path("trio")));
        Assert.Throws<FormulaException>(() => SheetModel.Need());
        Assert.Equal(15.0, SheetFormulas.Number(model.Path("ac")));

        var result = new SheetModel(System, new Entries(), Companion(), owners).Evaluate();
        Assert.Equal(8.0, Convert.ToDouble(result.Values["trio"]));
        Assert.Equal(5.0, Convert.ToDouble(result.Values["level"]));
        Assert.Contains(result.Warnings, w => w.FieldKey == "link:owner");
    }

    [Fact]
    public void AFamiliarsArmorClass_FollowsItsMastersArmorClass()
    {
        var master = Owner(dex: 10);
        Engine().Recompute(master);
        var first = new SheetModel(System, new Entries(), Companion(), new Actors(("owner", master))).Evaluate();
        Assert.Equal(Convert.ToDouble(master["armorClass"]), first.Values["ownerAc"]);

        master["dex"] = 18L;
        Engine().Recompute(master);
        var second = new SheetModel(System, new Entries(), Companion(), new Actors(("owner", master))).Evaluate();
        Assert.Equal(Convert.ToDouble(master["armorClass"]), second.Values["ownerAc"]);
        Assert.NotEqual(first.Values["ownerAc"], second.Values["ownerAc"]);
    }

    [Fact]
    public void NoOwnerSheetSupplied_TheLinkIsUnknown_AndNothingTheCompanionStoresIsSweptOrClamped()
    {
        var sheet = Companion(("heroPoints", 2L), ("uses_bite", 1L), ("changes_on__trick", "dailyPrep"));
        var evaluation = Engine().Recompute(sheet); // no ILinkedSheets: the link names an actor and the host gave no sheet

        Assert.True(evaluation.LinksUnknown);
        Assert.Equal(2L, sheet["heroPoints"]);
        Assert.Equal(1L, sheet["uses_bite"]);
        Assert.Equal("dailyPrep", sheet["changes_on__trick"]);
        Assert.Equal(0.0, Convert.ToDouble(evaluation.Model!.Values["ownerDexSave"])); // a link with nothing behind it reads 0
    }

    public void ALinkedSheetOfAnotherKind_IsAKnownEmptyLink_AndTheEngineIsNotRun()
    {
        var entries = new Entries();
        var other = Sheet("companion", ("level", 9L), ("class", "scout"));
        var evaluation = new SheetModel(System, entries, Companion(), new Actors(("owner", other))).Evaluate();
        Assert.Equal(0.0, Convert.ToDouble(evaluation.Values["ownerPerception"]));
        Assert.Equal(0.0, Convert.ToDouble(evaluation.Values["level"]));
        Assert.Equal(0, entries.ClassLookups); // the rule engine would have looked the class up
    }

    [Fact]
    public void ALinkedSheetWithNoKindKey_IsTheDefaultKind_SoItsLevelArmorClassAndSavesRead()
    {
        var owner = Owner(kind: null); // saved before kinds: no kind key
        var worked = Engine().Recompute(owner);
        var model = new SheetModel(System, new Entries(), Companion(), new Actors(("owner", owner))).Evaluate();

        Assert.Equal(5.0, Convert.ToDouble(model.Values["level"]));
        Assert.Equal(Convert.ToDouble(owner["armorClass"]), Convert.ToDouble(model.Values["ownerAc"]));
        Assert.True(Convert.ToDouble(model.Values["ownerDexSave"]) != 0);
        Assert.Equal(worked.SavingThrows.Single(s => s.Key == "dex").Bonus, Convert.ToDouble(model.Values["ownerDexSave"]));
    }

    [Fact]
    public void AStoredKeyRead_RunsNoRulePassOnTheLinkedSheet_AndARuleValueRunsOnePerLinkedSheetPerRecompute()
    {
        var owner = Owner();
        owner["class"] = "scout";
        owner["ancestry"] = "elf"; // matches the class, so the constraint holds
        Engine().Recompute(owner); // the rules write the armor class back
        var owners = new Actors(("owner", owner));

        // One pass over the owner, as the link runs it: how many class lookups it makes.
        var baseline = new Entries();
        new SheetRuleEngine(System, baseline) { Options = false }.Evaluate(owner);
        var perPass = baseline.ClassLookups;
        Assert.True(perPass > 0);

        // Stored keys (level, armorClass): the plain linked model, no pass.
        var stored = new Entries();
        var storedModel = new SheetModel(System, stored, Companion(), owners);
        Assert.Equal(5.0, SheetFormulas.Number(storedModel.Path("level")));
        Assert.Equal(Convert.ToDouble(owner["armorClass"]), SheetFormulas.Number(storedModel.Path("ownerAc")));
        Assert.Equal(0, stored.ClassLookups);

        // A name only the rules work out: one pass, however many such reads the model makes.
        Assert.NotEqual(0.0, SheetFormulas.Number(storedModel.Path("ownerDexSave")));
        Assert.Equal(perPass, stored.ClassLookups);
        storedModel.Path("pair");
        storedModel.Path("ownerPerception");
        Assert.Equal(perPass, stored.ClassLookups);

        // One Recompute builds several models of the sheet (the evaluation, the track maxima); the linked sheet's rules run once for all of them.
        var recomputed = new Entries();
        new SheetRuleEngine(System, recomputed).Recompute(Companion(), owners);
        Assert.Equal(perPass, recomputed.ClassLookups);
    }

    // ---- Party ----

    public void AKindThatIsNotAPartyMember_IsLeftOutOfWhatTheWholePartyGets()
    {
        var companion = Companion();
        var character = Owner();
        var legacy = Owner(kind: null);

        Assert.True(SheetLayout.IsPartyMember(System, character));
        Assert.True(SheetLayout.IsPartyMember(System, legacy)); // no stored kind key: the first kind, a character
        Assert.False(SheetLayout.IsPartyMember(System, companion));

        // The session-start handler sets the hero point track: it matters to a character's sheet, and to a companion's it does not, because only a party member gets the handler.
        Assert.True(SystemMoments.Matters(System, character, "sessionStart"));
        Assert.False(SystemMoments.Matters(System, companion, "sessionStart"));
        Assert.True(SystemMoments.RunsHandler(System, character));
        Assert.False(SystemMoments.RunsHandler(System, companion));
        Assert.True(SystemMoments.ReachesPartyMember(System, character, "sessionStart"));
        Assert.True(SystemMoments.ReachesPartyMember(System, legacy, "sessionStart"));
        Assert.False(SystemMoments.ReachesPartyMember(System, companion, "sessionStart"));
        Assert.True(SystemMoments.ReachesPartyMember(System, companion, FeatureRecovery.Dawn)); // dawn still goes to every character
    }

    [Fact]
    public void ACompanion_IsStillReachedByAMomentItsOwnSheetKeysAreWaitingFor()
    {
        // A choice the sheet says is usually changed at the daily preparations: the party bar's moment reaches the companion for that, though not for the session start.
        var companion = Companion(("changes_on__trick", "dailyPrep"), ("changes_on__trick_Label", "Trick"));
        Assert.True(SystemMoments.ReachesPartyMember(System, companion, "dailyPrep"));
        Assert.True(SystemMoments.Matters(System, companion, "dailyPrep"));
        Assert.False(SystemMoments.ReachesPartyMember(System, companion, "sessionStart"));
        Assert.False(SystemMoments.ReachesPartyMember(System, Companion(), "dailyPrep"));
    }

    [Fact]
    public void ASystemWithoutKinds_HasEveryoneInTheParty()
    {
        var plain = SystemDefinitionLoader.Parse("id: plain\nname: Plain\n");
        Assert.True(SheetLayout.IsPartyMember(plain, new Dictionary<string, object>()));
        Assert.True(SheetLayout.IsPartyMember(null, new Dictionary<string, object>()));
    }
}
