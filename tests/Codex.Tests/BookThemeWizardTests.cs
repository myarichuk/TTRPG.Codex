using Codex.Core.Book;
using Codex.Persistence;
using Codex.Web.Book;

namespace Codex.Tests;

/// <summary>
/// Themes the GM brings into a campaign with the theme wizard (docs/BOOK_EXPORT.md 41.22): a pasted stylesheet's faces read and measured for each
/// language, stand-ins proposed and written for the scripts its faces lack, and the theme kept with the campaign, for its GM only.
/// </summary>
[Collection("RavenDb")]
public class BookThemeWizardTests : IClassFixture<RavenDbFixture>
{
    private const string DmUser = "theme_dm_user";
    private const string PlayerUser = "theme_player_user";

    private readonly CampaignRepository _campaigns;
    private readonly RavenBookDraftRepository _store;
    private readonly RavenDbService _db;

    public BookThemeWizardTests(RavenDbFixture fixture)
    {
        var db = new RavenDbService(fixture.DbPath, fixture.DbName, runInMemory: true);
        _store = new RavenBookDraftRepository(db);
        _db = db;
        _campaigns = new CampaignRepository(db, new NoOpSystemCatalog(), new ActorRepository(db), new RavenSessionRepository(db), new RavenNoteRepository(db),
            new RegionRepository(db), new FactRepository(db), new RavenHomebrewRepository(db), new RavenEncounterRepository(db), new RavenWikiRepository(db),
            bookDraftRepository: _store);
    }

