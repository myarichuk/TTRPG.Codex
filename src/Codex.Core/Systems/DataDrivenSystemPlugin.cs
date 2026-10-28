using System.Collections.Generic;
using System.Linq;
using Codex.Core.Components;
using Codex.Plugin.Abstractions;

namespace Codex.Core.Systems;

/// <summary>
/// An <see cref="ICodexSystemPlugin"/> built entirely from a <see cref="SystemDefinition"/>. This is
/// how a system ships as data: the definition supplies attributes, resources, sheet fields and
/// content types, and this class turns them into the UI schemas and ECS component registrations
/// the rest of the app expects from any plugin.
/// </summary>
public sealed class DataDrivenSystemPlugin(SystemDefinition definition) : ICodexSystemPlugin
{
    public const string ActorSchemaType = "Actor";
    public const string AttributesCategory = "Attributes";
    public const string ResourcesCategory = "Resources";

    public SystemDefinition Definition { get; } = definition;

    public string SystemId => Definition.Id;

    public string DisplayName => Definition.Name;

    public void RegisterComponents(ComponentRegistry registry)
    {
        registry.Register<AttributesComponent>();
        registry.Register<ResourcePoolComponent>();
        registry.Register<StatModifierComponent>();
        registry.Register<TagsComponent>();
    }

    public void RegisterSystems(dynamic world)
    {
        // Damage, healing and durations are applied by CodexWorld directly; a data-driven system
        // has no code to run per tick.
    }

    public IEnumerable<UISchema> GetUISchemas()
    {
        yield return BuildActorSchema(Definition);

        foreach (var type in Definition.EntityTypes)
        {
            yield return new UISchema(type.Type, PreferredEditor.Form, type.EditorFields(), Intent: "Content");
        }
    }

    /// <summary>
    /// The character-sheet schema: attributes (one Number field each), resources (current and,
    /// where declared, maximum), then the system's own actor fields. Field keys are the
    /// attribute/resource keys so a saved sheet maps 1:1 onto <see cref="AttributesComponent"/>
    /// and <see cref="ResourcePoolComponent"/>.
    /// </summary>
    public static UISchema BuildActorSchema(SystemDefinition definition)
    {
        var fields = new List<FieldDefinition>();

        foreach (var attribute in definition.Attributes)
        {
            fields.Add(new FieldDefinition(
                attribute.Key,
                string.IsNullOrWhiteSpace(attribute.Abbreviation) ? attribute.Label : $"{attribute.Label} ({attribute.Abbreviation})",
                FieldType.Number,
                defaultValue: attribute.Default,
                category: definition.DisplayCategory(AttributesCategory)));
        }

        foreach (var resource in definition.Resources)
        {
            fields.Add(new FieldDefinition(resource.Key, resource.Label, FieldType.Number, defaultValue: resource.Default, category: definition.DisplayCategory(ResourcesCategory)));
            if (!string.IsNullOrWhiteSpace(resource.MaxKey))
            {
                fields.Add(new FieldDefinition(resource.MaxKey, $"{resource.Label} ({definition.MaxSuffix ?? "max"})", FieldType.Number, defaultValue: resource.Default, category: definition.DisplayCategory(ResourcesCategory)));
            }
        }

        fields.AddRange(definition.ActorFields);

        return new UISchema(ActorSchemaType, PreferredEditor.Form, fields, Intent: "Combat");
    }
}
