using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>
/// Shared sign-in flow for the Playwright suites. The circuit-connect re-render can wipe filled
/// login inputs when it lands between fill and submit (empty required fields then
/// validation-block the post - a silent no-op with no request), so this refills + resubmits
/// until the navigation proves a submit went through. A repeat submit after a slow first post
/// is harmless (sign-in is idempotent).
/// </summary>
public static class LoginHelper
{
    public static async Task LoginAsync(IPage page, string username, string password)
    {
        await page.GotoAsync("/login");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!page.Url.Contains("/login"))
            {
                return;
            }

            await page.Locator("#username").FillAsync(username);
            await page.Locator("#password").FillAsync(password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Enter Codex" }).ClickAsync();
            try
            {
                await page.WaitForURLAsync(url => !url.Contains("/login"), new() { Timeout = 10000 });
                return;
            }
            catch (TimeoutException) when (attempt < 2)
            {
                if (!page.Url.Contains("/login"))
                {
                    return; // Landed between the timeout and this check.
                }

                var alert = await page.Locator(".alert").First.InnerTextAsync(new() { Timeout = 2000 })
                    .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result.Trim() : "none")
                    .ConfigureAwait(false);
                if (alert != "none")
                {
                    throw new TimeoutException($"Login rejected for {username}: {alert}.");
                }
            }
        }
    }
}