    private static byte[]? Font(string file)
    {
        var path = Path.Combine(TestPaths.RepoRoot, "src", "Codex.Web", "wwwroot", "fonts", file);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static string Face(string family, string file, string weight = "400", string style = "normal", string? range = null) =>
        $"@font-face {{ font-family: \"{family}\"; font-weight: {weight}; font-style: {style}; src: url(data:font/woff2;base64,{Convert.ToBase64String(Font(file)!)}) format('woff2');"
        + (range != null ? $" unicode-range: {range};" : "") + " }\n";

    // A theme as a GM pastes one: a Latin text face in two styles, a title face, an icon-like face with no Latin letters, and a face it names but
    // does not bring.
    private static readonly string Pasted =
        Face("Saltmere Text", "im-fell-english/im-fell-english-latin-400-normal.woff2")
        + Face("Saltmere Text", "im-fell-english/im-fell-english-latin-400-italic.woff2", style: "italic")
        + Face("Saltmere Titles", "pirata-one/pirata-one-latin-400-normal.woff2", weight: "bold")
        + Face("Saltmere Text", "old-standard-tt/old-standard-tt-cyrillic-400-normal.woff2", range: BookThemes.Cyrillic)
        + Face("Saltmere Text", "old-standard-tt/old-standard-tt-cyrillic-400-italic.woff2", style: "italic", range: BookThemes.Cyrillic)
        + Face("Saltmere Titles", "ruslan-display/ruslan-display-cyrillic-400-normal.woff2", weight: "bold", range: BookThemes.Cyrillic)
        + Face("Dice", "assistant/assistant-hebrew-wght-normal.woff2")
        + ".page { font-family: 'Saltmere Text', serif; } h1 { font: bold 24px/1.1 \"Saltmere Titles\", Georgia, serif; } .note { font-family: \"Tasha Hand\", cursive; }";

    [Fact]
    public void APastedThemesFaces_AreRead_AndEachLanguageSaysWhichTextFacesDrawIt()
    {
        var measure = BookThemeCatalog.Measure(Pasted, false, new BookCssWeb(), Font);

        Assert.Equal(7, measure.Faces.Count);
        Assert.All(measure.Faces, f => Assert.NotNull(f.Map));
        // "Dice" has no Latin letters: a face of pictures, not held to a language.
        Assert.Equal(["Saltmere Text", "Saltmere Titles"], BookCustomTheme.TextFamilies(measure.Faces).Order());
        var by = measure.Languages.ToDictionary(l => l.Language);
        Assert.Empty(by["en"].Lacking);
        Assert.Empty(by["ru"].Lacking);
        Assert.Equal(["Saltmere Text", "Saltmere Titles"], by["he"].Lacking.Order());
        // Named in its rules but brought by nothing: printed in whatever the server has.
        Assert.Equal(["Georgia", "Tasha Hand"], measure.Unbrought.Order());
    }

    [Fact]
    public void OnHomebrewerysLayer_TheLayersFacesAreMeasuredToo()
    {
        var measure = BookThemeCatalog.Measure(".page { color: red; }", true, new BookCssWeb(), Font);

        var he = measure.Languages.Single(l => l.Language == "he");
        Assert.Contains("BookInsanityRemake", he.Lacking);
        Assert.Contains("ScalySansRemake", he.Lacking);
        Assert.DoesNotContain("BookInsanityRemake", measure.Unbrought);
    }

    [Theory]
    [InlineData("Special Elite", "ru", BookFaceKind.Mono)]
    [InlineData("Special Elite", "he", BookFaceKind.Sans)]
    [InlineData("NodestoCapsCondensed", "ru", BookFaceKind.Display)]
    [InlineData("ScalySansRemake", "he", BookFaceKind.Sans)]
    [InlineData("BookInsanityRemake", "he", BookFaceKind.Serif)]
    [InlineData("MrEavesRemake", "ru", BookFaceKind.Sans)]
    public void AStandInOfTheSameKind_IsProposed(string family, string language, BookFaceKind kind)
    {
        var proposed = BookStandIns.Propose(family, language);

        Assert.NotNull(proposed);
        Assert.Equal(kind, proposed!.Kind);
        Assert.Equal(language, proposed.Language);
    }

    [Fact]
    public void TheStandIns_TakeEachSlotTheFamilyDeclares_ForTheScriptsRangeOnly_AndAfterTheThemesOwnFaces()
    {
        var css = Face("Saltmere Text", "im-fell-english/im-fell-english-latin-400-normal.woff2")
            + Face("Saltmere Text", "im-fell-english/im-fell-english-latin-400-italic.woff2", style: "italic")
            + Face("Saltmere Text", "im-fell-english/im-fell-english-latin-400-normal.woff2", weight: "bold")
            + Face("Saltmere Text", "im-fell-english/im-fell-english-latin-400-normal.woff2", weight: "100 900")
            + Face("Untouched", "im-fell-english/im-fell-english-latin-400-normal.woff2");
        var chosen = new Dictionary<string, Dictionary<string, string>> { ["he"] = new() { ["saltmere text"] = "he-david" } };

        var built = BookThemeCatalog.Build("custom:x", "X", css, false, ["en", "he"], chosen, new BookCssWeb(), "he", Font);
        var sheet = built.Theme.Css(BookLayout.For("he", "A4", 2));
        var standIns = BookCustomTheme.Faces(sheet[sheet.IndexOf("Stand-ins for this book", StringComparison.Ordinal)..]);

        Assert.Equal(4, standIns.Count);
        Assert.All(standIns, f => Assert.Equal("Saltmere Text", f.Family));
        Assert.Equal(["100 900", "400", "400", "bold"], standIns.Select(f => f.Weight).Order());
        Assert.All(standIns, f => Assert.Equal(BookFontCoverage.Ranges(BookThemes.Hebrew), f.Range));
        // The bold slot takes David Libre's bold; the others its regular (it has no italic: upright, as the bundled themes' Hebrew is).
        Assert.Contains(standIns, f => f.Weight == "bold" && f.Map!.SetEquals(BookFontCoverage.Read(Font("david-libre/david-libre-hebrew-700-normal.woff2")!)!));
        // Only after the theme's own faces, and nothing for a family with no stand-in chosen, nor for another language's book.
        Assert.True(sheet.IndexOf("Stand-ins for this book", StringComparison.Ordinal) > sheet.LastIndexOf("Untouched", StringComparison.Ordinal));
        var russian = BookThemeCatalog.Build("custom:x", "X", css, false, ["en", "he"], chosen, new BookCssWeb(), "ru", Font).Theme.Css(BookLayout.For("ru"));
        Assert.Empty(BookCustomTheme.Faces(russian[russian.IndexOf("Stand-ins for this book", StringComparison.Ordinal)..]));
        // The editor's check sees the stand-ins: with "Untouched" left out the sample's Hebrew is all drawn; with it (no stand-in chosen for it)
        // its letters are named, since wherever that family is used they print in a system font.
        static bool Hebrew(IReadOnlyList<(string Section, string Characters, int More)> u) => u.Any(x => x.Characters.Any(c => c is >= 'א' and <= 'ת'));
        var sample = new[] { ("s", BookThemeSample.For("he").Paragraph) };
        Assert.False(Hebrew(BookFontCoverage.Uncovered(sample, built.Faces!.Where(f => f.Family != "Untouched").ToList())));
        Assert.True(Hebrew(BookFontCoverage.Uncovered(sample, built.Faces!)));
    }

    [Fact]
    public void AWeightOrStyleThatCouldCloseTheRule_IsNotWrittenBack()
    {
        var faces = new[] { new BookThemeFace("A", "400;} body{display:none", "italic}", null, null) };

        var css = BookCustomTheme.StandInCss(faces, "he", new Dictionary<string, string> { ["A"] = "he-david" }, Font);

        Assert.DoesNotContain("display", css);
        Assert.Contains("font-weight:normal;font-style:normal;", css);
    }

    private async Task<(CampaignAccess Dm, string Id)> SeedAsync()
    {
        var id = "theme-" + Guid.NewGuid().ToString("N");
        await _campaigns.SaveAsync(new CampaignDocument
        {
            Id = id, Name = "Saltmere", SystemId = "srd5e", OwnerId = DmUser,
            Members = { new CampaignMember { UserId = DmUser, Role = CampaignRole.DM }, new CampaignMember { UserId = PlayerUser, Role = CampaignRole.Player } },
            ActivePacks = { "srd5e" }
        });
        return (new CampaignAccess(id, DmUser, CampaignRole.DM), id);
    }

    private static readonly Dictionary<string, Dictionary<string, string>> NoStandIns = new();

    private static readonly Dictionary<string, Dictionary<string, string>> HebrewStandIns = new() { ["he"] = new() { ["Saltmere Text"] = "he-david" } };

    [Fact]
    public async Task ATheme_IsKeptWithTheCampaign_ForItsGmOnly_AndAPlayerOrAnotherCampaignGetsNothing()
    {
        var (dm, id) = await SeedAsync();
        var (saved, theme) = await _store.SaveThemeAsync(dm, null, "  Tasha  ", Pasted, true, ["en", "he", "xx"], HebrewStandIns, ready: true, 0);

        Assert.Equal(BookDraftSave.Saved, saved);
        Assert.Equal("Tasha", theme!.Name);
        Assert.Equal(["en", "he"], theme.Languages);
        Assert.Equal(theme.Key, Assert.Single(await _store.ThemesAsync(dm)).Key);
        var player = new CampaignAccess(id, PlayerUser, CampaignRole.Player);
        Assert.Empty(await _store.ThemesAsync(player));
        Assert.Null(await _store.ThemeAsync(player, theme.Key));
        Assert.Equal(BookDraftSave.NotAllowed, (await _store.SaveThemeAsync(player, null, "Mine", ".a{}", true, ["en"], NoStandIns, true, 0)).Result);
        var (otherDm, _) = await SeedAsync();
        Assert.Null(await _store.ThemeAsync(otherDm, theme.Key));
        Assert.False(await _store.DeleteThemeAsync(otherDm, theme.Key));
    }

    [Fact]
    public async Task AStandInNotInTheCatalogue_OrForAnotherLanguage_IsNotStored()
    {
        var (dm, _) = await SeedAsync();
        var standIns = new Dictionary<string, Dictionary<string, string>>
        {
            ["he"] = new() { ["A"] = "he-david", ["B"] = "ru-forum", ["C"] = "../../etc" },
            ["xx"] = new() { ["A"] = "he-david" }
        };

        var (_, theme) = await _store.SaveThemeAsync(dm, null, "T", ".a{}", true, ["en"], standIns, true, 0);

        Assert.Equal(["he"], theme!.StandIns.Keys);
        Assert.Equal(new Dictionary<string, string> { ["A"] = "he-david" }, theme.StandIns["he"]);
    }

    [Fact]
    public async Task ADraftTakesACampaignTheme_OnlyWhenItIsThisCampaignsAndFinished_AndPrintsInPlainOnceItIsDeleted()
    {
        var (dm, _) = await SeedAsync();
        var draft = await _store.CreateAsync(dm, "dm", "he", [new BookDraftGenerated("cast", "Cast", "# Cast\n", "k")], "A4");
        var (_, unfinished) = await _store.SaveThemeAsync(dm, null, "Half", Pasted, false, ["en", "he"], HebrewStandIns, ready: false, 0);
        Assert.Equal(BookDraftSave.NotAllowed, (await _store.SaveSettingsAsync(dm, "dm", "he", "", "A4", BookCustomTheme.Prefix + unfinished!.Key, true, draft!.Document.Version)).Result);
        var (otherDm, _) = await SeedAsync();
        var (_, foreign) = await _store.SaveThemeAsync(otherDm, null, "Theirs", Pasted, false, ["en", "he"], HebrewStandIns, ready: true, 0);
        Assert.Equal(BookDraftSave.NotAllowed, (await _store.SaveSettingsAsync(dm, "dm", "he", "", "A4", BookCustomTheme.Prefix + foreign!.Key, true, draft.Document.Version)).Result);
        Assert.Equal(BookDraftSave.NotAllowed, (await _store.SaveSettingsAsync(dm, "dm", "he", "", "A4", BookCustomTheme.Prefix + "../x", true, draft.Document.Version)).Result);

        var (_, ready) = await _store.SaveThemeAsync(dm, unfinished.Key, "Half", Pasted, false, ["en", "he"], HebrewStandIns, ready: true, unfinished.Version);
        Assert.Equal(BookDraftSave.Saved, (await _store.SaveSettingsAsync(dm, "dm", "he", "", "A4", BookCustomTheme.Prefix + ready!.Key, false, draft.Document.Version)).Result);

        var catalog = new BookThemeCatalog(_store);
        var resolved = await catalog.ResolveAsync(dm, BookCustomTheme.Prefix + ready.Key, "he", Font);
        Assert.Equal("Half", resolved.Theme.Name);
        Assert.True(resolved.Theme.Supports("he"));
        Assert.False(resolved.Homebrewery);
        Assert.Contains("Stand-ins for this book", resolved.Theme.Css(BookLayout.For("he")));

        Assert.True(await _store.DeleteThemeAsync(dm, ready.Key));
        Assert.Equal(BookDraftThemes.Plain, (await catalog.ResolveAsync(dm, BookCustomTheme.Prefix + ready.Key, "he", Font)).Theme);
    }

    [Fact]
    public async Task ASaveFromAnOlderCopy_IsRefused_AndACampaignKeepsTwentyThemesAtMost()
    {
        var (dm, _) = await SeedAsync();
        var (_, theme) = await _store.SaveThemeAsync(dm, null, "T", ".a{}", true, ["en"], NoStandIns, false, 0);
        Assert.Equal(BookDraftSave.Saved, (await _store.SaveThemeAsync(dm, theme!.Key, "T2", ".a{}", true, ["en"], NoStandIns, false, theme.Version)).Result);
        Assert.Equal(BookDraftSave.Conflict, (await _store.SaveThemeAsync(dm, theme.Key, "T3", ".a{}", true, ["en"], NoStandIns, false, theme.Version)).Result);

        for (var i = 1; i < BookThemeDocument.MaxPerCampaign; i++) await _store.SaveThemeAsync(dm, null, "T" + i, ".a{}", true, ["en"], NoStandIns, false, 0);
        Assert.Equal(BookThemeDocument.MaxPerCampaign, (await _store.ThemesAsync(dm)).Count);
        Assert.Equal(BookDraftSave.TooLarge, (await _store.SaveThemeAsync(dm, null, "One more", ".a{}", true, ["en"], NoStandIns, false, 0)).Result);
    }

    [Fact]
    public async Task AThemeAndItsCopies_GoWithTheCampaign()
    {
        var (dm, id) = await SeedAsync();
        var (_, theme) = await _store.SaveThemeAsync(dm, null, "T", "@import url(https://fonts.example/a.css);", true, ["en"], NoStandIns, true, 0);
        var copy = new BookCssWebFile("https://fonts.example/a.css", BookCssWebKind.Sheet, "text/css", "body{color:red}"u8.ToArray());
        Assert.Equal(BookDraftSave.Saved, await _store.SaveThemeWebAsync(dm, theme!.Key, [copy], new HashSet<string> { copy.Url }));
        Assert.Single((await _store.ThemeWebAsync(dm, theme.Key)).Files);

        Assert.Equal(CampaignDeleteResult.Deleted, await _campaigns.DeleteAsync(id, DmUser));

        // Read straight from the database: the GM is no longer anyone's GM here, so the store would say nothing either way.
        using var session = _db.Store.OpenAsyncSession();
        Assert.Empty(await session.Advanced.LoadStartingWithAsync<object>(BookThemeDocument.PrefixFor(id)));
    }
}
