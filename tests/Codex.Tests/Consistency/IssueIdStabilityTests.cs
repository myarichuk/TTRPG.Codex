using Codex.Core;
using Codex.Core.Consistency;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests.Consistency;

/// <summary>An issue id names a problem, not its position or its numbers: a dismissal or a <c>resolves</c> claim stays on the same problem when something
/// else in the world changes or the problem is partly fixed.</summary>
public class IssueIdStabilityTests
{
    private static List<Issue> Secrets(string body)
    {
        var snapshot = Fx.Snap(pages: new[] { Fx.Page("wikipages/1-A", "Road", body) });
        return new IConsistencyCheck[] { new SecretUnclosedCheck(), new SecretUnknownAudienceCheck() }.SelectMany(c => c.Run(snapshot)).OrderBy(i => i, Issue.Order).ToList();
    }

    [Fact]
    public void InsertingASecretBlockEarlier_LeavesTheIdsOfTheLaterOnesAsTheyWere()
    {
        const string later = ":::oops\nx\n:::\n:::dm\nnever closed";
        var before = Secrets(later).Select(i => i.Id).ToList();
        Assert.Equal(2, before.Count);

        var after = Secrets(":::partyy\nnew\n:::\nSome words.\n" + later).Select(i => i.Id).ToList();

        Assert.Equal(3, after.Count);
        foreach (var id in before) Assert.Contains(id, after);
    }

    [Fact]
    public void TwoBlocksWithTheSameWordAndFirstLine_AreToldApartByOrder_AndOnlyThen()
    {
        var ids = Secrets(":::oops\na\n:::\n:::oops note\na\n:::").Select(i => i.Id).ToList();
        Assert.Equal(new[] { "secret-unknown-audience:page:wikipages/1-A:body:oops:a", "secret-unknown-audience:page:wikipages/1-A:body:oops:a.2" }, ids);

        // Another first line is another key, without any number.
        ids = Secrets(":::oops\na\n:::\n:::oops\nb\n:::").Select(i => i.Id).ToList();
        Assert.Equal(new[] { "secret-unknown-audience:page:wikipages/1-A:body:oops:a", "secret-unknown-audience:page:wikipages/1-A:body:oops:b" }, ids);
    }

    private static List<string> AllSecretIds(string body, params ActorView[] actors)
    {
        var snapshot = Fx.Snap(pages: new[] { Fx.Page("wikipages/1-A", "Road", body) }, actors: actors);
        return ConsistencyChecker.Run(snapshot, new IConsistencyCheck[] { new SecretUnclosedCheck(), new SecretUnknownAudienceCheck(), new SecretForMissingCheck() })
            .Select(i => i.Id).ToList();
    }

    [Fact]
    public void FixingTheFirstOfTwoBlocksWithTheSameUnknownWord_LeavesTheSecondsIdAlone()
    {
        // A: two ":::gm" blocks. Fixing the first (":::gm" -> ":::dm") resolves its id and does not rename the second to it.
        var before = AllSecretIds(":::gm\nThe duke is a vampire.\n:::\n:::gm\nThe vault is empty.\n:::");
        Assert.Equal(new[] { "secret-unknown-audience:page:wikipages/1-A:body:gm:the-duke-is-a-vampire", "secret-unknown-audience:page:wikipages/1-A:body:gm:the-vault-is-empty" }, before);

        var after = AllSecretIds(":::dm\nThe duke is a vampire.\n:::\n:::gm\nThe vault is empty.\n:::");
        Assert.Equal(new[] { before[1] }, after);

        var recheck = ConsistencyChecker.Compare(before, after.Select(id => new Issue(id, "x", IssueSeverity.Warning, "page", "wikipages/1-A", "body", "c", Array.Empty<string>(), "", "", "", FixVia.Page)), new[] { before[0] });
        Assert.Equal(new[] { before[0] }, recheck.Resolved);
        Assert.Empty(recheck.StillOpen);
        Assert.Empty(recheck.ResolvedUnclaimed);
        Assert.Empty(recheck.New);
    }

