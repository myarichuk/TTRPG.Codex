using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Codex.ContentImport.Degrees;

/// <summary>
/// The hashed denylist: one lowercase hex SHA-256 of a lowercased word per line (<c>Data/degrees-denylist.sha256</c>), `#` lines are comments.
/// It holds the capitalized words of the sections the take-list leaves out, as hashes, so the file names no names. A pack is checked by hashing
/// each of its words; a match is reported by file, entry and hash, never by the word.
/// </summary>
public sealed partial class Denylist
{
    private readonly HashSet<string> _hashes;

    public Denylist(IEnumerable<string> hashes) => _hashes = new HashSet<string>(hashes, StringComparer.Ordinal);

    public int Count => _hashes.Count;

    public static string Hash(string word) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(word.Normalize(NormalizationForm.FormC).ToLowerInvariant()))).ToLowerInvariant();

    public bool Contains(string hash) => _hashes.Contains(hash);

    public static Denylist Parse(string text)
    {
        var hashes = new List<string>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!HexLine().IsMatch(line))
            {
                throw new DegreesException($"degrees-denylist.sha256 has a line that is not a SHA-256 hash (line {number}).");
            }

            hashes.Add(line);
        }

        return new Denylist(hashes);
    }

    public static Denylist Embedded() => Parse(BooksConfig.EmbeddedYaml("degrees-denylist.sha256"));

    /// <summary>
    /// The book's common lowercase words as hashes (<c>Data/degrees-common.sha256</c>, the same file shape): every word the book writes in lowercase at least three times.
    /// The allowlist gate lets a sentence's first word through when it is one of these, since ordinary words open sentences; a name is rarely written in lowercase.
    /// </summary>
    public static Denylist EmbeddedCommon() => Parse(BooksConfig.EmbeddedYaml("degrees-common.sha256"));

    /// <summary>
    /// The hashed pairs of capitalized words (<c>Data/degrees-pairs.sha256</c>, the same file shape; each hash is of the two words, lowercased, with a space between): the two-word runs of the
    /// left-out text whose words the word gates would let through one by one (a name made of an ordinary word and a game term). A pack that says such a pair fails the denylist gate.
    /// </summary>
    public static Denylist EmbeddedPairs() => Parse(BooksConfig.EmbeddedYaml("degrees-pairs.sha256"));

    public static string PairHash(string first, string second) => Hash(first.ToLowerInvariant() + " " + second.ToLowerInvariant());

    /// <summary>The file text for a set of words: sorted unique hashes, minus the words in <paramref name="exclude"/> (game terms the packs may use).</summary>
    public static string Format(IEnumerable<string> words, IReadOnlySet<string>? exclude = null)
    {
        var hashes = words
            .Where(w => w.Length > 0 && (exclude == null || !exclude.Contains(w.ToLowerInvariant())))
            .Select(Hash)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(h => h, StringComparer.Ordinal);
        return string.Concat(hashes.Select(h => h + "\n"));
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HexLine();
}

/// <summary>The game-term allowlist (<c>Data/degrees-allowlist.txt</c>): one term per line, `#` lines are comments, compared without case.</summary>
public static class Allowlist
{
    public static HashSet<string> Parse(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    public static HashSet<string> Embedded() => Parse(BooksConfig.EmbeddedYaml("degrees-allowlist.txt"));
}

/// <summary>
/// One finding of a pack gate. It is located by file, entry number (its position in the file, from 1) and field path, never by entry id, since ids are
/// slugs of names. A file name, a path segment or a word that is on the denylist is printed as its hash.
/// </summary>
public sealed record PackFinding(string File, int Entry, string Path, string Kind, string Detail)
{
    public override string ToString() => $"{File} entry #{Entry} {Path} {Kind}: {Detail}";
}

/// <summary>
/// The CI gates over a generated pack folder. Both gates read every YAML entry file (the manifest at the pack's root is not one) and fail with an
/// error, not a finding, on any file shape they do not understand: the root must be a list of mappings.
/// Allowlist: every capitalized word must be a game term (the allowlist plus the system's skill, condition and trait keys, lowercase). That includes
/// entry names, mapping keys, one-word values and list items. The first word of a sentence in a prose value (two words or more, ending in a stop) is
/// exempt only when its lowercase form is also used lowercase somewhere in the pack, so a name after "Mr." or "Step 1." is still flagged.
/// Denylist: every word of every key, value, id, tag and file name is hashed; a match is reported by hash.
/// </summary>
public static partial class PackWordCheck
{
    /// <summary>Top-level entry keys whose values are structure, not prose (the id, the tags and the source line). The denylist gate still reads them.</summary>
    private static readonly HashSet<string> StructuralKeys = new(StringComparer.Ordinal) { "id", "tags", "source" };

