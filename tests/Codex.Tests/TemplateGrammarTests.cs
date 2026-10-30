using System.Text.Json;
using Codex.Core;
using Microsoft.Playwright;

namespace Codex.Tests;

/// <summary>
/// One template grammar, two parsers: <see cref="LiveTemplate.Expand"/> on the server (new pages, the template preview) and
/// <c>codexTemplates.expand</c> in app-startup.js (the editor). Both run against the same cases (Fixtures/templates.json), so
/// they can't drift apart unnoticed: a template the server shows one way and the editor fills another.
/// </summary>
public class TemplateGrammarTests
{
    public sealed record Case(string Why, string Body, string Text, int End, IReadOnlyList<(string Name, int Start, int Length, bool Mirror)> Fields);

    public static IReadOnlyList<Case> Cases()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "templates.json")));
        return json.RootElement.EnumerateArray().Select(c => new Case(
            c.GetProperty("why").GetString()!,
            c.GetProperty("body").GetString()!,
            c.GetProperty("text").GetString()!,
            c.GetProperty("end").GetInt32(),
            c.GetProperty("fields").EnumerateArray()
                .Select(f => (f[0].GetString()!, f[1].GetInt32(), f[2].GetInt32(), f[3].GetBoolean()))
                .ToList())).ToList();
    }

    [Fact]
    public void TheServersParser_ReadsEveryCaseAsWritten()
    {
        foreach (var c in Cases())
        {
            var x = LiveTemplate.Expand(c.Body);
            Assert.True(c.Text == x.Text, $"{c.Why}: text \"{x.Text}\"");
            Assert.True(c.End == x.End, $"{c.Why}: end {x.End}");
            Assert.Equal(c.Fields, x.Fields.Select(f => (f.Name, f.Start, f.Length, f.Mirror)).ToList());
        }
    }

    [Fact]
    public async Task TheEditorsParser_ReadsEveryCaseAsTheServerDoes()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(FindRepo(), "src", "Codex.Web", "wwwroot", "js", "app-startup.js"));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<!doctype html><html><body></body></html>");
        await page.AddScriptTagAsync(new PageAddScriptTagOptions { Content = script });

        foreach (var c in Cases())
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
