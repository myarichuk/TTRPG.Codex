using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>
/// Every sheet-rule test again, on a copy of the system whose attribute labels and abbreviations are in Russian.
/// Pack entries and sheets still say "STR" and "Strength", so the rules must keep resolving them.
/// </summary>
public class LocalizedSheetRuleEngineTests : SheetRuleEngineTests
{
    private static readonly SystemDefinition Translated = Build();

    protected override SystemDefinition Sys => Translated;

    private static SystemDefinition Build()
    {
        var system = SystemDefinitionLoader.Parse(Yaml);
        system.Translations["ru"] = new SystemTranslation
        {
            Attributes =
            {
                ["str"] = new LabelTranslation { Label = "Сила", Abbreviation = "СИЛ" },
                ["dex"] = new LabelTranslation { Label = "Ловкость", Abbreviation = "ЛОВ" },
                ["int"] = new LabelTranslation { Label = "Интеллект", Abbreviation = "ИНТ" },
            }
        };
        return system.Localize("ru");
    }

    [Fact]
    public void TheCopyReallyIsTranslated()
    {
        Assert.Equal("Сила", Translated.Attributes[0].Label);
        Assert.Equal("STR", Translated.Attributes[0].CanonicalAbbreviation);
    }
}
