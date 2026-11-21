using System.Globalization;
using Codex.Core.Rules;
using Codex.Core.WorldBuilder;
using Codex.Plugin.Abstractions;
using Codex.Core;
using Codex.Core.Consistency;

namespace Codex.Tests.Consistency;

// The checks as they were before the performance pass (fa02e24), kept word for word as oracles: the differential tests
// (CheckDifferentialTests) run them beside the current checks on generated and adversarial worlds and require byte-identical issues.
// Never used by the app.

/// <summary><see cref="PlaceTreeCheck"/> at fa02e24: every place's chain walked to its end, one walk per place.</summary>
internal sealed class OraclePlaceTreeCheck : IConsistencyCheck
{
    public string Name => CheckNames.PlaceTree;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var inRegion = s.Places.GroupBy(p => p.RegionId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToDictionary(p => p.Id, StringComparer.Ordinal), StringComparer.Ordinal);
        var everywhere = s.Places.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var region in inRegion.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var tidy = new HashSet<string>(StringComparer.Ordinal);
            foreach (var place in region.Value.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(place.ParentId)) continue;
                if (place.ParentId == place.Id)
                {
                    yield return Issue(place, "self", "Self", new[] { place.Name }, $"The place \"{Make.Quote(place.Name)}\" is inside itself.");
                }
                else if (region.Value.ContainsKey(place.ParentId))
                {
                    tidy.Add(place.Id);
                }
                else if (everywhere.TryGetValue(place.ParentId, out var other))
                {
                    yield return Issue(place, "cross-region:" + place.ParentId, "CrossRegion", new[] { place.Name, other.Name },
                        $"The place \"{Make.Quote(place.Name)}\" is inside \"{Make.Quote(other.Name)}\", which is in another region; a place can only be inside one of its own region.");
                }
                else
                {
                    yield return Issue(place, "missing:" + place.ParentId, "Missing", new[] { place.Name, place.ParentId },
                        $"The place \"{Make.Quote(place.Name)}\" is inside a place ({place.ParentId}) that no longer exists.");
                }
            }

            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var start in region.Value.Values.Where(p => tidy.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                var path = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var at = start;
                while (at != null && tidy.Contains(at.Id) && seen.Add(at.Id))
                {
                    path.Add(at.Id);
                    at = region.Value.GetValueOrDefault(at.ParentId!);
                }

                if (at == null || !seen.Contains(at.Id) || !tidy.Contains(at.Id)) continue;
                var loop = path.Skip(path.IndexOf(at.Id)).ToList();
                var lowest = loop.Min(StringComparer.Ordinal)!;
                if (!reported.Add(lowest)) continue;
                var names = loop.OrderBy(id => id, StringComparer.Ordinal).Select(id => Make.Quote(region.Value[id].Name)).ToList();
                yield return Issue(region.Value[lowest], "cycle", "Cycle", new[] { string.Join(", ", names.Select(n => "\"" + n + "\"")) },
                    $"These places are inside one another in a loop, with no top: {string.Join(", ", names.Select(n => "\"" + n + "\""))}.");
            }
        }
    }

    private Issue Issue(PlaceView place, string detail, string code, IReadOnlyList<string> args, string message) =>
        Make.Issue(Name, IssueSeverity.Error, "place", place.Id, "parentId", detail, "Consistency.PlaceTree" + code, args, message,
            "Move the place to where it belongs with an edit of the place and its \"parent\" (a place of the same region, or \"none\" for the top of the region). Ask the DM where it goes; do not guess.",
            FixVia.Place);
}

/// <summary>One word of a page name in the trie of <see cref="OracleUnlinkedMentionCheck"/>; <see cref="Page"/> is set where a whole name ends.</summary>
internal sealed class OracleNameNode
{
    public Dictionary<string, OracleNameNode>? Next;
    public PageView? Page;
}

