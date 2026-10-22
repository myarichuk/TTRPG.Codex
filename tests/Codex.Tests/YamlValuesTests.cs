using Codex.Core;

namespace Codex.Tests;

public class YamlValuesTests
{
    [Fact]
    public void Normalize_TypesScalars_AndRecursesIntoMapsAndLists()
    {
        var raw = new Dictionary<string, object>
        {
            ["level"] = "3",
            ["ritual"] = "false",
            ["weight"] = "2.5",
            ["name"] = "Fireball",
            ["year"] = "0042",
            ["rows"] = new List<object> { new Dictionary<object, object> { ["name"] = "Yawn", ["level"] = "1" } }
        };

        var normalized = YamlValues.NormalizeMap(raw);

        Assert.Equal(3L, normalized["level"]);
        Assert.Equal(false, normalized["ritual"]);
        Assert.Equal(2.5, normalized["weight"]);
        Assert.Equal("Fireball", normalized["name"]);
        Assert.Equal(42L, normalized["year"]);
        var row = (Dictionary<string, object>)((List<object>)normalized["rows"])[0];
        Assert.Equal(1L, row["level"]);
    }

    [Fact]
    public void GetInt_ReadsAnyNumericShape()
    {
        var map = new Dictionary<string, object> { ["a"] = 5L, ["b"] = 6, ["c"] = 7.0, ["d"] = "8", ["e"] = "no" };
        Assert.Equal(5, YamlValues.GetInt(map, "a"));
        Assert.Equal(6, YamlValues.GetInt(map, "b"));
        Assert.Equal(7, YamlValues.GetInt(map, "c"));
        Assert.Equal(8, YamlValues.GetInt(map, "d"));
        Assert.Null(YamlValues.GetInt(map, "e"));
        Assert.Null(YamlValues.GetInt(map, "missing"));
    }

    [Fact]
    public void Normalize_FoldsOtherNumberTypesToLongOrDouble()
    {
        Assert.Equal(18L, YamlValues.Normalize(18.0m));
        Assert.Equal(2.5, YamlValues.Normalize(2.5f));
        Assert.Equal("2d6 + 4", YamlValues.Normalize("2d6 + 4"));
    }
}
