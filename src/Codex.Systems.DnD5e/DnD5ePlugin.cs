using System.Collections.Generic;
using Codex.Core.Components;
using Codex.Plugin.Abstractions;

namespace Codex.Systems.DnD5e;

public class DnD5ePlugin : ICodexSystemPlugin
{
    public string SystemId => "DnD5e";

    public void RegisterComponents(ComponentRegistry registry)
    {
        registry.Register<AbilityScoresComponent>();
        registry.Register<ResourcePoolComponent>();
        registry.Register<StatModifierComponent>();
        registry.Register<ConditionComponent>();
    }

    public void RegisterSystems(ISystemContext systems)
    {
        // Damage/healing are applied immediately via CodexWorld.ApplyDamage/ApplyHealing (3.4) -
        // there is no more DamageEvent component-based system to register here. It used to let two
        // hits in the same tick overwrite each other and never clamped HP (B7).
    }

    public IEnumerable<UISchema> GetUISchemas()
    {
        yield return new UISchema(
            "Actor",
            PreferredEditor.Form,
            new List<FieldDefinition>
            {
                new("Level", "Level", FieldType.Number, DefaultValue: 1),
                new("Class", "Class", FieldType.Text),
                new("Race", "Race", FieldType.Text),
                new("Strength", "STR", FieldType.Number, DefaultValue: 10),
                new("Dexterity", "DEX", FieldType.Number, DefaultValue: 10),
                new("Constitution", "CON", FieldType.Number, DefaultValue: 10),
                new("Intelligence", "INT", FieldType.Number, DefaultValue: 10),
                new("Wisdom", "WIS", FieldType.Number, DefaultValue: 10),
                new("Charisma", "CHA", FieldType.Number, DefaultValue: 10),
                new("Alignment", "Alignment", FieldType.Dropdown, Options: new List<string> { "LG", "NG", "CG", "LN", "N", "CN", "LE", "NE", "CE" })
            }
        );

        yield return new UISchema(
            "Ability",
            PreferredEditor.Form,
            new List<FieldDefinition>
            {
                new("Level", "Spell Level", FieldType.Number, DefaultValue: 0),
                new("School", "School of Magic", FieldType.Dropdown, Options: new List<string> { "Abjuration", "Conjuration", "Divination", "Enchantment", "Evocation", "Illusion", "Necromancy", "Transmutation" }),
                new("Triggers", "Triggers", FieldType.Collection, TargetEntityType: "Trigger"),
                new("Requires", "Requirements", FieldType.Collection, TargetEntityType: "Requirement"),
                new("Costs", "Costs", FieldType.Collection, TargetEntityType: "Cost"),
                new("Effects", "Effects", FieldType.Collection, TargetEntityType: "Effect")
            }
        );
    }
}