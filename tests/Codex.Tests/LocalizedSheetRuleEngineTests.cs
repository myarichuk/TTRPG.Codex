using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>
/// Every sheet-rule test again, on a copy of the system in which every label is pseudo-translated (see
/// <see cref="PseudoTranslation"/>). Pack entries and sheets still say "STR", "Strength" and "Athletics", so the rules must
/// keep resolving them whatever the reader sees.
/// </summary>
public class LocalizedSheetRuleEngineTests : SheetRuleEngineTests
{
    private static readonly SystemDefinition Translated = PseudoTranslation.Localize(SystemDefinitionLoader.Parse(Yaml));

    protected override SystemDefinition Sys => Translated;

    [Fact]
    public void TheCopyReallyIsTranslated()
    {
        Assert.Equal("§Strength", Translated.Attributes[0].Label);
        Assert.Equal("STR", Translated.Attributes[0].CanonicalAbbreviation);
        Assert.StartsWith("§", Translated.SheetRules!.Derived[0].Label);
    }
}

public class LocalizedSheetProgressionTests : SheetProgressionTests
{
    private static readonly SystemDefinition Translated = PseudoTranslation.Localize(SystemDefinitionLoader.Parse(Yaml));

    protected override SystemDefinition Sys => Translated;
}

public class LocalizedSheetVitalsTests : SheetVitalsTests
{
    private static readonly SystemDefinition Translated = PseudoTranslation.Localize(SystemDefinitionLoader.Parse(Yaml));

    protected override SystemDefinition Sys => Translated;
}
