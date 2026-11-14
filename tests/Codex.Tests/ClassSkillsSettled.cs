using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>The SRD classes ask their first class for its skills (a pending choice on every new sheet). Tests of other choices count the choices
/// they look for, so they leave that source out of the system they load; the class skills have tests of their own (SrdMappedGrantsTests).</summary>
internal static class ClassSkillsSettled
{
    public static void LeaveOut(SystemDefinition system) => system.SheetRules!.Features.RemoveAll(source => source.FirstOnly);
}
