using System.Collections.Generic;
using Codex.Core;
using Codex.Plugin.Abstractions;

namespace Codex.Systems.SWFFG;

public class SwffgPlugin : ICodexSystemPlugin
{
    public string SystemId => "SWFFG";

    public void RegisterComponents(ComponentRegistry registry)
    {
        registry.Register<CharacteristicsComponent>();
        registry.Register<WoundComponent>();
        registry.Register<StrainComponent>();
    }

    public void RegisterSystems(dynamic world)
    {
        if (world is CodexWorld codexWorld)
        {
            codexWorld.AddSystem(new WoundSystem(codexWorld.InnerWorld));
        }
    }

    public IEnumerable<UISchema> GetUISchemas()
    {
        yield return new UISchema(
            "Actor",
            PreferredEditor.Form,
            new List<FieldDefinition>
            {
                new("Brawn", "Brawn", FieldType.Number, DefaultValue: 2),
                new("Agility", "Agility", FieldType.Number, DefaultValue: 2),
                new("Intellect", "Intellect", FieldType.Number, DefaultValue: 2),
                new("Cunning", "Cunning", FieldType.Number, DefaultValue: 2),
                new("Willpower", "Willpower", FieldType.Number, DefaultValue: 2),
                new("Presence", "Presence", FieldType.Number, DefaultValue: 2)
            }
        );

        yield return new UISchema(
            "Location",
            PreferredEditor.Graph,
            new List<FieldDefinition>
            {
                new("Type", "Planet Type", FieldType.Dropdown, Options: new List<string> { "Forest", "Desert", "City", "Ocean", "Space Station" })
            },
            new List<GraphNodeMetadata>
            {
                new("Planet", "Globe", "#3498db", new List<string> { "Hyperspace", "Orbit" }),
                new("Station", "Server", "#95a5a6", new List<string> { "Dock" })
            }
        );
    }
}
