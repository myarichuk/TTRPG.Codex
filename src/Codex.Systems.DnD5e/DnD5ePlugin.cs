using System.Reflection;
using System.Text.Json;
using Codex.Core;
using Codex.Plugin.Abstractions;
using Codex.Systems.DnD5e.Models;

namespace Codex.Systems.DnD5e;

public class DnD5ePlugin : ICodexSystemPlugin
{
    public string SystemId => "DnD5e";

    public IReadOnlyList<SpellDto> Spells { get; } = Array.Empty<SpellDto>();
    public IReadOnlyList<MonsterDto> Monsters { get; } = Array.Empty<MonsterDto>();
    public IReadOnlyList<ClassDto> Classes { get; } = Array.Empty<ClassDto>();

    public DnD5ePlugin()
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var spellsStream = assembly.GetManifestResourceStream("Codex.Systems.DnD5e.SrdData.spells.json");
        if (spellsStream != null)
        {
            Spells = JsonSerializer.Deserialize<List<SpellDto>>(spellsStream) ?? new List<SpellDto>();
        }

        using var monstersStream = assembly.GetManifestResourceStream("Codex.Systems.DnD5e.SrdData.monsters.json");
        if (monstersStream != null)
        {
            Monsters = JsonSerializer.Deserialize<List<MonsterDto>>(monstersStream) ?? new List<MonsterDto>();
        }

        using var classesStream = assembly.GetManifestResourceStream("Codex.Systems.DnD5e.SrdData.classes.json");
        if (classesStream != null)
        {
            Classes = JsonSerializer.Deserialize<List<ClassDto>>(classesStream) ?? new List<ClassDto>();
        }
    }

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
