using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Codex.SystemCheck;
using Codex.Tests.Reference;

namespace Codex.Tests;

/// <summary>
/// The supplier contract (overhaul section 4, rules 1 to 5, step 9): the contract fixture (Fixtures/contract) writes the same
/// functions and handlers as formulas and handler lines, as scripts, and as compiled types in Codex.ContractFixture.dll, and its
/// spec expects the same from each. The DLL is copied beside a temporary copy of the fixture, as an author installs one.
/// </summary>
public sealed class SupplierContractTests : IDisposable
{
    private static readonly string Fixture = Path.Combine(TestPaths.RepoRoot, "tests", "Codex.Tests", "Fixtures", "contract");
    private static readonly string Dll = Path.Combine(AppContext.BaseDirectory, "contract-fixture", "Codex.ContractFixture.dll");
    private const string Type = "Codex.ContractFixture.{0}, Codex.ContractFixture";

    private readonly List<string> _folders = new();

    /// <summary>A copy of the fixture's folder, with the DLL beside system.yaml unless <paramref name="withDll"/> is false.</summary>
    private string Installed(bool withDll = true)
    {
        var folder = Path.Combine(Path.GetTempPath(), "codex-contract-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        _folders.Add(folder);
        foreach (var file in Directory.GetFiles(Fixture)) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        if (withDll) File.Copy(Dll, Path.Combine(folder, Path.GetFileName(Dll)));
        return folder;
    }

    public void Dispose()
    {
        foreach (var folder in _folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A loaded DLL may be held open (Windows); the temp folder is the OS's to clear.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void EveryExpectation_PassesForEachSupplier()
    {
        Assert.True(File.Exists(Dll), $"the contract fixture's DLL was not built to {Dll}");

        var results = ReferenceRunner.RunAll(new CurrentEngineHost(), new[] { Installed() });

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r.Status == CheckStatus.Pass, $"{r.Id}: {r.Status} {r.Detail}"));
    }

    [Fact]
    public void SystemCheck_LoadsTheSystemWithItsDll_AndReportsNoProblem()
    {
        var output = new StringWriter();

        var exit = Codex.SystemCheck.SystemCheck.Run(Installed(), writeTypes: false, quiet: true, output);

        Assert.True(exit == 0, output.ToString());
        Assert.DoesNotContain("PROBLEM", output.ToString());
        Assert.DoesNotContain("FAIL", output.ToString());
    }

    [Fact]
    public void WithoutTheDll_EachCompiledName_IsALoadProblem_AndTheRestStillWorks()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(Installed(withDll: false), SystemDefinitionLoader.FileName));

        var problems = CompiledSuppliers.Problems(system);

