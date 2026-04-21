using Codex.Core;
using Codex.Plugin.Abstractions;

namespace Codex.Systems.DnD5e;

public class DnD5ePlugin : ICodexSystemPlugin
{
    public string SystemId => "DnD5e";

    public void RegisterComponents(ComponentRegistry registry)
    {
        registry.Register<AbilityScoresComponent>();
        registry.Register<HitPointsComponent>();
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
