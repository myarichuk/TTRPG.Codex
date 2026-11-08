using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.AI;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>
/// A system that ships no content of its own (no packs, no wiki to read from) still tells GMs and the AI features what world
/// it is for, through its <c>setting:</c> block. The crews fixture (tests/Codex.Tests/Fixtures/crews) is such a system.
/// </summary>
public class SystemSettingTests
{
    private static readonly string CrewsRoot = Path.Combine(TestPaths.RepoRoot, "tests", "Codex.Tests", "Fixtures", "crews");

    private static SystemDefinition Crews() => SystemDefinitionLoader.Load(Path.Combine(CrewsRoot, "system.yaml"));

    private const string Minimal = "id: s\nname: S\n";

    [Fact]
    public async Task ASystemWithNoContent_Loads_WithNoProblems_AndItsSettingIsAllItHas()
    {
        var content = new ContentRegistry();
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(content));
        await loader.LoadAndInitializeAsync(CrewsRoot);

        Assert.Equal(new[] { "crews-fixture" }, loader.LoadedSystemIds);
        Assert.Empty(loader.LoadProblems);
        Assert.Empty(content.GetPacks("crews-fixture"));
        var system = loader.GetDefinition("crews-fixture")!;
        Assert.DoesNotContain(system.Resources, r => r.IsPrimary); // stress, not hit points
        Assert.False(system.Setting!.IsEmpty);
        Assert.Contains("heist", system.Setting.Genres);
    }

    [Fact]
    public void TheBrief_ReadsAsOneParagraph_InAFixedOrder_AndLeavesOutWhatIsEmpty()
    {
        var brief = AiPrompts.SettingBrief(new SystemSetting
        {
            Summary = "Crews in a haunted city",
            Genres = { "heist", " " },
            Tone = { "grim" },
            Factions = { "rival gangs", "a corrupt watch" },
            Avoid = { "high magic" }
        })!;

        Assert.Equal("Crews in a haunted city. Genre: heist. Tone: grim. Kinds of factions: rival gangs; a corrupt watch. Does not belong: high magic.", brief);
        Assert.Null(AiPrompts.SettingBrief(null));
        Assert.Null(AiPrompts.SettingBrief(new SystemSetting()));
    }

    [Fact]
    public void ContentPrompts_CarryTheSetting_AfterTheCampaign_WhichWins_AndAskForNamesOfTheirOwn()
    {
        var context = AiPrompts.WithSetting("The Long Night: a crew of smugglers", Crews().Setting);
        var (system, user) = AiPrompts.Describe(new DescribeRequest("The Gilded Rat", "place", "Crews", Array.Empty<string>(), "", "", AiAudience.Dm, context));
        var chat = AiPrompts.WorldChat(new WorldChatRequest("Crews", context, Array.Empty<string>()));

        foreach (var prompt in new[] { system + user, chat })
        {
            Assert.Contains("The Long Night: a crew of smugglers", prompt);
            Assert.Contains("Genre: heist; gothic horror; industrial fantasy.", prompt);
            Assert.Contains("where the campaign says otherwise, the campaign wins", prompt);
            Assert.Contains("invent names of your own", prompt);
        }

        // The fixture's own words carry no other game's assumptions either.
        Assert.DoesNotMatch(new Regex(@"hit points?|monster|\bd20\b|\blevels?\b|\bclass(es)?\b", RegexOptions.IgnoreCase), AiPrompts.SettingBrief(Crews().Setting)!);
    }

    [Fact]
    public void ASystemWithoutASetting_GetsTheCampaignLineUnchanged()
    {
        // The SRD has no setting of its own: a 5e campaign's world is whatever its description says, as before.
        var srd = SystemDefinitionLoader.Load(Path.Combine(TestPaths.RepoRoot, "content", "systems", "srd5e", "system.yaml"));
        Assert.Null(srd.Setting);
        Assert.Equal("Campaign line", AiPrompts.WithSetting("Campaign line", srd.Setting));
        Assert.Equal("Campaign line", AiPrompts.WithSetting("Campaign line", new SystemSetting()));
    }

    [Fact]
    public void ATranslatedSetting_IsWhatReadersSee_WhilePromptsKeepTheOriginal()
    {
        var system = Crews();
        system.Translations["ru"] = new SystemTranslation { Setting = new SystemSetting { Summary = "Банды в городе с призраками", Genres = { "ограбление" } } };

        var russian = system.Localize("ru");

        Assert.Equal("Банды в городе с призраками", russian.Setting!.Summary);
        Assert.Empty(russian.Setting.Factions); // replaced whole, not merged part by part
        Assert.Contains("heist", russian.Canonical.Setting!.Genres);
        Assert.Contains("heist", system.Localize("he").Setting!.Genres); // no Hebrew translation: the original
    }

    [Theory]
    [InlineData("summary", "setting.summary is 1201 characters")]
    [InlineData("genres", "setting.genres has 21 items")]
    [InlineData("factions", "setting.factions: an item is 161 characters")]
    public void ASettingIsABrief_NotASettingBook(string part, string expected)
    {
        var yaml = Minimal + part switch
        {
            "summary" => $"setting:\n  summary: \"{new string('x', SystemDefinitionLoader.SettingTextMax + 1)}\"\n",
            "genres" => "setting:\n  genres: [" + string.Join(", ", Enumerable.Range(0, SystemDefinitionLoader.SettingItemsMax + 1).Select(i => $"g{i}")) + "]\n",
            _ => $"setting:\n  factions: [\"{new string('x', SystemDefinitionLoader.SettingItemMax + 1)}\"]\n"
        };

        var ex = Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(yaml));
        Assert.Contains(expected, ex.Message);
    }
}
