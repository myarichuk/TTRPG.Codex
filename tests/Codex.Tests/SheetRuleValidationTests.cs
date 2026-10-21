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

        Assert.Equal("22 Oct 11:30", clock.Format(stored, "d MMM HH:mm"));
        Assert.Equal("22 Oct 11:30", clock.Format(DateTime.SpecifyKind(stored, DateTimeKind.Unspecified), "d MMM HH:mm"));
    }
}
