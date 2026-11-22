using System.Globalization;
using Codex.Localization;

namespace Codex.Tests;

/// <summary>Code that words English differently (a lowercased type name, the table's own slot wording) asks <see cref="Localizer.IsEnglish"/>.
/// A server or CI runner with no language set runs under the invariant culture, which reads the neutral (English) files, so it is English too.</summary>
public class LocalizerEnglishTests
{
    [Theory]
    [InlineData("en", true)]
    [InlineData("en-GB", true)]
    [InlineData("ru", false)]
    [InlineData("he", false)]
    public void APinnedCulture_IsEnglishOnlyForEnglish(string culture, bool english) =>
        Assert.Equal(english, Localizer.For(culture).IsEnglish);

    [Fact]
    public void TheInvariantCulture_IsEnglish_BecauseItReadsTheNeutralFiles()
    {
        var was = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            var l = new Localizer();
            Assert.True(l.IsEnglish);
            Assert.Equal(Localizer.For("en")["Library.Empty.AddOwn"], l["Library.Empty.AddOwn"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = was;
        }
    }
}
