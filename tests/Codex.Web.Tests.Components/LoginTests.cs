using Bunit;
using Xunit;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Codex.Web.Components.Pages;
using Codex.Persistence;

namespace Codex.Web.Tests.Components;

public class LoginTests : BunitContext
{
    [Fact]
    public void Render_WithLocalLoginEnabled_ShowsLoginForm()
    {
        // Arrange
        var mockUserRepository = new Mock<IUserRepository>();
        var mockSchemeProvider = new Mock<IAuthenticationSchemeProvider>();

        var mockConfig = new Mock<IConfiguration>();
        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(s => s.Value).Returns("true");
        mockConfig.Setup(c => c.GetSection("Authentication:EnableLocalLogin")).Returns(mockSection.Object);

        Services.AddSingleton(mockUserRepository.Object);
        Services.AddSingleton(mockSchemeProvider.Object);
        Services.AddSingleton(mockConfig.Object);

        mockSchemeProvider.Setup(s => s.GetAllSchemesAsync())
            .ReturnsAsync(new List<AuthenticationScheme>());

        // Act
        var cut = Render<Login>();

        // Assert
        cut.WaitForState(() => cut.FindAll("form").Count > 0);
        Assert.Contains("Account Username", cut.Markup);
        Assert.Contains("Secret Key", cut.Markup);
        Assert.Contains("Enter Codex", cut.Markup);
    }
}
