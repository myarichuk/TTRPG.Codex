using System.Collections.Generic;
using Codex.Core.Components;
using Codex.Plugin.Abstractions;

namespace Codex.Systems.Pf2e;

/// <summary>
/// Thin system shell for Pathfinder 2e Remastered (6.1): SystemId, shared core components,
/// and sheet schemas only. Every rule - spells, feats, and later ancestries and equipment -
/// ships as YAML in the <c>pf2e-remastered</c> content pack (see <c>scripts/import_pf2e.py</c>),
/// never as C# here.
/// </summary>
public class Pf2ePlugin : ICodexSystemPlugin
{
    public string SystemId => "Pf2e";

    public void RegisterComponents(ComponentRegistry registry)
    {
        registry.Register<AbilityScoresComponent>();
        registry.Register<ResourcePoolComponent>();
        registry.Register<StatModifierComponent>();
        registry.Register<ConditionComponent>();
    }

    public void RegisterSystems(ISystemContext systems)
    {
        // Damage/healing apply immediately via CodexWorld.ApplyDamage/ApplyHealing (3.4) -
        // same as DnD5e, there is no per-tick system to register here.
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
                new("Ancestry", "Ancestry", FieldType.Text),
                new("Background", "Background", FieldType.Text),
                new("Strength", "STR", FieldType.Number, DefaultValue: 10),
                new("Dexterity", "DEX", FieldType.Number, DefaultValue: 10),
                new("Constitution", "CON", FieldType.Number, DefaultValue: 10),
                new("Intelligence", "INT", FieldType.Number, DefaultValue: 10),
                new("Wisdom", "WIS", FieldType.Number, DefaultValue: 10),
                new("Charisma", "CHA", FieldType.Number, DefaultValue: 10),
            }
        );

        yield return new UISchema(
            "Ability",
            PreferredEditor.Form,
            new List<FieldDefinition>
            {
                new("Rank", "Spell Rank / Feat Level", FieldType.Number, DefaultValue: 1),
                new("Traditions", "Traditions", FieldType.TagList),
                new("Traits", "Traits", FieldType.TagList),
                new("Actions", "Actions", FieldType.Text),
                new("Triggers", "Triggers", FieldType.Collection, TargetEntityType: "Trigger"),
                new("Requires", "Requirements", FieldType.Collection, TargetEntityType: "Requirement"),
                new("Costs", "Costs", FieldType.Collection, TargetEntityType: "Cost"),
                new("Effects", "Effects", FieldType.Collection, TargetEntityType: "Effect")
            }
        );
    }
}