/// <summary><see cref="UnlinkedMentionCheck"/> at fa02e24: a trie of the names' words walked from every word of a text.
/// A page's text that names another page in plain words, where a <c>[[link]]</c> was meant. Only a name of two words or more counts (one word
/// is too often an ordinary one), written with a capital or three words long; a page already linking the target anywhere is left alone (links go
/// on the first mention, not every one); and nothing is suggested that would link a party page to a DM-only one.
/// </summary>
internal sealed class OracleUnlinkedMentionCheck : IConsistencyCheck
{
    public string Name => CheckNames.UnlinkedMention;

    public IEnumerable<Issue> Run(WorldSnapshot s) => Run(s, CancellationToken.None);

    /// <summary>The scan, looking at <paramref name="cancel"/> at every page and every few hundred positions of a text, so a time budget is kept even
    /// for one huge page; cancelled, it throws and reports nothing more (the checker then returns nothing at all).</summary>
    public IEnumerable<Issue> Run(WorldSnapshot s, CancellationToken cancel)
    {
        var lookup = new PageLookup(s.Pages);
        // The names a page may be mentioned by, as a trie of their words: a position in a text walks it word by word and stops at the first word
        // that no name continues with, so the work at a position is the length of the name it could be, never the length of the longest title.
        var root = new OracleNameNode();
        var candidates = 0;
        foreach (var (canonical, list) in lookup.Canonicals)
        {
            var words = canonical.Split(' ');
            if (words.Length < 2 || list.Count != 1) continue;
            var node = root;
            foreach (var word in words)
            {
                node.Next ??= new Dictionary<string, OracleNameNode>(StringComparer.Ordinal);
                if (!node.Next.TryGetValue(word, out var next)) node.Next[word] = next = new OracleNameNode();
                node = next;
            }

            node.Page = list[0];
            candidates++;
        }

        if (candidates == 0) yield break;

        foreach (var page in s.Pages)
        {
            cancel.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(page.Body)) continue;
            var linked = WikiText.ExtractLinks(page.Body).Select(WikiText.Slugify).ToHashSet(StringComparer.Ordinal);
            var text = TextFields.WithoutLinksAndMarkers(page.Body);
            var reported = new HashSet<string>(StringComparer.Ordinal);

            foreach (var run in TextNorm.Runs(text))
            {
                for (var i = 0; i < run.Count; i++)
                {
                    if ((i & 255) == 255) cancel.ThrowIfCancellationRequested();
                    PageView? target = null;
                    var length = 0;
                    OracleNameNode? at = root;
                    for (var k = i; k < run.Count && (at = Step(at, run[k].Norm)) != null; k++)
                    {
                        // A name is two words of the text or more, ending where a word ends (as the names are compared: the words joined by spaces).
                        if (at.Page == null || k == i) continue;
                        target = at.Page; // the longest name wins: keep walking while a longer one is still possible
                        length = k - i + 1;
                    }

                    if (target == null) continue;
                    var first = run[i];
                    var last = run[i + length - 1];
                    // The words written before the name that are only articles ("the the Mere of Dead Men") belong to what the text says.
                    var before = i;
                    while (before > 0 && TextNorm.IsArticle(run[before - 1].Norm)) before--;
                    var start = run[before].Start;
                    i += length - 1; // the name is used up: a shorter name inside it is not a second mention

                    if (target.Id == page.Id) continue;
                    if (!first.Capitalized && length < 3) continue;
                    if (linked.Contains(target.Slug) || linked.Contains(WikiText.Slugify(target.Title))) continue;
                    if (page.PartyVisible && !target.PartyVisible) continue;
                    if (!reported.Add(target.Id)) continue;

                    var written = Make.Quote(page.Body.Substring(start, Math.Min(last.End, page.Body.Length) - start));
                    yield return Make.Issue(Name, IssueSeverity.Warning, "page", page.Id, "body", target.Id, "Consistency.UnlinkedMention",
                        new[] { written, target.Title },
                        $"The text says \"{written}\" in plain words, which is the name of the page \"{Make.Quote(target.Title)}\".",
                        $"If a link was meant, write [[{Make.Quote(target.Title)}]] (or [[{Make.Quote(target.Title)}|{written}]] to keep the words). If not, dismiss the issue.",
                        FixVia.Page);
                }
            }
        }
    }

    /// <summary>The trie node after one word of the text. A word's compared form can hold spaces (Unicode-compatibility normalising a ligature such as
    /// U+FDFA gives four words), and a name is compared as the words of the text joined by spaces, so such a word walks as many steps as it has parts:
    /// splitting the joined text on spaces is splitting each word and putting the parts one after the other.</summary>
    private static OracleNameNode? Step(OracleNameNode node, string norm)
    {
        if (norm.IndexOf(' ') < 0) return node.Next != null && node.Next.TryGetValue(norm, out var next) ? next : null;
        OracleNameNode? at = node;
        foreach (var part in norm.Split(' '))
        {
            if (at.Next == null || !at.Next.TryGetValue(part, out at)) return null;
        }

        return at;
    }
}


