using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.SystemCheck;

namespace Codex.Tests.Reference;

/// <summary>Codex.SystemCheck, the command a system's author runs: it loads the folder, lists problems, runs the folder's spec,
/// and writes codex.d.ts.</summary>
public class SystemCheckTests
{
    private static readonly string Street = Path.Combine(ReferenceFixtures.Root, "street");

    [Fact]
    public void TheStreetFixture_LoadsWithoutProblems_AndEveryExpectationPasses()
    {
        var output = new StringWriter();

        var exit = SystemCheck.SystemCheck.Run(Street, writeTypes: false, quiet: false, output);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("PROBLEM", output.ToString());
        Assert.Contains("LOADED  ref-street", output.ToString());
        Assert.Contains("PASS    street.advancing-a-skill-spends-improvement-points", output.ToString());
        Assert.EndsWith("24 of 24 expectations pass; 0 fail." + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void TheCommittedTypings_AreWhatTheCheckWrites()
    {
        // rules.js reads them with // @ts-check; regenerate with: dotnet run --project tools/Codex.SystemCheck -- <folder> --types
        var system = SystemDefinitionLoader.Load(Path.Combine(Street, SystemDefinitionLoader.FileName));

        Assert.Equal(ScriptTypings.For(system), File.ReadAllText(Path.Combine(Street, ScriptTypings.FileName)));
    }

    [Fact]
    public void AFolderWhoseSystemFailsOrWhoseSpecFails_FailsTheCheck_AndProblemsAreListed()
    {
        var root = Path.Combine(Path.GetTempPath(), "codex-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "system.yaml"), "id: Bad Id\nname: x\n");
            var output = new StringWriter();
            Assert.Equal(1, SystemCheck.SystemCheck.Run(root, false, false, output));
            Assert.Contains("FAILED", output.ToString());

            File.WriteAllText(Path.Combine(root, "system.yaml"), """
                id: check-test
                name: Check test
                functions:
                  f: { script: gone.js#f }
                sheetKinds:
                  - kind: hero
                    label: Hero
                    values: [{ key: two, formula: "2" }]
                """);
            File.WriteAllText(Path.Combine(root, "spec.yaml"), """
                system: check-test
                sheets:
                  a: { kind: hero }
                expectations:
                  - { id: t.two, step: 2, sheet: a, expect: { values: { two: 2 } } }
                  - { id: t.three, step: 2, sheet: a, expect: { values: { two: 3 } } }
                """);
            output = new StringWriter();
            Assert.Equal(1, SystemCheck.SystemCheck.Run(root, writeTypes: true, quiet: true, output));
            Assert.Contains("PROBLEM check-test: The function \"f\" names the script gone.js", output.ToString());
            Assert.DoesNotContain("t.two", output.ToString());
            Assert.Contains("FAIL    t.three", output.ToString());
            Assert.True(File.Exists(Path.Combine(root, ScriptTypings.FileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
