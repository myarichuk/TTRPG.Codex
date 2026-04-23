using Bunit;
using Xunit;
using Codex.Web.Components.Pages;

namespace Codex.Web.Tests.Components;

public class LoadingScreenTests : BunitContext
{
    [Fact]
    public void Render_InitializesWithDefaultMessage()
    {
        // Act
        var cut = Render<LoadingScreen>();

        // Assert
        cut.MarkupMatches(@"
        <div class=""loading-screen-backdrop"">
          <div class=""loading-container"">
            <div class=""logo-anchor"">
              <svg xmlns=""http://www.w3.org/2000/svg"" width=""32"" height=""32"" viewBox=""0 0 24 24"" fill=""none"" stroke=""white"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"" class=""lucide-icon "">
                <path d=""M6 3h12l4 6-10 13L2 9Z""></path>
                <path d=""M11 3v21""></path>
                <path d=""M13 3v21""></path>
                <path d=""M2 9h20""></path>
                <path d=""M6 3l6 6""></path>
                <path d=""M18 3l-6 6""></path>
              </svg>
            </div>
            <div class=""brand-name"">TTRPG.Codex</div>
            <div class=""status-container"">
              <div id=""status"" class=""status-text active"">Initializing Systems...</div>
            </div>
            <div class=""progress-wrapper"">
              <div class=""progress-bar-inner""></div>
            </div>
            <div class=""version-tag"">Core Engine v2.5.0-wasm</div>
          </div>
        </div>
        ");
    }
}
