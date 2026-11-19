using Codex.Core.Consistency;

namespace Codex.Tests.Consistency;

/// <summary>References to things that are gone, fields left empty, and placeholders.</summary>
public class ContentCheckTests
{
    // ---- dangling references ----

    [Fact]
    public void AMentionOfACharacterOrPlaceThatIsGone_IsAnError_InEveryKindOfText()
    {
        var page = Fx.Page("wikipages/1-A", "Road", "Met [[actor:actors/9-A]] near [[place:p-9|the docks]].");
        var actor = Fx.Actor("actors/1-A", bio: "Sister of [[actor:actors/8-A]].");
        var fact = new FactView("facts/1-A", "Seen at [[place:p-9]].", null);
        var issues = Fx.Run(new DanglingMentionCheck(), Fx.Snap(pages: new[] { page }, actors: new[] { actor }, facts: new[] { fact }));

        Assert.All(issues, i => Assert.Equal(IssueSeverity.Error, i.Severity));
        Assert.Equal(new[]
        {
            "dangling-mention:actor:actors/1-A:biography:actor:actors/8-A",
            "dangling-mention:fact:facts/1-A:summary:place:p-9",
            "dangling-mention:page:wikipages/1-A:body:actor:actors/9-A",
            "dangling-mention:page:wikipages/1-A:body:place:p-9"
        }, issues.Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal));
        Assert.Equal(FixVia.ActorBiography, issues.Single(i => i.EntityKind == "actor").FixVia);
    }

    [Fact]
    public void AMentionOfARetiredCharacter_AMentionOfOneThatExists_AndAForLine_AreNotDangling()
    {
        var page = Fx.Page("wikipages/1-A", "Road", "[[actor:actors/1-A]] and [[place:p-1]]\n:::for [[actor:actors/gone]]\nx\n:::");
        var retired = Fx.Actor("actors/1-A", retired: true);
        var place = new PlaceView("p-1", "regions/1", "Docks", null, null);
        Assert.Empty(Fx.Run(new DanglingMentionCheck(), Fx.Snap(pages: new[] { page }, actors: new[] { retired }, places: new[] { place })));
    }

    [Fact]
    public void PointersBetweenDocuments_ThatNameAGoneDocument_AreReportedAsAnError_ThatOnlyTheDmCanFix()
    {
        var page = Fx.Page("wikipages/1-A", "Aria", actorId: "actors/gone", placeId: "p-gone");
        var actor = Fx.Actor("actors/1-A", wikiPageId: "wikipages/gone");
        var place = new PlaceView("p-1", "regions/1", "Docks", null, "wikipages/gone");
        var issues = Fx.Run(new DanglingReferenceCheck(), Fx.Snap(pages: new[] { page }, actors: new[] { actor }, places: new[] { place }));

        Assert.Equal(4, issues.Count);
        Assert.All(issues, i => { Assert.Equal(IssueSeverity.Error, i.Severity); Assert.Equal(FixVia.ReportOnly, i.FixVia); });
        Assert.Contains("dangling-reference:page:wikipages/1-A:actorId", issues.Select(i => i.Id));
    }

    // ---- empty fields ----

    [Fact]
    public void AnNpcWithAnEmptyBio_IsAnInfo_ThatNeverTellsTheModelToFillItIn()
    {
        var npc = Fx.Actor("actors/7-A", "Old Tam", ActorRole.NonPlayerCharacter, bio: "");
        var issue = Assert.Single(Fx.Run(new EmptyBioCheck(), Fx.Snap(actors: new[] { npc })));

        Assert.Equal("empty-bio:actor:actors/7-A:biography", issue.Id);
        Assert.Equal(IssueSeverity.Info, issue.Severity);
        Assert.Equal(FixVia.ActorBiography, issue.FixVia);
        Assert.Contains("Never invent", issue.FixHint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n\t ")]
    [InlineData(":::dm\n:::")]
    public void ABlankBio_IsEmpty_WhateverBlank(string? bio) =>
        Assert.Single(Fx.Run(new EmptyBioCheck(), Fx.Snap(actors: new[] { Fx.Actor("actors/1-A", bio: bio) })));

    [Fact]
    public void ABioThatHasText_ABestiaryCreature_AndARetiredCharacter_AreNotEmptyBios()
    {
        var actors = new[]
        {
            Fx.Actor("actors/1-A", bio: "Born at sea."),
            Fx.Actor("actors/2-A", bio: "", role: ActorRole.Monster),
            Fx.Actor("actors/3-A", bio: "", retired: true),
            Fx.Actor("actors/4-A", bio: ":::dm\nThe real story.\n:::")
        };
        Assert.Empty(Fx.Run(new EmptyBioCheck(), Fx.Snap(actors: actors)));
    }

    [Fact]
    public void APageWithNothingButHeadings_IsEmpty()
    {
        var pages = new[]
        {
            Fx.Page("wikipages/1-A", "Empty", ""),
            Fx.Page("wikipages/2-A", "Headings", "# Title\n\n## Sub\n"),
            Fx.Page("wikipages/3-A", "Text", "# Title\nSomething.")
        };
        Assert.Equal(new[] { "wikipages/1-A", "wikipages/2-A" }, Fx.Run(new EmptyBodyCheck(), Fx.Snap(pages: pages)).Select(i => i.EntityId));
    }

    [Fact]
    public void PlaceholderWords_AreFoundOncePerField_ButNotInTheDmsOwnNotes()
    {
        var page = Fx.Page("wikipages/1-A", "Road", "TODO: write this. TBD. Also TODO.");
        var ok = Fx.Page("wikipages/2-A", "Fine", "A todo list, and Tbd in lower case, and TODOS.");
        var actor = Fx.Actor("actors/1-A", bio: "Lorem ipsum dolor.", notes: "TODO: decide");
        var issues = Fx.Run(new TodoMarkerCheck(), Fx.Snap(pages: new[] { page, ok }, actors: new[] { actor }));

        Assert.Equal(new[] { "todo-marker:actor:actors/1-A:biography", "todo-marker:page:wikipages/1-A:body" }, issues.Select(i => i.Id));
    }

    // ---- secret blocks ----

    private static List<Issue> Secrets(string body, params ActorView[] actors)
    {
        var snapshot = Fx.Snap(pages: new[] { Fx.Page("wikipages/1-A", "Road", body) }, actors: actors);
        return new IConsistencyCheck[] { new SecretUnclosedCheck(), new SecretUnknownAudienceCheck(), new SecretForMissingCheck() }
            .SelectMany(c => c.Run(snapshot)).OrderBy(i => i, Issue.Order).ToList();
    }

    [Fact]
    public void ABlockThatIsNeverClosed_IsReported_WithItsLine()
    {
        var issue = Assert.Single(Secrets("Intro.\n:::dm\nThe keeper is a smuggler."));
        Assert.Equal("secret-unclosed:page:wikipages/1-A:body:dm", issue.Id);
        Assert.Contains("line 2", issue.Message);
    }

    [Fact]
    public void ABlockWithAnAudienceWordTheAppDoesNotKnow_IsReported_ButTheKnownOnesAreNot()
    {
        var issue = Assert.Single(Secrets(":::partyy\nx\n:::\n\n:::dm\ny\n:::\n:::DM\nz\n:::\n:::party\nq\n:::\n:::players\nw\n:::\n:::revealed\nr\n:::"));
        Assert.Equal("secret-unknown-audience:page:wikipages/1-A:body:partyy", issue.Id);
        Assert.Equal("partyy", issue.Args[0]);
    }

    [Fact]
    public void AForBlock_ForNobody_OrForACharacterWhoIsGone_IsReported_ButNotOneForACharacterWhoExists()
    {
        var aria = Fx.Actor("actors/1-A");
        var nobody = Assert.Single(Secrets(":::for\nx\n:::", aria));
        Assert.Equal("secret-for-missing:page:wikipages/1-A:body:for:none", nobody.Id);

        var gone = Assert.Single(Secrets(":::for [[actor:actors/1-A]] [[actor:actors/9-A]]\nx\n:::", aria));
        Assert.Equal("secret-for-missing:page:wikipages/1-A:body:for-actor-actors-1-a-actor-actors-9-a:actors/9-A", gone.Id);

        Assert.Empty(Secrets(":::for [[actor:actors/1-A]]\nx\n:::", aria));
    }

    [Fact]
    public void SecretChecks_ReadTheDmsNotesAndLoreToo_AndAreSilentOnTextWithoutMarkers()
    {
        var actor = Fx.Actor("actors/1-A", notes: ":::dm\nnever closed");
        var fact = new FactView("facts/1-A", "s", ":::oops\nx\n:::");
        var snapshot = Fx.Snap(pages: new[] { Fx.Page("wikipages/1-A", "Plain", "No markers at all.") }, actors: new[] { actor }, facts: new[] { fact });

        var ids = new IConsistencyCheck[] { new SecretUnclosedCheck(), new SecretUnknownAudienceCheck() }.SelectMany(c => c.Run(snapshot)).Select(i => i.Id).OrderBy(i => i, StringComparer.Ordinal);
        Assert.Equal(new[] { "secret-unclosed:actor:actors/1-A:dmNotes:dm", "secret-unknown-audience:fact:facts/1-A:details:oops" }, ids);
    }
}
