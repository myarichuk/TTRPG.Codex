using System.Text;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>The book routes over HTTP, against the real app: who may ask, what they get back, and that the PDF is a PDF.</summary>
[Collection("RavenDb")]
public class BookRouteE2ETests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public BookRouteE2ETests(AppFixture app) => _app = app;

    [Fact]
    public async Task OnlyTheDmGetsABook_AsAPageOrAsAPdf_AndTheLanguageGateAnswersWithAReason()
    {
        const string password = "Correct-Horse-1";
        var dmId = "bk-dm-" + Guid.NewGuid();
        var otherId = "bk-other-" + Guid.NewGuid();
        await _app.SeedUserAsync(dmId, "dm_" + dmId, password, "Player");
        await _app.SeedUserAsync(otherId, "other_" + otherId, password, "Player");

        await using var dmContext = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var dm = await dmContext.NewPageAsync();
        await E2EHelpers.LoginAsync(dm, "dm_" + dmId, password);
        var campaignId = await E2EHelpers.CreateCampaignAsync(dm, "Saltmere Chronicle");

        foreach (var audience in new[] { "dm", "party" })
        {
            var page = await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/{audience}");
            Assert.Equal(200, page.Status);
            Assert.StartsWith("text/html", page.Headers["content-type"]);
            Assert.Equal("no-store", page.Headers["cache-control"]);
            Assert.Contains("default-src 'none'", page.Headers["content-security-policy"]);
            Assert.Contains("Saltmere Chronicle", await page.TextAsync());
        }

        var pdf = await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/party/pdf");
        Assert.True(pdf.Status == 200, $"pdf status {pdf.Status}: {await pdf.TextAsync()}");
        Assert.Equal("application/pdf", pdf.Headers["content-type"]);
        Assert.Equal("no-store", pdf.Headers["cache-control"]);
        Assert.Contains("attachment", pdf.Headers["content-disposition"]);
        Assert.Contains(".pdf", pdf.Headers["content-disposition"]);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString((await pdf.BodyAsync()).AsSpan(0, 5)));

        // The theme cannot print Russian: a reason, not a half-English page.
        var russian = await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/dm/pdf?lang=ru");
        Assert.Equal(422, russian.Status);
        Assert.Equal(400, (await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/dm?theme=nope")).Status);
        Assert.Equal(404, (await dmContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/everyone")).Status);

        // Somebody who is not this campaign's DM gets nothing, and cannot tell a campaign from no campaign.
        await using var otherContext = await _app.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = _app.BaseUrl });
        var other = await otherContext.NewPageAsync();
        await E2EHelpers.LoginAsync(other, "other_" + otherId, password);
        foreach (var route in new[] { "dm", "party", "dm/pdf", "party/pdf" })
        {
            Assert.Equal(404, (await otherContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/{campaignId}/book/{route}")).Status);
        }

        Assert.Equal(404, (await otherContext.APIRequest.GetAsync($"{_app.BaseUrl}/campaigns/no-such-campaign/book/dm")).Status);
    }
}
