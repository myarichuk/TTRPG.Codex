using Codex.Core;
using Codex.Core.Models;
using Codex.Core.Scripting;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

public class ContentNamesTests
{
    private static ContentRegistry Registry()
    {
        var registry = new ContentRegistry(new ScriptEvaluator(NullLogger<ScriptEvaluator>.Instance));
        var captain = new ContentEntry { Id = "guard-captain", Type = "monster", SystemId = "srd5e", PackId = "srd", Name = "Guard Captain" };
        registry.RegisterEntry(captain, 0);
        registry.RegisterActor(YamlContentPackLoader.ToActorBlueprint(captain, new ContentTypeDefinition { Type = "monster", ActorBlueprint = true }), 0);
        registry.RegisterTranslation("srd5e", "monster", "guard-captain", "he", "srd-he", new Dictionary<string, object> { ["name"] = "קפטן שומרים" });
        // An actor a pack defines directly, with no Library entry behind it.
        registry.RegisterActor(new ActorDefinition { Id = "town-crier", SystemId = "srd5e", PackId = "srd", Name = "Town Crier" }, 0);
        return registry;
    }

    [Theory]
    [InlineData("he-IL", "קפטן שומרים")]
    [InlineData("ru", "Guard Captain")] // no Russian name: the pack's own
    [InlineData(null, "Guard Captain")]
    public void ActorName_IsTheCreaturesNameInTheReadersLanguage(string? culture, string expected)
    {
        Assert.Equal(expected, Registry().ActorName("srd5e:guard-captain", culture));
    }

    [Fact]
    public void ActorName_OfAnActorWithoutAnEntry_IsItsOwnName_AndOfAMissingOne_IsNull()
    {
        var registry = Registry();

        Assert.Equal("Town Crier", registry.ActorName("srd5e:town-crier", "he"));
        Assert.Null(registry.ActorName("srd5e:kraken", "he"));
    }
}