    private sealed record Item(string Path, string Text, bool IsKey, bool IsLabel, bool Structural);

    private sealed record EntryItems(int Number, List<Item> Items);

    private sealed record PackFile(string Name, List<EntryItems> Entries);

    /// <param name="common">The words a sentence may open with: the book's common lowercase words, and the words it capitalizes only at the start of a sentence (see <see cref="ImportPipeline.OpenerWords"/>).</param>
    public static List<PackFinding> CheckAllowlist(string packDir, IReadOnlySet<string> gameTerms, Denylist denylist, Denylist? common = null)
    {
        var files = Load(packDir, denylist);
        var lowerUsed = new HashSet<string>(gameTerms, StringComparer.Ordinal);
        foreach (var item in files.SelectMany(f => f.Entries).SelectMany(e => e.Items).Where(i => !i.IsKey))
        {
            foreach (var word in Words(item.Text).Where(w => !IsCapital(w)))
            {
                lowerUsed.Add(word.ToLowerInvariant());
            }
        }

        var findings = new List<PackFinding>();
        foreach (var file in files)
        {
            foreach (var entry in file.Entries)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in entry.Items.Where(i => !i.Structural))
                {
                    foreach (var word in CapitalsToCheck(item, lowerUsed, common, denylist))
                    {
                        var key = word.ToLowerInvariant();
                        if (!IsTerm(key, gameTerms) && seen.Add(key))
                        {
                            findings.Add(new PackFinding(file.Name, entry.Number, item.Path, "capitalized word not in the allowlist", Safe(word, denylist)));
                        }
                    }
                }
            }
        }

