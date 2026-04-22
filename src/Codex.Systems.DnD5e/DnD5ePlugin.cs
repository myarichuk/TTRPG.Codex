using Codex.Core;
using Codex.Core.Components;
using Codex.Plugin.Abstractions;

namespace Codex.Systems.DnD5e;

public class DnD5ePlugin : ICodexSystemPlugin
{
    public string SystemId => "DnD5e";
    public string Name => "Dungeons & Dragons 5e";
    public string Description => "The world's greatest roleplaying game.";
    public string ThumbnailPath => "dnd5e_logo.png"; // Placeholder

    public void RegisterComponents(ComponentRegistry registry)
    {
        registry.Register<AbilityScoresComponent>();
        registry.Register<ResourcePoolComponent>();
        registry.Register<StatModifierComponent>();
        registry.Register<ConditionComponent>();
    }

    public void RegisterSystems(dynamic world)
    {
        if (world is CodexWorld codexWorld)
        {
            codexWorld.AddSystem(new DamageSystem(codexWorld.InnerWorld));
        }
    }
}