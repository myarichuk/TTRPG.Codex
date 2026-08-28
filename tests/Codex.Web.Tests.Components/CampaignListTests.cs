using Bunit;
using Xunit;
using Moq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Codex.Web.Components.Pages;
using Codex.Persistence;

namespace Codex.Web.Tests.Components;

public class CampaignListTests : BunitContext
{
    [Fact]
    public void Render_InitialState_ShowsLoadingSpinner()
    {
        // Arrange
        var mockRepo = new Mock<ICampaignRepository>();
        var mockJsRuntime = new Mock<IJSRuntime>();

        // Setup repository to return a delayed task to capture the loading state
        var tcs = new TaskCompletionSource<IEnumerable<CampaignDocument>>();
        mockRepo.Setup(r => r.GetAllAsync()).Returns(tcs.Task);

        Services.AddSingleton(mockRepo.Object);
        Services.AddSingleton(mockJsRuntime.Object);

        // Act
        var cut = Render<CampaignList>();

        // Assert
        Assert.Contains("spinner-border", cut.Markup);
        Assert.Contains("Loading...", cut.Markup);
    }

    [Fact]
    public void Render_WithNoCampaigns_ShowsEmptyState()
    {
        // Arrange
        var mockRepo = new Mock<ICampaignRepository>();
        var mockJsRuntime = new Mock<IJSRuntime>();

        mockRepo.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<CampaignDocument>());

        Services.AddSingleton(mockRepo.Object);
        Services.AddSingleton(mockJsRuntime.Object);

        // Act
        var cut = Render<CampaignList>();

        // Wait for component to re-render after async load
        cut.WaitForState(() => !cut.Markup.Contains("Loading..."));

        // Assert
        Assert.Contains("No Campaigns Found", cut.Markup);
        Assert.Contains("Create Your First Campaign", cut.Markup);
    }

    [Fact]
    public void Render_WithCampaigns_ShowsCampaignCards()
    {
        // Arrange
        var mockRepo = new Mock<ICampaignRepository>();
        var mockJsRuntime = new Mock<IJSRuntime>();

        var campaigns = new List<CampaignDocument>
        {
            new CampaignDocument { Id = "1", Name = "Test Campaign 1", System = "D&D 5E", UpdatedAt = System.DateTime.UtcNow },
            new CampaignDocument { Id = "2", Name = "Test Campaign 2", System = "SWFFG", UpdatedAt = System.DateTime.UtcNow }
        };

        mockRepo.Setup(r => r.GetAllAsync())
            .ReturnsAsync(campaigns);

        Services.AddSingleton(mockRepo.Object);
        Services.AddSingleton(mockJsRuntime.Object);

        // Act
        var cut = Render<CampaignList>();

        cut.WaitForState(() => cut.FindAll(".campaign-card").Count > 0);

        // Assert
        Assert.Contains("Test Campaign 1", cut.Markup);
        Assert.Contains("Test Campaign 2", cut.Markup);
    }
}
