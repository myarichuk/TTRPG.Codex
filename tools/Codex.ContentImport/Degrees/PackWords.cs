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
                throw new DegreesException($"The denylist has a line that is not a SHA-256 hash (line {number}).");
            }

            hashes.Add(line);
        }

        return new Denylist(hashes);
    }

    public static Denylist Embedded() => Parse(BooksConfig.EmbeddedYaml("degrees-denylist.sha256"));

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

public sealed record PackFinding(string File, string Entry, string Kind, string Detail)
{
    public override string ToString() => $"{File} [{Entry}] {Kind}: {Detail}";
}

/// <summary>
/// The CI gates over a generated pack folder. The allowlist check flags every capitalized word that does not start a sentence unless it is
/// a word of a shipped entry name or one of the supplied game terms (skill, condition and trait keys, and the allowlist). The denylist check hashes
/// every word of every value and reports a match by hash. Both read the YAML entry files; the manifest at the pack's root is not an entry file.
/// </summary>
public static partial class PackWordCheck
{
    /// <summary>Entry keys whose values are structure, not prose.</summary>
    private static readonly HashSet<string> StructuralKeys = new(StringComparer.Ordinal) { "id", "name", "tags", "source" };

    /// <summary>The lowercased words of every top-level entry name in a pack folder.</summary>
    public static HashSet<string> EntryNameWords(string packDir) =>
        LoadEntries(packDir).SelectMany(f => f.Entries).SelectMany(e => Words(e.Name)).Select(w => w.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Flags capitalized mid-sentence words of <paramref name="packDir"/>. A word passes if it is part of a shipped entry name (of this pack or of
    /// any pack in <paramref name="otherPacks"/>, since entries name each other across packs) or is one of <paramref name="gameTerms"/>
    /// (lowercase: the allowlist plus the system's skill, condition and trait keys and labels).
    /// </summary>
    public static List<PackFinding> CheckAllowlist(string packDir, IReadOnlySet<string> gameTerms, IEnumerable<string>? otherPacks = null)
    {
        var files = LoadEntries(packDir);
        var shipped = EntryNameWords(packDir);
        foreach (var other in otherPacks ?? Array.Empty<string>())
        {
            shipped.UnionWith(EntryNameWords(other));
        }

        var findings = new List<PackFinding>();
        foreach (var (file, entries) in files)
        {
            foreach (var entry in entries)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var text in entry.Values(skipStructural: true))
                {
                    foreach (var word in MidSentenceCapitals(text))
                    {
                        var key = word.ToLowerInvariant();
                        if (!shipped.Contains(key) && !gameTerms.Contains(key) && seen.Add(key))
                        {
                            findings.Add(new PackFinding(file, entry.Id, "capitalized word not in the allowlist", word));
                        }
                    }
                }
            }
        }

        return findings;
    }

    public static List<PackFinding> CheckDenylist(string packDir, Denylist denylist)
    {
        var findings = new List<PackFinding>();
        foreach (var (file, entries) in LoadEntries(packDir))
        {
            foreach (var entry in entries)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var text in entry.Values(skipStructural: false))
                {
                    foreach (var word in Words(text))
                    {
                        var hash = Denylist.Hash(word);
                        if (denylist.Contains(hash) && seen.Add(hash))
                        {
                            findings.Add(new PackFinding(file, entry.Id, "word on the denylist", hash));
                        }
                    }
                }
            }
        }

        return findings;
    }

    /// <summary>Every capitalized word of a value that is not the first word of a sentence. A hyphen or an apostrophe ends a word.</summary>
    public static IEnumerable<string> MidSentenceCapitals(string text)
    {
        var normalized = SpanText.Normalize(text);
        foreach (var sentence in SpanText.Sentences(normalized))
        {
            var first = true;
            foreach (Match m in WordPattern().Matches(normalized.Substring(sentence.Start, sentence.Length)))
            {
                if (!first && char.IsUpper(m.Value[0]))
                {
                    yield return m.Value;
                }

                first = false;
            }
        }
    }

    public static IEnumerable<string> Words(string text) => WordPattern().Matches(SpanText.Normalize(text)).Select(m => m.Value);

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex WordPattern();

    private sealed record EntryText(string Id, string Name, List<(string Key, string Text)> Strings)
    {
        public IEnumerable<string> Values(bool skipStructural) => Strings.Where(s => !skipStructural || !StructuralKeys.Contains(s.Key)).Select(s => s.Text);
    }

    private static List<(string File, List<EntryText> Entries)> LoadEntries(string packDir)
    {
        var result = new List<(string, List<EntryText>)>();
        var files = Directory.EnumerateFiles(packDir, "*.yaml", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetRelativePath(packDir, f), "manifest.yaml", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var path in files)
        {
            object? root;
            try
            {
                root = BooksConfig.Deserializer.Deserialize<object>(File.ReadAllText(path));
            }
            catch (Exception)
            {
                throw new DegreesException("A pack file is not valid YAML.");
            }

            var entries = new List<EntryText>();
            foreach (var item in root as IEnumerable<object> ?? Array.Empty<object>())
            {
                if (item is not IDictionary<object, object> map)
                {
                    continue;
                }

                var strings = new List<(string, string)>();
                Collect(map, string.Empty, strings);
                var id = map.TryGetValue("id", out var i) ? i?.ToString() ?? "?" : "?";
                var name = map.TryGetValue("name", out var n) ? n?.ToString() ?? string.Empty : string.Empty;
                entries.Add(new EntryText(id, name, strings));
            }

            result.Add((Path.GetRelativePath(packDir, path).Replace('\\', '/'), entries));
        }

        return result;
    }

    /// <summary>Every string value, tagged with its top-level key. Mapping keys are structure and are not collected.</summary>
    private static void Collect(object? node, string topKey, List<(string, string)> into)
    {
        switch (node)
        {
            case IDictionary<object, object> map:
                foreach (var kv in map)
                {
                    Collect(kv.Value, topKey.Length == 0 ? kv.Key.ToString() ?? string.Empty : topKey, into);
                }

                break;
            case IEnumerable<object> list when node is not string:
                foreach (var item in list)
                {
                    Collect(item, topKey, into);
                }

                break;
            case string s when s.Length > 0:
                into.Add((topKey, s));
                break;
        }
    }
}
