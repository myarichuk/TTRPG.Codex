namespace Codex.ContentImport.Degrees;

/// <summary>The arguments after <c>degrees</c>. Paths come only from the command line: no book file name is kept anywhere in the repository.</summary>
public sealed record DegreesOptions(string Action, string Book, string Pdf, string Work)
{
    public const string Usage = """
          degrees [run|fingerprint|notice] --book <pc|mc> --pdf <path> [--work <dir>]

          run          fingerprint, then layout, spine and blocks stages, then the notice check (the default)
          fingerprint  compare the page count and the bookmark counts with the committed ones
          notice       compare the credit line on the bookmarked notice page with the committed one; prints no book text
          --book       pc (the player book) or mc (the monster book)
          --pdf        the book file; it must be outside the git work tree
          --work       directory for layout.json, spine.json, blocks.json (outside the git work tree; default: <temp>/codex-degrees/<book>)
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

        if (action is not ("run" or "fingerprint" or "notice"))
        {
            return null;
        }

        string? book = null, pdf = null, work = null;
        for (var i = start; i < args.Length; i++)
        {
            // A value that starts with "--" is the next flag, not a path; a repeated flag is a mistake, not an override.
            if (args[i] is not ("--book" or "--pdf" or "--work") || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            var value = args[i + 1];
            var taken = args[i] switch { "--book" => book, "--pdf" => pdf, _ => work };
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
                default:
                    work = value;
                    break;
            }

            i++;
        }

        if (book == null || !Books.Contains(book) || string.IsNullOrWhiteSpace(pdf))
        {
            return null;
        }

        return new DegreesOptions(action, book, pdf, string.IsNullOrWhiteSpace(work) ? DefaultWork(book) : work);
    }
}
