using Codex.Localization;
using Microsoft.AspNetCore.Localization;

namespace Codex.Web;

/// <summary>The one place the interface-language cookie is written, so every sign-in path agrees on it.</summary>
public static class CultureCookie
{
    public static void Write(HttpContext context, string? culture)
    {
        var tag = SupportedCultures.Normalize(culture);
        context.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(tag)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                HttpOnly = true,
            });
    }
}
