using Codex.Core.Consistency;

namespace Codex.Tests.Consistency;

/// <summary>Broken links and names written in plain words, the checks the world builder's "unconnected links" warning is made of.</summary>
public class LinkCheckTests
{
    private static readonly PageView Mere = Fx.Page("wikipages/1-A", "The Mere of Dead Men", "A black water.");

    // ---- broken links ----

    [Fact]
    public void ALinkWithADoubledThe_ToAPageThatExists_IsBroken_AndTheHintNamesThePage()
    {
        var source = Fx.Page("wikipages/2-A", "Road North", "We cross [[the the Mere of Dead Men]] at dusk.");
        var issues = Fx.Run(new BrokenLinkCheck(), Fx.Snap(pages: new[] { Mere, source }));

        var issue = Assert.Single(issues);
        Assert.Equal("broken-link:page:wikipages/2-A:body:the-the-mere-of-dead-men", issue.Id);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Equal("wikipages/2-A", issue.EntityId);
        Assert.Equal(FixVia.Page, issue.FixVia);
        Assert.Contains("[[The Mere of Dead Men]]", issue.FixHint);
        Assert.Equal(new[] { "the the Mere of Dead Men", "The Mere of Dead Men" }, issue.Args);
    }

    [Fact]
    public void ALinkToAPageThatDoesNotExist_IsBroken_WithTheSlugInItsId_AndNothingSuggested()
    {
        var source = Fx.Page("wikipages/2-A", "Road North", "We cross [[The Mere of Dead Men]] at dusk.");
        var issue = Assert.Single(Fx.Run(new BrokenLinkCheck(), Fx.Snap(pages: new[] { source })));

        Assert.Equal("broken-link:page:wikipages/2-A:body:the-mere-of-dead-men", issue.Id);
        Assert.Single(issue.Args);
        Assert.Contains("Create a page titled", issue.FixHint);
    }

    [Fact]
    public void ALinkThatReachesAPage_ByItsTitle_ItsSlug_OrAnOldSlugAfterARename_IsFine()
    {
        var renamed = Fx.Page("wikipages/3-A", "Old Name") with { Title = "New Name" }; // slug stays "old-name"
        var source = Fx.Page("wikipages/2-A", "Road", "[[The Mere of Dead Men]] [[the-mere-of-dead-men]] [[Old Name]] [[New Name]] [[the mere of dead men|label]]");
        Assert.Empty(Fx.Run(new BrokenLinkCheck(), Fx.Snap(pages: new[] { Mere, renamed, source })));
    }

    [Fact]
    public void AMention_IsNotAWikiLink_AndTheSameLinkTwiceIsOneIssue()
    {
        var source = Fx.Page("wikipages/2-A", "Road", "[[actor:actors/1-A]] [[Nowhere]] [[nowhere]] [[Nowhere|again]]");
        var issue = Assert.Single(Fx.Run(new BrokenLinkCheck(), Fx.Snap(pages: new[] { source })));
        Assert.EndsWith(":nowhere", issue.Id);
    }

    [Fact]
    public void ABrokenLinkInLore_IsReportedOnTheFact_AndFixedThroughTheFact()
    {
        var fact = new FactView("facts/1-A", "The keeper knows [[Lost Light]].", null);
        var issue = Assert.Single(Fx.Run(new BrokenLinkCheck(), Fx.Snap(facts: new[] { fact })));
        Assert.Equal("fact", issue.EntityKind);
        Assert.Equal(FixVia.Fact, issue.FixVia);
    }

    // ---- names written in plain words ----

    [Fact]
    public void ANameWrittenInPlainWords_WithADoubledThe_IsFound_AndTheWholePhraseQuoted()
    {
        var source = Fx.Page("wikipages/2-A", "Road North", "Beyond the ridge lies the the Mere of Dead Men, where the dead walk.");
        var issue = Assert.Single(Fx.Run(new UnlinkedMentionCheck(), Fx.Snap(pages: new[] { Mere, source })));

        Assert.Equal("unlinked-mention:page:wikipages/2-A:body:wikipages/1-A", issue.Id);
        Assert.Equal("the the Mere of Dead Men", issue.Args[0]);
        Assert.Equal("The Mere of Dead Men", issue.Args[1]);
        Assert.Contains("[[The Mere of Dead Men]]", issue.FixHint);
    }

    [Fact]
    public void AWordTypedTwiceInTheMiddleOfAName_StillMatchesIt()
    {
        var source = Fx.Page("wikipages/2-A", "Road North", "We cross the Mere of Dead Dead Men tomorrow.");
        var issue = Assert.Single(Fx.Run(new UnlinkedMentionCheck(), Fx.Snap(pages: new[] { Mere, source })));
        Assert.Equal("the Mere of Dead Dead Men", issue.Args[0]);
    }

