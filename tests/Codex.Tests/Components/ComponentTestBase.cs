using Bunit;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Localization;
using Codex.Persistence;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests.Components;

/// <summary>
/// The shared setup of the component tests: bUnit's context with what the sheet components inject. The localizer is the app's real one,
/// pinned to English, so a test reads the text a player reads; JS interop is loose, so a component that asks the browser for nothing
/// the test cares about renders anyway. The components used here need no auth state, router or database.
/// </summary>
public abstract class ComponentTestBase : BunitContext
{
    protected ComponentTestBase()
    {
        Services.AddSingleton(Localizer.For("en"));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>The test id of an element, as a selector.</summary>
    protected static string Id(string testId) => $"[data-testid='{testId}']";
}

/// <summary>The real Forged in the Dark system and pack, loaded once per test class the way the content tests load it.</summary>
public sealed class FitdContent : IAsyncLifetime
{
    private static readonly IReadOnlySet<string> Packs = new HashSet<string> { "srd-fitd-2017" };

    private PluginLoader loader = null!;

    public SystemDefinition System { get; private set; } = null!;

    /// <summary>Any other loaded system, by id (the card tests use the fifth-edition one).</summary>
    public SystemDefinition SystemById(string id) => loader.GetDefinition(id)!;
    public IEntryResolver Resolver { get; private set; } = null!;

    /// <summary>The loaded content, for components that look up entries' translated names.</summary>
    public ContentRegistry Registry { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var registry = Registry = new ContentRegistry();
        var loader = this.loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(registry));
        await loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        System = loader.GetDefinition("fitd-srd")!;
        Resolver = new PackResolver(registry);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A scoundrel sheet linked to the crew <c>crew-1</c>, with one dot in two actions.</summary>
    public static Dictionary<string, object> Scoundrel(params string[] abilities) => new()
    {
        ["kind"] = "scoundrel",
        ["link__crew"] = "crew-1",
        ["abilities"] = abilities.Cast<object>().ToList(),
        ["actions"] = new Dictionary<string, object>
        {
            ["prowl"] = new Dictionary<string, object> { ["rating"] = 1L },
            ["skirmish"] = new Dictionary<string, object> { ["rating"] = 2L }
        }
    };

    public static Dictionary<string, object> Crew(params string[] abilities) => new()
    {
        ["kind"] = "crew",
        ["upgrades"] = new List<object>(),
        ["abilities"] = abilities.Cast<object>().ToList()
    };

    public static ActorDocument Actor(string id, string name, Dictionary<string, object> sheet) => new() { Id = id, CampaignId = "camp-1", Name = name, Sheet = sheet };

    /// <summary>Hands out the sheets of the actors a sheet links to. An actor it has no sheet for is not found, unless <see cref="Gone"/> says it was deleted.</summary>
    public sealed class Linked(params ActorDocument[] actors) : ILinkedSheets
    {
        /// <summary>The ids of actors that are in the trash: no sheet is handed out for them, and the status says why.</summary>
        public HashSet<string> Gone { get; } = new();

        public IReadOnlyDictionary<string, object>? Sheet(string actorId) => Gone.Contains(actorId) ? null : actors.FirstOrDefault(a => a.Id == actorId)?.Sheet;

        public LinkedActorStatus Status(string actorId) => Gone.Contains(actorId) ? LinkedActorStatus.Deleted : Sheet(actorId) == null ? LinkedActorStatus.NotFound : LinkedActorStatus.Available;
    }

    private sealed class PackResolver(ContentRegistry registry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => registry.GetEntries("fitd-srd", type, Packs).FirstOrDefault(e => e.Id == id);
        public IEnumerable<ContentEntry> Candidates(string type) => registry.GetEntries("fitd-srd", type, Packs);
    }
}
