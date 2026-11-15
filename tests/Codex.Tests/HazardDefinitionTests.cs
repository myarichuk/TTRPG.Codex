using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The <c>hazard:</c> block of an entity type and <c>hazards.scaling</c>: what the loader reads and what it refuses.</summary>
public class HazardDefinitionTests
{
    private const string Fields =
        "    fields:\n"
        + "      - { key: level, label: Level, type: Number }\n"
        + "      - { key: noticeDc, label: Notice DC, type: Number }\n"
        + "      - { key: noticeCheck, label: Noticed with, type: Text }\n"
        + "      - { key: disableDc, label: Disable DC, type: Number }\n"
        + "      - { key: trigger, label: Trigger, type: LongText }\n"
        + "      - { key: damage, label: Damage, type: Text }\n"
        + "      - { key: complex, label: Complex, type: Boolean }\n"
        + "      - { key: routine, label: Routine, type: Collection }\n";

    /// <summary>A system with one hazard type. <paramref name="hazard"/> is the flat body of its <c>hazard:</c> block (one line per part),
    /// <paramref name="extra"/> top-level YAML after the entity types, <paramref name="typeExtra"/> one more line in the type.</summary>
    private static string Yaml(string hazard, string extra = "", string typeExtra = "") =>
        "id: hazard-test\nname: Hazard test\nentityTypes:\n  - type: hazard\n    label: Hazard\n"
        + (typeExtra.Length > 0 ? "    " + typeExtra.Trim() + "\n" : "")
        + Fields + "    hazard:\n"
        + string.Concat(hazard.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => "      " + l + "\n"))
        + extra;

    private static IReadOnlyList<string> Errors(string yaml)
    {
        try
        {
            SystemDefinitionLoader.Parse(yaml);
            return Array.Empty<string>();
        }
        catch (InvalidDataException ex)
        {
            return SystemDefinitionLoader.ErrorsOf(ex);
        }
    }

    [Fact]
    public void TheHazardBlock_IsRead()
    {
        var system = SystemDefinitionLoader.Parse(Yaml("""
                notice: { dc: noticeDc, check: noticeCheck, passive: true }
                disable: { dc: disableDc }
                trigger: trigger
                effect: { damage: damage }
                level: level
                complex: complex
                routine: routine
            """));

        var hazard = system.FindType("hazard")!.Hazard!;
        Assert.Equal("noticeDc", hazard.Notice!.Dc);
        Assert.True(hazard.Notice.Passive);
        Assert.Equal("damage", hazard.Effect!.Damage);
        Assert.Equal("complex", hazard.Complex);
    }

    [Fact]
    public void AnUnknownField_IsRefused()
    {
        var errors = Errors(Yaml("""
                notice: { dc: noticeDcc }
            """));

        Assert.Contains(errors, e => e.Contains("notice.dc") && e.Contains("noticeDcc"));
    }

    [Fact]
    public void AFieldOfTheWrongKind_IsRefused()
    {
        var errors = Errors(Yaml("""
                notice: { dc: noticeCheck }
                complex: level
            """));

        Assert.Contains(errors, e => e.Contains("notice.dc") && e.Contains("Number"));
        Assert.Contains(errors, e => e.Contains("hazard.complex") && e.Contains("Boolean"));
    }

    [Fact]
    public void ARank_NeedsRankedSkills()
    {
        var unranked = Errors(Yaml("""
                notice: { dc: noticeDc, rank: noticeCheck }
            """));
        Assert.Contains(unranked, e => e.Contains("notice.rank") && e.Contains("ranked skills"));

        var ranked = Errors(Yaml("""
                notice: { dc: noticeDc, rank: noticeCheck }
            """, """
            sheetRules:
              skills:
                ranks: [{ key: untrained, value: 0 }, { key: trained, value: 2 }]
                bonusKey: proficiencyBonus
            """));
        Assert.DoesNotContain(ranked, e => e.Contains("notice.rank"));
    }

    [Fact]
    public void AType_CannotBeBothAHazardAndAnActorBlueprint()
    {
        var errors = Errors(Yaml("""
                trigger: trigger
            """, typeExtra: "    actorBlueprint: true"));

        Assert.Contains(errors, e => e.Contains("actorBlueprint") && e.Contains("hazard"));
    }

    [Fact]
    public void Scaling_IsReadAndChecked()
    {
        var good = SystemDefinitionLoader.Parse(Yaml("""
                level: level
            """, """
            hazards:
              scaling:
                - { level: 1, noticeDc: 10, damage: 2d10 }
                - { level: 5, noticeDc: 12, damage: 4d10 }
            """));
        Assert.Equal(2, good.Hazards!.Scaling.Count);
        Assert.Equal("4d10", good.Hazards.Scaling[1]["damage"]);

        var bad = Errors(Yaml("""
                level: level
            """, """
            hazards:
              scaling:
                - { level: one, noticeDc: 10 }
                - { level: 5, noticeDc: 12, bogus: 1 }
                - { level: 5, noticeDc: 14 }
            """));
        Assert.Contains(bad, e => e.Contains("scaling[0]") && e.Contains("whole-number level"));
        Assert.Contains(bad, e => e.Contains("scaling[1]") && e.Contains("bogus"));
        Assert.Contains(bad, e => e.Contains("scaling[2]") && e.Contains("repeats level 5"));
    }

    [Fact]
    public void ScalingValues_AreCheckedAgainstTheirFields()
    {
        var errors = Errors(Yaml("""
                level: level
                effect: { damage: damage }
            """, """
            hazards:
              scaling:
                - { level: 1, noticeDc: fifteen, damage: 4d10 }
                - { level: 5, noticeDc: 12, damage: lots }
                - { level: 11, noticeDc: 14, damage: 2d6+3 }
            """));

        Assert.Contains(errors, e => e.Contains("scaling[0].noticeDc") && e.Contains("whole number"));
        Assert.Contains(errors, e => e.Contains("scaling[1].damage") && e.Contains("dice"));
        Assert.DoesNotContain(errors, e => e.Contains("scaling[2]"));
    }

    [Fact]
    public void AFieldKeyThatDiffersInCase_IsRefusedWithAHint()
    {
        var errors = Errors(Yaml("""
                notice: { dc: noticedc, check: noticeCheck }
            """));

        Assert.Contains(errors, e => e.Contains("notice.dc") && e.Contains("did you mean 'noticeDc'"));
    }

    [Fact]
    public void APassiveNotice_NeedsACheck()
    {
        var errors = Errors(Yaml("""
                notice: { dc: noticeDc, passive: true }
            """));

        Assert.Contains(errors, e => e.Contains("notice.passive") && e.Contains("notice.check"));
    }

    [Fact]
    public void ASystem_HasOneHazardType()
    {
        // A second type, declared after the first one's block in the same entityTypes list.
        var yaml = Yaml("""
                trigger: trigger
            """) + "  - type: haunt\n    label: Haunt\n" + Fields + "    hazard:\n      trigger: trigger\n";
        var errors = Errors(yaml);

        Assert.Contains(errors, e => e.Contains("one hazard type"));
    }

    [Fact]
    public void Scaling_NeedsAHazardType()
    {
        var errors = Errors("""
            id: no-hazards
            name: No hazards
            hazards:
              scaling:
                - { level: 1, noticeDc: 10 }
            """);

        Assert.Contains(errors, e => e.Contains("hazards.scaling"));
    }

    [Fact]
    public void ATranslatedSystem_KeepsTheHazardBlock()
    {
        var system = SystemDefinitionLoader.Parse(Yaml("""
                trigger: trigger
            """));

        system.Translations["ru"] = new SystemTranslation();
        var copy = system.Localize("ru");

        Assert.Equal("trigger", copy.FindType("hazard")!.Hazard!.Trigger);
    }
}
