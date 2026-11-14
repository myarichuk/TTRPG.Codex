using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>Choices over Library entries (docs/ENTRY_CHOICES_DESIGN.md), steps 1 to 3: an <c>adds</c> item that leaves entries of a list to the player,
/// narrowed by <c>where</c> conditions, or a value the later choices of the feature read; their picks go on the list like fixed entries.</summary>
public class EntryChoiceTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public EntryChoiceTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        ClassSkillsSettled.LeaveOut(_loader.GetDefinition("srd5e")!); // the class's own skills are not what these tests look at
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    private static ContentEntry Feat(string id, params Dictionary<string, object>[] adds)
    {
        var feat = new ContentEntry { Type = "feat", Id = id, Name = id, SystemId = "srd5e", PackId = "homebrew" };
        feat.Properties["category"] = "general";
        feat.Properties["repeatable"] = true;
        feat.Properties["adds"] = adds.Cast<object>().ToList();
        return feat;
    }

    private static Dictionary<string, object> Arcanum() => Map(("to", "grantedSpells"), ("choice", "arcanum-6"), ("choose", 1L), ("where", Items(
        Map(("property", "classes"), ("contains", "Warlock")),
        Map(("property", "level"), ("equals", 6L)))));

    /// <summary>Magic Initiate's shape: the spell list first (a value), then what depends on it.</summary>
    private static ContentEntry Initiate(string id = "initiate") => Feat(id,
        Map(("choice", "spell-list"), ("choose", 1L), ("from", Items("Cleric", "Druid", "Wizard"))),
        Map(("to", "grantedSpells"), ("choice", "cantrips"), ("choose", 2L), ("where", Items(
            Map(("property", "level"), ("equals", 0L)),
            Map(("property", "classes"), ("containsChoice", "spell-list"))))),
        Map(("to", "grantedSpells"), ("choice", "spell"), ("choose", 1L), ("where", Items(
            Map(("property", "level"), ("equals", 1L)),
            Map(("property", "classes"), ("containsChoice", "spell-list"))))));

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) Character(int level, params ContentEntry[] homebrew)
    {
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, homebrew));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "fighter";
        sheet["species"] = "halfling";
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        sheet["feats"] = homebrew.Where(h => h.Type == "feat").Select(h => (object)h.Id).ToList();
        return (engine, sheet);
    }

    private static PendingChoice Choice(SheetEvaluation evaluation, string key) =>
        evaluation.PendingChoices.Concat(evaluation.MadeChoices).Single(c => c.Key == key);

    private ContentEntry Spell(string id) => _registry.GetEntries("srd5e", "spell").Single(s => s.Id == id);

    [Fact]
    public void AnEntryChoice_OffersOnlyTheEntriesItsConditionsAllow_AndAPickGoesOnTheList()
    {
        var (engine, sheet) = Character(11, Feat("arcanum", Arcanum()));

        var open = Choice(engine.Recompute(sheet), "choice__arcanum__arcanum-6");

        Assert.Equal("grantedSpells", open.Target);
        Assert.Equal(1, open.Count);
        Assert.NotEmpty(open.Options);
        Assert.All(open.Options, o => Assert.Equal(6, Convert.ToInt32(Spell(o.Key).Properties["level"])));
        Assert.All(open.Options, o => Assert.Contains("Warlock", ((IEnumerable<object>)Spell(o.Key).Properties["classes"]).Select(c => c.ToString())));
        Assert.Contains(open.Options, o => o.Key == "eyebite");
        Assert.DoesNotContain(open.Options, o => o.Key == "chain-lightning"); // a level-6 spell, but not a Warlock's

        SheetRuleEngine.SetChoice(sheet, open.Key, new[] { "eyebite" });
        var made = engine.Recompute(sheet);

        Assert.DoesNotContain(made.PendingChoices, c => c.Key == open.Key);
        Assert.Equal(new[] { "eyebite" }, Choice(made, open.Key).Made);
        Assert.Contains("eyebite", SheetRuleEngine.RawList(sheet, "grantedSpells"));
        Assert.Contains("eyebite", SheetRuleEngine.RawList(sheet, SheetRuleEngine.GrantedPrefix + "grantedSpells"));
    }

    [Fact]
    public void APickThatNoLongerFits_IsStale_AndLeavesTheList()
    {
        var (engine, sheet) = Character(11, Feat("arcanum", Arcanum()));
        sheet["choice__arcanum__arcanum-6"] = Items("fireball"); // level 3: never a legal pick

        var evaluation = engine.Recompute(sheet);

        Assert.Contains(evaluation.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey && v.FieldKey == "choice__arcanum__arcanum-6");
        Assert.DoesNotContain("fireball", SheetRuleEngine.RawList(sheet, "grantedSpells"));
        Assert.Contains(evaluation.PendingChoices, c => c.Key == "choice__arcanum__arcanum-6"); // still to choose
    }

    [Fact]
    public void AChoiceWithPicksLeft_IsAWarning_NeverAViolation()
    {
        var (engine, sheet) = Character(11, Feat("arcanum", Arcanum()));

        var evaluation = engine.Recompute(sheet);

        Assert.Contains(evaluation.Warnings, w => w.Key == SheetRuleEngine.ChoicePendingKey);
        Assert.DoesNotContain(evaluation.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey);
        Assert.DoesNotContain(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError");
    }

    [Fact]
    public void MagicInitiate_AsksForTheListFirst_AndWhatDependsOnItOffersNothingUntilThen()
    {
        var (engine, sheet) = Character(1, Initiate());

        var evaluation = engine.Recompute(sheet);

        var list = Choice(evaluation, "choice__initiate__spell-list");
        Assert.Equal(SheetRuleEngine.ValueTarget, list.Target);
        Assert.Equal(new[] { "Cleric", "Druid", "Wizard" }, list.Options.Select(o => o.Key));
        var cantrips = Choice(evaluation, "choice__initiate__cantrips");
        Assert.Equal("spell-list", cantrips.BlockedBy);
        Assert.Empty(cantrips.Options);
        Assert.Equal("spell-list", Choice(evaluation, "choice__initiate__spell").BlockedBy);
        Assert.Single(evaluation.Warnings, w => w.Key == SheetRuleEngine.ChoicePendingKey); // only the list asks; the rest waits on it
    }

    [Fact]
    public void PickingAList_NarrowsWhatFollows_AndSwitchingItMakesTheOldPicksStale()
    {
        var (engine, sheet) = Character(1, Initiate());
        sheet["choice__initiate__spell-list"] = Items("Wizard");
        var wizard = engine.Recompute(sheet);

        var cantrips = Choice(wizard, "choice__initiate__cantrips");
        Assert.Null(cantrips.BlockedBy);
        Assert.All(cantrips.Options, o => Assert.Equal(0, Convert.ToInt32(Spell(o.Key).Properties["level"])));
        Assert.Contains(cantrips.Options, o => o.Key == "fire-bolt");
        Assert.DoesNotContain(cantrips.Options, o => o.Key == "sacred-flame"); // a Cleric's
        var spells = Choice(wizard, "choice__initiate__spell");
        Assert.All(spells.Options, o => Assert.Equal(1, Convert.ToInt32(Spell(o.Key).Properties["level"])));

        sheet["choice__initiate__cantrips"] = Items("fire-bolt", "mage-hand");
        sheet["choice__initiate__spell"] = Items("magic-missile");
        engine.Recompute(sheet);
        Assert.Equal(new[] { "fire-bolt", "mage-hand", "magic-missile" }.Order(), SheetRuleEngine.RawList(sheet, "grantedSpells").Order());

        sheet["choice__initiate__spell-list"] = Items("Cleric");
        var cleric = engine.Recompute(sheet);

        Assert.Contains(cleric.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey && v.FieldKey == "choice__initiate__spell"); // magic missile is not a Cleric's
        Assert.DoesNotContain("magic-missile", SheetRuleEngine.RawList(sheet, "grantedSpells"));
        Assert.DoesNotContain("fire-bolt", SheetRuleEngine.RawList(sheet, "grantedSpells"));
    }

    [Fact]
    public void TwoCopiesOfAFeat_KeepTheirOwnPicks()
    {
        var (engine, sheet) = Character(1, Initiate());
        sheet["feats"] = Items("initiate", "initiate");
        sheet["choice__initiate__spell-list"] = Items("Wizard");
        sheet["choice__initiate#2__spell-list"] = Items("Cleric");

        var evaluation = engine.Recompute(sheet);

        var first = Choice(evaluation, "choice__initiate__cantrips");
        var second = Choice(evaluation, "choice__initiate#2__cantrips");
        Assert.Contains(first.Options, o => o.Key == "fire-bolt");
        Assert.DoesNotContain(second.Options, o => o.Key == "fire-bolt");
        Assert.Contains(second.Options, o => o.Key == "sacred-flame");
    }

    [Fact]
    public void FromList_OffersOnlyWhatTheSheetAlreadyHoldsThere()
    {
        var signature = Feat("signature", Map(("choice", "signature"), ("choose", 2L), ("fromList", "spells"),
            ("where", Items(Map(("property", "level"), ("equals", 3L))))));
        var (engine, sheet) = Character(20, signature);
        sheet["spells"] = Items("fireball", "fly", "magic-missile", "shield");

        var evaluation = engine.Recompute(sheet);

        var open = Choice(evaluation, "choice__signature__signature");
        Assert.Equal(new[] { "fireball", "fly" }, open.Options.Select(o => o.Key).Order());

        sheet["choice__signature__signature"] = Items("fireball", "fly");
        sheet["spells"] = Items("fireball", "magic-missile", "shield"); // Fly is no longer prepared
        var after = engine.Recompute(sheet);

        Assert.Contains(after.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey && v.FieldKey == "choice__signature__signature");
        Assert.Equal(new[] { "fireball" }, Choice(after, "choice__signature__signature").Made);
        // A pick that only marks what is held never puts it back: the player may take the spell off the list.
        Assert.DoesNotContain("fly", SheetRuleEngine.RawList(sheet, "spells"));
        Assert.Empty(SheetRuleEngine.RawList(sheet, SheetRuleEngine.GrantedPrefix + "spells"));
        Assert.Equal("spells", Choice(after, "choice__signature__signature").Target);
    }

    [Fact]
    public void AChoiceFromTheListItPutsPicksOn_AddsNothing_AndTheCheckSaysSo()
    {
        var system = _loader.GetDefinition("srd5e")!;
        var loop = Feat("loop", Map(("to", "spells"), ("choice", "signature"), ("choose", 2L), ("fromList", "spells"),
            ("where", Items(Map(("property", "level"), ("equals", 3L))))));

        Assert.Contains(FeatureGrantCheck.Problems(system, loop), p => p.Contains("adds nothing; give no \"to\""));
    }

    [Fact]
    public void ClearingTheValueChoice_LeavesTheOldPicksAloneWithoutViolations_AndPickingItAgainBringsThemBack()
    {
        var (engine, sheet) = Character(1, Initiate());
        sheet["choice__initiate__spell-list"] = Items("Wizard");
        sheet["choice__initiate__cantrips"] = Items("fire-bolt", "mage-hand");
        sheet["choice__initiate__spell"] = Items("magic-missile");
        engine.Recompute(sheet);
        Assert.Contains("fire-bolt", SheetRuleEngine.RawList(sheet, "grantedSpells"));

        sheet.Remove("choice__initiate__spell-list");
        var cleared = engine.Recompute(sheet);

        Assert.DoesNotContain(cleared.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey);
        Assert.Equal("spell-list", Choice(cleared, "choice__initiate__cantrips").BlockedBy);
        Assert.DoesNotContain("fire-bolt", SheetRuleEngine.RawList(sheet, "grantedSpells")); // not granted while blocked
        Assert.Equal(new[] { "fire-bolt", "mage-hand" }, SheetRuleEngine.RawList(sheet, "choice__initiate__cantrips")); // still stored

        sheet["choice__initiate__spell-list"] = Items("Wizard");
        var back = engine.Recompute(sheet);

        Assert.Equal(new[] { "fire-bolt", "mage-hand" }, Choice(back, "choice__initiate__cantrips").Made);
        Assert.DoesNotContain(back.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey);
        Assert.Contains("fire-bolt", SheetRuleEngine.RawList(sheet, "grantedSpells"));
    }

    [Fact]
    public void MorePicksThanTheChoiceAsksFor_AreDroppedWithoutAViolation()
    {
        var (engine, sheet) = Character(11, Feat("arcanum", Arcanum()));
        sheet["choice__arcanum__arcanum-6"] = Items("eyebite", "circle-of-death");

        var evaluation = engine.Recompute(sheet);

        Assert.DoesNotContain(evaluation.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey);
        Assert.Equal(new[] { "eyebite" }, Choice(evaluation, "choice__arcanum__arcanum-6").Made);
        Assert.DoesNotContain("circle-of-death", SheetRuleEngine.RawList(sheet, "grantedSpells"));
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("he")]
    public void AValueChoice_StoresTheCanonicalWord_WhateverTheLanguageOfTheSystemShown(string language)
    {
        var system = _loader.GetDefinition("srd5e")!.Localize(language);
        var engine = new SheetRuleEngine(system, new WithHomebrew(_registry, Initiate()));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "fighter";
        sheet["level"] = 1L;
        sheet["feats"] = Items("initiate");
        sheet["choice__initiate__spell-list"] = Items("Wizard"); // what the picker stores: the word the spells' classes use

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(new[] { "Wizard" }, Choice(evaluation, "choice__initiate__spell-list").Made);
        var cantrips = Choice(evaluation, "choice__initiate__cantrips");
        Assert.Null(cantrips.BlockedBy);
        Assert.Contains(cantrips.Options, o => o.Key == "fire-bolt");
    }

    [Fact]
    public void AHomebrewSpell_IsOfferedWhenItMeetsTheConditions()
    {
        var homebrew = new ContentEntry { Type = "spell", Id = "frog-whisper", Name = "Frog Whisper", SystemId = "srd5e", PackId = "homebrew" };
        homebrew.Properties["level"] = 6L;
        homebrew.Properties["classes"] = Items("Warlock");
        var (engine, sheet) = Character(11, Feat("arcanum", Arcanum()), homebrew);

        var open = Choice(engine.Recompute(sheet), "choice__arcanum__arcanum-6");

        Assert.Contains(open.Options, o => o.Key == "frog-whisper");
    }

    private static Dictionary<string, object> OncePerLongRest() => Map(("max", 1L), ("recoversOn", Items("Long Rest")));

    [Fact]
    public void APickWithFree_GetsACounterOfItsOwnSlot_AndTheSpellsActionOneMoreCost()
    {
        var arcanum = Arcanum();
        arcanum["free"] = OncePerLongRest();
        var (engine, sheet) = Character(11, Feat("arcanum", arcanum));
        sheet["choice__arcanum__arcanum-6"] = Items("eyebite");

        var evaluation = engine.Recompute(sheet);

        var counter = evaluation.Counters.Single(c => c.Key.StartsWith("uses_free_arcanum"));
        Assert.Equal("uses_free_arcanum_arcanum_6_1", counter.Key);
        Assert.Equal("arcanum: Eyebite", counter.Label);
        var cast = evaluation.Actions.Single(a => a.Entry.Id == "eyebite");
        Assert.Contains(cast.Costs, c => c.Key == counter.Key);
    }

    [Fact]
    public void ChangingThePick_KeepsWhatWasSpent_AndTheCounterFollowsTheNewSpell()
    {
        var arcanum = Arcanum();
        arcanum["free"] = OncePerLongRest();
        var (engine, sheet) = Character(11, Feat("arcanum", arcanum));
        sheet["choice__arcanum__arcanum-6"] = Items("eyebite");
        engine.Recompute(sheet);
        sheet["uses_free_arcanum_arcanum_6_1"] = 0L; // spent

        sheet["choice__arcanum__arcanum-6"] = Items("circle-of-death");
        var evaluation = engine.Recompute(sheet);

        Assert.Equal(0L, sheet["uses_free_arcanum_arcanum_6_1"]); // re-picking does not refill it
        Assert.Equal("arcanum: Circle of Death", evaluation.Counters.Single(c => c.Key == "uses_free_arcanum_arcanum_6_1").Label);
        Assert.Contains(evaluation.Actions.Single(a => a.Entry.Id == "circle-of-death").Costs, c => c.Key == "uses_free_arcanum_arcanum_6_1");
        Assert.DoesNotContain(evaluation.Actions.Where(a => a.Entry.Id == "eyebite"), a => a.Costs.Any(c => c.Key == "uses_free_arcanum_arcanum_6_1"));
    }

    [Fact]
    public void TwoPicks_GetTwoSlotCounters_AndAMarkOnlyPickCarriesFreeWithoutPuttingTheSpellAnywhere()
    {
        var signature = Map(("choice", "signature"), ("choose", 2L), ("fromList", "spells"), ("where", Items(Map(("property", "level"), ("equals", 3L)))),
            ("free", Map(("max", 1L), ("recoversOn", Items("Short Rest", "Long Rest")))));
        var (engine, sheet) = Character(20, Feat("signature", signature));
        sheet["spells"] = Items("fireball", "fly", "shield");
        sheet["choice__signature__signature"] = Items("fireball", "fly");

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(new[] { "uses_free_signature_signature_1", "uses_free_signature_signature_2" }, evaluation.Counters.Select(c => c.Key).Where(k => k.StartsWith("uses_free_signature")).Order());
        Assert.Contains(evaluation.Actions.Single(a => a.Entry.Id == "fly").Costs, c => c.Key == "uses_free_signature_signature_2");
        Assert.Empty(SheetRuleEngine.RawList(sheet, "grantedSpells")); // nothing was put on any list
    }

    [Fact]
    public void ASecondCopyOfTheFeat_HasItsOwnSlotCounters()
    {
        var spell = Map(("to", "grantedSpells"), ("choice", "spell"), ("choose", 1L), ("where", Items(Map(("property", "level"), ("equals", 1L)))),
            ("free", OncePerLongRest()));
        var (engine, sheet) = Character(1, Feat("initiate", spell));
        sheet["feats"] = Items("initiate", "initiate");
        sheet["choice__initiate__spell"] = Items("magic-missile");
        sheet["choice__initiate#2__spell"] = Items("shield");

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(2, evaluation.Counters.Count(c => c.Key.StartsWith("uses_free_initiate")));
        Assert.Contains(evaluation.Counters, c => c.Key == "uses_free_initiate_spell_1");
        Assert.Contains(evaluation.Counters, c => c.Key == "uses_free_initiate_2_spell_1");
    }

    [Fact]
    public void AtWillOnAPick_IsAFreeCostWithNoCounter()
    {
        var mastery = Map(("choice", "mastery"), ("choose", 1L), ("fromList", "spells"), ("where", Items(Map(("property", "level"), ("equals", 1L)))), ("free", "atWill"));
        var (engine, sheet) = Character(18, Feat("mastery", mastery));
        sheet["spells"] = Items("shield", "magic-missile");
        sheet["choice__mastery__mastery"] = Items("shield");

        var evaluation = engine.Recompute(sheet);

        Assert.Contains(evaluation.Actions.Single(a => a.Entry.Id == "shield").Costs, c => c.Free && c.Label == ActionCost.AtWillLabel);
        Assert.DoesNotContain(evaluation.Counters, c => c.Key.StartsWith("uses_free_mastery"));
    }

    [Fact]
    public void TheNewConditions_AlsoWorkInAListsOwnWhere()
    {
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry));
        var spell = Spell("eyebite");

        Assert.True(engine.Holds(new RuleCondition { Property = "classes", Contains = "warlock" }, spell, new Dictionary<string, object>()));
        Assert.False(engine.Holds(new RuleCondition { Property = "classes", Contains = "Paladin" }, spell, new Dictionary<string, object>()));
        var picks = new Dictionary<string, IReadOnlyList<string>> { ["list"] = new[] { "Wizard" } };
        Assert.True(engine.Holds(new RuleCondition { Property = "classes", ContainsChoice = "list" }, spell, new Dictionary<string, object>(), picks));
        Assert.False(engine.Holds(new RuleCondition { Property = "classes", ContainsChoice = "list" }, spell, new Dictionary<string, object>())); // no pick: fails
        Assert.True(engine.Holds(new RuleCondition { Property = "school", EqualsChoice = "list" }, spell, new Dictionary<string, object>(),
            new Dictionary<string, IReadOnlyList<string>> { ["list"] = new[] { "Necromancy" } }));
    }

    [Fact]
    public void TheCheck_AllowsEntryAndValueChoices_ButNotOnesThatWouldOfferEverything_OrReadWhatComesLater()
    {
        var system = _loader.GetDefinition("srd5e")!;
        Assert.Empty(FeatureGrantCheck.Problems(system, Feat("arcanum", Arcanum())));
        Assert.Empty(FeatureGrantCheck.Problems(system, Initiate()));

        var everything = Feat("all", Map(("to", "grantedSpells"), ("choice", "any"), ("choose", 1L)));
        Assert.Contains(FeatureGrantCheck.Problems(system, everything), p => p.Contains("needs \"where\" conditions or a \"fromList\""));

        var backwards = Feat("backwards",
            Map(("to", "grantedSpells"), ("choice", "spell"), ("choose", 1L), ("where", Items(Map(("property", "classes"), ("containsChoice", "spell-list"))))),
            Map(("choice", "spell-list"), ("choose", 1L), ("from", Items("Cleric"))));
        Assert.Contains(FeatureGrantCheck.Problems(system, backwards), p => p.Contains("which is not a choice written before it"));

        var unknownList = Feat("unknown", Map(("to", "grantedSpells"), ("choice", "x"), ("choose", 1L), ("fromList", "nothing")));
        Assert.Contains(FeatureGrantCheck.Problems(system, unknownList), p => p.Contains("not a list of this system"));

        var noValues = Feat("novalues", Map(("choice", "pick"), ("choose", 1L)));
        Assert.Contains(FeatureGrantCheck.Problems(system, noValues), p => p.Contains("needs \"from\""));

        var elsewhere = Feat("elsewhere", Map(("to", "resistances"), ("choice", "x"), ("choose", 1L)));
        Assert.Contains(FeatureGrantCheck.Problems(system, elsewhere), p => p.Contains("a choice can only go to"));
    }

    [Fact]
    public void TheFeatureEditor_KeepsTheWhereAndTheListAnEntryChoiceCarries()
    {
        var feature = new Dictionary<string, object> { ["name"] = "Arcanum", ["adds"] = Items(Arcanum()) };

        var data = FeatureGrantData.Read(feature);
        data.Adds.Single().Extra["choose"] = 2L; // the author changes the count
        var written = new Dictionary<string, object>();
        data.Apply(written);

        var add = ((List<object>)written["adds"]).Cast<Dictionary<string, object>>().Single();
        Assert.Equal("grantedSpells", add["to"]);
        Assert.Equal(2L, add["choose"]);
        Assert.Equal(2, ((List<object>)add["where"]).Count);
        Assert.Equal("arcanum-6", add["choice"]);
    }

    private static ContentEntry HomebrewSpell(string id, int level, string caster)
    {
        var spell = new ContentEntry { Type = "spell", Id = id, Name = id, SystemId = "srd5e", PackId = "homebrew" };
        spell.Properties["level"] = (long)level;
        spell.Properties["classes"] = Items(caster);
        return spell;
    }

    [Fact]
    public void ASpellPickedThroughAChoice_IsWhatTheSheetUses_AndReachesTheJoinReview()
    {
        var feat = Feat("arcanum", Arcanum());
        var brew = HomebrewSpell("gloom-bolt", 6, "Warlock");
        var (engine, sheet) = Character(11, feat, brew);
        SheetRuleEngine.SetChoice(sheet, "choice__arcanum__arcanum-6", new[] { "gloom-bolt" });
        var system = _loader.GetDefinition("srd5e")!;
        var mine = new WithHomebrew(_registry, feat, brew);
        var theirs = new WithHomebrew(_registry, feat); // the campaign has the feat, not the spell

        var uses = SheetReferences.Of(system, sheet, mine);
        Assert.Contains(uses, r => r.FieldKey == "choice__arcanum__arcanum-6" && r.Id == "gloom-bolt" && r.Types.Contains("spell"));

        var missing = SheetReferences.Missing(system, sheet, theirs, mine);
        Assert.Contains(missing, r => r.Id == "gloom-bolt");
        Assert.DoesNotContain(missing, r => r.Id == "arcanum");

        var left = SheetReferences.Without(system, sheet, missing);
        Assert.Empty(SheetRuleEngine.RawList(left, "choice__arcanum__arcanum-6"));
        Assert.Single(SheetRuleEngine.RawList(sheet, "choice__arcanum__arcanum-6")); // the original is left as it was
    }

    [Fact]
    public void ASwap_RepointsThePickOfAChoice_AndAValueChoiceIsNotAnEntry()
    {
        var feat = Initiate();
        var brew = HomebrewSpell("hedge-light", 0, "Wizard");
        var (engine, sheet) = Character(1, feat, brew);
        SheetRuleEngine.SetChoice(sheet, "choice__initiate__spell-list", new[] { "Wizard" });
        SheetRuleEngine.SetChoice(sheet, "choice__initiate__cantrips", new[] { "hedge-light", "fire-bolt" });
        var system = _loader.GetDefinition("srd5e")!;
        var resolver = new WithHomebrew(_registry, feat, brew);

        var uses = SheetReferences.Of(system, sheet, resolver);
        Assert.DoesNotContain(uses, r => r.Id == "Wizard");
        Assert.Contains(uses, r => r.FieldKey == "choice__initiate__cantrips" && r.Id == "hedge-light");

        var swapped = SheetReferences.Repointed(system, sheet, "spell", "hedge-light", "light", resolver);
        Assert.Equal(new[] { "light", "fire-bolt" }, SheetRuleEngine.RawList(swapped, "choice__initiate__cantrips"));
        Assert.Equal(new[] { "Wizard" }, SheetRuleEngine.RawList(swapped, "choice__initiate__spell-list"));

        var alreadyHeld = SheetReferences.Repointed(system, sheet, "spell", "hedge-light", "fire-bolt", resolver);
        Assert.Equal(new[] { "fire-bolt" }, SheetRuleEngine.RawList(alreadyHeld, "choice__initiate__cantrips"));
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}