        // hitPointsC, halfC, half, signedC, boomC, the summary widget, hitC and locateC.
        Assert.Equal(8, problems.Count(p => p.Contains("there is no Codex.ContractFixture.dll in the system's folder", StringComparison.Ordinal)));
        Assert.Contains(problems, p => p.StartsWith("The function \"hitPointsC\" names the type", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("The hitC handler names the type", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("The widget \"summary\" names the type", StringComparison.Ordinal));
        Assert.Empty(SystemScripts.Problems(system));
    }

    [Fact]
    public void ParsingAlone_NeverLoadsADll()
    {
        var system = SystemDefinitionLoader.Parse(File.ReadAllText(Path.Combine(Installed(), SystemDefinitionLoader.FileName)));

        Assert.Empty(system.Compiled);
        Assert.Contains(CompiledSuppliers.Problems(system), p => p.Contains("compiled code is read only from a system's folder", StringComparison.Ordinal));
    }

    [Fact]
    public void ATypeThatDoesNotFitItsPlace_IsALoadProblem_SayingWhy()
    {
        var folder = Installed();
        // The host's own assembly, copied in: a system cannot name a host type, whatever its folder holds.
        File.Copy(typeof(ICodexFunction).Assembly.Location, Path.Combine(folder, "Codex.Plugin.Abstractions.dll"));
        var system = SystemDefinitionLoader.Parse($$"""
            id: contract-misfits
            name: Misfits
            functions:
              notOne: { type: "{{string.Format(Type, "NotAFunction")}}" }
              tooMany: { type: "{{string.Format(Type, "TooMany")}}" }
              noConstructor: { type: "{{string.Format(Type, "NoConstructor")}}" }
              missing: { type: "{{string.Format(Type, "Missing")}}" }
              host: { type: "Codex.Plugin.Abstractions.CodexMath, Codex.Plugin.Abstractions" }
              unqualified: { type: "Codex.ContractFixture.Half" }
              both: { type: "{{string.Format(Type, "Half")}}", formula: "(n) => n" }
            events:
              hit: { type: "{{string.Format(Type, "HitPoints")}}" }
            widgets:
              track: { type: "{{string.Format(Type, "Summary")}}" }
              nothing: {}
            """);
        CompiledSuppliers.Resolve(system, folder);

        var problems = CompiledSuppliers.Problems(system);

        Assert.Contains("The function \"notOne\" names the type Codex.ContractFixture.NotAFunction, Codex.ContractFixture, which is not an ICodexFunction.", problems);
        Assert.Contains("The function \"tooMany\" (Codex.ContractFixture.TooMany, Codex.ContractFixture) takes 5 arguments; a function takes 0 to 4.", problems);
        Assert.Contains("The function \"noConstructor\" names the type Codex.ContractFixture.NoConstructor, Codex.ContractFixture: Codex.ContractFixture.NoConstructor has no public constructor without parameters.", problems);
        Assert.Contains("The function \"missing\" names the type Codex.ContractFixture.Missing, Codex.ContractFixture: Codex.ContractFixture.dll has no type Codex.ContractFixture.Missing.", problems);
        Assert.Contains("The function \"host\" names the type Codex.Plugin.Abstractions.CodexMath, Codex.Plugin.Abstractions: Codex.Plugin.Abstractions.dll is the host's own assembly, which a system cannot name.", problems);
        Assert.Contains("The function \"unqualified\" names the type Codex.ContractFixture.Half: \"Codex.ContractFixture.Half\" is not \"Namespace.Type, Assembly\".", problems);
        Assert.Contains("The function \"both\" has a type and a formula; the type is used.", problems);
        Assert.Contains("The hit handler names the type Codex.ContractFixture.HitPoints, Codex.ContractFixture, which is not an ICodexEventHandler.", problems);
        Assert.Contains("The widget \"track\" has the name of the host's own widget, which a layout means instead.", problems);
        Assert.Contains("The widget \"nothing\" names no type; a widget is a compiled ICodexWidget.", problems);
    }

    [Fact]
    public void TheSameDll_InOneFolder_IsLoadedOnce_AndEachFolderHasItsOwn()
    {
        var folder = Installed();
        var first = SystemDefinitionLoader.Load(Path.Combine(folder, SystemDefinitionLoader.FileName));
        var again = SystemDefinitionLoader.Load(Path.Combine(folder, SystemDefinitionLoader.FileName));
        var elsewhere = SystemDefinitionLoader.Load(Path.Combine(Installed(), SystemDefinitionLoader.FileName));
        var name = string.Format(Type, "Half");

        var type = first.Compiled[name].Instance!.GetType();

        Assert.Same(type, again.Compiled[name].Instance!.GetType());
        Assert.NotSame(type, elsewhere.Compiled[name].Instance!.GetType());
        // The interface is the host's in every folder: that is what lets the host call it.
        Assert.IsAssignableFrom<ICodexFunction>(elsewhere.Compiled[name].Instance);
    }

    [Fact]
    public void AChangedDll_GetsANewLoadContext_WhileWhatWasLoadedFromTheOldOneKeepsWorking()
    {
        // A reload (overhaul step 10) reads the folder again: the same DLLs give the same types, a DLL added, removed or
        // replaced gives a fresh context, and a table that started before keeps its instances.
        var folder = Installed();
        var yaml = Path.Combine(folder, SystemDefinitionLoader.FileName);
        var name = string.Format(Type, "Half");
        var before = SystemDefinitionLoader.Load(yaml);
        var old = (ICodexFunction)before.Compiled[name].Instance!;

        File.Copy(Dll, Path.Combine(folder, "Extra.dll"));
        var after = SystemDefinitionLoader.Load(yaml);

        Assert.NotSame(old.GetType(), after.Compiled[name].Instance!.GetType());
        Assert.Equal(3, old.Call(new object?[] { 7.0 }).Value);
        Assert.Contains("Extra.dll", after.Fingerprint!.Files.Keys);
        Assert.Equal(new[] { new SystemChange(SystemChangeKind.Added, "Extra.dll", null, after.Fingerprint.Files["Extra.dll"]) }, after.Fingerprint.ChangesSince(before.Fingerprint!));

        // The DLL is read from its bytes: the file is free to be replaced or deleted while it is loaded.
        File.Delete(Path.Combine(folder, "Extra.dll"));
        File.Delete(Path.Combine(folder, Path.GetFileName(Dll)));
    }

    [Fact]
    public void AReplacedLoadContext_IsUnloaded_OnceNothingItMadeIsAlive_AndNotBefore()
    {
        var folder = Installed();
        var (kept, keptContext) = LoadThenReplace(folder);
        var droppedContext = DroppedAfterReplacing(Installed());

        for (var i = 0; i < 20 && droppedContext.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            FolderLoadContexts.Sweep();
        }

        // Nothing holds what the second folder's old context made: it was unloaded and collected.
        Assert.False(droppedContext.IsAlive, "the replaced context was not unloaded");
        // A table that started before still holds the first folder's instance: its context stays, and the instance works.
        Assert.True(keptContext.IsAlive);
        Assert.Equal(3, kept.Call(new object?[] { 7.0 }).Value);
        GC.KeepAlive(kept);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private WeakReference DroppedAfterReplacing(string folder) => LoadThenReplace(folder).Context;

    /// <summary>Loads the folder's system, keeps one instance from it, and replaces the folder's DLLs so the next load gets a new
    /// context. Returns the instance and a weak reference to its context. Kept out of the test's frame, so nothing else of the
    /// first load stays reachable from the test's locals.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private (ICodexFunction Instance, WeakReference Context) LoadThenReplace(string folder)
    {
        var yaml = Path.Combine(folder, SystemDefinitionLoader.FileName);
        var instance = (ICodexFunction)SystemDefinitionLoader.Load(yaml).Compiled[string.Format(Type, "Half")].Instance!;
        var context = new WeakReference(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(instance.GetType().Assembly));
        File.Copy(Dll, Path.Combine(folder, "Extra.dll"));
        SystemDefinitionLoader.Load(yaml);
        return (instance, context);
    }

    [Fact]
    public void AHandlerThatReturnsWhatIsNotPlainData_FailsTheEvent_NamingTheType()
    {
        var folder = Installed();
        var system = SystemDefinitionLoader.Parse($$"""
            id: contract-leaky
            name: Leaky
            sheetKinds:
              - kind: being
                tracks: [{ key: HP, max: 10 }]
            events:
              hit: { type: "{{string.Format(Type, "Leaky")}}" }
            """);
        CompiledSuppliers.Resolve(system, folder);

        var ex = Assert.Throws<SheetEventException>(() =>
            SheetEvents.Handle(system, new NullEntryResolver(), new Dictionary<string, object>(), new SheetEvent("hit", new Dictionary<string, object?>())));

        Assert.Contains("contract-leaky: the hit handler (compiled Codex.ContractFixture.Leaky, Codex.ContractFixture)", ex.Message);
        Assert.Contains("a Uri, which is not plain data", ex.Message);
    }

    [Fact]
    public void AWidget_SaysWhatToShow_AsPlainData()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(Installed(), SystemDefinitionLoader.FileName));
        var sheet = new Dictionary<string, object> { ["kind"] = "being", ["BODY"] = 6, ["WILL"] = 6, ["HP"] = 12, ["conditions"] = new List<object> { "Wounded" } };

        var content = CompiledSuppliers.Widget(system, "summary",
            new WidgetRequest(SheetEvents.PlainView(system, new NullEntryResolver(), sheet), null, Array.Empty<string>(), "Summary", "en"));

        Assert.Collection(content.Blocks,
            b => Assert.Equal(new WidgetHeading("Summary"), b),
            b => Assert.Equal(("HP", "12"), (((WidgetRow)b).Label, ((WidgetRow)b).Value)),
            b => Assert.Equal(new[] { "Wounded" }, ((WidgetPills)b).Items),
            b => Assert.Equal(1.0, ((WidgetAction)b).Intents.Single().Fields["by"]));
    }

