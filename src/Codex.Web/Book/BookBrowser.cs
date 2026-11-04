namespace Codex.Web.Book;

/// <summary>
/// Finds a Chromium-family browser to print with. The app never downloads one: a self-hosted install that already has Chrome, Edge or
/// Chromium prints with it, and one that has none gets a message saying where to point the app. A path the user configured is
/// the only candidate when it is set, so a typo is reported instead of silently printing with some other browser.
/// </summary>
public static class BookBrowser
{
    /// <summary>The first release whose print engine does everything the themes lean on (page margin boxes, tagged output).</summary>
    public const int MinimumMajorVersion = 131;

    public const string EnvironmentVariable = "CODEX_BOOK_BROWSER";

    /// <summary>Where to look, best first. <paramref name="configured"/> wins alone when set.</summary>
    public static IReadOnlyList<string> Candidates(string? configured, Func<string, string?> environment, Func<string, IEnumerable<string>> expand, bool windows, bool mac)
    {
        var explicitPath = !string.IsNullOrWhiteSpace(configured) ? configured : environment(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return [explicitPath];
        }

        var found = new List<string>();
        if (windows)
        {
            foreach (var root in new[] { environment("ProgramFiles"), environment("ProgramFiles(x86)"), environment("LocalAppData") })
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                found.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                found.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                found.Add(Path.Combine(root, "Chromium", "Application", "chrome.exe"));
            }
        }
        else if (mac)
        {
            found.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            found.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            found.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
        }
        else
        {
            // A browser Playwright already fetched (the e2e suite's, a CI image's) is as good as any other.
            var playwright = environment("PLAYWRIGHT_BROWSERS_PATH");
            foreach (var root in new[] { playwright, "/opt/pw-browsers", Path.Combine(environment("HOME") ?? "/root", ".cache", "ms-playwright") })
            {
                if (!string.IsNullOrEmpty(root) && root != "0")
                {
                    found.AddRange(expand(Path.Combine(root, "chromium-*", "chrome-linux*", "chrome")).OrderDescending(StringComparer.Ordinal));
                }
            }

            foreach (var directory in (environment("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var name in new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "microsoft-edge-stable" })
                {
                    found.Add(Path.Combine(directory, name));
                }
            }
        }

        return found;
    }

    /// <summary>The first candidate that exists, or null with what was looked at.</summary>
    public static string? Find(string? configured, out IReadOnlyList<string> looked)
    {
        looked = Candidates(configured, Environment.GetEnvironmentVariable, Expand, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());
        return looked.FirstOrDefault(File.Exists);
    }

    /// <summary>"HeadlessChrome/131.0.6778.33" and "Chrome/153.0.1.2" to 131 and 153; 0 when it cannot be read.</summary>
    public static int MajorVersion(string? product)
    {
        var slash = product?.IndexOf('/') ?? -1;
        if (slash < 0)
        {
            return 0;
        }

        var digits = new string(product!.Skip(slash + 1).TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var major) ? major : 0;
    }

    private static IEnumerable<string> Expand(string pattern)
    {
        var directory = Path.GetDirectoryName(pattern);
        // Only the last two segments may hold a wildcard here (chromium-*/chrome-linux*/chrome): walk them by hand.
        var parts = pattern.Split(Path.DirectorySeparatorChar);
        var star = Array.FindIndex(parts, p => p.Contains('*'));
        if (star < 0 || directory == null)
        {
            return File.Exists(pattern) ? [pattern] : [];
        }

        var results = new List<string>();
        var root = string.Join(Path.DirectorySeparatorChar, parts.Take(star));
        if (root.Length == 0 || !Directory.Exists(root))
        {
            return results;
        }

        foreach (var first in Directory.EnumerateDirectories(root, parts[star]))
        {
            var rest = parts.Skip(star + 1).ToArray();
            if (rest.Length == 2 && rest[0].Contains('*'))
            {
                foreach (var second in Directory.EnumerateDirectories(first, rest[0]))
                {
                    results.Add(Path.Combine(second, rest[1]));
                }
            }
            else
            {
                results.Add(Path.Combine([first, .. rest]));
            }
        }

        return results;
    }
}
