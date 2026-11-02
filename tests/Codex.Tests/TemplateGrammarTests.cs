using System.Text.Json;
using Codex.Core;

namespace Codex.Tests;

/// <summary>
/// One template grammar, two parsers: <see cref="LiveTemplate.Expand"/> on the server (new pages, the template preview) and
/// <c>codexTemplates.expand</c> in app-startup.js (the editor). Both run against the same cases (Fixtures/templates.json), so
/// they can't drift apart unnoticed: a template the server shows one way and the editor fills another. The editor's half needs
/// a browser and is <see cref="E2E.TemplateGrammarE2ETests"/>.
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
}
