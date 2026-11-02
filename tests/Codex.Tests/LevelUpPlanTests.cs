using Codex.Core.Rules;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>U4: what the level-up stepper says the next level brings, from the sheet evaluated now and at that level.</summary>
public class LevelUpPlanTests
{
    private static ContentEntry Fighter() => new()
    {
        Id = "fighter",
        Type = "class",
        Name = "Fighter",
        Properties = new Dictionary<string, object>
        {
            ["features"] = new List<object>
            {
                new Dictionary<string, object> { ["name"] = "Second Wind", ["level"] = "1", ["description"] = "Heal." },
                new Dictionary<string, object> { ["name"] = "Fighter Subclass", ["level"] = "3", ["description"] = "Pick one." },
                new Dictionary<string, object> { ["name"] = "Ability Score Improvement", ["level"] = 4L, ["description"] = "A feat." },
                new Dictionary<string, object> { ["level"] = "4", ["description"] = "A row without a name." }
            }
        }
    };

    private static SheetRules Rules() => new()
    {
        Lists = { new ListRule { Key = "feats", Label = "Feats" }, new ListRule { Key = "spells", Label = "Spells" } },
        Multiclass = new MulticlassRules { SubclassField = "subclass", SubclassLevel = 3 }
    };

    [Fact]
    public void FeaturesAt_TakesTheRowsOfThatLevelOnly_AndSkipsNamelessOnes()
    {
        var features = LevelUpPlan.FeaturesAt(Fighter(), 4);

        var feature = Assert.Single(features);
        Assert.Equal("Ability Score Improvement", feature.Name);
        Assert.Equal("Fighter", feature.Source);
        Assert.Empty(LevelUpPlan.FeaturesAt(Fighter(), 2));
        Assert.Empty(LevelUpPlan.FeaturesAt(null, 1));
    }

    [Fact]
    public void Build_ListsTheFeatures_TheGrownLimits_AndWhatTheLevelBreaks()
    {
        var before = new SheetEvaluation { HitPoints = new HitPointSummary("average", true, 28, 2, Array.Empty<HitPointLevel>()) };
        before.ListLimits["feats"] = 0;
        before.ListLimits["spells"] = null;
        before.Derived["proficiencyBonus"] = 2L;
        before.Violations.Add(new RuleViolation("name", "Already wrong."));

        var after = new SheetEvaluation
        {
            HitPoints = new HitPointSummary("average", true, 36, 2, new[] { new HitPointLevel(4, "Fighter", 10, 6, false, 2, 8) })
        };
        after.ListLimits["feats"] = 1;
        after.ListLimits["spells"] = null;
        after.Derived["proficiencyBonus"] = 2L;
        after.Violations.Add(new RuleViolation("name", "Already wrong."));
        after.Violations.Add(new RuleViolation("multiclass", "The level broke this."));

        var plan = LevelUpPlan.Build(Rules(), before, after, 4, Fighter(), 4, null, "proficiencyBonus");

        Assert.Equal("Ability Score Improvement", Assert.Single(plan.Features).Name);
        var choice = Assert.Single(plan.Choices);
        Assert.Equal(("feats", 1), (choice.Key, choice.Added));
        Assert.Equal(8, plan.HitPoints?.Gain);
        Assert.Equal((28, 36), (plan.HitPointsBefore, plan.HitPointsAfter));
        Assert.Equal((2, 2), (plan.ProficiencyBefore, plan.ProficiencyAfter));
        Assert.Equal("The level broke this.", Assert.Single(plan.NewViolations).Message);
        // Level 4 is past the subclass level, and the class has none.
        Assert.True(plan.NeedsSubclass);
    }

    [Fact]
    public void Build_AskForNoSubclass_BeforeItsLevel_OrWhenOneIsChosen()
    {
        var subclass = new ContentEntry { Id = "champion", Type = "subclass", Name = "Champion" };

        Assert.False(LevelUpPlan.Build(Rules(), new SheetEvaluation(), new SheetEvaluation(), 2, Fighter(), 2, null, null).NeedsSubclass);
        Assert.True(LevelUpPlan.Build(Rules(), new SheetEvaluation(), new SheetEvaluation(), 3, Fighter(), 3, null, null).NeedsSubclass);
        Assert.False(LevelUpPlan.Build(Rules(), new SheetEvaluation(), new SheetEvaluation(), 3, Fighter(), 3, subclass, null).NeedsSubclass);
    }
}
