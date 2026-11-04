using System.Globalization;
using System.Security.Claims;
using Codex.Core.Book;
using Codex.Persistence;

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
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var access = userId == null ? null : await resolver.ResolveAsync(campaignId, userId);
            if (access is not { IsDm: true } || !Enum.TryParse<BookAudience>(audience, ignoreCase: true, out var edition))
            {
                return Results.NotFound();
            }

            var chosen = theme == null ? BookThemes.Plain : BookThemes.Find(theme);
            if (chosen == null)
            {
                return Results.BadRequest("Unknown theme.");
            }

            var source = await reader.ReadAsync(access, edition);
            if (source == null)
            {
                return Results.NotFound();
            }

            var language = string.IsNullOrWhiteSpace(lang) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : lang;
            var fonts = Path.Combine(env.WebRootPath, "fonts");
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

            if (built.Html == null)
            {
                // The language gate: a theme that cannot print this language says so, instead of printing it in the wrong fonts.
                return Results.Problem(built.Refused, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            response.Headers.CacheControl = "no-store";
            response.Headers["Content-Security-Policy"] = BookHtml.ContentSecurityPolicy;
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            return Results.Content(built.Html, "text/html; charset=utf-8");
        });
    }
}