/// <summary><see cref="TextNorm.Tokenize(string?)"/> at fa02e24: every word built in a StringBuilder and normalised.</summary>
internal static class OracleTokenizer
{
    private static bool IsApostrophe(char c) => c is '\'' or '’' or '‘' or 'ʼ';

    public static List<Token> Tokenize(string? text)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrEmpty(text)) return tokens;
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
            var start = i;
            var word = new System.Text.StringBuilder();
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsLetterOrDigit(c)) { word.Append(c); i++; }
                else if (IsApostrophe(c) && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]) && word.Length > 0) i++;
                else break;
            }

            var norm = word.ToString().Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
            tokens.Add(new Token(norm, start, i, char.IsUpper(text[start])));
        }

        return tokens;
    }
}

/// <summary><see cref="SheetRuleCheck"/> at fa02e24: the engine works out the pickers' options as well.</summary>
internal sealed class OracleSheetRuleCheck : IConsistencyCheck
{
    public string Name => CheckNames.SheetRule;

    public IEnumerable<Issue> Run(WorldSnapshot s) => Run(s, CancellationToken.None);

    /// <inheritdoc/>
    public IEnumerable<Issue> Run(WorldSnapshot s, CancellationToken cancel)
    {
        if (s.System?.SheetRules == null || s.Enforcement == "off") yield break;
        var engine = new SheetRuleEngine(s.System, s.Library);
        // The sheets a character's links point to, from the same snapshot, as the sheet page runs the rules with them.
        var linked = new SnapshotLinkedSheets(s.Actors);

        foreach (var a in s.Actors.Where(a => a.Role != ActorRole.Monster && !a.Retired))
        {
            cancel.ThrowIfCancellationRequested();
            var (evaluation, failure) = Evaluate(engine, a, linked);
            if (evaluation == null)
            {
                yield return Make.Issue(Name, IssueSeverity.Info, "actor", a.Id, "sheet", "evaluation-failed", "Consistency.SheetEvaluationFailed",
                    new[] { a.Name, failure! },
                    $"The sheet rules could not be evaluated for \"{Make.Quote(a.Name)}\" ({failure}).",
                    "Nothing to propose; tell the DM the sheet could not be read.", FixVia.ReportOnly);
                continue;
            }

            foreach (var issue in Issues(s.System, a, evaluation.Violations, IssueSeverity.Error).Concat(Issues(s.System, a, evaluation.Warnings, IssueSeverity.Warning))) yield return issue;
        }
    }

