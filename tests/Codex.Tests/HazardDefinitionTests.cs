using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The <c>hazard:</c> block of an entity type and <c>hazards.scaling</c>: what the loader reads and what it refuses.</summary>
public class HazardDefinitionTests
{
    private const string Fields =
        "    fields:\n"
        + "      - { key: level, label: Level, type: Number }\n"
        + "      - { key: saveDc, label: Save DC, type: Number }\n"
        + "      - { key: detect, label: Detect, type: Collection }\n"
        + "      - { key: disarm, label: Disarm, type: Collection }\n"
        + "      - { key: summary, label: Summary, type: Text }\n"
        + "      - { key: trigger, label: Trigger, type: LongText }\n"
        + "      - { key: damage, label: Damage, type: Collection }\n"
        + "      - { key: onSave, label: On a save, type: Dropdown, options: [half, none] }\n"
        + "      - { key: badSave, label: Bad save, type: Dropdown, options: [half, basic] }\n"
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
                notice: { routes: detect, passive: true }
                disable: { routes: disarm }
                trigger: trigger
                effect: { damage: damage, onSave: onSave, saveDc: saveDc }
                level: level
                complex: complex
                routine: routine
            """));

        var hazard = system.FindType("hazard")!.Hazard!;
        Assert.Equal("detect", hazard.Notice!.Routes);
        Assert.True(hazard.Notice.Passive);
        Assert.Equal("disarm", hazard.Disable!.Routes);
        Assert.Equal("damage", hazard.Effect!.Damage);
        Assert.Equal("onSave", hazard.Effect.OnSave);
        Assert.Equal("complex", hazard.Complex);
    }

    [Fact]
    public void AnUnknownField_IsRefused()
    {
        var errors = Errors(Yaml("""
                notice: { routes: detectt }
            """));

        Assert.Contains(errors, e => e.Contains("notice.routes") && e.Contains("detectt"));
    }

    [Fact]
    public void AFieldOfTheWrongKind_IsRefused()
    {
        var errors = Errors(Yaml("""
                notice: { routes: summary }
                effect: { damage: summary, saveDc: summary }
                complex: level
            """));

        Assert.Contains(errors, e => e.Contains("notice.routes") && e.Contains("Collection"));
        Assert.Contains(errors, e => e.Contains("effect.damage") && e.Contains("Collection"));
        Assert.Contains(errors, e => e.Contains("effect.saveDc") && e.Contains("Number"));
        Assert.Contains(errors, e => e.Contains("hazard.complex") && e.Contains("Boolean"));
    }

    [Fact]
    public void AnOnSaveDropdown_OnlyOffersWhatTheEngineKnows()
    {
        Assert.DoesNotContain(Errors(Yaml("""
                effect: { onSave: onSave }
            """)), e => e.Contains("onSave"));

        var errors = Errors(Yaml("""
                effect: { onSave: badSave }
            """));

        Assert.Contains(errors, e => e.Contains("effect.onSave") && e.Contains("'basic'") && e.Contains("half and none"));
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
                effect: { damage: damage }
            """, """
            hazards:
              scaling:
                - { level: 1, saveDc: 10, damage: 2d10 }
                - { level: 5, saveDc: 12, damage: 4d10 }
            """));
        Assert.Equal(2, good.Hazards!.Scaling.Count);
        Assert.Equal("4d10", good.Hazards.Scaling[1]["damage"]);

        var bad = Errors(Yaml("""
                level: level
                effect: { damage: damage }
            """, """
            hazards:
              scaling:
                - { level: one, saveDc: 10 }
                - { level: 5, saveDc: 12, bogus: 1 }
                - { level: 5, saveDc: 14 }
            """));
        Assert.Contains(bad, e => e.Contains("scaling[0]") && e.Contains("whole-number level"));
        Assert.Contains(bad, e => e.Contains("scaling[1]") && e.Contains("bogus"));
        Assert.Contains(bad, e => e.Contains("scaling[2]") && e.Contains("repeats level 5"));
    }

    [Fact]
    public void AScalingRow_MayGiveTheDamageDiceButNoOtherList()
    {
        var errors = Errors(Yaml("""
                level: level
                effect: { damage: damage }
            """, """
            hazards:
              scaling:
                - { level: 1, damage: 4d10 }
                - { level: 5, detect: lots }
            """));

        Assert.DoesNotContain(errors, e => e.Contains("scaling[0]"));
        Assert.Contains(errors, e => e.Contains("scaling[1].detect") && e.Contains("Collection"));
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
                - { level: 1, saveDc: fifteen, damage: 4d10 }
                - { level: 5, saveDc: 12, damage: lots }
                - { level: 11, saveDc: 14, damage: 2d6+3 }
            """));

        Assert.Contains(errors, e => e.Contains("scaling[0].saveDc") && e.Contains("whole number"));
        Assert.Contains(errors, e => e.Contains("scaling[1].damage") && e.Contains("dice"));
        Assert.DoesNotContain(errors, e => e.Contains("scaling[2]"));
    }

    [Fact]
    public void AFieldKeyThatDiffersInCase_IsRefusedWithAHint()
    {
        var errors = Errors(Yaml("""
                notice: { routes: Detect }
            """));

        Assert.Contains(errors, e => e.Contains("notice.routes") && e.Contains("did you mean 'detect'"));
    }

    [Fact]
    public void APassiveNotice_NeedsRoutes()
    {
        var errors = Errors(Yaml("""
                notice: { passive: true }
            """));

        Assert.Contains(errors, e => e.Contains("notice.passive") && e.Contains("notice.routes"));
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
                - { level: 1, saveDc: 10 }
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