    [Fact]
    public void TheSheet_ShowsACompiledWidget_AndSaysWhyOneCannotBeShown()
    {
        var folder = Installed();
        var system = SystemDefinitionLoader.Parse($$"""
            id: contract-widgets
            name: Widgets
            sheetKinds:
              - kind: being
                tracks: [{ key: HP, max: 10, start: max }]
                layout:
                  - { section: Sheet, widgets: [{ widget: summary, label: At a glance }, { widget: wrong }, { widget: track, key: HP }] }
            widgets:
              summary: { type: "{{string.Format(Type, "Summary")}}" }
              wrong: { type: "{{string.Format(Type, "Half")}}" }
            """);
        CompiledSuppliers.Resolve(system, folder);
        var view = new SheetView(system, new NullEntryResolver(), new Dictionary<string, object> { ["HP"] = 7 });
        var widgets = SheetLayout.For(system, view.Sheet).Single().Widgets;

        var (content, none) = view.Compiled(widgets[0], "en");
        var (nothing, why) = view.Compiled(widgets[1], "en");

        Assert.True(view.IsCompiled(widgets[0]) && view.Shows(widgets[0]));
        Assert.False(view.IsCompiled(widgets[2]));
        Assert.Null(none);
        Assert.Equal(new WidgetHeading("At a glance"), content!.Blocks[0]);
        Assert.Equal("7", ((WidgetRow)content.Blocks[1]).Value);
        Assert.Null(nothing);
        Assert.Contains("contract-widgets: the widget \"wrong\" (compiled Codex.ContractFixture.Half, Codex.ContractFixture): the type is not an ICodexWidget", why);
        // Only the type is a problem; the layout knows both names.
        Assert.Empty(SheetLayout.Problems(system));
        Assert.Contains(CompiledSuppliers.Problems(system), p => p.Contains("which is not an ICodexWidget", StringComparison.Ordinal));
    }

