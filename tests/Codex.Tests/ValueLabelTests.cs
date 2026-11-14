using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>A system names particular values of a field (<c>valueLabels</c>): level 0 of a spell is a "Cantrip". Read from yaml, checked on load,
/// worded in the reader's language, and used by the words a choice's title is built from.</summary>
public class ValueLabelTests
{
    private const string Demo = """
        id: demo
        name: Demo
        entityTypes:
          - type: spell
            label: Spell
            pluralLabel: Spells
            fields:
              - { key: level, label: Level, type: Number, valueLabels: { "0": { label: Cantrip, pluralLabel: Cantrips } } }
        """;

    private static FieldDefinition Level(SystemDefinition system) => system.FindType("spell")!.Fields.Single(f => f.Key == "level");

    [Fact]
    public void Parse_ReadsValueLabels_AndNamesTheValueWhateverWayItIsWritten()
    {
        var level = Level(SystemDefinitionLoader.Parse(Demo));

        Assert.Equal("Cantrip", level.ValueLabels!["0"].Label);
        Assert.Equal("Cantrips", level.ValueLabels["0"].PluralLabel);
        Assert.Equal("Cantrip", level.ValueLabelFor(0L)!.Label);
        Assert.Equal("Cantrip", level.ValueLabelFor(0d)!.Label);
        Assert.Equal("Cantrip", level.ValueLabelFor("0")!.Label);
        Assert.Null(level.ValueLabelFor(1L));
        Assert.Equal("Cantrip", level.Display("0"));
        Assert.Equal("3", level.Display("3"));
        Assert.Equal("Cantrip", level.ValueLabelFor(0)!.For(1));
        Assert.Equal("Cantrips", level.ValueLabelFor(0)!.For(2));
    }

    [Theory]
    [InlineData("{ key: level, type: Number, valueLabels: { zero: { label: Cantrip } } }", "is not a number")]
    [InlineData("{ key: level, type: Number, valueLabels: { \"0\": { pluralLabel: Cantrips } } }", "without a label")]
    [InlineData("{ key: level, type: Text, valueLabels: { \"0\": { label: Cantrip } } }", "only a Number or Dropdown")]
    [InlineData("{ key: level, type: Dropdown, options: [Low], valueLabels: { High: { label: Top } } }", "not one of its options")]
    public void Parse_RejectsValueLabelsThatDoNotFitTheField(string field, string expectedFragment)
    {
        var yaml = $"id: demo\nname: Demo\nentityTypes:\n  - type: spell\n    label: Spell\n    fields:\n      - {field}\n";

        var ex = Assert.Throws<InvalidDataException>(() => SystemDefinitionLoader.Parse(yaml));

        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void ATranslation_WordsTheValueWhole_AndAValueTheSystemDoesNotNameIsLeftOut()
    {
        var system = SystemDefinitionLoader.Parse(Demo);
        system.Translations["ru"] = new SystemTranslation
        {
            EntityTypes =
            {
                ["spell"] = new TypeTranslation
                {
                    Fields = { ["level"] = new LabelTranslation { ValueLabels = { ["0"] = new ValueLabel { Label = "Заговор", PluralLabel = "Заговоры" }, ["9"] = new ValueLabel { Label = "Лишнее" } } } }
                }
            }
        };

        var level = Level(system.Localize("ru"));

        Assert.Equal("Заговор", level.ValueLabelFor(0)!.Label);
        Assert.Equal("Заговоры", level.ValueLabelFor(0)!.PluralLabel);
        Assert.Null(level.ValueLabelFor(9));
        Assert.Equal("Cantrip", Level(system).ValueLabelFor(0)!.Label); // the original is untouched
    }

    [Fact]
    public void TheShippedRussianAndHebrewPacks_WordLevelZero()
    {
        var system = SystemDefinitionLoader.Load(Path.Combine(TestPaths.ContentRoot, "systems/srd5e", "system.yaml"));

        Assert.Equal("Cantrips", Level(system).ValueLabelFor(0)!.PluralLabel);
        Assert.Equal("Заговоры", Level(system.Localize("ru")).ValueLabelFor(0)!.PluralLabel);
        Assert.Equal("לחשונים", Level(system.Localize("he")).ValueLabelFor(0)!.PluralLabel);
    }

}
