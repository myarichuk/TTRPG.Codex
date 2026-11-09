using System.Globalization;
using System.Security.Claims;
using Codex.Core.Book;
using Codex.Localization;
using Codex.Persistence;
using Codex.Web.Book;

namespace Codex.Web;

/// <summary>
/// The page a campaign book is printed from, and previewed in: one self-contained HTML document (fonts and art inline, no script,
/// a policy that allows nothing to be fetched), so the live preview shows what the PDF engine prints. Only the campaign's DM may ask,
/// for either edition; the party edition is read as a stranger would read the campaign (<see cref="BookSourceReader"/>). The page
/// holds the DM's prep in the DM edition, so it is never cached and never framed by another site.
/// </summary>
public static class BookEndpoints
{
    public static void MapBooks(this WebApplication app)
    {
        app.MapGet("/campaigns/{campaignId}/book/{audience}", async (string campaignId, string audience, string? lang, string? theme, string? size, string? cols, string? pics, string? ai,
            ClaimsPrincipal user, HttpResponse response, ICampaignAccessResolver resolver, BookSourceReader reader, IBookBuildRepository builds, BookArtReader artReader, IBookArtRepository bookArt, IWebHostEnvironment env, CancellationToken cancel) =>
        {
            var made = await BuildAsync(campaignId, audience, lang, theme, size, cols, pics, ai, user, resolver, reader, builds, artReader, bookArt, env, cancel: cancel);
            if (made.Failure != null)
            {
                return made.Failure;
            }

            Protect(response);
            return Results.Content(made.Html, "text/html; charset=utf-8");
        });

        // The same page, printed: what the live preview shows is what comes out.
        app.MapGet("/campaigns/{campaignId}/book/{audience}/pdf", async (string campaignId, string audience, string? lang, string? theme, string? size, string? cols, string? pics, string? ai,
            ClaimsPrincipal user, HttpResponse response, ICampaignAccessResolver resolver, BookSourceReader reader, IBookBuildRepository builds, BookArtReader artReader, IBookArtRepository bookArt, IWebHostEnvironment env, BookPdf pdf, CancellationToken cancel) =>
        {
            // A place in the printing line is taken before the campaign is read and the page built, so a request that would be
            // turned away costs nothing, and nobody holds more than one place.
            BookPdfSlot? slot = null;
            try
            {
                var made = await BuildAsync(campaignId, audience, lang, theme, size, cols, pics, ai, user, resolver, reader, builds, artReader, bookArt, env, cancel: cancel, forPrint: true, admit: userId =>
                {
                    slot = pdf.TryReserve(userId);
                    return slot == null ? Busy(response, new BookPdfBusyException()) : null;
                });
                if (made.Failure != null)
                {
                    return made.Failure;
                }

                var printed = await pdf.PrintAsync(slot!, made.Job!, cancel);
                Protect(response);
                return Results.File(printed.Pdf, "application/pdf", FileName(made.Title, audience));
            }
            catch (BookPdfTimeoutException timeout)
            {
                return Results.Problem(timeout.Message, statusCode: StatusCodes.Status504GatewayTimeout, extensions: Code("timeout"));
            }
            catch (BookPdfUnavailableException unavailable)
            {
                return Results.Problem(unavailable.Message, statusCode: StatusCodes.Status503ServiceUnavailable, extensions: Code(unavailable.Code));
            }
            finally
            {
                slot?.Dispose();
            }
        });

        // The editable book (docs/BOOK_EXPORT.md 41). Its preview draws in the GM's browser; these give it the edition's pictures and print it.
        app.MapGet("/campaigns/{campaignId}/book/{audience}/draft/{lang}/art", async (string campaignId, string audience, string lang, ClaimsPrincipal user, HttpResponse response,
            ICampaignAccessResolver resolver, BookDrafts drafts, CancellationToken cancel) =>
        {
            if (await GmAsync(campaignId, user, resolver) is not { } access || BookDrafts.Audience(audience) == null) return Results.NotFound();
            response.Headers.CacheControl = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Json(await drafts.ArtAsync(access, audience, BookArtQuality.Preview, cancel));
        });

        // The draft's Markdown as one file, for Homebrewery or a text editor (docs/BOOK_EXPORT.md 41.24). The party edition's leak check and
        // the GM edition's warning are the editor's, before it asks; this is the GM's own text either way.
        // ?player=<user id>: that player's own copy of the party draft (45), named by their characters.
        app.MapGet("/campaigns/{campaignId}/book/{audience}/draft/{lang}/md", async (string campaignId, string audience, string lang, string? player, ClaimsPrincipal user,
            HttpResponse response, ICampaignAccessResolver resolver, BookDrafts drafts) =>
        {
            if (await GmAsync(campaignId, user, resolver) is not { } access || BookDrafts.Audience(audience) == null) return Results.NotFound();
            if (await drafts.MarkdownAsync(access, audience, lang, Player(player)) is not { } made) return Results.NotFound();
            Protect(response);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(made.Markdown), "text/markdown; charset=utf-8", FileName(made.Title, audience, "md", made.Player));
        });

