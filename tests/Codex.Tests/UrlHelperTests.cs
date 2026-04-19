using Codex.Web;
using Xunit;

namespace Codex.Tests;

public class UrlHelperTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/home")]
    [InlineData("/campaign/123")]
    [InlineData("/login?returnUrl=/")]
    public void IsLocalUrl_ReturnsTrue_ForValidLocalUrls(string url)
    {
        Assert.True(UrlHelper.IsLocalUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://google.com")]
    [InlineData("https://google.com")]
    [InlineData("//google.com")]
    [InlineData("/\\google.com")]
    [InlineData("javascript:alert(1)")]
    public void IsLocalUrl_ReturnsFalse_ForInvalidOrRemoteUrls(string url)
    {
        Assert.False(UrlHelper.IsLocalUrl(url));
    }
}