        return findings;
    }

    /// <summary>A game term, or the plural of one (a trait or a basic action written in the plural): the word, or the word without a final "s" or "es", is a term.</summary>
    private static bool IsTerm(string lowerWord, IReadOnlySet<string> gameTerms) =>
        gameTerms.Contains(lowerWord)
        || (lowerWord.Length > 3 && lowerWord.EndsWith('s') && (gameTerms.Contains(lowerWord[..^1]) || (lowerWord.EndsWith("es", StringComparison.Ordinal) && gameTerms.Contains(lowerWord[..^2]))));

    /// <summary>
    /// The trait keys of the shipped packs as game terms: the words of the id and the name of every entry in a pack's trait folder (the system's `trait` type), lowercase.
    /// A pack with no such folder adds nothing, so the gate is unchanged until chapter 2 generates trait entries.
    /// </summary>
    public static HashSet<string> TraitTerms(IEnumerable<string> packDirs, string traitFolder)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var packDir in packDirs)
        {
            var folder = Path.Combine(packDir, traitFolder);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                if (BooksConfig.Deserializer.Deserialize<object>(File.ReadAllText(path)) is not List<object> list)
                {
                    throw new DegreesException($"Trait file {Path.GetFileName(path)} is not a list of entries.");
                }

                foreach (var map in list.OfType<IDictionary<object, object>>())
                {
                    foreach (var key in new[] { "id", "name" })
                    {
                        if (map.TryGetValue(key, out var value) && value is string text)
                        {
                            terms.UnionWith(Words(text).Select(w => w.ToLowerInvariant()));
                        }
                    }
                }
            }
        }

        return terms;
    }

    /// <summary>The two-word runs of capitalized words in a text, lowercased: each pair of capitalized words (two letters or more) that follow one another with only punctuation or space between.</summary>
    public static IEnumerable<(string First, string Second)> CapitalPairs(string text)
    {
        string? previous = null;
        foreach (var word in Words(text))
        {
            if (word == "s")
            {
                continue; // the possessive's letter after an apostrophe: "Smith's Hammer" is the pair smith, hammer
            }

            if (word.Length > 1 && IsCapital(word))
            {
                if (previous != null)
                {
                    yield return (previous.ToLowerInvariant(), word.ToLowerInvariant());
                }

                previous = word;
            }
            else
            {
                previous = null;
            }
        }
    }

    public static List<PackFinding> CheckDenylist(string packDir, Denylist denylist, Denylist? pairs = null)
    {
        var findings = new List<PackFinding>();
        foreach (var file in Load(packDir, denylist))
        {
            // The raw relative path of the file is read again here only to hash it; it is never printed.
            foreach (var entry in file.Entries)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in entry.Items)
                {
                    foreach (var (first, second) in pairs == null ? Enumerable.Empty<(string, string)>() : CapitalPairs(item.Text))
                    {
                        var hash = Denylist.PairHash(first, second);
                        if (pairs!.Contains(hash) && seen.Add(hash))
                        {
                            findings.Add(new PackFinding(file.Name, entry.Number, item.Path, "pair of words on the denylist", hash));
                        }
                    }

                    foreach (var word in Words(item.Text))
                    {
                        var hash = Denylist.Hash(word);
                        if (denylist.Contains(hash) && seen.Add(hash))
                        {
                            findings.Add(new PackFinding(file.Name, entry.Number, item.Path, "word on the denylist", hash));
                        }
                    }
                }
            }
        }

        foreach (var rel in PackFiles(packDir).Select(f => Path.GetRelativePath(packDir, f)))
        {
            foreach (var word in Words(rel))
            {
                var hash = Denylist.Hash(word);
                if (denylist.Contains(hash))
                {
                    findings.Add(new PackFinding(Safe(rel.Replace('\\', '/'), denylist), 0, string.Empty, "file name has a word on the denylist", hash));
                }
            }
        }

        return findings;
    }

    /// <summary>The letter runs of a text after normalization (invisible characters are gone), including letters outside the BMP.</summary>
    public static IEnumerable<string> Words(string text)
    {
        var current = new StringBuilder();
        foreach (var rune in SpanText.Normalize(text).EnumerateRunes())
        {
            if (Rune.IsLetter(rune))
            {
                current.Append(rune.ToString());
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    public static bool IsCapital(string word)
    {
        var first = Rune.GetRuneAt(word, 0);
        return Rune.IsUpper(first) || Rune.GetUnicodeCategory(first) == UnicodeCategory.TitlecaseLetter;
    }

    [GeneratedRegex(@"(?m)^((?:- )?\*\*[^*\n]+\.\*\*) ")]
    private static partial Regex BoldLabel();

    private static IEnumerable<string> CapitalsToCheck(Item item, HashSet<string> lowerUsed, Denylist? common, Denylist denylist)
    {
        if (item.IsLabel)
        {
            return Words(item.Text).Where(IsCapital);
        }

        // A bold label ("**Success.** The target ...", a degree of success or a labelled field) is a sentence of its own: what follows it opens a sentence.
        var text = BoldLabel().Replace(SpanText.Normalize(item.Text), "$1\n");
        var result = new List<string>();
        foreach (var sentence in SpanText.Sentences(text))
        {
            var first = true;
            var previous = ' ';
            var word = new StringBuilder();

            void Finish()
            {
                if (word.Length == 0)
                {
                    return;
                }

                var w = word.ToString();
                word.Clear();

                // The first word of a sentence is exempt when its lowercase form is used in the pack, or is one of the book's common lowercase words (a hash list, so no
                // text is kept): an ordinary opening word is not a name. A word on the denylist is never exempt through the common list, so a one-word name that is
                // also an ordinary word is still caught.
                if (IsCapital(w) && !(first && (lowerUsed.Contains(w.ToLowerInvariant())
                    || ((common?.Contains(Denylist.Hash(w)) ?? false) && !denylist.Contains(Denylist.Hash(w))))))
                {
                    result.Add(w);
                }

                first = false;
            }

            foreach (var rune in text.Substring(sentence.Start, sentence.Length).EnumerateRunes())
            {
                if (Rune.IsLetter(rune))
                {
                    word.Append(rune.ToString());
                    previous = 'a';
                    continue;
                }

                Finish();

                // A quotation opens a sentence of its own: a capital right after an opening quote (one that follows a space or an opening bracket) is a sentence start.
                if (rune.Value == '"' && previous is ' ' or '(' or '[')
                {
                    first = true;
                }

                previous = rune.Value < 0x10000 ? (char)rune.Value : 'a';
            }

            Finish();
        }

        return result;
    }

    /// <summary>A prose value has two words or more and ends a sentence with a stop (or has several sentences). Anything else is a label.</summary>
    private static bool IsProse(string text)
    {
        var normalized = SpanText.Normalize(text);
        if (Words(normalized).Count() < 2)
        {
            return false;
        }

        var trimmed = normalized.TrimEnd('"', '\'', ')', ']');
        return SpanText.Sentences(normalized).Count > 1 || trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?');
    }

    /// <summary>The text itself, or the hash of its first denylisted word.</summary>
    private static string Safe(string text, Denylist? denylist)
    {
        if (denylist == null)
        {
            return text;
        }

        foreach (var word in Words(text))
        {
            var hash = Denylist.Hash(word);
            if (denylist.Contains(hash))
            {
                return $"[denylisted {hash}]";
            }
        }

        return text;
    }

    private static IEnumerable<string> PackFiles(string packDir) =>
        Directory.EnumerateFiles(packDir, "*.yaml", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetRelativePath(packDir, f), "manifest.yaml", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);

    private static List<PackFile> Load(string packDir, Denylist? denylist)
    {
        var result = new List<PackFile>();
        foreach (var path in PackFiles(packDir))
        {
            var name = Safe(Path.GetRelativePath(packDir, path).Replace('\\', '/'), denylist);
            object? root;
            try
            {
                root = BooksConfig.Deserializer.Deserialize<object>(File.ReadAllText(path));
            }
            catch (Exception)
            {
                throw new DegreesException($"Pack file {name} is not valid YAML.");
            }

            if (root is not List<object> list)
            {
                throw new DegreesException($"Pack file {name} is not a list of entries (its root is not a sequence).");
            }

            var entries = new List<EntryItems>();
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is not IDictionary<object, object> map)
                {
                    throw new DegreesException($"Pack file {name}: entry #{i + 1} is not a mapping.");
                }

                var items = new List<Item>();
                Walk(map, string.Empty, string.Empty, false, items, denylist);
                entries.Add(new EntryItems(i + 1, items));
            }

            result.Add(new PackFile(name, entries));
        }

        return result;
    }

    /// <summary>Every mapping key and every string value, with its field path. Keys are items too.</summary>
    private static void Walk(object? node, string path, string topKey, bool inList, List<Item> items, Denylist? denylist)
    {
        switch (node)
        {
            case IDictionary<object, object> map:
                foreach (var kv in map)
                {
                    var key = Convert.ToString(kv.Key) ?? string.Empty;
                    var child = path.Length == 0 ? Safe(key, denylist) : path + "." + Safe(key, denylist);
                    items.Add(new Item(child, key, true, true, false));
                    Walk(kv.Value, child, path.Length == 0 ? key : topKey, false, items, denylist);
                }

                break;
            case List<object> list:
                for (var i = 0; i < list.Count; i++)
                {
                    Walk(list[i], $"{path}[{i}]", topKey, true, items, denylist);
                }

                break;
            case string s when s.Length > 0:
                items.Add(new Item(path, s, false, inList || !IsProse(s), StructuralKeys.Contains(topKey)));
                break;
            case null:
                break;
            default:
                if (node is not (IFormattable or bool))
                {
                    throw new DegreesException("A pack file holds a value of a kind the gates do not read.");
                }

                break;
        }
    }
}