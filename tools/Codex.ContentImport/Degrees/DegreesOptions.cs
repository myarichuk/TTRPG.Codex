namespace Codex.ContentImport.Degrees;

/// <summary>The arguments after <c>degrees</c>. Paths come only from the command line: no book file name is kept anywhere in the repository.</summary>
public sealed record DegreesOptions(string Action, string Book, string Pdf, string Work, string? Out = null, bool Check = false)
{
    public const string Usage = """
          degrees [run|fingerprint|notice|denylist] --book <pc|mc> --pdf <path> [--work <dir>] [--out <path>] [--check]

          run          fingerprint, then layout, spine and blocks stages, then (for the book's take-list rows) the ranges, entries and redaction
                       stages, then the notice check (the default)
          fingerprint  compare the page count and the bookmark counts with the committed ones
          notice       compare the credit line on the bookmarked notice page with the committed one; prints no book text
          denylist     hash the capitalized words of everything the take-list leaves out (never printed)
          --book       pc (the player book) or mc (the monster book)
          --pdf        the book file; it must be outside the git work tree
          --work       directory for layout.json, spine.json, blocks.json, entries.json, redacted.json, import-report.yaml
                       (outside the git work tree; default: <temp>/codex-degrees/<book>)
          --out        run: the pack folder to write for the approved rows (it is wiped first, but its manifest.yaml is kept);
                       denylist: the hash file to merge into (default: denylist.sha256 in the work folder)
          --check      run, with --out: regenerate into a temp folder and fail on any byte difference from the pack folder; writes nothing there
        """;

    public static readonly string[] Books = { "pc", "mc" };

    public static string DefaultWork(string book) => Path.Combine(Path.GetTempPath(), "codex-degrees", book);

    public static DegreesOptions? Parse(string[] args)
    {
        var action = "run";
        var start = 0;
        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            action = args[0];
            start = 1;
        }

        if (action is not ("run" or "fingerprint" or "notice" or "denylist"))
        {
            return null;
        }

        string? book = null, pdf = null, work = null, output = null;
        var check = false;
        for (var i = start; i < args.Length; i++)
        {
            if (args[i] == "--check")
            {
                if (check)
                {
                    return null;
                }

                check = true;
                continue;
            }

            // A value that starts with "--" is the next flag, not a path; a repeated flag is a mistake, not an override.
            if (args[i] is not ("--book" or "--pdf" or "--work" or "--out") || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            var value = args[i + 1];
            var taken = args[i] switch { "--book" => book, "--pdf" => pdf, "--work" => work, _ => output };
            if (taken != null)
            {
                return null;
            }

            switch (args[i])
            {
                case "--book":
                    book = value;
                    break;
                case "--pdf":
                    pdf = value;
                    break;
                case "--work":
                    work = value;
                    break;
                default:
                    output = value;
                    break;
            }

            i++;
        }

        if (book == null || !Books.Contains(book) || string.IsNullOrWhiteSpace(pdf))
        {
            return null;
        }

        // --out only means something for run and denylist; --check is a run that compares, so it needs somewhere to compare with.
        if ((output != null && action is not ("run" or "denylist")) || (check && (action != "run" || output == null)) || (output != null && string.IsNullOrWhiteSpace(output)))
        {
            return null;
        }

        return new DegreesOptions(action, book, pdf, string.IsNullOrWhiteSpace(work) ? DefaultWork(book) : work, output, check);
    }
}
