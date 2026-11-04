using System.Globalization;
using System.Security.Claims;
using Codex.Core.Book;
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
        app.MapGet("/campaigns/{campaignId}/book/{audience}", async (string campaignId, string audience, string? lang, string? theme, string? size,
            ClaimsPrincipal user, HttpResponse response, ICampaignAccessResolver resolver, BookSourceReader reader, IWebHostEnvironment env) =>
        {
            var made = await BuildAsync(campaignId, audience, lang, theme, size, user, resolver, reader, env);
            if (made.Failure != null)
            {
                return made.Failure;
            }

            Protect(response);
            return Results.Content(made.Html, "text/html; charset=utf-8");
        });

        // The same page, printed: what the live preview shows is what comes out.
        app.MapGet("/campaigns/{campaignId}/book/{audience}/pdf", async (string campaignId, string audience, string? lang, string? theme, string? size,
            ClaimsPrincipal user, HttpResponse response, ICampaignAccessResolver resolver, BookSourceReader reader, IWebHostEnvironment env, BookPdf pdf, CancellationToken cancel) =>
        {
            var made = await BuildAsync(campaignId, audience, lang, theme, size, user, resolver, reader, env);
            if (made.Failure != null)
            {
                return made.Failure;
            }

            try
            {
                var printed = await pdf.PrintAsync(made.Html!, cancel);
                Protect(response);
                return Results.File(printed.Pdf, "application/pdf", FileName(made.Title, audience));
            }
            catch (BookPdfBusyException busy)
            {
                response.Headers.RetryAfter = "15";
                return Results.Problem(busy.Message, statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (BookPdfTimeoutException timeout)
            {
                return Results.Problem(timeout.Message, statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (BookPdfUnavailableException unavailable)
            {
                return Results.Problem(unavailable.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    private static void Protect(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers["Content-Security-Policy"] = BookHtml.ContentSecurityPolicy;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    }

    /// <summary>"Campaign name (dm).pdf" with nothing in it a file system or a header would object to; the framework encodes the rest.</summary>
    internal static string FileName(string title, string audience)
    {
        var clean = new string(title.Where(c => !char.IsControl(c) && !Path.GetInvalidFileNameChars().Contains(c) && c is not ('"' or '\\' or ';')).ToArray()).Trim();
        if (clean.Length > 80)
        {
            clean = clean[..80].Trim();
        }

        return (clean.Length == 0 ? "campaign" : clean) + " (" + audience.ToLowerInvariant() + ").pdf";
    }

    private sealed record Made(string? Html, string Title, IResult? Failure);

    private static async Task<Made> BuildAsync(string campaignId, string audience, string? lang, string? theme, string? size, ClaimsPrincipal user,
        ICampaignAccessResolver resolver, BookSourceReader reader, IWebHostEnvironment env)
    {
        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var access = userId == null ? null : await resolver.ResolveAsync(campaignId, userId);
        if (access is not { IsDm: true } || !Enum.TryParse<BookAudience>(audience, ignoreCase: true, out var edition))
        {
            return new Made(null, "", Results.NotFound());
        }

        var chosen = theme == null ? BookThemes.Plain : BookThemes.Find(theme);
        if (chosen == null)
        {
            return new Made(null, "", Results.BadRequest("Unknown theme."));
        }

        var source = await reader.ReadAsync(access, edition);
        if (source == null)
        {
            return new Made(null, "", Results.NotFound());
        }

        var language = string.IsNullOrWhiteSpace(lang) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : lang;
        var fonts = Path.GetFullPath(Path.Combine(env.WebRootPath, "fonts"));
        var built = BookHtml.Build(BookCompiler.Compile(source, language), chosen, new BookHtmlOptions
        {
            BoxSecrets = edition == BookAudience.Dm,
            Mention = BookCompiler.Mentions(source),
            PageSize = size ?? "A4",
            Font = file =>
            {
                var path = Path.GetFullPath(Path.Combine(fonts, file));
                return path.StartsWith(fonts, StringComparison.Ordinal) && File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
        });

        // The language gate: a theme that cannot print this language says so, instead of printing it in the wrong fonts.
        return built.Html == null
            ? new Made(null, "", Results.Problem(built.Refused, statusCode: StatusCodes.Status422UnprocessableEntity))
            : new Made(built.Html, source.Campaign.Name, null);
    }
}
