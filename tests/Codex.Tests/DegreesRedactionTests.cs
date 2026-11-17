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
            normalization: 2
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
    public void OverlappingRowsAreNamedByNumber()
    {
        var entries = Entries("One; two; three.");
        var table = Table(Row("alpha", "description", "One; two; three.", "cut"), Row("alpha", "description", "two;", "cut"));

        var ex = Assert.Throws<DegreesException>(() => RedactionStage.Run(entries, table));

        Assert.Contains("rows 1 and 2", ex.Message);
    }

    [Fact]
    public void AFailedRunLeavesTheInputUntouched_EvenWhereNormalizationWouldChangeIt()
    {
        var entries = Entries("It’s   here.  Stay.");
        var before = entries[0].Fields["description"];

        Assert.Throws<DegreesException>(() => RedactionStage.Run(entries, Table(Row("alpha", "description", "No such line.", "cut"))));
        Assert.Equal(before, entries[0].Fields["description"]);

        var overlapping = Table(Row("alpha", "description", "One; two; three.", "cut"), Row("alpha", "description", "two;", "cut"));
        var more = Entries("It’s  here. One; two; three.");
        var beforeMore = more[0].Fields["description"];
        Assert.Throws<DegreesException>(() => RedactionStage.Run(more, overlapping));
        Assert.Equal(beforeMore, more[0].Fields["description"]);
    }

    [Fact]
    public void NormalizationKeepsSuperscriptsAndFractions_ExpandsLigaturesAndDropsInvisibleCharacters()
    {
        Assert.Equal("Deal x² and ½ damage, ﬁne.".Replace("ﬁ", "fi"), SpanText.Normalize("Deal x² and ½ damage, ﬁne."));
        Assert.Equal("ab", SpanText.Normalize("a" + (char)0x200B + "b"));
        Assert.Equal("ab", SpanText.Normalize("a" + (char)0x00AD + "b" + (char)0xFEFF));
    }

    [Fact]
    public void MoreAbbreviationsDoNotEndASentence()
    {
        var text = SpanText.Normalize("Ask Mr. Smith, Dr. Jones, St. Ann and so on, etc. Then stop. Roll 3 ft. Away.");

        var sentences = SpanText.Sentences(text).Select(s => text.Substring(s.Start, s.Length)).ToList();

        Assert.Equal(2, sentences.Count);
    }

    [Fact]
    public void CuttingTheLastClauseClosesTheSentence_AndCuttingTheFirstCapitalizesTheNext()
    {
        var last = Entries("Move; shout. Next.");
        RedactionStage.Run(last, Table(Row("alpha", "description", "shout.", "cut")));
        Assert.Equal("Move. Next.", last[0].Fields["description"]);

        var first = Entries("Move; shout. Next.");
        RedactionStage.Run(first, Table(Row("alpha", "description", "Move;", "cut")));
        Assert.Equal("Shout. Next.", first[0].Fields["description"]);

        var both = Entries("Move; shout. Next.");
        RedactionStage.Run(both, Table(Row("alpha", "description", "Move;", "cut"), Row("alpha", "description", "shout.", "cut")));
        Assert.Equal("Next.", both[0].Fields["description"]);
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

    private string FixturePack(string entriesYaml, string file = "things/things.yaml")
    {
        var pack = Path.Combine(_temp, "pack");
        Directory.CreateDirectory(Path.Combine(pack, Path.GetDirectoryName(file) ?? string.Empty));
        File.WriteAllText(Path.Combine(pack, "manifest.yaml"), "id: fixture\nname: Fixture Pack Of Capitals\n");
        File.WriteAllText(Path.Combine(pack, file), entriesYaml);
        return pack;
    }

    private const string CleanEntries = """
        - id: ember-ring
          name: Ember Ring
          tags: [Fire]
          description: The Ember Ring burns. It works with Stealth checks and the Wobbly condition, so use it with care.
          properties:
            effect: Roll a Craft save; the other Ring answers.
            routes:
              - { id: a, name: Quick fix, text: "Use the Ember Ring twice." }
        - id: plain-stone
          name: Plain Stone
          description: A stone. Heavy.
        """;

    private static readonly HashSet<string> Terms = new() { "ember", "ring", "stealth", "wobbly", "craft", "plain", "stone", "roll", "quick", "heavy" };

    private static readonly Denylist NoDenylist = new(Array.Empty<string>());

    private static readonly Denylist DenyZorblax = new(new[] { Denylist.Hash("zorblax") });

    private static List<string> Details(IEnumerable<PackFinding> findings) => findings.Select(f => f.Detail).ToList();

    [Fact]
    public void ACleanFixturePackPassesTheAllowlist() =>
        Assert.Empty(PackWordCheck.CheckAllowlist(FixturePack(CleanEntries), Terms, NoDenylist));

    [Fact]
    public void AnUnlistedCapitalizedWordIsFlaggedByFileEntryNumberAndPath_ButAnExemptSentenceStartIsNot()
    {
        var pack = FixturePack(CleanEntries.Replace("A stone. Heavy.", "A stone from Zorblax. Heavy. Zorblax again."));

        var finding = Assert.Single(PackWordCheck.CheckAllowlist(pack, Terms, NoDenylist));

        Assert.Equal("things/things.yaml", finding.File);
        Assert.Equal(2, finding.Entry);
        Assert.Equal("description", finding.Path);
        Assert.Equal("Zorblax", finding.Detail);
    }

    [Fact]
    public void NamesAndEveryOtherWordNeedTheAllowlist_SoWithoutTermsEveryCapitalIsFlagged()
    {
        var flagged = PackWordCheck.CheckAllowlist(FixturePack(CleanEntries), new HashSet<string>(), NoDenylist);

        // Every name word counts: nothing is allowed because it appears in an entry name.
        Assert.Contains("Ember", Details(flagged));
        Assert.Contains("Plain", Details(flagged));
        Assert.Contains("Stealth", Details(flagged));
    }

    [Theory]
    [InlineData("family: Zorblax")]
    [InlineData("traits: [Zorblax]")]
    [InlineData("strikes: [{ name: Zorblax, bonus: 5 }]")]
    [InlineData("notes: Step 1. Zorblax waits.")]
    [InlineData("notes: Ask Mr. Zorblax now.")]
    [InlineData("notes: \"Do it.\\nZorblax is here.\"")]
    [InlineData("langs: { Zorblaxian: common }")]
    [InlineData("notes: Zorblax's Ward holds.")]
    public void EachBypassOfTheFirstWordRuleIsFlagged(string line)
    {
        var word = line.Contains("Zorblaxian") ? "Zorblaxian" : "Zorblax";
        var pack = FixturePack(CleanEntries + "\n- id: extra\n  name: Plain Stone\n  " + line + "\n");

        var flagged = Details(PackWordCheck.CheckAllowlist(pack, Terms, NoDenylist));

        Assert.Contains(word, flagged);
    }

    [Fact]
    public void ADenylistedNameIsInTheEntryNameAndIsFlaggedLikeAnyOtherWord()
    {
        var pack = FixturePack(CleanEntries.Replace("name: Plain Stone", "name: Zorblax's Ward"));

        Assert.Contains("Zorblax", Details(PackWordCheck.CheckAllowlist(pack, Terms, NoDenylist)));
    }

    [Fact]
    public void ASentenceStartPassesOnlyWhenItsLowercaseFormIsUsedElsewhere()
    {
        var pack = FixturePack("- id: one\n  name: Plain Stone\n  text: Roll twice. Heavy is a word. Light is a word. Then light it.\n");

        var flagged = Details(PackWordCheck.CheckAllowlist(pack, Terms, NoDenylist));

        Assert.Equal(new[] { "Then" }, flagged);
    }

    [Fact]
    public void ACommonWordOfTheBookMayOpenASentence_ButAnUnknownWordMayNot()
    {
        var common = new Denylist(new[] { Denylist.Hash("then") });
        var pack = FixturePack("- id: one\n  name: Plain Stone\n  text: Roll twice. Then use it. Zorblax came.\n");

        Assert.Equal(new[] { "Then", "Zorblax" }, Details(PackWordCheck.CheckAllowlist(pack, Terms, NoDenylist)));
        Assert.Equal(new[] { "Zorblax" }, Details(PackWordCheck.CheckAllowlist(pack, Terms, NoDenylist, common)));

        // The common list only exempts a sentence's first word; the same word in the middle of a sentence is still flagged.
        var middle = FixturePack("- id: one\n  name: Plain Stone\n  text: Roll twice and then Then again.\n");
        Assert.Equal(new[] { "Then" }, Details(PackWordCheck.CheckAllowlist(middle, Terms, NoDenylist, common)));
    }

    [Fact]
    public void ACapitalAfterAnOpeningQuoteIsASentenceStart_ButAfterAClosingQuoteOrAWordItIsNot()
    {
        // One fixture folder is reused, so each text is checked before the next is written.
        Assert.Empty(PackWordCheck.CheckAllowlist(FixturePack("- id: one\n  name: Plain Stone\n  text: 'Roll twice and say that light is bright, as in \"Light is bright\" said twice.'\n"), Terms, NoDenylist));
        Assert.Equal(new[] { "Light" }, Details(PackWordCheck.CheckAllowlist(FixturePack("- id: one\n  name: Plain Stone\n  text: 'Roll twice and say that light is bright, as in Light is bright said twice.'\n"), Terms, NoDenylist)));
        Assert.Equal(new[] { "Light" }, Details(PackWordCheck.CheckAllowlist(FixturePack("- id: one\n  name: Plain Stone\n  text: 'Roll twice and say that light is bright, in the \"light,\"Light is bright.'\n"), Terms, NoDenylist)));
    }

    [Fact]
    public void TheTakeListNamesOnlyGameTerms_ItsTitlesPassTheAllowlistOrAreCommonWordsOfTheBook()
    {
        var take = TakeList.Embedded();
        var terms = Allowlist.Embedded();
        var common = Denylist.EmbeddedCommon();
        var titles = take.Rows.SelectMany(r => r.Paths.Concat(r.Own).SelectMany(p => p).Concat(new[] { r.OwnName ?? string.Empty, r.Category ?? string.Empty }))
            .Concat(take.Planned.SelectMany(p => p.Paths).SelectMany(p => p))
            .Where(t => t.Length > 0)
            .ToList();

        Assert.NotEmpty(titles);
        var flagged = titles.SelectMany(PackWordCheck.Words).Where(PackWordCheck.IsCapital)
            .Where(w => !terms.Contains(w.ToLowerInvariant()) && !common.Contains(Denylist.Hash(w)))
            .Distinct().ToList();
        Assert.True(flagged.Count == 0, string.Join(", ", flagged));
    }

    [Theory]
    [InlineData("zorblax-staff: { description: \"Use it with Zorblax daily.\" }")]
    [InlineData("- just a string")]
    [InlineData("- [a, b]")]
    [InlineData("")]
    public void AFileShapeTheGatesDoNotUnderstandFailsBothGates(string yaml)
    {
        var pack = FixturePack(yaml, "odd.yaml");

        var ex1 = Assert.Throws<DegreesException>(() => PackWordCheck.CheckAllowlist(pack, Terms, NoDenylist));
        var ex2 = Assert.Throws<DegreesException>(() => PackWordCheck.CheckDenylist(pack, DenyZorblax));

        Assert.Contains("odd.yaml", ex1.Message);
        Assert.DoesNotContain("Zorblax", ex1.Message + ex2.Message);
    }

    [Fact]
    public void InvalidYamlNamesTheFile()
    {
        var pack = FixturePack("- id: [unclosed\n", "broken.yaml");

        var ex = Assert.Throws<DegreesException>(() => PackWordCheck.CheckDenylist(pack, NoDenylist));

        Assert.Contains("broken.yaml", ex.Message);
    }

    [Fact]
    public void TheDenylistCheckReportsFileEntryNumberFieldPathAndHash_NeverAnIdOrAWord()
    {
        var yaml = CleanEntries.Replace("id: ember-ring", "id: zorblax-ring").Replace("tags: [Fire]", "tags: [Zorblax]")
            .Replace("A stone. Heavy.", "A stone. HEAVY zorblax.") + "\n  langs: { Zorblax: common }\n";
        var pack = FixturePack(yaml, "things/zorblax-things.yaml");

        var findings = PackWordCheck.CheckDenylist(pack, DenyZorblax);

        Assert.NotEmpty(findings);
        Assert.Contains(findings, f => f.Entry == 1 && f.Detail == Denylist.Hash("zorblax"));
        Assert.Contains(findings, f => f.Entry == 2);
        Assert.Contains(findings, f => f.Kind.StartsWith("file name"));
        Assert.All(findings, f => Assert.DoesNotContain("orblax", f.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Empty(PackWordCheck.CheckDenylist(pack, NoDenylist));
    }

    [Fact]
    public void AnInvisibleCharacterInsideAWordDoesNotHideItFromTheDenylist()
    {
        var hidden = "Zor" + (char)0x200B + "blax";
        var pack = FixturePack("- id: one\n  name: Plain Stone\n  text: A " + hidden + " here.\n");

        Assert.NotEmpty(PackWordCheck.CheckDenylist(pack, DenyZorblax));
    }

    [Fact]
    public void ADenylistedWordThatFailsTheAllowlistIsPrintedAsItsHash()
    {
        var pack = FixturePack(CleanEntries.Replace("A stone. Heavy.", "A stone from Zorblax. Heavy."));

        var finding = Assert.Single(PackWordCheck.CheckAllowlist(pack, Terms, DenyZorblax));

        Assert.Equal($"[denylisted {Denylist.Hash("zorblax")}]", finding.Detail);
        Assert.DoesNotContain("orblax", finding.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TraitKeysAndNamesOfAPackJoinTheGameTerms_AndAPackWithoutTraitsAddsNone()
    {
        var pack = FixturePack("- id: quick-draw\n  name: Quick Draw\n  description: Light.\n- id: agile\n  name: Agile\n", "traits/traits.yaml");
        var terms = PackWordCheck.TraitTerms(new[] { pack }, "traits");
        Assert.Equal(new[] { "agile", "draw", "quick" }, terms.Order().ToArray());
        Assert.Empty(PackWordCheck.TraitTerms(new[] { pack }, "no-such-folder"));

        // Without the trait terms the capitalized names are flagged; with them the pack passes.
        var user = Path.Combine(_temp, "user");
        Directory.CreateDirectory(Path.Combine(user, "feats"));
        File.WriteAllText(Path.Combine(user, "feats", "feats.yaml"), "- id: one\n  name: Agile Draw\n  tags: [agile]\n");
        Assert.NotEmpty(PackWordCheck.CheckAllowlist(user, new HashSet<string>(), NoDenylist));
        Assert.Empty(PackWordCheck.CheckAllowlist(user, terms, NoDenylist));
    }

    /// <summary>The generated packs the CI gate must find, so it never passes on nothing; each chapter that adds a pack folder
    /// adds its name here, and a folder that is not listed fails the test.</summary>
    private static readonly string[] ExpectedGeneratedPacks = { "rules-core" };

    /// <summary>The CI gate over the generated packs: the shipped allowlist and the system's skill and condition keys and labels (trait keys join them when
    /// the system has a trait type) are the only capitalized words allowed, and no denylisted hash appears.</summary>
    [Fact]
    public async Task TheGeneratedPacksPassBothChecks()
    {
        var registry = new ContentRegistry();
        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance, new ComponentRegistry(), new YamlContentPackLoader(registry));
        await loader.LoadAndInitializeAsync(TestPaths.ContentRoot);
        var system = loader.GetDefinition("four-degrees")!;

        var terms = Allowlist.Embedded();
        foreach (var word in system.Conditions.Concat(system.SheetRules?.Skills?.List.SelectMany(s => new[] { s.Key, s.Label }) ?? Enumerable.Empty<string>()))
        {
            terms.UnionWith(PackWordCheck.Words(word).Select(w => w.ToLowerInvariant()));
        }

        var packsRoot = Path.Combine(TestPaths.ContentRoot, "systems", "four-degrees", "packs");

        // Trait keys of every shipped pack are game terms too, read from the system's trait type folder.
        var traitFolder = system.EntityTypes.Single(t => t.Type == "trait").Folder;
        terms.UnionWith(PackWordCheck.TraitTerms(Directory.EnumerateDirectories(packsRoot), traitFolder));

        var generated = Directory.EnumerateDirectories(packsRoot).Select(Path.GetFileName).Where(n => n != "homebrew-core").OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(ExpectedGeneratedPacks.OrderBy(n => n, StringComparer.Ordinal), generated);

        var deny = Denylist.Embedded();
        foreach (var name in ExpectedGeneratedPacks)
        {
            var pack = Path.Combine(packsRoot, name);
            // Joined, so a failure lists every finding (xunit shortens a collection to five).
            var flagged = string.Join("; ", PackWordCheck.CheckAllowlist(pack, terms, deny, Denylist.EmbeddedCommon()).Select(f => f.ToString()).Distinct().Order());
            Assert.True(flagged.Length == 0, flagged);
            var denied = string.Join("; ", PackWordCheck.CheckDenylist(pack, deny).Select(f => f.ToString()));
            Assert.True(denied.Length == 0, denied);
        }
    }
}