    // One bad sheet must not hide every other character's issues; it becomes an issue of its own. Not in the iterator: it cannot yield from a catch.
    private static (SheetEvaluation? Evaluation, string? Failure) Evaluate(SheetRuleEngine engine, ActorView actor, ILinkedSheets linked)
    {
        try
        {
            // Recompute writes the values it derives onto the sheet it is given: a copy, so the snapshot stays as it was taken.
            return (engine.Recompute(new Dictionary<string, object>(actor.Sheet), linked), null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (null, e.GetType().Name);
        }
    }

    private IEnumerable<Issue> Issues(SystemDefinition? system, ActorView actor, IEnumerable<RuleViolation> violations, IssueSeverity severity)
    {
        foreach (var v in violations)
        {
            if (v.Key != null && v.Key.StartsWith("Actor.Rule.PointBuy", StringComparison.Ordinal)) continue;
            var args = (v.Args ?? Array.Empty<object?>()).Select(ArgText).ToList();
            // The rule's key and the field (already in the id), and, for a rule that is about one item among several on the field, which item: never
            // the numbers or the whole sentence, which a partial fix changes (a score lowered from 25 to 20 is still the same problem).
            // Keyed by a Library entry's id, not its name, as the check is now (the id scheme changed after fa02e24; what this oracle stands for, the
            // verdicts the engine gives with and without the pickers' options, did not).
            var detail = (v.Key ?? "rule") + Item(v.Key, (v.Args ?? Array.Empty<object?>()).Select(a => a is ContentEntry { Id.Length: > 0 } e ? e.Id : ArgText(a)).ToList());
            // Fixable through a sheet edit only when the rule is about a key an edit may set; a derived value is the sheet's own to compute.
            var fixable = SheetProposal.IsEditable(system, v.FieldKey);
            yield return Make.Issue(Name, severity, "actor", actor.Id, v.FieldKey, detail, v.Key ?? "Consistency.SheetRule", args,
                v.Message,
                fixable
                    ? "Ask the DM which value they want, then propose a sheet edit of this key (existing:sheet:<actor id>); do not guess a value or invent an entry. Anything that stays broken is reported again."
                    : "This is a value the sheet works out itself, which no proposal can set. Tell the DM which rule the sheet breaks; do not guess.",
                fixable ? FixVia.Sheet : FixVia.ReportOnly);
        }
    }

    // For these rules, the args that name the item the violation is about (the entry, the missing id). Any other rule is one per field and key; two
    // that still share an id are told apart by order (ConsistencyChecker.Run), the one limit of the scheme.
    private static readonly Dictionary<string, int[]> ItemArgs = new(StringComparer.Ordinal)
    {
        ["Actor.Rule.NotInLibrary"] = new[] { 1 }, ["Actor.Rule.Pick"] = new[] { 1 }, ["Actor.Rule.Once"] = new[] { 1 }, ["Actor.Rule.SlotHolds"] = new[] { 1 },
        ["Actor.Rule.Prerequisite"] = new[] { 0 }, ["Actor.Rule.ArmorUntrained"] = new[] { 0 }, ["Actor.Rule.ArmorStrength"] = new[] { 0 },
        ["Actor.Rule.ArmorStealth"] = new[] { 0 }, ["Actor.Rule.WeaponUntrained"] = new[] { 0 }, ["Actor.Rule.FeatureError"] = new[] { 0 },
        ["Actor.Rule.SpendNoPool"] = new[] { 0 }, ["Actor.Rule.NoHitDie"] = new[] { 0 }, ["Actor.Rule.NoHitPointsPerLevel"] = new[] { 0 },
        ["Actor.Rule.NoSpellAbility"] = new[] { 0 }, ["Actor.Rule.NoSpellTable"] = new[] { 0 }, ["Actor.Rule.RuneNotFound"] = new[] { 1, 2 },
        ["Actor.Rule.RuneWrongItem"] = new[] { 1, 2 }, ["Actor.Rule.IncNone"] = new[] { 1 }, ["Actor.Rule.IncRemaining"] = new[] { 1 },
        ["Actor.Rule.IncGrants"] = new[] { 1 }, ["Actor.Rule.IncCant"] = new[] { 1 }
    };

    private static string Item(string? key, IReadOnlyList<string> args)
    {
        if (key == null || !ItemArgs.TryGetValue(key, out var indexes)) return string.Empty;
        var item = string.Join('-', indexes.Where(i => i < args.Count).Select(i => Slug(args[i])).Where(t => t.Length > 0));
        return item.Length == 0 ? string.Empty : ":" + item;
    }

    private static string ArgText(object? arg) => arg switch
    {
        null => string.Empty,
        ContentEntry e => e.Name,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => arg.ToString() ?? string.Empty
    };

    // A short readable stand-in for a sentence in an id: the slug of its words, cut so an id stays one line.
    private static string Slug(string text)
    {
        var slug = WikiText.Slugify(text);
        return slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }
}


/// <summary>The secret-block checks at fa02e24, each reading every text's blocks itself.
/// What the secret-block checks share: the blocks of every text field, each with the text of its opening line and a key that names it.</summary>
internal static class OracleSecretScan
{
    /// <summary>The longest slug of a block's first line a key holds.</summary>
    private const int KeyLength = 40;