    [Fact]
    public void ACompiledWidget_InTheLayoutOfASystemWithoutSheetKinds_IsALoadProblem()
    {
        var system = SystemDefinitionLoader.Parse($$"""
            id: contract-legacy
            name: Legacy
            layout:
              - { section: Sheet, widgets: [{ widget: summary }] }
            widgets:
              summary: { type: "{{string.Format(Type, "Summary")}}" }
            """);

        Assert.Contains(SheetLayout.Problems(system), p => p.EndsWith("summary is the system's own widget, which reads a sheet kind's view, and the system has no sheet kinds; it shows nothing.", StringComparison.Ordinal));
    }

    [Fact]
    public void AWidgetsAction_AppliesItsIntents_AsATrackWidgetDoes()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(Installed(), SystemDefinitionLoader.FileName));
        var sheet = new Dictionary<string, object> { ["kind"] = "being", ["HP"] = 12 };
        var view = new SheetView(system, new NullEntryResolver(), sheet);
        var action = view.Compiled(new LayoutWidget { Widget = "summary" }, "en").Content!.Blocks.OfType<WidgetAction>().Single();

        SheetIntents.Apply(system, new NullEntryResolver(), sheet, action.Intents.Select(i => new SheetIntent(i.Kind, null, i.Fields)));

        Assert.Equal(13.0, Convert.ToDouble(sheet["HP"]));
    }

    [Fact]
    public async Task TheLibrary_SaysASystemContainsCompiledCode()
    {
        var folder = Installed();
        var loader = new Codex.Core.PluginLoader(Microsoft.Extensions.Logging.Abstractions.NullLogger<Codex.Core.PluginLoader>.Instance, new ComponentRegistry(),
            new Codex.Core.YamlContentPackLoader(new Codex.Core.ContentRegistry()));

        await loader.LoadAndInitializeAsync(folder);

        var summary = Assert.Single(loader.Systems);
        Assert.True(summary.IsDataDriven);
        Assert.True(summary.ContainsCompiledCode);
        Assert.Empty(loader.LoadProblems);
    }
}