    [Fact]
    public void TakingOneMissingCharacterOffTheOpeningLine_LeavesTheOtherMissingCharactersIdAlone()
    {
        // B: two characters named on a :::for line are gone. Taking actor 9 off the line (what the hint asks) resolves its issue; 8's id stays.
        var before = AllSecretIds(":::for [[actor:actors/9]] [[actor:actors/8]]\nThe map is false.\n:::");
        Assert.Equal(new[] { "secret-for-missing:page:wikipages/1-A:body:for:the-map-is-false:actors/8", "secret-for-missing:page:wikipages/1-A:body:for:the-map-is-false:actors/9" }, before);

        var after = AllSecretIds(":::for [[actor:actors/8]]\nThe map is false.\n:::");
        Assert.Equal(new[] { before[0] }, after);
    }

    [Fact]
    public void AddingAnEarlierClosedBlockOfTheSameWord_LeavesAnUnclosedBlocksIdAlone()
    {
        // C: an unclosed ":::dm" keeps its id when a closed ":::dm" is added before it (even one with the very same first line: it has no unclosed issue).
        var before = Assert.Single(AllSecretIds(":::dm\nfirst\n:::\nWords.\n:::dm\nThe keeper lies."));
        Assert.Equal("secret-unclosed:page:wikipages/1-A:body:dm:the-keeper-lies", before);

        Assert.Equal(new[] { before }, AllSecretIds(":::dm\nnew\n:::\n:::dm\nfirst\n:::\nWords.\n:::dm\nThe keeper lies."));
        Assert.Equal(new[] { before }, AllSecretIds(":::dm\nThe keeper lies.\n:::\n:::dm\nfirst\n:::\nWords.\n:::dm\nThe keeper lies."));
    }

    /// <summary>One generated block: its opener word, the characters a <c>:::for</c> names, its first line, and whether it is closed.</summary>
    private sealed record GenBlock(string Word, List<string> Knowers, string Content, bool Closed)
    {
        public string Text => $":::{Word}{string.Concat(Knowers.Select(k => $" [[actor:{k}]]"))}\n{Content}\n" + (Closed ? ":::\n" : "");
    }

    private static string Render(IEnumerable<GenBlock> blocks) => string.Join("Plain words.\n", blocks.Select(b => b.Text));

    [Fact]
    public void FixingAnyOneBlock_NeverChangesTheIdsOfTheOtherBlocksIssues_OnGeneratedPages()
    {
        var aria = Fx.Actor("actors/1");
        var words = new[] { "gm", "oops", "dm", "party", "for", "for", "players" };
        var lines = new[] { "the duke lies", "a vault", "secret door", "the map", "x" };
        for (var seed = 0; seed < 400; seed++)
        {
            var random = new Random(seed);
            var count = random.Next(2, 6);
            var blocks = new List<GenBlock>();
            var used = new HashSet<string>();
            for (var b = 0; b < count; b++)
            {
                var word = words[random.Next(words.Length)];
                var knowers = word == "for" ? new[] { "actors/1", "actors/8", "actors/9" }.Where(_ => random.Next(2) == 0).ToList() : new List<string>();
                // A first line no other block has (two blocks with the same word and first line are the documented limit; see the test above).
                string content;
                do content = lines[random.Next(lines.Length)] + " " + random.Next(4); while (!used.Add(content));
                // At most the last block is left open, so every closer closes its own block.
                blocks.Add(new GenBlock(word, knowers, content, b < count - 1 || random.Next(3) > 0));
            }

            var before = AllSecretIds(Render(blocks), aria);
            for (var fix = 0; fix < blocks.Count; fix++)
            {
                var block = blocks[fix];
                // The fix of this block's own issue: close it, give its word a known audience, or take the missing characters off its line.
                var fixedBlock = !block.Closed ? block with { Closed = true }
                    : block.Word is "gm" or "oops" ? block with { Word = "dm" }
                    : block.Word == "for" ? block with { Knowers = block.Knowers.Where(k => k == "actors/1").DefaultIfEmpty("actors/1").ToList() }
                    : block;
                var changed = blocks.Select((b, i) => i == fix ? fixedBlock : b).ToList();
                var after = AllSecretIds(Render(changed), aria);

                // The other blocks' issues are those whose key holds their own first line.
                var others = before.Where(id => !id.Contains(":" + WikiText.Slugify(block.Content), StringComparison.Ordinal)).ToList();
                foreach (var id in others) Assert.True(after.Contains(id), $"seed {seed}, fixing block {fix}: {id} changed. Before: {string.Join(", ", before)}; after: {string.Join(", ", after)}");
                Assert.True(after.Count <= before.Count, $"seed {seed}, fixing block {fix} made new issues: {string.Join(", ", after.Except(before))}");
            }
        }
    }