    /// <summary>
    /// The blocks of every text field that <paramref name="reported"/> keeps, each with a key for its issue id: the word it was opened with and the slug of its
    /// first line of content (bounded; <c>empty</c> for a block with none), <c>dm:the-keeper-is-a-smuggler</c>; for an unclosed block the first line alone,
    /// as the word is what another issue of the same block (an unknown audience) fixes. The key is made only from what a fix of the
    /// block's own issue, or of another issue of the same block, does not touch: the rest of the opening line (the characters a <c>:::for</c> block names, the reveal stamp) is left out, so taking a
    /// character off the line or closing the block leaves the key alone, and changing an unknown word changes the key because it fixes the issue. Never the
    /// block's position, and never another block's: a block added, fixed or removed elsewhere in the text moves no other key. <c>.2</c>, <c>.3</c> tell
    /// apart only blocks that <paramref name="reported"/> keeps and that open with the same word and the same first line (the one limit of the scheme: two such
    /// blocks cannot be told apart, so fixing the first gives its key to the second).
    /// </summary>
    /// <param name="withWord">False for a check whose issue a change of the opener word does not fix (an unclosed block): its key is the first line alone,
    /// so fixing the block's unknown word (another issue) leaves it as it was.</param>
    public static IEnumerable<(TextField Field, SecretBlock Block, string Opening, string Key)> Blocks(WorldSnapshot s, Func<SecretBlock, bool> reported, bool withWord = true)
    {
        foreach (var field in TextFields.Of(s))
        {
            if (!SecretText.HasMarkers(field.Text)) continue;
            // The same line splitting the parser uses, so SecretBlock.Line is the same line here.
            var lines = field.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var block in SecretText.Blocks(field.Text).Where(reported))
            {
                var opening = block.Line < lines.Length ? lines[block.Line] : string.Empty;
                var content = Slug(FirstContentLine(block), "empty");
                var identity = withWord ? $"{Slug(block.OpenerWord, "block")}:{content}" : content;
                seen[identity] = seen.GetValueOrDefault(identity) + 1;
                yield return (field, block, opening, seen[identity] == 1 ? identity : $"{identity}.{seen[identity]}");
            }
        }
    }

    /// <summary>The block's first line that has something in it and is not itself a marker line (a nested block's opener or closer).</summary>
    private static string FirstContentLine(SecretBlock block) =>
        block.Inner.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !SecretText.IsMarkerLine(l)) ?? string.Empty;

    private static string Slug(string text, string none)
    {
        if (!text.Any(char.IsLetterOrDigit)) return none;
        var slug = WikiText.Slugify(text);
        return slug.Length > KeyLength ? slug[..KeyLength].TrimEnd('-') : slug;
    }
}

