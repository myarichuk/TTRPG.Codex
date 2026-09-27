using Microsoft.AspNetCore.Authorization;

namespace Codex.Web;

/// <summary>
/// The auth fallback (see Program.cs): everything needs a signed-in user except the Blazor
/// framework endpoints under <c>/_blazor</c>, which carry no authorize metadata of their own.
/// Without the carve-out the fallback 302s <c>/_blazor/initializers</c> to /login for anonymous
/// visitors, and <c>blazor.web.js</c> JSON-parses the login HTML - an uncaught SyntaxError on
/// every anonymous page load. Anonymous circuits can still only reach
/// <c>[AllowAnonymous]</c> pages: the endpoint fallback and AuthorizeRouteView guard the rest.
/// </summary>
public sealed class AuthenticatedOrBlazorFramework : IAuthorizationRequirement;

/// <summary>Handler for <see cref="AuthenticatedOrBlazorFramework"/>.</summary>
public sealed class BlazorFrameworkEndpointHandler : AuthorizationHandler<AuthenticatedOrBlazorFramework>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AuthenticatedOrBlazorFramework requirement)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.Resource is HttpContext http &&
            http.Request.Path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
