using Codex.Core.Systems;

namespace Codex.Tests;

/// <summary>A system's own mistakes in the newer sheet rules are reported when it loads, not at the table.</summary>
public class SheetRuleValidationTests
{
    /// <summary>The problems a system with these sheet rules is refused for (empty: it loads).</summary>
    private static string Errors(string rules)
    {
        try
        {
            SystemDefinitionLoader.Parse("""
        id: demo
        name: Demo
        attributes:
          - { key: str, label: Strength, abbreviation: STR, default: 10 }
        actorFields:
          - { key: level, label: Level, type: Number, defaultValue: 1 }
          - { key: class, label: Class, type: EntityReference, targetEntityType: class }
          - { key: species, label: Species, type: EntityReference, targetEntityType: species }
          - { key: speed, label: Speed, type: Number }
        entityTypes:
          - { type: class, label: Class }
          - { type: species, label: Species }
        sheetRules:
          {RULES}
        """.Replace("{RULES}", rules));
            return string.Empty;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }

    [Fact]
    public void SkillsMustUseARealAttribute_AndBeUniqueAndHaveABonusToAdd()
    {
        var errors = Errors("""
            derived: []
            skills:
              bonusKey: proficiencyBonus
              list:
                - { key: climb, label: Climb, attribute: nope }
                - { key: climb, label: Climb again, attribute: str }
              passives:
                - { key: passiveSwim, label: Passive Swim, skill: swim }
            """.Replace("\n", "\n  "));
        Assert.Contains("attribute 'nope'", errors);
        Assert.Contains("'climb' is declared more than once", errors);
        Assert.Contains("skill 'swim'", errors);
        Assert.Contains("bonusKey 'proficiencyBonus'", errors);
    }

    [Fact]
    public void InheritedValues_NeedAReferenceFieldAndAKeyOutsideTheActorFields()
    {
        var errors = Errors("""
            inherits:
              - { field: speed, appliedKey: speed, copy: { speed: speed } }
            """.Replace("\n", "\n  "));
        Assert.Contains("'speed' is not an EntityReference", errors);
        Assert.Contains("appliedKey", errors);

        Assert.Equal(string.Empty, Errors("""
            inherits:
              - { field: species, appliedKey: speciesApplied, copy: { speed: speed } }
            """.Replace("\n", "\n  ")));
    }

    [Fact]
    public void Ranks_NeedUniqueKeysAndSheetKeys_AndFormulasThatCanBeRead()
    {
        var errors = Errors("""
            derived:
              - { key: proficiencyBonus, label: Proficiency, formula: "2" }
            skills:
              bonusKey: proficiencyBonus
              list:
                - { key: climb, label: Climb, attribute: str }
              ranks:
                - { key: trained, sheetKey: skillsTrained, value: "level + 2" }
                - { key: trained, sheetKey: skillsTrained }
                - { key: expert, sheetKey: level }
                - { key: master, sheetKey: skillsMaster, value: "level +" }
                - { key: "3", sheetKey: skillsThree }
              saves:
                ranks:
                  - { key: proficient, sheetKey: skillsTrained }
            """.Replace("\n", "\n  "));
        Assert.Contains("sheetRules.skills.ranks key 'trained' is declared more than once", errors);
        Assert.Contains("sheetKey 'skillsTrained' is used by more than one rank", errors);
        Assert.Contains("sheetKey 'level' collides with an actor field", errors);
        Assert.Contains("'master' value 'level +' cannot be read", errors);
        Assert.Contains("key '3' cannot be a number", errors);

        Assert.Equal(string.Empty, Errors("""
            derived:
              - { key: proficiencyBonus, label: Proficiency, formula: "2" }
            skills:
              bonusKey: proficiencyBonus
              list:
                - { key: climb, label: Climb, attribute: str }
              ranks:
                - { key: trained, sheetKey: skillsTrained, value: "level + 2" }
                - { key: expert, sheetKey: skillsExpert, value: "level + 4" }
              saves:
                ranks:
                  - { key: proficient, sheetKey: savesProficient, value: proficiencyBonus }
            """.Replace("\n", "\n  ")));
    }

    [Fact]
    public void AFourRankSystem_NeedsNoBonusKey_WhenEveryListNamesItsRanks_ButADefaultListDoes()
    {
        // The design's Pathfinder-like yaml: each rank carries its own value, so there is no bonus to name.
        Assert.Equal(string.Empty, Errors("""
            skills:
              list:
                - { key: climb, label: Climb, attribute: str }
              ranks:
                - { key: trained,   label: Trained,   sheetKey: skillsTrained,   value: "level + 2" }
                - { key: expert,    label: Expert,    sheetKey: skillsExpert,    value: "level + 4" }
                - { key: master,    label: Master,    sheetKey: skillsMaster,    value: "level + 6" }
                - { key: legendary, label: Legendary, sheetKey: skillsLegendary, value: "level + 8" }
              saves:
                ranks:
                  - { key: trained, label: Trained, sheetKey: savesTrained, value: "level + 2" }
            """.Replace("\n", "\n  ")));

        // Saves left to their default are worth the bonus, so it is asked for again.
        var errors = Errors("""
            skills:
              list:
                - { key: climb, label: Climb, attribute: str }
              ranks:
                - { key: trained, sheetKey: skillsTrained, value: "level + 2" }
            """.Replace("\n", "\n  "));
        Assert.Contains("sheetRules.skills.bonusKey is required", errors);
    }
}

public class ViewerClockTests
{
    [Fact]
    public void Format_ConvertsStoredUtcIntoTheViewersZone()
    {
        var clock = new Codex.Web.Services.ViewerClock();
        var stored = new DateTime(2026, 10, 21, 22, 30, 0, DateTimeKind.Utc);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"); // UTC+13 in October
        typeof(Codex.Web.Services.ViewerClock).GetField("_zone", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(clock, zone);

        // Format is culture-sensitive by design (viewers see their own month names), so pin the
        // culture; otherwise the machine's locale decides what "MMM" renders.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("22 Oct 11:30", clock.Format(stored, "d MMM HH:mm"));
            Assert.Equal("22 Oct 11:30", clock.Format(DateTime.SpecifyKind(stored, DateTimeKind.Unspecified), "d MMM HH:mm"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