    [Fact]
    public void ANameInPlainWords_IsReportedOncePerPage_AndNotWhenThePageAlreadyLinksIt()
    {
        var twice = Fx.Page("wikipages/2-A", "A", "The Mere of Dead Men is dark. The Mere of Dead Men is cold.");
        var linked = Fx.Page("wikipages/3-A", "B", "[[The Mere of Dead Men]] is dark. The Mere of Dead Men is cold.");
        var issues = Fx.Run(new UnlinkedMentionCheck(), Fx.Snap(pages: new[] { Mere, twice, linked }));

        Assert.Equal("wikipages/2-A", Assert.Single(issues).EntityId);
    }

    [Fact]
    public void WordsInsideALinkLabelOrAMarkerLine_AreNotAMention()
    {
        var source = Fx.Page("wikipages/2-A", "A", "See [[Elsewhere|the Mere of Dead Men]] and\n:::for [[actor:x]]\nnothing.\n:::");
        Assert.Empty(Fx.Run(new UnlinkedMentionCheck(), Fx.Snap(pages: new[] { Mere, source })));
    }

    [Fact]
    public void OnlyNamesOfTwoWordsOrMore_WrittenWithACapital_OrThreeWordsLong_Count()
    {
        var keep = Fx.Page("wikipages/4-A", "Iron Keep");
        var single = Fx.Page("wikipages/5-A", "Waterdeep");
        var low = Fx.Page("wikipages/2-A", "A", "the iron keep and waterdeep and Waterdeep");
        var cap = Fx.Page("wikipages/3-A", "B", "Iron Keep stands.");

        var issues = Fx.Run(new UnlinkedMentionCheck(), Fx.Snap(pages: new[] { keep, single, low, cap }));
        Assert.Equal(new[] { "wikipages/3-A" }, issues.Select(i => i.EntityId));
    }

    [Fact]
    public void ANamePunctuationOrAParagraphBreakSplits_IsNotAMention()
    {
        var source = Fx.Page("wikipages/2-A", "A", "Iron. Keep stands.\n\nIron\n\nKeep, then Iron,\nKeep");
        Assert.Empty(Fx.Run(new UnlinkedMentionCheck(), Fx.Snap(pages: new[] { Fx.Page("wikipages/4-A", "Iron Keep"), source })));
    }

    [Fact]
    public void ThePageItselfIsNotLinkedToItself_AndTwoPagesOfOneNameAreNotGuessedBetween()
    {
        var a = Fx.Page("wikipages/1-A", "Iron Keep", "Iron Keep is a fort.");
        var b = Fx.Page("wikipages/2-A", "The Iron Keep", "Another.");
        var other = Fx.Page("wikipages/3-A", "Other", "Iron Keep!");

        Assert.Empty(Fx.Run(new UnlinkedMentionCheck(), Fx.Snap(pages: new[] { a, b, other })));
        Assert.Empty(Fx.Run(new UnlinkedMentionCheck(), Fx.Snap(pages: new[] { a })));
    }

    [Fact]
    public void APartyPageIsNeverToldToLinkADmOnlyPage()
    {
        var secret = Fx.Page("wikipages/1-A", "Iron Keep", party: false);
        var party = Fx.Page("wikipages/2-A", "Road", "Iron Keep looms.", party: true);
        var dm = Fx.Page("wikipages/3-A", "Notes", "Iron Keep looms.", party: false);

        var issues = Fx.Run(new UnlinkedMentionCheck(), Fx.Snap(pages: new[] { secret, party, dm }));
        Assert.Equal(new[] { "wikipages/3-A" }, issues.Select(i => i.EntityId));
    }

    // ---- links that show players nothing ----

    [Fact]
    public void AnOpenPageLinkingAPageOnlyTheDmCanOpen_IsReported_ButNotInsideADmBlock()
    {
        var secret = Fx.Page("wikipages/1-A", "Hidden Vault", party: false);
        var leaky = Fx.Page("wikipages/2-A", "Road", "Beware [[Hidden Vault]].", party: true);
        var inBlock = Fx.Page("wikipages/3-A", "Road 2", "Fine.\n:::dm\nSee [[Hidden Vault]].\n:::", party: true);
        var dmPage = Fx.Page("wikipages/4-A", "Notes", "See [[Hidden Vault]].", party: false);

        var issue = Assert.Single(Fx.Run(new VisibilityLeakCheck(), Fx.Snap(pages: new[] { secret, leaky, inBlock, dmPage })));
        Assert.Equal("visibility-leak:page:wikipages/2-A:body:wikipages/1-A", issue.Id);
    }
}
