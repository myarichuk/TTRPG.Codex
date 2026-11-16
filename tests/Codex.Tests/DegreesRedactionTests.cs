using Codex.ContentImport.Degrees;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Codex.Tests;

/// <summary>The redaction mechanism of the local book importer. Every text here is generated for the test; no book is read.</summary>
public class DegreesRedactionTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "CodexRedaction_" + Guid.NewGuid());

    public DegreesRedactionTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }

    private static RedactionRow Row(string entry, string field, string span, string action, string? text = null)
    {
        var normalized = SpanText.Normalize(span);
        return new RedactionRow { Entry = entry, Field = field, Span = RedactionSpan.Of(normalized), Action = action, Text = text, Reason = "A test reason." };
    }

    private static RedactionTable Table(params RedactionRow[] rows) => new() { Rows = rows.ToList() };

    private static List<RedactionEntry> Entries(string text) => new()
    {
        new RedactionEntry("alpha", new Dictionary<string, string> { ["description"] = text }),
        new RedactionEntry("beta", new Dictionary<string, string> { ["description"] = "Plain text." }),
    };

    // ---- normalization and sentences

    [Fact]
    public void NormalizeMakesQuotesDashesLigaturesAndSpacesPlain()
    {
        Assert.Equal("It's a \"fine\" day - really.\nNext line.", SpanText.Normalize("It’s  a “ﬁne” day – really. \r\n \n Next line."));
        Assert.Equal(SpanText.Normalize("a−b"), SpanText.Normalize("a-b"));
        Assert.Equal(string.Empty, SpanText.Normalize(null));
    }

    [Fact]
    public void SentencesSplitAtAStopThenACapital_NotAtAnAbbreviationADecimalOrALowercase()
    {
        var text = SpanText.Normalize("Roll 1.5 times, e.g. twice. Then stop! Is it done? yes it is. (Last one.) End.");
        var sentences = SpanText.Sentences(text).Select(s => text.Substring(s.Start, s.Length)).ToList();

        Assert.Equal(new[] { "Roll 1.5 times, e.g. twice.", "Then stop!", "Is it done? yes it is.", "(Last one.)", "End." }, sentences);
    }

    [Fact]
    public void AParagraphBreakEndsASentence()
    {
        var text = SpanText.Normalize("First line without a stop\nSecond line.");

        Assert.Equal(2, SpanText.Sentences(text).Count);
    }

    // ---- the table

    [Fact]
    public void TheShippedTableLoadsAndValidates()
    {
        var table = RedactionTable.Embedded();

        Assert.Equal(SpanText.Version, table.Normalization);
        Assert.Equal(SpanText.Version, BooksConfig.Embedded().Normalization);
        Assert.NotNull(Denylist.Embedded());
        Assert.NotNull(Allowlist.Embedded());
    }

    [Fact]
    public void ARowRoundTripsThroughYaml()
    {
        var hash = SpanText.Sha256("Some span.");
        var table = RedactionTable.Parse($$"""
            normalization: 1
            rows:
              - entry: alpha
                field: description
                span: { sha256: "{{hash}}", length: 10 }
                action: replace
                text: "Our words."
                reason: "Carried a proper noun."
            """);

        var row = Assert.Single(table.Rows);
        Assert.Equal(hash, row.Span.Sha256);
        Assert.Equal("replace", row.Action);
        Assert.Equal("Our words.", row.Text);
    }

    [Theory]
    [InlineData("entry", "")]
    [InlineData("sha", "ABC")]
    [InlineData("length", "0")]
    [InlineData("action", "rewrite")]
    [InlineData("reason", "")]
    [InlineData("replace-without-text", "")]
    [InlineData("cut-with-text", "x")]
    [InlineData("unnormalized-text", "It’s")]
    public void AMalformedRowIsRefused(string what, string value)
    {
        var row = Row("alpha", "description", "A span.", what.StartsWith("cut") ? "cut" : "replace", "Fine.");
        switch (what)
        {
            case "entry": row.Entry = value; break;
            case "sha": row.Span.Sha256 = value; break;
            case "length": row.Span.Length = int.Parse(value); break;
            case "action": row.Action = value; break;
            case "reason": row.Reason = value; break;
            case "replace-without-text": row.Text = null; break;
            case "cut-with-text": row.Text = value; break;
            case "unnormalized-text": row.Text = value; break;
        }

        Assert.Throws<DegreesException>(() => Table(row).Validate());
    }

    [Fact]
    public void TwoRowsOnTheSameSpanAreRefused()
    {
        var a = Row("alpha", "description", "A span.", "cut");
        var b = Row("alpha", "description", "A span.", "drop-entry");

        Assert.Throws<DegreesException>(() => Table(a, b).Validate());
    }

    [Fact]
    public void ATableForAnotherNormalizationVersionIsRefused() =>
        Assert.Throws<DegreesException>(() => RedactionTable.Parse("normalization: 99\nrows: []\n"));

    // ---- the stage

    [Fact]
    public void ReplaceCutAndDropEntryApply()
    {
        var entries = Entries("Keep this. Swap this one. Remove this one. Keep the end.");
        entries.Add(new RedactionEntry("gamma", new Dictionary<string, string> { ["description"] = "Whole entry goes." }));
        var table = Table(
            Row("alpha", "description", "Swap this one.", "replace", "Swapped."),
            Row("alpha", "description", "Remove this one.", "cut"),
            Row("gamma", "description", "Whole entry goes.", "drop-entry"));

        var report = RedactionStage.Run(entries, table);

        Assert.Equal("Keep this. Swapped. Keep the end.", entries[0].Fields["description"]);
        Assert.Equal(new[] { "alpha", "beta" }, entries.Select(e => e.Id));
        Assert.Equal(new RedactionReport(3, 1, 1, 1, 2), report);
    }

    [Fact]
    public void AClauseOfASentenceCanBeRedacted_AndACutClauseLeavesAProperEnd()
    {
        var entries = Entries("Move one step; shout the name; then rest.");
        var table = Table(Row("alpha", "description", "shout the name;", "cut"), Row("beta", "description", "Plain text.", "replace", "Other text."));

        RedactionStage.Run(entries, table);

        Assert.Equal("Move one step; then rest.", entries[0].Fields["description"]);

        var last = Entries("Move one step; shout the name.");
        RedactionStage.Run(last, Table(Row("alpha", "description", "shout the name.", "cut")));
        Assert.Equal("Move one step.", last[0].Fields["description"]);
    }

    [Fact]
    public void ASpanMatchesAfterNormalization_SoCurlyQuotesAndExtraSpacesDoNotMatter()
    {
        var entries = Entries("It’s   here.  Stay.");
        var table = Table(Row("alpha", "description", "It's here.", "cut"));

        RedactionStage.Run(entries, table);

        Assert.Equal("Stay.", entries[0].Fields["description"]);
    }

    [Fact]
    public void ARowAppliesToEveryIdenticalSpanInTheField()
    {
        var entries = Entries("Same line. Other. Same line.");

        var report = RedactionStage.Run(entries, Table(Row("alpha", "description", "Same line.", "cut")));

        Assert.Equal("Other.", entries[0].Fields["description"]);
        Assert.Equal(2, report.Cut);
    }

    [Fact]
    public void ARowWhoseHashIsNotFoundFailsTheRun_ChangesNothing_AndPrintsNoText()
    {
        const string secret = "Zorblax the Unnamed ruled here.";
        var entries = Entries("A reworded line. " + secret);
        var before = entries[0].Fields["description"];
        var table = Table(
            Row("alpha", "description", "A different line.", "cut"),
            Row("alpha", "description", secret, "cut"),
            Row("missing-entry", "description", secret, "cut"),
            Row("alpha", "missing-field", secret, "cut"));

        var ex = Assert.Throws<DegreesException>(() => RedactionStage.Run(entries, table));

        Assert.Contains("3 row(s)", ex.Message);
        Assert.Contains("1, 3, 4", ex.Message);
        Assert.DoesNotContain("Zorblax", ex.Message);
        Assert.Equal(before, entries[0].Fields["description"]);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void AMatchNeedsTheLengthToo()
    {
        var row = Row("alpha", "description", "A span.", "cut");
        row.Span.Length += 1;

        Assert.Throws<DegreesException>(() => RedactionStage.Run(Entries("A span."), Table(row)));
    }

    [Fact]
    public void OverlappingRowsFail()
    {
        var entries = Entries("One; two; three.");
        var table = Table(Row("alpha", "description", "One; two; three.", "cut"), Row("alpha", "description", "two;", "cut"));

        Assert.Throws<DegreesException>(() => RedactionStage.Run(entries, table));
    }

    [Fact]
    public void TheReportIsCountsOnlyAndTheRunIsDeterministic()
    {
        string Once()
        {
            var entries = Entries("Alpha stays. Delta goes.");
            var report = RedactionStage.Run(entries, Table(Row("alpha", "description", "Delta goes.", "replace", "Changed.")));
            return report + "|" + entries[0].Fields["description"];
        }

        var first = Once();

        Assert.Equal(first, Once());
        Assert.DoesNotContain("Delta", first.Split('|')[0]);
        Assert.StartsWith("redaction: 1 row(s) applied", first);
    }

    // ---- the denylist

    [Fact]
    public void DenylistHashesAreOfLowercasedWords_AndTheFileFormatRoundTrips()
    {
        Assert.Equal(Denylist.Hash("ZORBLAX"), Denylist.Hash("zorblax"));
        var file = "# comment\n" + Denylist.Format(new[] { "Zorblax", "Quen", "Zorblax", "Skip" }, new HashSet<string> { "skip" });
        var parsed = Denylist.Parse(file);

        Assert.Equal(2, parsed.Count);
        Assert.True(parsed.Contains(Denylist.Hash("quen")));
        Assert.False(parsed.Contains(Denylist.Hash("skip")));
        Assert.Throws<DegreesException>(() => Denylist.Parse("not a hash\n"));
    }

    // ---- the allowlist and denylist tests over a generated pack

    private string FixturePack(string entriesYaml)
    {
        var pack = Path.Combine(_temp, "pack");
        Directory.CreateDirectory(Path.Combine(pack, "things"));
        File.WriteAllText(Path.Combine(pack, "manifest.yaml"), "id: fixture\nname: Fixture Pack Of Capitals\n");
        File.WriteAllText(Path.Combine(pack, "things", "things.yaml"), entriesYaml);
        return pack;
    }

    private const string CleanEntries = """
        - id: ember-ring
          name: Ember Ring
          tags: [Fire]
          description: The Ember Ring burns. It works with Stealth checks and the Wobbly condition.
          properties:
            effect: Roll a Craft save; the other Ring answers.
            routes:
              - { id: a, name: Quick fix, text: "Use the Ember Ring twice." }
        - id: plain-stone
          name: Plain Stone
          description: A stone. Heavy.
        """;

    [Fact]
    public void ACleanFixturePackPassesTheAllowlist()
    {
        var pack = FixturePack(CleanEntries);
        var terms = new HashSet<string> { "stealth", "wobbly", "craft" };

        Assert.Empty(PackWordCheck.CheckAllowlist(pack, terms));
    }

    [Fact]
    public void AnUnlistedCapitalizedWordIsFlaggedByFileEntryAndWord_ButASentenceStartIsNot()
    {
        var pack = FixturePack(CleanEntries.Replace("A stone. Heavy.", "A stone from Zorblax. Heavy. Zorblax again."));
        var terms = new HashSet<string> { "stealth", "wobbly", "craft" };

        var finding = Assert.Single(PackWordCheck.CheckAllowlist(pack, terms));

        Assert.Equal("things/things.yaml", finding.File);
        Assert.Equal("plain-stone", finding.Entry);
        Assert.Equal("Zorblax", finding.Detail);
    }

    [Fact]
    public void ANestedValueAndAFlaggedTermAddedToTheAllowlistAreHandled()
    {
        var pack = FixturePack(CleanEntries);
        var flagged = PackWordCheck.CheckAllowlist(pack, new HashSet<string>());

        Assert.Equal(new[] { "craft", "stealth", "wobbly" }, flagged.Select(f => f.Detail.ToLowerInvariant()).OrderBy(w => w, StringComparer.Ordinal));
        Assert.All(flagged, f => Assert.Equal("ember-ring", f.Entry));
    }

    [Fact]
    public void TheDenylistCheckReportsTheHashNotTheWord_AndSeesNamesIdsAndTags()
    {
        var pack = FixturePack(CleanEntries.Replace("tags: [Fire]", "tags: [Zorblax]").Replace("A stone. Heavy.", "A stone. HEAVY zorblax."));
        var deny = new Denylist(new[] { Denylist.Hash("zorblax"), Denylist.Hash("absent") });

        var findings = PackWordCheck.CheckDenylist(pack, deny);

        Assert.Equal(new[] { "ember-ring", "plain-stone" }, findings.Select(f => f.Entry).ToArray());
        Assert.All(findings, f =>
        {
            Assert.Equal(Denylist.Hash("zorblax"), f.Detail);
            Assert.DoesNotContain("orblax", f.ToString(), StringComparison.OrdinalIgnoreCase);
        });
        Assert.Empty(PackWordCheck.CheckDenylist(pack, new Denylist(Array.Empty<string>())));
    }

    [Fact]
    public void AWordPassesBecauseItNamesAnEntryOfASecondPack_OrIsASystemSkill()
    {
        var second = Path.Combine(_temp, "second");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "things.yaml"), "- id: hush-bell\n  name: Hush Bell\n  description: It rings. Pair it with the Ember Ring while Sneaking.\n");
        var first = FixturePack(CleanEntries);
        var terms = new HashSet<string> { "wobbly", "craft", "stealth" };

        var alone = PackWordCheck.CheckAllowlist(second, terms);
        var withOther = PackWordCheck.CheckAllowlist(second, terms, new[] { first });

        Assert.Equal(new[] { "Ember", "Ring", "Sneaking" }, alone.Select(f => f.Detail));
        Assert.Equal(new[] { "Sneaking" }, withOther.Select(f => f.Detail));
    }

    /// <summary>The CI gate over the generated packs: the shipped allowlist, the system's skills, conditions and attributes (keys and labels) and every
    /// pack's entry names are the only capitalized words allowed in mid-sentence, and no denylisted hash appears. No pack is generated yet, so this walks
    /// nothing today; chapter 1 makes it real.</summary>
    [Fact]
    public async Task TheGeneratedPacksPassBothChecks()
    {
        var registry = new ContentRegistry();
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(registry));
        await loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        var system = loader.GetDefinition("four-degrees")!;

        var terms = Allowlist.Embedded();
        foreach (var word in system.Conditions.Concat(system.Attributes.SelectMany(a => new[] { a.Key, a.Label }))
                     .Concat(system.SheetRules?.Skills?.List.SelectMany(s => new[] { s.Key, s.Label }) ?? Enumerable.Empty<string>())
                     .Concat(system.SheetRules?.Skills?.Ranks.SelectMany(r => new[] { r.Key, r.Label }) ?? Enumerable.Empty<string>()))
        {
            terms.UnionWith(PackWordCheck.Words(word).Select(w => w.ToLowerInvariant()));
        }

        var packs = Directory.EnumerateDirectories(Path.Combine(TestPaths.ContentRoot, "systems", "four-degrees", "packs")).OrderBy(d => d, StringComparer.Ordinal).ToList();
        var deny = Denylist.Embedded();
        foreach (var pack in packs.Where(d => Path.GetFileName(d) is "rules-core" or "rules-creatures"))
        {
            Assert.Empty(PackWordCheck.CheckAllowlist(pack, terms, packs.Where(p => p != pack)));
            Assert.Empty(PackWordCheck.CheckDenylist(pack, deny));
        }
    }
}