/// <summary>A secret block that is never closed. It runs to the end of the text: everything after its opening line is the DM's alone.</summary>
internal sealed class OracleSecretUnclosedCheck : IConsistencyCheck
{
    public string Name => CheckNames.SecretUnclosed;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        foreach (var (field, block, opening, key) in OracleSecretScan.Blocks(s, b => b.ClosingLine == null, withWord: false))
        {
            yield return Make.Issue(Name, IssueSeverity.Warning, field.Kind, field.EntityId, field.Field, key,
                "Consistency.SecretUnclosed", new[] { Make.Quote(opening), (block.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                $"The block opened by \"{Make.Quote(opening)}\" (line {block.Line + 1}) is never closed with a line of :::, so it runs to the end of the text.",
                "Add a line with only ::: where the block should end. Do not change what the block says or who it is for.",
                field.Fix);
        }
    }
}

/// <summary>A block opened with a word the app does not know (<c>:::dm</c>, <c>:::party</c>, <c>:::players</c>, <c>:::for</c>): the block is
/// treated as DM-only, so a typo hides what the DM meant to show, never the other way round.</summary>
internal sealed class OracleSecretUnknownAudienceCheck : IConsistencyCheck
{
    public string Name => CheckNames.SecretUnknownAudience;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        foreach (var (field, block, opening, key) in OracleSecretScan.Blocks(s, IsUnknownAudience))
        {
            var word = block.OpenerWord;
            yield return Make.Issue(Name, IssueSeverity.Warning, field.Kind, field.EntityId, field.Field, key,
                "Consistency.SecretUnknownAudience", new[] { Make.Quote(word), Make.Quote(opening), (block.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                $"The block opened by \"{Make.Quote(opening)}\" (line {block.Line + 1}) names an audience the app does not know (\":::{Make.Quote(word)}\"), so only the DM can read it.",
                "The audiences are dm, party, players and for. Change the word only if the DM's intent is clear; otherwise ask, since a wrong guess can show a secret.",
                field.Fix);
        }
    }

    /// <summary>The word the block was opened with, as the parser read it: any word that is not "dm" yet came out as DM-only is one the app does not know.
    /// (Filtered before the keys are made, so a block of a known audience never takes a <c>.2</c> from another.)</summary>
    private static bool IsUnknownAudience(SecretBlock block) =>
        block.Marked == SecretAudience.Dm && block.OpenerWord.Length > 0 && !block.OpenerWord.Equals("dm", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A <c>:::for</c> block that names no character, or one that no longer exists. A block for nobody is read by nobody.</summary>
internal sealed class OracleSecretForMissingCheck : IConsistencyCheck
{
    public string Name => CheckNames.SecretForMissing;

    public IEnumerable<Issue> Run(WorldSnapshot s)
    {
        var actors = s.Actors.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        // Only the blocks with an issue (no character, or one who is gone), so a block for characters who exist never numbers another's key.
        foreach (var (field, block, opening, key) in OracleSecretScan.Blocks(s, b => b.Marked == SecretAudience.Some && (b.Knowers.Count == 0 || b.Knowers.Any(id => !actors.Contains(id)))))
        {
            if (block.Knowers.Count == 0)
            {
                yield return Make.Issue(Name, IssueSeverity.Warning, field.Kind, field.EntityId, field.Field, $"{key}:none", "Consistency.SecretForNobody",
                    new[] { Make.Quote(opening), (block.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    $"The block opened by \"{Make.Quote(opening)}\" (line {block.Line + 1}) names no character, so no player can read it.",
                    "Name the characters it is for as mentions on the opening line, or ask the DM who should read it.",
                    field.Fix);
                continue;
            }

            foreach (var id in block.Knowers.Where(id => !actors.Contains(id)).OrderBy(id => id, StringComparer.Ordinal))
            {
                yield return Make.Issue(Name, IssueSeverity.Warning, field.Kind, field.EntityId, field.Field, $"{key}:{id}", "Consistency.SecretForMissing",
                    new[] { id, Make.Quote(opening), (block.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    $"The block opened by \"{Make.Quote(opening)}\" (line {block.Line + 1}) is for a character ({id}) that no longer exists.",
                    "Take that character off the opening line, or ask the DM who should read the block instead.",
                    field.Fix);
            }
        }
    }
}
