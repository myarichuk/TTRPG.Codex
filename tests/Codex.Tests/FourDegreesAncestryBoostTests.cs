using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;
using YamlDotNet.Serialization;

namespace Codex.Tests;

/// <summary>The real four-degrees system and a real ancestry of its pack: the trade is two free boosts instead of the ancestry's boosts and flaw
/// (rules entry <c>alternate-ancestry-boosts</c>), and voluntary flaws are extra flaws, one to an attribute (<c>voluntary-flaws</c>).</summary>
public class FourDegreesAncestryBoostTests
{
    private static readonly string Root = Path.Combine(TestPaths.ContentRoot, "systems", "four-degrees");

    private sealed class Pack(ContentEntry entry) : IEntryResolver
    {
        public ContentEntry? Resolve(string type, string id) => type == entry.Type && id == entry.Id ? entry : null;
        public IEnumerable<ContentEntry> Candidates(string type) => type == entry.Type ? new[] { entry } : Array.Empty<ContentEntry>();
    }

    private static (SheetRuleEngine Engine, List<string> Words) Load()
    {
        var rows = new DeserializerBuilder().Build().Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(Path.Combine(Root, "packs", "rules-core", "ancestries", "ancestries.yaml")));
        var row = rows.First(r => r["id"].ToString() == "dwarf-ancestry");
        var words = ((List<object>)((Dictionary<object, object>)row["properties"])["boosts"]).Select(o => o.ToString()!).ToList();
        var entry = new ContentEntry { Type = "ancestry", Id = "dwarf-ancestry", Name = "Dwarf", SystemId = "four-degrees", PackId = "rules-core" };
        entry.Properties["boosts"] = words.Cast<object>().ToList();
        var system = SystemDefinitionLoader.Load(Path.Combine(Root, SystemDefinitionLoader.FileName));
        return (new SheetRuleEngine(system, new Pack(entry)), words);
    }

    private static Dictionary<string, object> Sheet(params (string Key, string[] Picks)[] picks)
    {
        var sheet = new Dictionary<string, object> { ["level"] = 1L, ["attrMethod"] = "boosts", ["ancestry"] = "dwarf-ancestry" };
        foreach (var (key, list) in picks) sheet[key] = list.Cast<object>().ToList();
        return sheet;
    }

    [Fact]
    public void AnAncestryListsTwoFixedBoostsOneFreeAndAFlaw()
    {
        var (_, words) = Load();
        Assert.Equal(new[] { "con", "wis", "free", "-cha" }, words);
    }

    [Fact]
    public void TheAncestrysFlaw_AppliesUntilTheTradeIsMade_ThenTheTradeDropsIt()
    {
        var (engine, _) = Load();
        var plain = Sheet(("boost__ancestry__dwarf-ancestry.b1", new[] { "str" }));
        engine.Recompute(plain);
        Assert.Equal(-1L, plain["cha"]);

        var traded = Sheet(("boost__ancestry__dwarf-ancestry.trade", new[] { "con", "wis" }), ("boost__ancestry__dwarf-ancestry.b1", new[] { "str" }), ("boost__ancestry__dwarf-ancestry.t1", new[] { "dex" }));
        var evaluation = engine.Recompute(traded);
        Assert.Equal(0L, traded["cha"]);
        Assert.Equal(1L, traded["str"]);
        Assert.Equal(1L, traded["dex"]);
        Assert.Empty(evaluation.Attributes!.Boosts!.Sources.Single(s => s.Key == "ancestry").Flaws);
    }

    [Fact]
    public void AVoluntaryFlaw_AndALaterMilestoneBoostOnTheSameAttribute_MeetAtZero()
    {
        var (engine, _) = Load();
        var sheet = Sheet(("boost__ancestry__dwarf-ancestry.flaws", new[] { "int" }), ("boost__lvl5__m1", new[] { "int" }));
        sheet["level"] = 5L;
        engine.Recompute(sheet);
        Assert.Equal(0L, sheet["int"]);
    }

    [Fact]
    public void AVoluntaryFlaw_LowersAnAttribute_ButNotOneThatAlreadyHasTheAncestrysFlaw()
    {
        var (engine, _) = Load();
        var sheet = Sheet(("boost__ancestry__dwarf-ancestry.flaws", new[] { "int" }));
        Assert.DoesNotContain(engine.Recompute(sheet).Violations, v => v.Key == SheetRuleEngine.FlawTwiceKey);
        Assert.Equal(-1L, sheet["int"]);

        var twice = Sheet(("boost__ancestry__dwarf-ancestry.flaws", new[] { "cha" }));
        Assert.Contains(engine.Recompute(twice).Violations, v => v.Key == SheetRuleEngine.FlawTwiceKey);
    }
}