    [Fact]
    public void ALongOrOddOpeningLine_GivesABoundedSlug_NotItsText()
    {
        var id = Assert.Single(Secrets(":::weird " + new string('x', 300) + "\nbody\n:::")).Id;
        Assert.True(id.Length < 120, id);
        Assert.DoesNotContain(" ", id);
    }

    private static readonly SystemDefinition Limited = SystemDefinitionLoader.Parse("""
        id: demo
        name: Demo
        entityTypes:
          - { type: feat, label: Feat }
        sheetRules:
          enforcement: strict
          lists:
            - { key: feats, label: Feats, targetEntityType: feat, maxCount: { value: 2 } }
        """);

    private static readonly Fx.Entries Library = new(Fx.Entry("feat", "alert", "Alert"), Fx.Entry("feat", "lucky", "Lucky"), Fx.Entry("feat", "tough", "Tough"), Fx.Entry("feat", "sharp", "Sharp"));

    private static List<Issue> Rules(params string[] feats) => Fx.Run(new SheetRuleCheck(), Fx.Snap(
        actors: new[] { Fx.Actor("actors/1-A", sheet: new Dictionary<string, object> { ["feats"] = feats.Cast<object>().ToList() }) }, system: Limited, library: Library));

    [Fact]
    public void APartialFix_OfARuleWithNumbersInIt_KeepsTheIssuesId()
    {
        // Five picked where two are allowed; dropping to four is still the same problem, with other numbers in its sentence.
        var five = Assert.Single(Rules("alert", "lucky", "tough", "sharp", "alert|x"), i => i.Code == "Actor.Rule.Limit");
        var four = Assert.Single(Rules("alert", "lucky", "tough", "sharp"), i => i.Code == "Actor.Rule.Limit");

        Assert.NotEqual(five.Message, four.Message);
        Assert.Equal(five.Id, four.Id);
        Assert.Equal("sheet-rule:actor:actors/1-A:feats:Actor.Rule.Limit", four.Id);
    }

    [Fact]
    public void FixingOneOfTwoMissingEntries_KeepsTheOthersId_NoIdIsRenumbered()
    {
        var both = Rules("ghost", "wraith").Where(i => i.Code == "Actor.Rule.NotInLibrary").Select(i => i.Id).ToList();
        Assert.Equal(new[] { "sheet-rule:actor:actors/1-A:feats:Actor.Rule.NotInLibrary:ghost", "sheet-rule:actor:actors/1-A:feats:Actor.Rule.NotInLibrary:wraith" }, both);

        // Whichever of the two is fixed, the other is still the same id (no "~2" that would move to the other).
        Assert.Equal(new[] { both[1] }, Rules("wraith").Where(i => i.Code == "Actor.Rule.NotInLibrary").Select(i => i.Id));
        Assert.Equal(new[] { both[0] }, Rules("ghost").Where(i => i.Code == "Actor.Rule.NotInLibrary").Select(i => i.Id));
    }

    [Fact]
    public void TwoIssuesThatStillShareAnId_AreNumberedByWhatTheySay_NotByTheOrderTheCheckMadeThem()
    {
        IReadOnlyList<Issue> Run(params string[] messages) => ConsistencyChecker.Run(Fx.Snap(), new IConsistencyCheck[] { new TwoOf(messages) });
        var forward = Run("alpha", "beta");
        var backward = Run("beta", "alpha");

        Assert.Equal(new[] { "x:y:z:f", "x:y:z:f~2" }, forward.Select(i => i.Id));
        Assert.Equal(forward.Select(i => (i.Id, i.Message)), backward.Select(i => (i.Id, i.Message)));
    }

    private sealed class TwoOf(string[] messages) : IConsistencyCheck
    {
        public string Name => "x";

        public IEnumerable<Issue> Run(WorldSnapshot snapshot) => messages.Select(m => new Issue("x:y:z:f", "x", IssueSeverity.Info, "y", "z", "f", "Consistency.Test", Array.Empty<string>(), m, "", "y:z", FixVia.ReportOnly));
    }
}
