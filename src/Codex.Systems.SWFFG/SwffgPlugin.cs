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
}