        app.MapGet("/campaigns/{campaignId}/book/{audience}/draft/{lang}/pdf", async (string campaignId, string audience, string lang, string? player, ClaimsPrincipal user,
            HttpResponse response, ICampaignAccessResolver resolver, BookDrafts drafts, IWebHostEnvironment env, BookPdf pdf, ILoggerFactory logs, CancellationToken cancel) =>
        {
            if (await GmAsync(campaignId, user, resolver) is not { } access || BookDrafts.Audience(audience) == null) return Results.NotFound();
            // The place in the line first, as for the book: a request that would be turned away costs nothing.
            using var slot = pdf.TryReserve(access.UserId);
            if (slot == null) return Busy(response, new BookPdfBusyException());
            try
            {
                var fonts = Path.GetFullPath(Path.Combine(env.WebRootPath, "fonts"));
                var (page, refused) = await drafts.PageAsync(access, audience, lang, BookArtQuality.Print, file => Font(fonts, file), cancel,
                    player: Player(player));
                if (refused != null) return Results.Problem(refused, statusCode: StatusCodes.Status422UnprocessableEntity);
                if (page == null) return Results.NotFound();
                var printed = await pdf.PrintAsync(slot, new BookPrintJob(page.Shell, [], _ => page.Shell)
                {
                    Layout = new BookPrintLayout(RenderScript(env), page.Markdown, page.Art) { Chapters = page.Layout }
                }, cancel);
                if (printed.Overflow.Count > 0)
                {
                    // What printed cut off, as this browser laid it out (the GM's may break lines differently): the editor names each page's
                    // chapter from this. Page numbers and section ids only, which are plain ASCII (BookDraftDocument.IsSection).
                    logs.CreateLogger("Codex.Book").LogWarning("A printed book draft has {Count} pages whose content does not fit", printed.Overflow.Count);
                    response.Headers["X-Book-Overflow"] = OverflowHeader(printed.Overflow);
                }

                Protect(response);
                return Results.File(printed.Pdf, "application/pdf", FileName(page.Title, audience, player: page.Player));
            }
            catch (BookPdfTimeoutException timeout)
            {
                return Results.Problem(timeout.Message, statusCode: StatusCodes.Status504GatewayTimeout, extensions: Code("timeout"));
            }
            catch (BookPdfUnavailableException unavailable)
            {
                return Results.Problem(unavailable.Message, statusCode: StatusCodes.Status503ServiceUnavailable, extensions: Code(unavailable.Code));
            }
        });
    }

    // An empty ?player= is no player: the party's own book. Anything else is checked by the reader (a player of the campaign now, with a character).
    private static string? Player(string? player) => string.IsNullOrWhiteSpace(player) ? null : player;

    /// <summary>"7:cast,9:lore": each page that prints cut off, with its chapter when it has one.</summary>
    public static string OverflowHeader(IEnumerable<BookOverflow> overflow) =>
        string.Join(",", overflow.Select(o => o.Section is { } section && BookDraftDocument.IsSection(section) ? $"{o.Page}:{section}" : o.Page.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>The header read back: each page and its section (null when it has none). Anything malformed is skipped.</summary>
    public static IReadOnlyList<(int Page, string? Section)> ReadOverflowHeader(string? header) =>
        [.. (header ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split(':', 2))
            .Where(p => int.TryParse(p[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0)
            .Select(p => (int.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), p.Length > 1 && BookDraftDocument.IsSection(p[1]) ? p[1] : null))];

    private static async Task<CampaignAccess?> GmAsync(string campaignId, ClaimsPrincipal user, ICampaignAccessResolver resolver) =>
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } userId && await resolver.ResolveAsync(campaignId, userId) is { IsDm: true } access ? access : null;

    private static byte[]? Font(string fonts, string file)
    {
        var path = Path.GetFullPath(Path.Combine(fonts, file));
        return path.StartsWith(fonts, StringComparison.Ordinal) && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private static string? _renderScript;

    /// <summary>The bundled renderer (<c>wwwroot/lib/book/book-render.js</c>), the only script the print browser ever runs.</summary>
    public static string RenderScript(IWebHostEnvironment env) =>
        _renderScript ??= File.ReadAllText(Path.Combine(env.WebRootPath, "lib", "book", "book-render.js"));

    private static IResult Busy(HttpResponse response, BookPdfBusyException busy)
    {
        response.Headers.RetryAfter = "15";
        return Results.Problem(busy.Message, statusCode: StatusCodes.Status429TooManyRequests, extensions: Code("busy"));
    }

    private static Dictionary<string, object?> Code(string code) => new() { ["code"] = code };

    private static void Protect(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers["Content-Security-Policy"] = BookHtml.ContentSecurityPolicy;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    }

    /// <summary>"Campaign name (dm).pdf" with nothing in it a file system, a header or a reader's eye would be fooled by: a fixed
    /// set of characters Windows and Unix both refuse, no control, format (bidi override, zero width) or separator characters, and a cut
    /// on a whole character, not in the middle of a surrogate pair. The framework encodes what remains.</summary>
    /// <para>One player's own book (<paramref name="player"/>, their characters' names) is named by them in place of the edition:
    /// "Campaign name (Mira, Tor).pdf".</para>
    public static string FileName(string title, string audience, string extension = "pdf", string? player = null)
    {
        var clean = Kept(title, 80);
        var edition = player != null && Kept(player, 60) is { Length: > 0 } who ? who
            : audience.Equals("party", StringComparison.OrdinalIgnoreCase) ? "party" : "dm";
        return (clean.Length == 0 ? "campaign" : clean) + " (" + edition + ")." + extension;
    }

    private static string Kept(string title, int max)
    {
        var kept = new System.Text.StringBuilder();
        foreach (var c in title)
        {
            var category = char.GetUnicodeCategory(c);
            if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or ';' or '%'
                || category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            kept.Append(c);
        }

        var clean = kept.ToString().Trim();
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(clean);
        var cut = new System.Text.StringBuilder();
        for (var count = 0; count < max && elements.MoveNext(); count++)
        {
            cut.Append(elements.GetTextElement());
        }

        return cut.ToString().Trim();
    }

    private sealed record Made(string? Html, string Title, IResult? Failure, BookPrintJob? Job = null);

    private static async Task<Made> BuildAsync(string campaignId, string audience, string? lang, string? theme, string? size, string? cols, string? pics, string? ai, ClaimsPrincipal user,
        ICampaignAccessResolver resolver, BookSourceReader reader, IBookBuildRepository builds, BookArtReader artReader, IBookArtRepository bookArt, IWebHostEnvironment env, bool forPrint = false, Func<string, IResult?>? admit = null, CancellationToken cancel = default)
    {
        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var access = userId == null ? null : await resolver.ResolveAsync(campaignId, userId);
        // Exactly "dm" or "party": Enum.TryParse would take "2" as an undefined edition that reads as a stranger but is not cleaned.
        BookAudience? asked = audience.ToLowerInvariant() switch { "dm" => BookAudience.Dm, "party" => BookAudience.Party, _ => null };
        if (access is not { IsDm: true } || asked is not { } edition)
        {
            return new Made(null, "", Results.NotFound());
        }

        var chosen = theme == null ? BookThemes.Plain : BookThemes.Find(theme);
        if (chosen == null)
        {
            return new Made(null, "", Results.BadRequest("Unknown theme."));
        }

        if (admit?.Invoke(userId!) is { } refused)
        {
            return new Made(null, "", refused);
        }

        var source = await reader.ReadAsync(access, edition);
        if (source == null)
        {
            return new Made(null, "", Results.NotFound());
        }

        var language = string.IsNullOrWhiteSpace(lang) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : lang;
        var fonts = Path.GetFullPath(Path.Combine(env.WebRootPath, "fonts"));
        var document = BookComposer.Compose(source, language, out var entries);
        var compiled = document;
        // The DM's stored AI rewrite stands in for a chapter only when asked for ("ai=1") and only for a chapter built from exactly what
        // it is made from now: a chapter that changed, or was hidden from this edition since, prints from the campaign as it is.
        if (ai == "1")
        {
            document = BookComposer.Overlay(document, source, await builds.GetAsync(access, BookComposer.Edition(edition)));
        }

        // The pictures the book already has, read for this edition only (the art reader asks the media store for nothing the source does not
        // hold), placed after the chapters are final. "pics=0" leaves them out: a book with many pictures is a heavy page to preview.
        // The GM's own book pictures go in the slots this edition's source makes, and only those made for this edition (BookSlots.Choose).
        var slots = BookSlots.For(source, compiled, entries);
        var art = pics == "0" ? BookArt.None : await artReader.ReadAsync(source, forPrint ? BookArtQuality.Print : BookArtQuality.Preview, cols == "2" ? 2 : 1, cancel,
            BookSlots.Choose(slots, await bookArt.GetAsync(access)), access.UserId);
        document = BookComposer.PlaceArt(document, compiled, entries, art, out _, slots);

        BookHtmlResult Build(IReadOnlyDictionary<string, int>? numbers) => BookHtml.Build(document, chosen, new BookHtmlOptions
        {
            BoxSecrets = edition == BookAudience.Dm,
            Mention = BookCompiler.Mentions(source),
            Image = art.DataUri,
            PageSize = size ?? "A4",
            Columns = cols == "2" ? 2 : 1,
            PageNumbers = numbers,
            Font = file =>
            {
                var path = Path.GetFullPath(Path.Combine(fonts, file));
                return path.StartsWith(fonts, StringComparison.Ordinal) && File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
        });

        // The page for a reader has no page numbers (it has no pages); the one that is printed reserves their column, is printed, and
        // is built again with the pages its headings landed on.
        var built = Build(forPrint ? new Dictionary<string, int>() : null);

        // The language gate: a theme that cannot print this language says so, instead of printing it in the wrong fonts.
        return built.Html == null
            ? new Made(null, "", Results.Problem(built.Refused, statusCode: StatusCodes.Status422UnprocessableEntity))
            : new Made(built.Html, source.Campaign.Name, null, forPrint ? new BookPrintJob(built.Html, built.Headings, numbers => Build(numbers).Html!) : null);
    }
}
