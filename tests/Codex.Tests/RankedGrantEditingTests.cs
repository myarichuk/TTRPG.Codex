using Codex.Core;
using Codex.Core.Rules;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>What authors and writers are given to grant ranks and choices with (docs/FEATURE_GRANTS_DESIGN.md, step 7): the vocabulary's ranked
/// lists, the guide's three examples, how the editor reads and writes an <c>adds</c> row, and the draft check on a written entry.</summary>
public class RankedGrantEditingTests : IAsyncLifetime
{
    private readonly ContentRegistry _registry = new();
    private readonly PluginLoader _loader;

    public RankedGrantEditingTests()
    {
        _loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(_registry));
    }

    public async Task InitializeAsync() => await _loader.LoadAndInitializeAsync(TestPaths.ContentRoot);

    public Task DisposeAsync() => Task.CompletedTask;

    private SystemDefinition Srd() => _loader.GetDefinition("srd5e")!;

    private static Dictionary<string, object> Map(params (string Key, object Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static List<object> Items(params object[] items) => items.ToList();

    private static ContentEntry Subclass(params Dictionary<string, object>[] rows)
    {
        var entry = new ContentEntry { Type = "subclass", Id = "grantor", Name = "Grantor", SystemId = "srd5e", PackId = "homebrew" };
        entry.Properties["class"] = "sorcerer";
        entry.Properties["features"] = rows.Cast<object>().ToList();
        return entry;
    }

    private static Dictionary<string, object> Row(string name, int level, params Dictionary<string, object>[] adds) =>
        Map(("name", name), ("level", (long)level), ("description", name + "."), ("adds", adds.Cast<object>().ToList()));

    private static Dictionary<string, object> Choice(string? id, object? choose, params (string Key, object Value)[] more)
    {
        var add = Map(("to", "skills"));
        if (id != null) add["choice"] = id;
        if (choose != null) add["choose"] = choose;
        foreach (var (key, value) in more) add[key] = value;
        return add;
    }

    // ---- Vocabulary and guide ----

    [Fact]
    public void TheVocabulary_OffersTheRankedLists_WithTheirRanksAndKeys()
    {
        var vocabulary = FeatureGrantVocabulary.For(Srd())!;

        var skills = Assert.Single(vocabulary.Lists, l => l.Key == "skills");
        Assert.True(skills.IsRanked);
        Assert.Equal(new[] { "proficient", "expert" }, skills.Ranks!.Select(r => r.Key));
        Assert.Contains(skills.Keys!, k => k.Key == "stealth");
        var saves = Assert.Single(vocabulary.Lists, l => l.Key == "saves");
        Assert.Equal(new[] { "proficient" }, saves.Ranks!.Select(r => r.Key));
        Assert.Contains(saves.Keys!, k => k.Key == "wis");
        Assert.False(vocabulary.Lists.First(l => l.Key == "grantedSpells").IsRanked);
    }

    [Fact]
    public void TheGuide_GivesOneExampleEachOfFixedRaiseAndChoice_AndSaysOfYourChoiceIsAlwaysAChoice()
    {
        var guide = FeatureGrantGuide.For(Srd(), "subclass")!;

        Assert.Contains("Ranked lists hold each key at a rank", guide);
        Assert.Contains("proficient (Proficient), expert (Expert)", guide); // the ranks are named, lowest first
        Assert.Contains("\"rank\": \"proficient\"}", guide); // fixed
        Assert.Contains("\"raise\": 1}", guide); // raise
        Assert.Contains("\"choice\": \"expertise-3\", \"choose\": 2", guide); // choice
        Assert.Contains("\"minRank\": \"proficient\"", guide);
        Assert.Contains("never written as fixed", guide);
        Assert.Contains("unique within the entry", guide);
        Assert.DoesNotContain("skills (Skills: ids or names)", guide); // not described as a pick list of entries
    }

    // ---- How the editor reads and writes a row ----

    [Fact]
    public void AnAddsRow_IsFixedRaiseOrChoice_BySomethingItAlreadyHolds()
    {
        var data = FeatureGrantData.Read(Map(("adds", Items(
            Map(("to", "skills"), ("entries", Items("stealth"))),
            Map(("to", "skills"), ("entries", Items("stealth")), ("raise", 1L)),
            Map(("to", "skills"), ("choose", 2L), ("choice", "keen"), ("from", Items("insight", "perception")), ("minRank", "proficient"), ("rank", "expert"))))));

        Assert.Equal(new[] { GrantAddEdit.FixedMode, GrantAddEdit.RaiseMode, GrantAddEdit.ChoiceMode }, data.Adds.Select(GrantAddEdit.ModeOf));
        var choice = data.Adds[2];
        Assert.Equal(2, GrantAddEdit.Number(choice, "choose", 1));
        Assert.Equal("keen", GrantAddEdit.Text(choice, "choice"));
        Assert.Equal(GrantAddEdit.FromThese, GrantAddEdit.FromKind(choice));
        Assert.Equal(new[] { "insight", "perception" }, GrantAddEdit.FromKeys(choice));
        Assert.Equal(3, data.Counts().Adds); // every row counts: it has entries, or is a choice
    }

    [Fact]
    public void ChangingTheKind_TakesOutWhatTheNewKindDoesNotUse_AndWritesTheRestBack()
    {
        var feature = new Dictionary<string, object>
        {
            ["name"] = "Keen",
            ["adds"] = Items(Map(("to", "skills"), ("entries", Items("stealth")), ("rank", "expert"), ("note", "kept")))
        };
        var data = FeatureGrantData.Read(feature);
        var add = data.Adds[0];

        GrantAddEdit.SetMode(add, GrantAddEdit.ChoiceMode, "keen-1");
        GrantAddEdit.SetFrom(add, GrantAddEdit.FromThese, new[] { "insight", " perception" });
        GrantAddEdit.SetText(add, "minRank", "proficient");
        data.Apply(feature);

        var written = (IDictionary<string, object>)((List<object>)feature["adds"]).Single();
        Assert.False(written.ContainsKey("entries")); // the player picks them
        Assert.Equal(1L, written["choose"]);
        Assert.Equal("keen-1", written["choice"]);
        Assert.Equal("expert", written["rank"]);
        Assert.Equal("kept", written["note"]); // a key the editor does not know round-trips
        Assert.Equal(new object[] { "insight", "perception" }, (List<object>)written["from"]);

        GrantAddEdit.SetMode(add, GrantAddEdit.RaiseMode);
        Assert.False(add.Extra.ContainsKey("choice") || add.Extra.ContainsKey("from") || add.Extra.ContainsKey("minRank") || add.Extra.ContainsKey("rank"));
        Assert.Equal(1, GrantAddEdit.Number(add, "raise", 0));

        GrantAddEdit.SetMode(add, GrantAddEdit.FixedMode);
        Assert.False(add.Extra.ContainsKey("raise"));
        Assert.Equal("kept", add.Extra["note"]);
    }

    [Fact]
    public void AnyIsTheDefaultFrom_AndAnAttributeFilterIsAMap()
    {
        var add = new GrantAdd { To = "saves" };

        Assert.Equal(GrantAddEdit.FromAny, GrantAddEdit.FromKind(add));
        GrantAddEdit.SetFrom(add, GrantAddEdit.FromAttribute, new[] { "str", "dex" });
        Assert.Equal(GrantAddEdit.FromAttribute, GrantAddEdit.FromKind(add));
        Assert.Equal(new[] { "str", "dex" }, GrantAddEdit.FromKeys(add));
        GrantAddEdit.SetFrom(add, GrantAddEdit.FromAny, Array.Empty<string>());
        Assert.False(add.Extra.ContainsKey("from"));
        add.Extra["from"] = "any";
        Assert.Equal(GrantAddEdit.FromAny, GrantAddEdit.FromKind(add));
    }

    // ---- Changing a made choice is logged to the table's session ----

    [Fact]
    public void AChangedChoice_IsLoggedToTheSession_WithTheFeatureAndThePicksOnly()
    {
        var command = new LogChoiceChangedCommand("actor-1", "Mira Vael", "Expertise", "Stealth, Perception");

        var logged = command.Describe(null!);

        Assert.Equal("ChoiceChanged", logged.Type);
        Assert.Equal(new[] { "actor-1" }, logged.ActorIds); // a player who can't see the character doesn't get it
        Assert.Contains("Stealth, Perception", logged.Description);
        Assert.Contains("Expertise", logged.Description);
        Assert.Empty(command.AffectedActorIds); // it writes nothing to the character: the sheet is saved on its own
    }

    // ---- The draft check ----

    [Fact]
    public void ADraftWithSoundGrants_PassesTheCheck()
    {
        var entry = Subclass(Row("Keen Eyes", 3,
            Map(("to", "skills"), ("entries", Items("perception")), ("rank", "expert")),
            Choice("keen-eyes-3", 2L, ("from", "any"), ("minRank", "proficient"), ("rank", "expert")),
            Choice("lore-3", 1L, ("raise", 1L))));

        Assert.Empty(FeatureGrantCheck.Problems(Srd(), entry));
    }

    [Fact]
    public void ADuplicateChoiceId_InOneEntry_IsRefused_EvenAcrossFeatures()
    {
        var entry = Subclass(
            Row("One", 3, Choice("same", 1L)),
            Row("Two", 6, Choice("SAME", 1L)));

        var problem = Assert.Single(FeatureGrantCheck.Problems(Srd(), entry));
        Assert.Contains("used more than once", problem);
        Assert.Contains("Two", problem);
    }

    [Fact]
    public void AChoiceWithoutAnIdOrACount_AnUnknownRank_AndRankWithRaise_AreRefused()
    {
        var entry = Subclass(Row("Loose", 3,
            Choice(null, 2L),
            Choice("no-count", null, ("choose", 0L)),
            Choice("bad-rank", 1L, ("minRank", "legendary")),
            Map(("to", "skills"), ("entries", Items("stealth")), ("rank", "expert"), ("raise", 1L))));

        var problems = FeatureGrantCheck.Problems(Srd(), entry);

        Assert.Contains(problems, p => p.Contains("needs a \"choice\" id"));
        Assert.Contains(problems, p => p.Contains("needs \"choose\""));
        Assert.Contains(problems, p => p.Contains("\"legendary\" is not a rank of skills"));
        Assert.Contains(problems, p => p.Contains("cannot both be given"));
    }

    [Fact]
    public void AnAuthorsChoiceMayLeaveItsIdToTheDefault_ButTwoRowsOfOneNameAndLevelStillCollide()
    {
        var fine = Subclass(Row("Expertise", 3, Choice(null, 2L)), Row("Expertise", 6, Choice(null, 2L))); // expertise-3 and expertise-6
        var clash = Subclass(Row("Expertise", 3, Choice(null, 2L)), Row("Expertise", 3, Choice(null, 2L)));

        Assert.Empty(FeatureGrantCheck.Problems(Srd(), fine, requireId: false));
        Assert.Single(FeatureGrantCheck.Problems(Srd(), clash, requireId: false), p => p.Contains("used more than once"));
        Assert.Contains(FeatureGrantCheck.Problems(Srd(), fine), p => p.Contains("needs a \"choice\" id")); // a model's draft must name it
    }

    [Fact]
    public void AChoiceIntoAnUnrankedList_IsRefused_AndAFeatureWithoutGrantsIsLeftAlone()
    {
        var entry = Subclass(
            Row("Resist", 3, Map(("to", "resistances"), ("choose", 1L), ("choice", "pick"))),
            Row("Spells", 3, Map(("to", "grantedSpells"), ("choose", 1L), ("choice", "pick-spell"))), // entries are fine, but with nothing narrowing them
            Map(("name", "Plain"), ("level", 5L), ("description", "Words only.")));

        var problems = FeatureGrantCheck.Problems(Srd(), entry);
        Assert.Contains(problems, p => p.Contains("a choice can only go to"));
        Assert.Contains(problems, p => p.Contains("needs \"where\" conditions or a \"fromList\""));
        var plain = new ContentEntry { Type = "spell", Id = "x", Name = "X", SystemId = "srd5e" };
        Assert.Empty(FeatureGrantCheck.Problems(Srd(), plain)); // the system reads no features from a spell
    }
}
