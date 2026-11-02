using System.Text.Json;
using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// The editor's half of <see cref="TemplateGrammarTests"/>: <c>codexTemplates.expand</c> in app-startup.js, run in a real
/// browser against the same cases as the server's parser. It needs a Playwright browser, so it lives with the end-to-end
/// tests and the unit-test filter (<c>FullyQualifiedName!~E2E</c>) leaves it out (T1).
/// </summary>
public class TemplateGrammarE2ETests
{
    [Fact]
    public async Task TheEditorsParser_ReadsEveryCaseAsTheServerDoes()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(FindRepo(), "src", "Codex.Web", "wwwroot", "js", "app-startup.js"));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body></body></html>");
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Content = script });

        foreach (var c in TemplateGrammarTests.Cases())
        {
            var x = await page.EvaluateAsync<JsonElement>("body => window.codexTemplates.expand(body, false)", c.Body);
            Assert.True(c.Text == x.GetProperty("text").GetString(), $"{c.Why}: text \"{x.GetProperty("text").GetString()}\"");
            Assert.True(c.End == x.GetProperty("end").GetInt32(), $"{c.Why}: end {x.GetProperty("end").GetInt32()}");
            Assert.Equal(c.Fields, x.GetProperty("fields").EnumerateArray()
                .Select(f => (f.GetProperty("name").GetString()!, f.GetProperty("start").GetInt32(),
                    f.GetProperty("end").GetInt32() - f.GetProperty("start").GetInt32(), f.GetProperty("mirror").GetBoolean()))
                .ToList());
        }
    }

    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Codex.Web", "wwwroot", "js", "app-startup.js"))) return dir.FullName;
        }

        throw new DirectoryNotFoundException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
