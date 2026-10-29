using Microsoft.Playwright;

namespace Codex.Tests.E2E;

/// <summary>Steps every end-to-end scenario repeats: sign in, create a campaign, join by invite.</summary>
public static class E2EHelpers
{
    public static async Task LoginAsync(IPage page, string username, string password)
    {
        await page.GotoAsync("/login");
        await page.Locator("#username").FillAsync(username);
        await page.Locator("#password").FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/login"));
    }

    /// <summary>Creates a campaign through the New campaign dialog and returns its id (from the URL).</summary>
    public static async Task<string> CreateCampaignAsync(IPage dm, string name, string? systemId = null)
    {
        await dm.GotoAsync("/");
        await WaitForInteractiveAsync(dm);
        await dm.GetByTestId("new-campaign").ClickAsync();
        await dm.GetByTestId("new-campaign-name").FillAsync(name);
        if (systemId != null)
        {
            await dm.GetByTestId("new-campaign-system").SelectOptionAsync(systemId);
        }

        await dm.GetByTestId("new-campaign-create").ClickAsync();
        await dm.WaitForURLAsync(url => url.Contains("/campaigns/") && !url.EndsWith("/campaigns/"));
        await dm.GetByTestId("campaign-title").WaitForAsync();
        return new Uri(dm.Url).Segments[2].TrimEnd('/');
    }

    public static async Task<string> ReadInviteLinkAsync(IPage dm, string campaignId)
    {
        await dm.GotoAsync($"/campaigns/{campaignId}");
        await WaitForInteractiveAsync(dm);
        return await dm.GetByTestId("invite-link").InputValueAsync();
    }

    public static async Task GotoInteractiveAsync(IPage page, string url)
    {
        await page.GotoAsync(url);
        await WaitForInteractiveAsync(page);
    }

    public static async Task JoinAsync(IPage player, string inviteUrl)
    {
        await player.GotoAsync(inviteUrl);
        await player.GetByText("You're in!").WaitForAsync();
        await player.GetByRole(AriaRole.Link, new() { Name = "Go to campaign" }).ClickAsync();
        await player.GetByTestId("campaign-title").WaitForAsync();
        await WaitForInteractiveAsync(player);
    }

    public static async Task GoToSectionAsync(IPage page, string campaignId, string section)
    {
        await page.GotoAsync($"/campaigns/{campaignId}/{section}");
        await WaitForInteractiveAsync(page);
    }

    /// <summary>A full page load prerenders statically first; input typed before the circuit takes
    /// over is lost. MainLayout renders a marker only from the interactive circuit.</summary>
    /// <summary>Fill a field and move focus on, so Blazor's change handler runs (number inputs commit on change, not on every keystroke).</summary>
    public static async Task CommitAsync(this ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.PressAsync("Tab");
    }

    /// <summary>Presses the sheet's Save and waits for "Saved.", which shows once the actor is stored. The unsaved-changes
    /// count clears before that (the edits are applied to the page's copy first), so waiting for it races the write. Use on a
    /// page load's first save: a "Saved." left from an earlier one would pass at once.</summary>
    public static async Task SaveSheetAsync(IPage page)
    {
        await page.GetByTestId("sheet-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("status-message")).ToContainTextAsync("Saved.");
    }

    public static Task WaitForInteractiveAsync(IPage page) =>
        page.Locator("[data-interactive]").WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 });

    /// <summary>Form controls and buttons on the page that have no accessible name - the single most
    /// common screen-reader failure. Returns a description of each offender, so the assertion message
    /// says what to fix.</summary>
    public static Task<string[]> UnlabelledControlsAsync(IPage page) =>
        page.EvaluateAsync<string[]>("""
            () => {
              const offenders = [];
              const hasName = (el) => {
                if (el.getAttribute('aria-label') || el.getAttribute('aria-labelledby') || el.getAttribute('title')) return true;
                if (el.id && document.querySelector(`label[for="${CSS.escape(el.id)}"]`)) return true;
                if (el.closest('label')) return true;
                if (el.tagName === 'BUTTON' || el.tagName === 'A') {
                  if ((el.textContent || '').trim().length > 0) return true;
                  if (el.querySelector('[aria-label], img[alt]')) return true;
                }
                if (el.tagName === 'INPUT' && ['hidden', 'submit', 'button'].includes(el.type)) return true;
                return false;
              };
              document.querySelectorAll('input, select, textarea, button, a[href]').forEach(el => {
                if (el.closest('[hidden]') || el.closest('dialog:not([open])')) return;
                if (!hasName(el)) offenders.push(el.outerHTML.slice(0, 160));
              });
              return offenders;
            }
            """);
}
