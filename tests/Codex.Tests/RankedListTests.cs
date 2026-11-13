using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>Skills and saving throws as ranked lists (docs/FEATURE_GRANTS_DESIGN.md, ranked lists and grant choices), steps 1 to 4: the ranks
/// live lists already hold, fixed and raise grants from features, saves from the class, and choices a feature leaves to the player. The real
/// SRD system carries the 5e cases (a homebrew subclass and feat beside its pack); a small system built here carries four ranks.</summary>
public class RankedListTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public RankedListTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync()
    {
        await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        ClassSkillsSettled.LeaveOut(_loader.GetDefinition("srd5e")!);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    private static Dictionary<string, object> Row(string name, int level, params (string Key, object Value)[] grants)
    {
        var row = new Dictionary<string, object> { ["name"] = name, ["level"] = (long)level, ["description"] = name + "." };
        foreach (var (key, value) in grants) row[key] = value;
        return row;
    }

    private static Dictionary<string, object> Add(params (string Key, object Value)[] pairs) => Map(pairs);

    private static ContentEntry Stacker(params Dictionary<string, object>[] rows)
    {
        var entry = new ContentEntry { Type = "subclass", Id = "stacker", Name = "Stacker", SystemId = "srd5e", PackId = "homebrew" };
        entry.Properties["class"] = "sorcerer";
        entry.Properties["features"] = rows.Cast<object>().ToList();
        return entry;
    }

    private static ContentEntry Feat(string id, string name, params Dictionary<string, object>[] adds)
    {
        var entry = new ContentEntry { Type = "feat", Id = id, Name = name, SystemId = "srd5e", PackId = "homebrew" };
        entry.Properties["category"] = "general";
        entry.Properties["repeatable"] = true;
        entry.Properties["adds"] = adds.Cast<object>().ToList();
        return entry;
    }

    private (SheetRuleEngine Engine, Dictionary<string, object> Sheet) Character(int level, params ContentEntry[] homebrew)
    {
        var engine = new SheetRuleEngine(_loader.GetDefinition("srd5e")!, new WithHomebrew(_registry, homebrew));
        var sheet = new Dictionary<string, object>();
        engine.InitializeSheet(sheet);
        sheet["class"] = "sorcerer";
        if (homebrew.Any(e => e.Type == "subclass")) sheet["subclass"] = "stacker";
        sheet["species"] = "halfling";
        sheet["level"] = (long)level;
        sheet[SheetRuleEngine.MethodKey] = SheetRuleEngine.ManualMethod;
        sheet[SheetRuleEngine.BasePrefix + "cha"] = 16L;
        sheet[SheetRuleEngine.BasePrefix + "dex"] = 14L;
        sheet[SheetRuleEngine.BasePrefix + "int"] = 16L;
        return (engine, sheet);
    }

    private static SkillScore Skill(SheetEvaluation evaluation, string key) => evaluation.Skills.Single(s => s.Key == key);

    // ---- Step 1: ranks on skills, defaulting to today's keys ----

    [Fact]
    public void Srd5e_NeedsNoRanksInItsYaml_AndItsDefaultsAreTodaysTwoLists()
    {
        var skills = _loader.GetDefinition("srd5e")!.SheetRules!.Skills!;

        Assert.Empty(skills.Ranks);
        Assert.Null(skills.Saves);
        Assert.Equal(new[] { "skillProficiencies", "skillExpertise" }, skills.SkillRanks().Select(r => r.SheetKey));
        Assert.Equal(new[] { "proficiencyBonus", "proficiencyBonus * 2" }, skills.SkillRanks().Select(r => r.Value));
        Assert.Equal(new[] { SkillRules.DefaultSaveKey }, skills.SaveRanks().Select(r => r.SheetKey));
    }

    [Fact]
    public void Srd5e_SkillsAndSaves_ReadTheirRanksFromTheListsSheetsAlreadyHave()
    {
        var (engine, sheet) = Character(5);
        sheet["background"] = "sage"; // Arcana and History
        sheet["skillProficiencies"] = Items("perception", "arcana");
        sheet["skillExpertise"] = Items("arcana");
        var before = sheet.Where(p => p.Key.StartsWith("skill")).ToDictionary(p => p.Key, p => string.Join(",", SheetRuleEngine.RawList(sheet, p.Key)));

        var evaluation = engine.Recompute(sheet);

        var arcana = Skill(evaluation, "arcana");
        Assert.Equal(2, arcana.Rank);
        Assert.Equal("Expert", arcana.RankLabel);
        Assert.True(arcana.Proficient && arcana.Expert);
        Assert.Equal(2, arcana.OwnRank);
        Assert.Equal(3 + 3 * 2, arcana.Bonus); // Intelligence +3, proficiency +3 twice
        Assert.Equal(new[] { (1, RankSourceKinds.Background, "Sage") }, arcana.Sources!.Select(s => (s.Rank, s.Kind, s.Name))); // the background also gives it

        var history = Skill(evaluation, "history");
        Assert.Equal(1, history.Rank);
        Assert.Equal(0, history.OwnRank);
        Assert.Equal(3 + 3, history.Bonus);

        var perception = Skill(evaluation, "perception");
        Assert.Equal(1, perception.Rank);
        Assert.Empty(perception.Sources!);
        Assert.Equal(0, Skill(evaluation, "stealth").Rank);

        // Nothing stored was rewritten, and nothing new was stored.
        Assert.Equal(before, sheet.Where(p => p.Key.StartsWith("skill")).ToDictionary(p => p.Key, p => string.Join(",", SheetRuleEngine.RawList(sheet, p.Key))));
        Assert.DoesNotContain(sheet.Keys, k => (k.StartsWith(SheetRuleEngine.ChoicePrefix) && !k.EndsWith("__" + SheetRuleEngine.ClassSkillsChoiceId)) || k.StartsWith(SheetRuleEngine.GrantedPrefix + "skill") || k == SkillRules.DefaultSaveKey);
        Assert.Empty(evaluation.PendingChoices);
    }

    [Fact]
    public void ExpertiseWithoutProficiency_CountsNow_SinceTheHighestListWins()
    {
        var (engine, sheet) = Character(5);
        sheet["skillExpertise"] = Items("stealth");

        var evaluation = engine.Recompute(sheet);

        var stealth = Skill(evaluation, "stealth");
        Assert.Equal(2, stealth.Rank);
        Assert.Equal(2 + 3 * 2, stealth.Bonus); // Dexterity +2
        Assert.DoesNotContain(evaluation.Warnings, w => w.Message.Contains("expertise but no proficiency"));
    }

    [Fact]
    public void ANameThatIsNoSkill_IsStillSaid()
    {
        var (engine, sheet) = Character(5);
        sheet["skillProficiencies"] = Items("basket-weaving");

        var evaluation = engine.Recompute(sheet);

        Assert.Contains(evaluation.Warnings, w => w.FieldKey == "skillProficiencies" && w.Message.Contains("basket-weaving"));
    }

    // ---- Step 2: fixed and raise grants ----

    [Fact]
    public void AFixedRankGrant_IsALockedRankWithItsSource_AndNeverWritesTheSheet()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Cat's Eyes", 3,
            ("adds", Items(Add(("to", "skills"), ("entries", Items("stealth")), ("rank", "expert")))))));
        sheet["skillProficiencies"] = Items("stealth", "athletics");

        var evaluation = engine.Recompute(sheet);

        var stealth = Skill(evaluation, "stealth");
        Assert.Equal(2, stealth.Rank);
        Assert.Equal(1, stealth.OwnRank); // the player's own tick stays, below the grant
        Assert.Equal(2 + 3 * 2, stealth.Bonus);
        var source = Assert.Single(stealth.Sources!);
        Assert.Equal((2, RankSourceKinds.Feature, "Stacker: Cat's Eyes"), (source.Rank, source.Kind, source.Name));
        Assert.Equal("Cat's Eyes", source.Feature!.Name);

        Assert.Equal(new[] { "stealth", "athletics" }, SheetRuleEngine.RawList(sheet, "skillProficiencies"));
        Assert.False(sheet.ContainsKey("skillExpertise"));
        Assert.DoesNotContain(sheet.Keys, k => k.StartsWith(SheetRuleEngine.GrantedPrefix + "skill"));
    }

    [Fact]
    public void ANumberedRank_AndAnUnnamedRank_MeanTheNumberAndTheFirstRank()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Training", 3,
            ("adds", Items(
                Add(("to", "skills"), ("entries", Items("stealth")), ("rank", 2L)),
                Add(("to", "skills"), ("entries", Items("athletics"))))))));

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(2, Skill(evaluation, "stealth").Rank);
        Assert.Equal(1, Skill(evaluation, "athletics").Rank);
    }

    [Fact]
    public void ARaise_GoesOneStepUp_AndStopsAtTheTopRank()
    {
        var (engine, sheet) = Character(5, Stacker(
            Row("Practice", 3, ("adds", Items(Add(("to", "skills"), ("entries", Items("stealth", "arcana")), ("raise", 1L))))),
            Row("More Practice", 3, ("adds", Items(Add(("to", "skills"), ("entries", Items("arcana")), ("raise", 5L)))))));
        sheet["skillProficiencies"] = Items("stealth");
        sheet["skillExpertise"] = Items("arcana");

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(2, Skill(evaluation, "stealth").Rank); // proficient, one step up
        Assert.Equal(2, Skill(evaluation, "arcana").Rank); // already at the top: nothing to add
        Assert.Equal(RankSourceKinds.Raise, Skill(evaluation, "stealth").Sources!.Single().Kind);
    }

    [Fact]
    public void ARankAndARaiseTogether_AreRefusedWithAWarning()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Muddled", 3,
            ("adds", Items(Add(("to", "skills"), ("entries", Items("stealth")), ("rank", "expert"), ("raise", 1L)))))));

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(0, Skill(evaluation, "stealth").Rank);
        Assert.Contains(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError");
    }

    [Fact]
    public void ARankGrantToARanksOwnSheetList_IsNotWrittenToTheSheetEither_AndTheOldRecordIsCleanedOnce()
    {
        // An earlier version added the entries to the list on the sheet and recorded them in granted__skillProficiencies.
        var (engine, sheet) = Character(5, Stacker(Row("Old Habit", 3,
            ("adds", Items(Add(("to", "skillProficiencies"), ("entries", Items("stealth"))))))));
        sheet["skillProficiencies"] = Items("perception", "stealth");
        sheet["granted__skillProficiencies"] = Items("stealth");

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(new[] { "perception" }, SheetRuleEngine.RawList(sheet, "skillProficiencies")); // the player's own pick stays; the grant's copy goes
        Assert.False(sheet.ContainsKey("granted__skillProficiencies"));
        Assert.Equal(1, Skill(evaluation, "stealth").Rank); // the feature still gives it, in the evaluation
        Assert.Equal(1, Skill(evaluation, "perception").Rank);

        engine.Recompute(sheet);
        Assert.Equal(new[] { "perception" }, SheetRuleEngine.RawList(sheet, "skillProficiencies"));
        Assert.False(sheet.ContainsKey("granted__skillProficiencies"));
    }

    [Fact]
    public void EmptiedRecord_LeavesNoEmptyList()
    {
        var (engine, sheet) = Character(5);
        sheet["skillProficiencies"] = Items("stealth");
        sheet["granted__skillProficiencies"] = Items("stealth");

        engine.Recompute(sheet);

        Assert.False(sheet.ContainsKey("skillProficiencies"));
        Assert.False(sheet.ContainsKey("granted__skillProficiencies"));
    }

    // ---- Step 3: saving throws ----

    [Fact]
    public void SavingThrows_ComeFromTheClass_AndFromTheirOwnTicksAndGrants()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Slippery Mind", 3, ("adds", Items(Add(("to", "saves"), ("entries", Items("wis"))))))));
        sheet[SkillRules.DefaultSaveKey] = Items("dex");

        var evaluation = engine.Recompute(sheet);

        var charisma = evaluation.SavingThrows.Single(s => s.Key == "cha");
        Assert.Equal(1, charisma.Rank);
        Assert.True(charisma.Proficient);
        Assert.False(charisma.Expert);
        Assert.Equal(3 + 3, charisma.Bonus);
        var source = Assert.Single(charisma.Sources!);
        Assert.Equal((RankSourceKinds.Class, "Sorcerer"), (source.Kind, source.Name));

        var dexterity = evaluation.SavingThrows.Single(s => s.Key == "dex");
        Assert.Equal((1, 1), (dexterity.Rank, dexterity.OwnRank)); // the player may tick saves too
        Assert.Empty(dexterity.Sources!);
        Assert.Equal(2 + 3, dexterity.Bonus);

        var wisdom = evaluation.SavingThrows.Single(s => s.Key == "wis");
        Assert.Equal(1, wisdom.Rank);
        Assert.Equal(RankSourceKinds.Feature, wisdom.Sources!.Single().Kind);
        Assert.Equal(0, evaluation.SavingThrows.Single(s => s.Key == "str").Rank);
        Assert.Equal(6, evaluation.SavingThrows.Count);
    }

    // ---- Step 4: choices ----

    private static Dictionary<string, object> ExpertiseChoice(string id = "expertise-3") =>
        Add(("to", "skills"), ("choice", id), ("choose", 2L), ("minRank", "proficient"), ("rank", "expert"));

    private const string ExpertiseKey = "choice__stacker__expertise-3";

    [Fact]
    public void AChoice_IsPendingUntilMade_AWarningNotAViolation()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Expertise", 3, ("adds", Items(ExpertiseChoice())))));
        sheet["skillProficiencies"] = Items("stealth", "perception", "athletics");

        var evaluation = engine.Recompute(sheet);

        var pending = Assert.Single(evaluation.PendingChoices);
        Assert.Equal(ExpertiseKey, pending.Key);
        Assert.Equal(("Expertise", "Stacker", 2, "skills"), (pending.Label, pending.Source, pending.Count, pending.Target));
        Assert.Equal(2, pending.Rank);
        Assert.False(pending.Raise);
        Assert.False(pending.Adopted);
        Assert.Empty(pending.Made);
        Assert.Equal(new[] { "athletics", "perception", "stealth" }, pending.Options.Select(o => o.Key).Order()); // only the proficient ones
        Assert.Equal("Expertise", pending.Feature.Name);

        var warning = Assert.Single(evaluation.Warnings, w => w.Key == SheetRuleEngine.ChoicePendingKey);
        Assert.Equal(ExpertiseKey, warning.FieldKey);
        Assert.False(evaluation.HasViolations, string.Join("; ", evaluation.Violations.Select(v => v.Message)));
        Assert.Empty(Skill(evaluation, "stealth").Sources!); // nothing granted yet: the tick alone
        Assert.Equal(1, Skill(evaluation, "stealth").Rank);
    }

    [Fact]
    public void MadePicks_Apply_AndTheChoiceIsNoLongerPending()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Expertise", 3, ("adds", Items(ExpertiseChoice())))));
        sheet["skillProficiencies"] = Items("stealth", "perception", "athletics");
        SheetRuleEngine.SetChoice(sheet, ExpertiseKey, new[] { "stealth", "perception" });

        var evaluation = engine.Recompute(sheet);

        Assert.Empty(evaluation.PendingChoices);
        Assert.DoesNotContain(evaluation.Warnings, w => SheetRuleEngine.IsChoiceNotice(w));
        var made = Assert.Single(evaluation.MadeChoices);
        Assert.Equal(new[] { "stealth", "perception" }, made.Made);
        var stealth = Skill(evaluation, "stealth");
        Assert.Equal(2, stealth.Rank);
        Assert.Equal(1, stealth.OwnRank);
        Assert.Equal(2 + 3 * 2, stealth.Bonus);
        Assert.Equal(RankSourceKinds.Choice, stealth.Sources!.Single().Kind);
        Assert.Equal(1, Skill(evaluation, "athletics").Rank);
        Assert.False(evaluation.HasViolations);
        Assert.Equal(new[] { "stealth", "perception" }, SheetRuleEngine.RawList(sheet, ExpertiseKey)); // the pick is stored as a list of keys

        // A partly made choice applies what is made and stays open.
        SheetRuleEngine.SetChoice(sheet, ExpertiseKey, new[] { "stealth" });
        var partly = engine.Recompute(sheet);
        Assert.Equal(2, Skill(partly, "stealth").Rank);
        Assert.Equal(1, Skill(partly, "perception").Rank);
        Assert.Equal(1, Assert.Single(partly.PendingChoices).Remaining);
    }

    [Fact]
    public void MinRank_IsEnforced_AndAPickThatDoesNotFitIsAChooseAgainViolation()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Expertise", 3, ("adds", Items(ExpertiseChoice())))));
        sheet["skillProficiencies"] = Items("stealth", "perception");
        SheetRuleEngine.SetChoice(sheet, ExpertiseKey, new[] { "stealth", "athletics" }); // athletics is not proficient

        var evaluation = engine.Recompute(sheet);

        var stale = Assert.Single(evaluation.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey);
        Assert.Equal(ExpertiseKey, stale.FieldKey);
        Assert.Contains("Athletics", stale.Message);
        Assert.Contains("Choose again", stale.Message);
        Assert.Equal(2, Skill(evaluation, "stealth").Rank);
        Assert.Equal(0, Skill(evaluation, "athletics").Rank); // the stale pick counts nothing
        Assert.Equal(1, Assert.Single(evaluation.PendingChoices).Remaining); // one of two is left to choose
    }

    [Fact]
    public void OnePickTooMany_IsAViolationToo()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Expertise", 3, ("adds", Items(ExpertiseChoice())))));
        sheet["skillProficiencies"] = Items("stealth", "perception", "athletics");
        SheetRuleEngine.SetChoice(sheet, ExpertiseKey, new[] { "stealth", "perception", "athletics" });

        var evaluation = engine.Recompute(sheet);

        Assert.Single(evaluation.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey);
        Assert.Equal(1, Skill(evaluation, "athletics").Rank);
        Assert.Equal(2, Skill(evaluation, "perception").Rank);
    }

    [Fact]
    public void ANarrowedFrom_MakesAnEarlierPickStale()
    {
        var narrowed = Add(("to", "skills"), ("choice", "keen"), ("choose", 1L), ("from", Items("insight", "perception", "survival")));
        var (engine, sheet) = Character(5, Stacker(Row("Keen Senses", 3, ("adds", Items(narrowed)))));
        var key = "choice__stacker__keen";
        SheetRuleEngine.SetChoice(sheet, key, new[] { "athletics" });

        var evaluation = engine.Recompute(sheet);
        Assert.Single(evaluation.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey);
        var pending = Assert.Single(evaluation.PendingChoices);
        Assert.Equal(new[] { "insight", "perception", "survival" }, pending.Options.Select(o => o.Key));

        SheetRuleEngine.SetChoice(sheet, key, new[] { "survival" });
        var fixedUp = engine.Recompute(sheet);
        Assert.False(fixedUp.HasViolations);
        Assert.Equal(1, Skill(fixedUp, "survival").Rank);
    }

    [Fact]
    public void AFilterOnTheKeysOwnFields_NarrowsWhatIsOffered()
    {
        var strong = Add(("to", "skills"), ("choice", "brawn"), ("choose", 1L), ("from", Map(("attribute", Items("str", "dex")))));
        var (engine, sheet) = Character(5, Stacker(Row("Brawn", 3, ("adds", Items(strong)))));

        var options = engine.Recompute(sheet).PendingChoices.Single().Options.Select(o => o.Key).ToList();

        Assert.Contains("athletics", options);
        Assert.Contains("stealth", options);
        Assert.DoesNotContain("arcana", options);
    }

    [Fact]
    public void TheDefaultChoiceId_IsTheRowsSlugAndLevel()
    {
        var plain = Add(("to", "skills"), ("choose", 1L));
        var (engine, sheet) = Character(5, Stacker(Row("Keen Senses", 3, ("adds", Items(plain)))));

        var pending = Assert.Single(engine.Recompute(sheet).PendingChoices);

        Assert.Equal("choice__stacker__keen-senses-3", pending.Key);
        Assert.Equal("keen-senses-3", SheetRuleEngine.DefaultChoiceId("Keen Senses", 3));
        Assert.Equal(("stacker", 1, "keen-senses-3"), SheetRuleEngine.ParseChoiceKey(pending.Key));
        Assert.Equal(("skilled", 2, "skilled"), SheetRuleEngine.ParseChoiceKey(SheetRuleEngine.ChoiceKey("skilled", 2, "skilled")));
    }

    [Fact]
    public void ChoicesAreInOrder_SoSkilledsPicksCanFeedExpertise_AndAPickCannotPropItselfUp()
    {
        var skilled = Feat("skilled", "Skilled", Add(("to", "skills"), ("choice", "skilled"), ("choose", 3L)));
        var (engine, sheet) = Character(5, skilled, Stacker(Row("Expertise", 3, ("adds", Items(ExpertiseChoice())))));
        sheet["feats"] = Items("skilled");
        SheetRuleEngine.SetChoice(sheet, "choice__skilled__skilled", new[] { "stealth", "perception", "athletics" });
        SheetRuleEngine.SetChoice(sheet, ExpertiseKey, new[] { "stealth", "perception" }); // proficient only through Skilled

        var evaluation = engine.Recompute(sheet);

        Assert.DoesNotContain(evaluation.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey);
        Assert.Equal(2, Skill(evaluation, "stealth").Rank);
        Assert.Equal(new[] { RankSourceKinds.Choice, RankSourceKinds.Choice }, Skill(evaluation, "stealth").Sources!.Select(s => s.Kind));
        Assert.Equal(1, Skill(evaluation, "athletics").Rank);

        // Take Skilled's pick away: the expertise pick no longer fits (it would otherwise hold itself up).
        SheetRuleEngine.SetChoice(sheet, "choice__skilled__skilled", new[] { "athletics" });
        var without = engine.Recompute(sheet);
        Assert.Equal(2, without.Violations.Count(v => v.Key == SheetRuleEngine.ChoiceStaleKey));
        Assert.Equal(0, Skill(without, "stealth").Rank);
    }

    [Fact]
    public void ARepeatableFeatTakenTwice_KeepsItsSecondCopysPicksUnderANumberedKey()
    {
        var skilled = Feat("skilled", "Skilled", Add(("to", "skills"), ("choice", "skilled"), ("choose", 3L)));
        var (engine, sheet) = Character(5, skilled);
        sheet["feats"] = Items("skilled", "skilled");

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(new[] { "choice__skilled__skilled", "choice__skilled#2__skilled" }, evaluation.PendingChoices.Select(c => c.Key));

        SheetRuleEngine.SetChoice(sheet, "choice__skilled#2__skilled", new[] { "arcana", "history", "religion" });
        var second = engine.Recompute(sheet);
        Assert.Equal(1, Skill(second, "arcana").Rank);
        Assert.Equal(new[] { "choice__skilled__skilled" }, second.PendingChoices.Select(c => c.Key));
        Assert.Equal("choice__skilled#2__skilled", Assert.Single(second.MadeChoices).Key);
    }

    [Fact]
    public void PicksOfAFeatureThatNoLongerApplies_CountNothing_AndComeBackWithIt()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Expertise", 3, ("adds", Items(ExpertiseChoice())))));
        sheet["skillProficiencies"] = Items("stealth", "perception");
        SheetRuleEngine.SetChoice(sheet, ExpertiseKey, new[] { "stealth", "perception" });

        sheet["level"] = 2L;
        var dropped = engine.Recompute(sheet);
        Assert.Equal(1, Skill(dropped, "stealth").Rank);
        Assert.Empty(dropped.PendingChoices);
        Assert.Empty(dropped.MadeChoices);
        Assert.DoesNotContain(dropped.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey);
        Assert.True(sheet.ContainsKey(ExpertiseKey)); // kept, for when the level returns

        sheet["level"] = 5L;
        Assert.Equal(2, Skill(engine.Recompute(sheet), "stealth").Rank);
    }

    [Fact]
    public void PicksAreDropped_WhenTheirEntryLeavesTheSheet_AndANumberedCopyWhenItsCopyGoes()
    {
        var skilled = Feat("skilled", "Skilled", Add(("to", "skills"), ("choice", "skilled"), ("choose", 3L)));
        var (engine, sheet) = Character(5, skilled, Stacker(Row("Expertise", 3, ("adds", Items(ExpertiseChoice())))));
        sheet["feats"] = Items("skilled", "skilled");
        sheet["skillProficiencies"] = Items("stealth");
        SheetRuleEngine.SetChoice(sheet, "choice__skilled__skilled", new[] { "arcana" });
        SheetRuleEngine.SetChoice(sheet, "choice__skilled#2__skilled", new[] { "history" });
        SheetRuleEngine.SetChoice(sheet, ExpertiseKey, new[] { "stealth" });
        engine.Recompute(sheet);
        engine.PruneChoices(sheet);
        Assert.Contains("choice__skilled#2__skilled", sheet.Keys);

        sheet["feats"] = Items("skilled"); // the second copy goes
        engine.Recompute(sheet);
        Assert.Contains("choice__skilled#2__skilled", sheet.Keys); // editing alone drops nothing
        engine.PruneChoices(sheet); // saving does
        Assert.Contains("choice__skilled__skilled", sheet.Keys);
        Assert.DoesNotContain("choice__skilled#2__skilled", sheet.Keys);

        sheet.Remove("feats"); // the feat goes
        sheet["subclass"] = "draconic-sorcery"; // and so does the subclass
        engine.Recompute(sheet);
        engine.PruneChoices(sheet);
        Assert.DoesNotContain(sheet.Keys, k => k.StartsWith(SheetRuleEngine.ChoicePrefix));
    }

    [Fact]
    public void TakingAFeatOffAndPuttingItBack_WhileEditing_KeepsItsPicks()
    {
        var skilled = Feat("skilled", "Skilled", Add(("to", "skills"), ("choice", "skilled"), ("choose", 1L)));
        var (engine, sheet) = Character(5, skilled);
        sheet["feats"] = Items("skilled");
        SheetRuleEngine.SetChoice(sheet, "choice__skilled__skilled", new[] { "arcana" });
        Assert.Equal(1, Skill(engine.Recompute(sheet), "arcana").Rank);

        sheet.Remove("feats");
        Assert.Equal(0, Skill(engine.Recompute(sheet), "arcana").Rank); // counts nothing while the feat is off

        sheet["feats"] = Items("skilled");
        Assert.Equal(1, Skill(engine.Recompute(sheet), "arcana").Rank); // and is back with it
    }

    [Fact]
    public void AFeatInTwoLists_IsCopyOneAndCopyTwo_SoItsPicksDoNotShareAKey()
    {
        var skilled = Feat("skilled", "Skilled", Add(("to", "skills"), ("choice", "skilled"), ("choose", 1L)));
        skilled.Properties["category"] = "origin"; // an origin feat: the extra origin feats list takes it
        var (engine, sheet) = Character(5, skilled);
        sheet["species"] = "halfling";
        sheet["extraOriginFeats"] = Items("skilled");
        sheet["feats"] = Items("skilled");

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(new[] { "choice__skilled__skilled", "choice__skilled#2__skilled" }.Order(), evaluation.PendingChoices.Select(c => c.Key).Order());
        Assert.DoesNotContain(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError");
    }

    [Fact]
    public void ExpertiseOnATickedSkill_DoesNotExcuseTheTickFromTheClassesSkillCount()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Expertise", 3, ("adds", Items(ExpertiseChoice())))));
        sheet["skillProficiencies"] = Items("arcana", "insight", "religion"); // a sorcerer chooses 2
        SheetRuleEngine.SetChoice(sheet, ExpertiseKey, new[] { "arcana", "insight" }); // expertise needs the proficiency; it gives none

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(2, Skill(evaluation, "arcana").Rank);
        Assert.Contains(evaluation.Warnings, w => w.Key == "Actor.Rule.SkillChoices");
    }

    [Fact]
    public void TheirOwnTicks_AreAdoptedAsThePicks_AndConfirmingWritesThemWithoutLosingARank()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Expertise", 3, ("adds", Items(ExpertiseChoice())))));
        sheet["skillProficiencies"] = Items("stealth", "perception", "athletics");
        sheet["skillExpertise"] = Items("stealth", "perception"); // ticked by hand, before the feature granted a choice
        var asTicked = engine.Evaluate(sheet);

        var evaluation = engine.Recompute(sheet);

        var adopted = Assert.Single(evaluation.PendingChoices);
        Assert.True(adopted.Adopted);
        Assert.Equal(new[] { "perception", "stealth" }, adopted.Made); // in the system's order of skills
        Assert.Contains(evaluation.Warnings, w => w.Key == SheetRuleEngine.ChoiceConfirmKey && w.FieldKey == ExpertiseKey);
        Assert.False(evaluation.HasViolations);
        Assert.False(sheet.ContainsKey(ExpertiseKey)); // nothing is written until they confirm
        Assert.Equal(asTicked.Skills.Select(s => (s.Key, s.Rank, s.Bonus)), evaluation.Skills.Select(s => (s.Key, s.Rank, s.Bonus))); // no rank lost, none gained
        Assert.Equal(RankSourceKinds.Choice, Skill(evaluation, "stealth").Sources!.Single().Kind);

        SheetRuleEngine.SetChoice(sheet, adopted.Key, adopted.Made);
        var confirmed = engine.Recompute(sheet);
        Assert.Empty(confirmed.PendingChoices);
        Assert.Equal(asTicked.Skills.Select(s => (s.Key, s.Rank, s.Bonus)), confirmed.Skills.Select(s => (s.Key, s.Rank, s.Bonus)));
        Assert.DoesNotContain(confirmed.Warnings, w => SheetRuleEngine.IsChoiceNotice(w));
    }

    [Fact]
    public void TooFewOfTheirOwnTicks_AreNotAdopted_AndStillCount()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Expertise", 3, ("adds", Items(ExpertiseChoice())))));
        sheet["skillProficiencies"] = Items("stealth", "perception");
        sheet["skillExpertise"] = Items("stealth"); // the choice asks for two

        var evaluation = engine.Recompute(sheet);

        var pending = Assert.Single(evaluation.PendingChoices);
        Assert.False(pending.Adopted);
        Assert.Empty(pending.Made);
        Assert.Equal(2, Skill(evaluation, "stealth").Rank);
        Assert.Empty(Skill(evaluation, "stealth").Sources!);
    }

    [Fact]
    public void ChoiceCount_CountsOnlyThePlayersOwnTicks_NotWhatAFeatureOrAChoiceGave()
    {
        var skilled = Feat("skilled", "Skilled", Add(("to", "skills"), ("choice", "skilled"), ("choose", 1L)));
        var (plain, bare) = Character(5);
        bare["skillProficiencies"] = Items("arcana", "insight", "religion"); // a sorcerer chooses 2
        Assert.Contains(plain.Recompute(bare).Warnings, w => w.Key == "Actor.Rule.SkillChoices");

        var (engine, sheet) = Character(5, skilled);
        sheet["feats"] = Items("skilled");
        sheet["skillProficiencies"] = Items("arcana", "insight", "religion");
        SheetRuleEngine.SetChoice(sheet, "choice__skilled__skilled", new[] { "religion" }); // Skilled gave religion, so 2 are the player's own
        Assert.DoesNotContain(engine.Recompute(sheet).Warnings, w => w.Key == "Actor.Rule.SkillChoices");
    }

    [Fact]
    public void AnOpenChoice_IsNotAViolationTheLevelUpBroke_AndIsListedAsOneItOpened()
    {
        var (engine, sheet) = Character(2, Stacker(Row("Expertise", 3, ("adds", Items(ExpertiseChoice())))));
        sheet["skillProficiencies"] = Items("stealth", "perception");
        var before = engine.Recompute(sheet);
        sheet["level"] = 3L;
        var after = engine.Recompute(sheet);

        var plan = LevelUpPlan.Build(_loader.GetDefinition("srd5e")!.SheetRules!, before, after, 3, null, 3, null, "proficiencyBonus");

        Assert.Empty(before.PendingChoices);
        Assert.Single(after.PendingChoices);
        Assert.DoesNotContain(plan.NewViolations, SheetRuleEngine.IsChoiceNotice);
        Assert.Empty(plan.NewViolations);
        Assert.Equal(ExpertiseKey, Assert.Single(plan.NewChoices!).Key);
    }

    [Fact]
    public void ADuplicateChoiceIdInOneEntry_IsWarnedAbout_AndTheSecondIsLeftOut()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Twice", 3, ("adds", Items(ExpertiseChoice("same"), ExpertiseChoice("same"))))));
        sheet["skillProficiencies"] = Items("stealth", "perception");

        var evaluation = engine.Recompute(sheet);

        Assert.Single(evaluation.PendingChoices);
        Assert.Contains(evaluation.Warnings, w => w.Key == "Actor.Rule.FeatureError");
    }

    // ---- A system with four ranks ----

    private sealed class ListResolver(params ContentEntry[] entries) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => entries.FirstOrDefault(e => e.Type == type && e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        public IEnumerable<ContentEntry> Candidates(string type) => entries.Where(e => e.Type == type);
    }

    private static SystemDefinition Pathlike() => new()
    {
        Id = "pf",
        Name = "Pathlike",
        Attributes =
        {
            new AttributeDefinition { Key = "str", Label = "Strength", Abbreviation = "STR", Default = 10 },
            new AttributeDefinition { Key = "dex", Label = "Dexterity", Abbreviation = "DEX", Default = 10 }
        },
        ActorFields =
        {
            new FieldDefinition("level", "Level", FieldType.Number) { Min = 1, Max = 20, DefaultValue = 1L },
            new FieldDefinition("class", "Class", FieldType.EntityReference) { TargetEntityType = "class" }
        },
        EntityTypes = { new ContentTypeDefinition { Type = "class" }, new ContentTypeDefinition { Type = "feat" } },
        SheetRules = new SheetRules
        {
            Lists = { new ListRule { Key = "feats", Label = "Feats", TargetEntityType = "feat" } },
            Features =
            {
                new FeatureSource { Field = "class", Property = "features", ByField = "level" },
                new FeatureSource { List = "feats" }
            },
            Skills = new SkillRules
            {
                BonusKey = "unused",
                List =
                {
                    new SkillDefinition { Key = "athletics", Label = "Athletics", Attribute = "str" },
                    new SkillDefinition { Key = "stealth", Label = "Stealth", Attribute = "dex" },
                    new SkillDefinition { Key = "acrobatics", Label = "Acrobatics", Attribute = "dex" }
                },
                Ranks =
                {
                    new RankDefinition { Key = "trained", Label = "Trained", SheetKey = "skillsTrained", Value = "level + 2" },
                    new RankDefinition { Key = "expert", Label = "Expert", SheetKey = "skillsExpert", Value = "level + 4" },
                    new RankDefinition { Key = "master", Label = "Master", SheetKey = "skillsMaster", Value = "level + 6" },
                    new RankDefinition { Key = "legendary", Label = "Legendary", SheetKey = "skillsLegendary", Value = "level + 8" }
                },
                Saves = new SaveRules
                {
                    Ranks =
                    {
                        new RankDefinition { Key = "trained", SheetKey = "savesTrained", Value = "level + 2" },
                        new RankDefinition { Key = "expert", SheetKey = "savesExpert", Value = "level + 4" }
                    }
                }
            }
        }
    };

    private static (SheetRuleEngine Engine, Dictionary<string, object> Sheet) Fighter(int level)
    {
        var champion = new ContentEntry { Type = "class", Id = "champion", Name = "Champion", SystemId = "pf" };
        champion.Properties["savingThrows"] = Items("str");
        champion.Properties["features"] = Items(
            Row("Weapon Training", 1, ("adds", Items(Add(("to", "skills"), ("entries", Items("athletics")), ("rank", "trained")), Add(("to", "saves"), ("entries", Items("dex")), ("rank", 2L))))),
            Row("Skill Increase", 3, ("adds", Items(Add(("to", "skills"), ("choice", "increase-3"), ("choose", 1L), ("raise", 1L), ("maxRank", "master"))))),
            Row("Skill Increase", 5, ("adds", Items(Add(("to", "skills"), ("choice", "increase-5"), ("choose", 1L), ("raise", 1L), ("maxRank", "trained"))))),
            Row("Legend", 5, ("adds", Items(Add(("to", "skills"), ("choice", "legend"), ("choose", 1L), ("minRank", "master"), ("rank", "legendary"))))));
        var engine = new SheetRuleEngine(Pathlike(), new ListResolver(champion));
        var sheet = new Dictionary<string, object> { ["class"] = "champion", ["level"] = (long)level };
        return (engine, sheet);
    }

    [Fact]
    public void AFourRankSystem_ValuesEachRankByItsOwnFormula_AndRaisesStack()
    {
        var (engine, sheet) = Fighter(5);
        sheet["skillsTrained"] = Items("stealth");
        SheetRuleEngine.SetChoice(sheet, "choice__champion__increase-3", new[] { "stealth" }); // trained to expert
        SheetRuleEngine.SetChoice(sheet, "choice__champion__increase-5", new[] { "stealth" }); // maxRank trained: it is expert now

        var evaluation = engine.Evaluate(sheet);

        var athletics = Skill(evaluation, "athletics");
        Assert.Equal(("Trained", 1, 5 + 2), (athletics.RankLabel, athletics.Rank, athletics.Bonus));
        var stealth = Skill(evaluation, "stealth");
        Assert.Equal(("Expert", 2, 5 + 4), (stealth.RankLabel, stealth.Rank, stealth.Bonus));
        Assert.Contains(evaluation.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey && v.FieldKey == "choice__champion__increase-5");
        Assert.Equal(0, Skill(evaluation, "acrobatics").Bonus);
        Assert.Equal(0, Skill(evaluation, "acrobatics").Rank);

        // The second raise, aimed at a skill that is still trained, stacks on the first.
        SheetRuleEngine.SetChoice(sheet, "choice__champion__increase-5", new[] { "athletics" });
        Assert.Equal(2, Skill(engine.Evaluate(sheet), "athletics").Rank);
    }

    [Fact]
    public void AFourRankSystem_MinRankIsReadAgainstTheRanksSoFar_AndTheTopRankIsTheCap()
    {
        var (engine, sheet) = Fighter(5);
        sheet["skillsMaster"] = Items("stealth");
        sheet["skillsTrained"] = Items("athletics");
        SheetRuleEngine.SetChoice(sheet, "choice__champion__legend", new[] { "athletics" }); // only trained

        var stale = engine.Evaluate(sheet);
        Assert.Contains(stale.Violations, v => v.Key == SheetRuleEngine.ChoiceStaleKey && v.FieldKey == "choice__champion__legend");
        Assert.Equal(1, Skill(stale, "athletics").Rank);

        SheetRuleEngine.SetChoice(sheet, "choice__champion__legend", new[] { "stealth" });
        var legendary = engine.Evaluate(sheet);
        Assert.Equal(("Legendary", 4, 5 + 8), (Skill(legendary, "stealth").RankLabel, Skill(legendary, "stealth").Rank, Skill(legendary, "stealth").Bonus));
        Assert.DoesNotContain(legendary.Violations, v => v.FieldKey == "choice__champion__legend");

        // A raise cannot pass the top: a skill the player holds at legendary is no option.
        sheet["skillsLegendary"] = Items("athletics");
        var topped = engine.Evaluate(sheet);
        Assert.DoesNotContain(topped.PendingChoices.Concat(topped.MadeChoices), c => c.Raise && c.Options.Any(o => o.Key == "athletics"));
        Assert.Equal(4, Skill(topped, "athletics").Rank);
    }

    [Fact]
    public void AFourRankSystem_SavesAreARankedListToo_WithTheClassAsAFixedSource()
    {
        var (engine, sheet) = Fighter(5);
        sheet["savesExpert"] = Items("str");

        var evaluation = engine.Evaluate(sheet);

        var strength = evaluation.SavingThrows.Single(s => s.Key == "str");
        Assert.Equal((2, 5 + 4), (strength.Rank, strength.Bonus)); // the class gives trained; the player's own tick is higher
        var dexterity = evaluation.SavingThrows.Single(s => s.Key == "dex");
        Assert.Equal((2, 5 + 4), (dexterity.Rank, dexterity.Bonus)); // a grant to rank 2 by number
        Assert.Equal(RankSourceKinds.Class, strength.Sources!.Single().Kind);
    }

    [Fact]
    public void ARepeatableFeatsChoice_IsKeyedByItsEntryAndCopy_InAnyRankedSystem()
    {
        var skilled = new ContentEntry { Type = "feat", Id = "skilled", Name = "Skilled", SystemId = "pf" };
        skilled.Properties["repeatable"] = true;
        skilled.Properties["adds"] = Items(Add(("to", "skills"), ("choice", "skilled"), ("choose", 1L)));
        var system = Pathlike();
        system.SheetRules!.Lists[0].RepeatableProperty = "repeatable";
        var engine = new SheetRuleEngine(system, new ListResolver(skilled));
        var sheet = new Dictionary<string, object> { ["level"] = 1L, ["feats"] = Items("skilled", "skilled") };

        var evaluation = engine.Evaluate(sheet);

        Assert.Equal(new[] { "choice__skilled__skilled", "choice__skilled#2__skilled" }, evaluation.PendingChoices.Select(c => c.Key));
        Assert.Empty(evaluation.Violations);
    }

    // ---- Steps 5 to 8: what the sheet, the stepper, the editor and the drafter build on ----

    public void APlainChooseN_AdoptsOnlyTheTicksBeyondTheClassAllowance_SoAnOverPickStaysVisibleOtherwise()
    {
        var skilled = Feat("skilled", "Skilled", Add(("to", "skills"), ("choice", "skilled"), ("choose", 2L)));
        var (engine, sheet) = Character(5, skilled);
        sheet["feats"] = Items("skilled");
        // A sorcerer picks two skills; four ticks leave two spare, which cover what Skilled asks for.
        sheet["skillProficiencies"] = Items("arcana", "insight", "religion", "stealth");

        var adopted = Assert.Single(engine.Recompute(sheet).PendingChoices);
        Assert.True(adopted.Adopted);
        Assert.Equal(2, adopted.Made.Count);

        // Only the two the class allows: nothing is spare, so the pick is still to make and nothing is taken as made.
        sheet["skillProficiencies"] = Items("arcana", "insight");
        var evaluation = engine.Recompute(sheet);
        var pending = Assert.Single(evaluation.PendingChoices);
        Assert.False(pending.Adopted);
        Assert.Empty(pending.Made);
        Assert.DoesNotContain(evaluation.Warnings, w => w.Key == SheetRuleEngine.ChoiceConfirmKey);
        Assert.Contains(evaluation.Warnings, w => w.Key == SheetRuleEngine.ChoicePendingKey);

        // Three ticks and a pick of two: one is spare, one short of the choice. It stays open and the class over-pick is said.
        sheet["skillProficiencies"] = Items("arcana", "insight", "religion");
        var over = engine.Recompute(sheet);
        Assert.False(Assert.Single(over.PendingChoices).Adopted);
        Assert.Contains(over.Warnings, w => w.Key == "Actor.Rule.SkillChoices");
    }

    [Fact]
    public void ALockedTick_KnowsItsSource_AndAnOwnTickAtOrBelowItIsLocked()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Eyes", 1, ("adds", Items(Add(("to", "skills"), ("entries", Items("perception")), ("rank", "expert")))))));
        sheet["skillProficiencies"] = Items("perception");

        var perception = Skill(engine.Recompute(sheet), "perception");

        Assert.Equal(2, perception.GrantedRank);
        Assert.Equal("Stacker: Eyes", perception.LockedBy(1)!.Name); // proficient is locked too: expert is above it
        Assert.Equal("Stacker: Eyes", perception.LockedBy(2)!.Name);
        Assert.Null(Skill(engine.Recompute(sheet), "stealth").LockedBy(1));
        Assert.Equal(0, Skill(engine.Recompute(sheet), "stealth").GrantedRank);
    }

    [Fact]
    public void SettingTheirOwnRank_PutsTheKeyInThatRanksListOnly_AndZeroClearsIt()
    {
        var ranks = _loader.GetDefinition("srd5e")!.SheetRules!.Skills!.SkillRanks();
        var sheet = Map(("skillProficiencies", Items("stealth", "arcana")), ("skillExpertise", Items("stealth")));

        SheetRuleEngine.SetOwnRank(sheet, ranks, 2, "stealth", "Stealth");
        Assert.Equal(new[] { "arcana" }, SheetRuleEngine.RawList(sheet, "skillProficiencies"));
        Assert.Equal(new[] { "stealth" }, SheetRuleEngine.RawList(sheet, "skillExpertise"));

        SheetRuleEngine.SetOwnRank(sheet, ranks, 0, "stealth", "Stealth");
        Assert.False(sheet.ContainsKey("skillExpertise")); // no empty list is kept
        Assert.Equal(new[] { "arcana" }, SheetRuleEngine.RawList(sheet, "skillProficiencies"));
    }

    [Fact]
    public void ScoresCarryWhatTheirRankIsWorth_AndTheSheetListsWhatEveryRankIsWorth()
    {
        var (engine, sheet) = Character(5);
        sheet["skillProficiencies"] = Items("stealth", "arcana");
        sheet["skillExpertise"] = Items("arcana");

        var evaluation = engine.Recompute(sheet);

        Assert.Equal(3, Skill(evaluation, "stealth").RankBonus);
        Assert.Equal(6, Skill(evaluation, "arcana").RankBonus);
        Assert.Equal(0, Skill(evaluation, "history").RankBonus);
        Assert.Equal(new[] { (SheetRuleEngine.SkillsTarget, "proficient", 3), (SheetRuleEngine.SkillsTarget, "expert", 6) },
            evaluation.RankValues.Where(r => r.Target == SheetRuleEngine.SkillsTarget).Select(r => (r.Target, r.Key, r.Value)));
    }

    [Fact]
    public void TheLevelUp_SaysHowARankIsWorthMore_AndKeepsChoicesTheStepperHasMade()
    {
        var (engine, sheet) = Character(4, Stacker(Row("Expertise", 5, ("adds", Items(ExpertiseChoice("expertise-5"))))));
        sheet["skillProficiencies"] = Items("stealth", "perception");
        var before = engine.Recompute(sheet);
        var copy = new Dictionary<string, object>(sheet) { ["level"] = 5L };
        var opened = engine.Evaluate(copy);
        SheetRuleEngine.SetChoice(copy, "choice__stacker__expertise-5", new[] { "stealth", "perception" });
        var picked = engine.Evaluate(copy);
        var rules = _loader.GetDefinition("srd5e")!.SheetRules!;

        var open = LevelUpPlan.Build(rules, before, opened, 5, null, 5, null, "proficiencyBonus");
        var made = LevelUpPlan.Build(rules, before, picked, 5, null, 5, null, "proficiencyBonus");

        Assert.Equal(new[] { ("proficient", 2, 3), ("expert", 4, 6) }, open.RankChanges!.Select(c => (c.Key, c.Before, c.After)));
        Assert.Equal(2, Assert.Single(open.NewChoices!).Remaining); // two asked, none made
        Assert.Empty(Assert.Single(open.NewChoices!).Made);
        var kept = Assert.Single(made.NewChoices!); // still listed once made, so the picker stays
        Assert.Equal(new[] { "stealth", "perception" }, kept.Made);
        Assert.Equal(new[] { "Stealth", "Perception" }, kept.MadeLabels);
    }

    [Fact]
    public void APickerToggle_IsCappedAtTheCount_AndOnlyTakesWhatIsOffered()
    {
        var (engine, sheet) = Character(5, Stacker(Row("Expertise", 3, ("adds", Items(ExpertiseChoice())))));
        sheet["skillProficiencies"] = Items("stealth", "perception", "athletics");
        var choice = engine.Recompute(sheet).PendingChoices.Single();

        var one = choice.Toggled("stealth", true);
        Assert.Equal(new[] { "stealth" }, one);
        SheetRuleEngine.SetChoice(sheet, choice.Key, new[] { "stealth", "perception" });
        var full = engine.Recompute(sheet).MadeChoices.Single();

        Assert.Equal(new[] { "stealth", "perception" }, full.Toggled("athletics", true)); // two asked: a third is not taken
        Assert.Equal(new[] { "perception" }, full.Toggled("stealth", false));
        Assert.Equal(new[] { "stealth", "perception" }, full.Toggled("arcana", true)); // not offered
    }

    [Fact]
    public void RankLabels_AreTheSystemsOwn_InTheLanguageItHasThemIn()
    {
        var english = _loader.GetDefinition("srd5e")!.SheetRules!.Skills!;
        var russian = _loader.GetDefinition("srd5e")!.Localize("ru").SheetRules!.Skills!;
        var hebrew = _loader.GetDefinition("srd5e")!.Localize("he").SheetRules!.Skills!;

        Assert.Equal(new[] { "Proficient", "Expert" }, english.SkillRanks().Select(r => r.Label));
        Assert.Equal(new[] { "Владение", "Компетентность" }, russian.SkillRanks().Select(r => r.Label));
        Assert.Equal(new[] { "מיומן" }, hebrew.SaveRanks().Select(r => r.Label));
        Assert.Equal(new[] { "proficient", "expert" }, russian.SkillRanks().Select(r => r.Key)); // keys and sheet lists do not move
        Assert.Equal(new[] { "skillProficiencies", "skillExpertise" }, russian.SkillRanks().Select(r => r.SheetKey));

        var (engine, sheet) = Character(5);
        var localized = new SheetRuleEngine(_loader.GetDefinition("srd5e")!.Localize("ru"), new WithHomebrew(_registry));
        sheet["skillProficiencies"] = Items("stealth");
        sheet["skillExpertise"] = Items("stealth");
        Assert.Equal("Компетентность", Skill(localized.Recompute(sheet), "stealth").RankLabel);
        Assert.Equal("Expert", Skill(engine.Recompute(sheet), "stealth").RankLabel);
    }

    private sealed class WithHomebrew(ContentRegistry registry, params ContentEntry[] extra) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => extra.FirstOrDefault(e => e.Type == type && e.Id == id) ?? registry.GetEntries("srd5e", type).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("srd5e", type).Concat(extra.Where(e => e.Type == type));
    }
}
