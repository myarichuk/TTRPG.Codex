using Codex.ContentImport.Degrees;

namespace Codex.Tests;

/// <summary>
/// Chapter 8d's companions, familiars and archetypes over synthetic blocks (invented words only): a companion type is a unit with a line of attribute modifiers, its labelled lines are fields and its attacks rows, its advanced maneuver
/// is the entry's text; the other headings are rules; a list of abilities is split at the bold names; an archetype is a section with a text about multiclassing as it and feats that name it, the first of them its dedication feat.
/// Every gate has a test. CI never sees a book.
/// </summary>
public class DegreesCompanionsTests
{
    private static Block Lines(string role, double top, params (double Left, string Label, string Text)[] lines) =>
        new(1, 0, role, lines.Min(l => l.Left), 0, 300, top, lines.Select(l => l.Text).ToList(), lines.Select(l => new LineInfo(l.Left, 299, l.Label)).ToList());

    private static Block Plain(string role, double top, string text) => Lines(role, top, (60, string.Empty, text));

    private static SpineNode Node(int depth, string title, params string[] path) => new(depth, title, path, 1, 1, false);

    private static BlocksResult Book(List<Block> blocks) =>
        new(blocks, 0, 0, 0, new Dictionary<string, int>(), new List<int>(), new List<Block>(), new List<int>());

    private static (List<SpineNode> Spine, BlocksResult Blocks, List<TakenRange> Ranges, List<ParsedEntry> Parsed) Parse(string yaml, List<SpineNode> spine, List<Block> blocks)
    {
        var book = Book(blocks);
        var ranges = TakeStage.Resolve(spine, book, TakeList.Parse(yaml).Rows);
        return (spine, book, ranges, EntriesStage.Run(book, ranges, "pc", null, spine));
    }

    private static (double, string, string)[] MouseLines(string? hitPoints = "Hit Points 4", string maneuver = "Squeak", bool edgeLine = false)
    {
        var lines = new List<(double, string, string)>
        {
            (60, string.Empty, "Your pet is a small mouse."),
            (60, "Size", "Size Tiny"),
            (60, "Melee", "Melee [one-action] nibble (agile), Damage 1d4 piercing"),
            (60, "Str", "Str +1, Dex +4, Con +0, Int –3, Wis +2, Cha +1"),
        };
        if (hitPoints != null)
        {
            lines.Add((60, "Hit Points", hitPoints));
        }

        lines.AddRange(new (double, string, string)[]
        {
            (60, "Skill", "Skill Stealth"),
            (60, "Senses", "Senses scent (imprecise, 30 feet)"),
            (60, "Speed", "Speed 25 feet"),
            (60, "Support Benefit", "Support Benefit Your mouse distracts foes"),
            (edgeLine ? 60 : 69, string.Empty, "so that they miss."),
            (60, "Advanced Maneuver", "Advanced Maneuver " + maneuver),
        });
        return lines.ToArray();
    }

    private static List<Block> Pets(string? hitPoints = "Hit Points 4", string maneuver = "Squeak", bool edgeLine = false, string? extraLabel = null)
    {
        var heron = new List<(double, string, string)>
        {
            (60, string.Empty, "Your pet is a long-legged bird."),
            (60, "Size", "Size Small"),
            (60, "Ranged", "Ranged [one-action] spit, Damage 2d4 acid plus a stain (see Special)"),
            (60, "Melee", "Melee [one-action] beak (finesse, reach), Damage 1d6 piercing"),
            (60, "Str", "Str +0, Dex +2, Con +3, Int –2, Wis +1, Cha +1"),
            (60, "Hit Points", "Hit Points 6"),
            (60, "Skill", "Skill Survival"),
            (60, "Senses", "Senses low-light vision"),
            (60, "Speed", "Speed 12 feet, fly 44 feet"),
            (60, "Special", "Special The heron stands on one leg for"),
            (69, string.Empty, "a very long time."),
            (60, "Support Benefit", "Support Benefit Your heron watches the water."),
            (60, "Advanced Maneuver", "Advanced Maneuver Dive"),
        };
        if (extraLabel != null)
        {
            heron.Insert(2, (60, extraLabel, extraLabel + " odd"));
        }

        return new List<Block>
        {
            Plain("section", 900, "Pets"),
            Plain("rules", 890, "Pets are loyal."),
            Plain("section", 880, "Tame Pets"),
            Plain("rules", 870, "A tame pet follows you."),
            Plain("entry-header", 860, "Base Stats"),
            Plain("rules", 850, "Base statistics are as follows."),
            Plain("entry-header", 840, "Kinds"),
            Plain("rules", 830, "Each kind has its own numbers."),
            Lines("body", 820, (60, "Size", "Size The size of the pet"), (60, "Hit Points", "Hit Points The pet's own points")),
            Plain("entry-header", 810, "Mouse"),
            Lines("body", 800, MouseLines(hitPoints, maneuver, edgeLine)),
            Plain("block-header", 790, "SQUEAK [one-action]"),
            Plain("body", 780, "The mouse squeaks loudly."),
            Plain("entry-header", 770, "Heron"),
            Plain("tag", 760, "UNCOMMON BIRD"),
            Lines("body", 750, heron.ToArray()),
            Plain("block-header", 740, "DIVE [two-actions]"),
            Plain("tag", 730, "FLOURISH"),
            Plain("body", 720, "The heron dives at the water."),
            Plain("entry-header", 710, "Growth"),
            Plain("rules", 700, "A pet may grow."),
            Plain("block-header", 690, "Elder Pets"),
            Plain("rules", 680, "An elder pet is bigger."),
        };
    }

    private static List<SpineNode> PetsSpine()
    {
        string[] P(params string[] more) => new[] { "Chapter 9: Makers", "Pets" }.Concat(more).ToArray();
        return new List<SpineNode>
        {
            Node(0, "Chapter 9: Makers", "Chapter 9: Makers"),
            Node(1, "Pets", P()),
            Node(2, "Tame Pets", P("Tame Pets")),
            Node(3, "Base Stats", P("Tame Pets", "Base Stats")),
            Node(3, "Kinds", P("Tame Pets", "Kinds")),
            Node(3, "Mouse", P("Tame Pets", "Mouse")),
            Node(3, "Heron", P("Tame Pets", "Heron")),
            Node(3, "Growth", P("Tame Pets", "Growth")),
            Node(4, "Elder Pets", P("Tame Pets", "Growth", "Elder Pets")),
        };
    }

    private const string PetsRow = """
        rows:
          - { chapter: makers, book: pc, parser: companions, target: companion, folder: companions, category: Classes, approved: true, columns: 1, paths: [["Chapter 9: Makers", "Pets"]] }
        """;

    [Fact]
    public void ACompanionTypeIsTheUnitWithALineOfModifiers_ItsLinesAreFields_ItsAttacksRows_AndItsManeuverItsText_AndEveryOtherHeadingIsARule()
    {
        var parsed = Parse(PetsRow, PetsSpine(), Pets()).Parsed;

        Assert.Equal(new[] { "heron", "mouse" }, parsed.Where(e => e.Type == "companion").Select(e => e.Id));
        Assert.Equal(new[] { "base-stats", "elder-pets", "growth", "kinds", "pets", "tame-pets" }, parsed.Where(e => e.Type == "rule").Select(e => e.Id));
        var mouse = parsed.Single(e => e.Id == "mouse");
        Assert.Equal(("companions", "pc > Chapter 9: Makers > Pets > Mouse"), (mouse.Folder, mouse.Source));
        Assert.Equal(new[] { ("size", "Tiny"), ("senses", "scent (imprecise, 30 feet)"), ("speed", "25 feet"), ("supportBenefit", "Your mouse distracts foes so that they miss."), ("advancedManeuver", "Squeak") },
            mouse.Extracted.Select(x => (x.Label, x.Text)));
        // A minus sign is an en dash in the book; the numbers are bare, the skill a lowercase word, and each attack a row with its traits.
        Assert.Equal(new[] { ("str", "1"), ("dex", "4"), ("con", "0"), ("int", "-3"), ("wis", "2"), ("cha", "1"), ("hp", "4"), ("skill", "stealth") }, mouse.Raw.Take(8).Select(x => (x.Label, x.Text)));
        Assert.Equal("\n      - { kind: melee, name: \"nibble\", traits: [\"agile\"], damage: \"1d4\", damageType: \"piercing\" }", mouse.Raw.Single(x => x.Label == "attacks").Text);
        Assert.Equal("Your pet is a small mouse.\n**Squeak (1 action).**\nThe mouse squeaks loudly.", mouse.Render());

        // A rarity and a kind of creature are traits; a second attack, a text added to the damage and a special line are kept; the maneuver's own trait line is a labelled line of the text.
        var heron = parsed.Single(e => e.Id == "heron");
        Assert.Equal(new[] { "uncommon", "bird" }, heron.Traits);
        Assert.Equal("\n      - { kind: ranged, name: \"spit\", traits: [], damage: \"2d4\", damageType: \"acid\", extra: \"plus a stain (see Special)\" }\n      - { kind: melee, name: \"beak\", traits: [\"finesse\", \"reach\"], damage: \"1d6\", damageType: \"piercing\" }",
            heron.Raw.Single(x => x.Label == "attacks").Text);
        Assert.Equal("The heron stands on one leg for a very long time.", heron.Extracted.Single(x => x.Label == "special").Text);
        Assert.Equal("Your pet is a long-legged bird.\n**Dive (2 actions).**\n**Traits.** flourish\nThe heron dives at the water.", heron.Render());

        // The text before the first heading is a rule named by the path's last title; a heading in the block-header font with no icon is a rule of its own, with its parent's source.
        Assert.Equal("Pets are loyal.", parsed.Single(e => e.Id == "pets").Render());
        Assert.Equal("pc > Chapter 9: Makers > Pets > Elder Pets", parsed.Single(e => e.Id == "elder-pets").Source);
        // The legend of the lines (a unit with a line labelled Size but none of attribute modifiers) is a rule.
        Assert.Contains("**Size.** The size of the pet", parsed.Single(e => e.Id == "kinds").Render());
    }

    [Fact]
    public void TheCompanionsCounts_AreTakenFromTheDocument_AndEveryGateFailsLoud()
    {
        var spine = PetsSpine();
        var (_, blocks, ranges, parsed) = Parse(PetsRow, spine, Pets());
        var counts = ImportPipeline.CheckCompanionCounts(spine, blocks, ranges, parsed)!;

        // 7 bookmarks and 7 headings; 2 headings open a companion, with 2 lines of each label and 2 entries; the other 5 headings and the lead are 6 rules.
        Assert.Equal((7, 7, 2, 2, 2, 2, 2, 2, 2, 6), (counts.Bookmarks, counts.Headings, counts.CompanionHeadings, counts.SizeLines, counts.HitPointLines, counts.AttributeLines, counts.SupportLines, counts.ManeuverLines, counts.Companions, counts.Rules));

        // A companion with no hit points line, a maneuver the heading does not name, a bold label that is none of the lines', an unlabelled line at the edge among the lines and an attack of an unknown shape.
        Assert.Throws<DegreesException>(() => Parse(PetsRow, PetsSpine(), Pets(hitPoints: null)));
        Assert.Throws<DegreesException>(() => Parse(PetsRow, PetsSpine(), Pets(maneuver: "Whistle")));
        Assert.Throws<DegreesException>(() => Parse(PetsRow, PetsSpine(), Pets(extraLabel: "Nonsense")));
        Assert.Throws<DegreesException>(() => Parse(PetsRow, PetsSpine(), Pets(edgeLine: true)));
        Assert.Throws<DegreesException>(() => Parse(PetsRow, PetsSpine(), Pets(hitPoints: "Hit Points many")));
        // A bookmark more than the headings, and a heading more than the bookmarks (unless the row says why).
        var more = PetsSpine().Append(Node(3, "Extra", "Chapter 9: Makers", "Pets", "Tame Pets", "Extra")).ToList();
        var (_, b2, r2, p2) = Parse(PetsRow, more, Pets());
        Assert.Throws<DegreesException>(() => ImportPipeline.CheckCompanionCounts(more, b2, r2, p2));
        var fewer = PetsSpine().Where(n => n.Title != "Elder Pets").ToList();
        var (_, b3, r3, p3) = Parse(PetsRow, fewer, Pets());
        Assert.Throws<DegreesException>(() => ImportPipeline.CheckCompanionCounts(fewer, b3, r3, p3));
        var said = PetsRow.Replace("approved: true", "approved: true, countsDiffer: \"a heading has no bookmark\"", StringComparison.Ordinal);
        var (_, b4, r4, p4) = Parse(said, fewer, Pets());
        Assert.Equal(6, ImportPipeline.CheckCompanionCounts(fewer, b4, r4, p4)!.Bookmarks);
    }

    private static List<Block> Sprites(bool twoLists = true) => new()
    {
        Plain("section", 900, "Sprites"),
        Plain("rules", 890, "Sprites are bound to you."),
        Plain("entry-header", 880, "Talking"),
        Plain("rules", 870, "A sprite talks in its own way."),
        Plain("entry-header", 860, "Minor Tricks"),
        Lines("rules", 850, (60, string.Empty, "Minor tricks change the sprite itself."), (69, "Glow:", "Glow: The sprite lights up"), (60, string.Empty, "when it is happy."), (69, "Hide-Away:", "Hide-Away: The sprite hides in your hair.")),
        Lines("rules", 840, (60, string.Empty, "It can stay there all day."), (69, "Swift Wing:", "Swift Wing: The sprite flies at a high speed.")),
        Plain("entry-header", 830, "Major Tricks"),
        Lines("rules", 820, (60, string.Empty, "Major tricks change you."), (69, "Share Glow:", "Share Glow: You glow too.")),
        twoLists ? Lines("rules", 810, (69, "Link:", "Link: You feel what it feels.")) : Plain("rules", 810, "Nothing else."),
    };

    private static List<SpineNode> SpritesSpine() => new()
    {
        Node(0, "Chapter 9: Makers", "Chapter 9: Makers"),
        Node(1, "Sprites", "Chapter 9: Makers", "Sprites"),
        Node(2, "Talking", "Chapter 9: Makers", "Sprites", "Talking"),
        Node(2, "Minor Tricks", "Chapter 9: Makers", "Sprites", "Minor Tricks"),
        Node(2, "Major Tricks", "Chapter 9: Makers", "Sprites", "Major Tricks"),
    };

    private const string SpritesRow = """
        rows:
          - { chapter: makers, book: pc, parser: familiars, target: familiar-ability, folder: familiar-abilities, category: Classes, approved: true, columns: 1, abilityKinds: { Minor Tricks: minor, Major Tricks: major }, paths: [["Chapter 9: Makers", "Sprites"]] }
        """;

    [Fact]
    public void AListOfAbilities_IsSplitAtTheBoldNames_ItsOwnSentencesAreARule_AndTheCountsAgreeThreeWays()
    {
        var spine = SpritesSpine();
        var (_, blocks, ranges, parsed) = Parse(SpritesRow, spine, Sprites());

        Assert.Equal(new[] { "glow", "hide-away", "link", "share-glow", "swift-wing" }, parsed.Where(e => e.Type == "familiar-ability").Select(e => e.Id));
        Assert.Equal(new[] { ("glow", "minor"), ("hide-away", "minor"), ("link", "major"), ("share-glow", "major"), ("swift-wing", "minor") },
            parsed.Where(e => e.Type == "familiar-ability").Select(e => (e.Id, e.Raw.Single(r => r.Label == "kind").Text)));
        // A line that goes on across a block (the layout cut it) is one text.
        Assert.Equal("The sprite hides in your hair. It can stay there all day.", parsed.Single(e => e.Id == "hide-away").Render());
        Assert.Equal("The sprite lights up when it is happy.", parsed.Single(e => e.Id == "glow").Render());
        Assert.Equal(new[] { "major-tricks", "minor-tricks", "sprites", "talking" }, parsed.Where(e => e.Type == "rule").Select(e => e.Id));
        Assert.Equal("Minor tricks change the sprite itself.", parsed.Single(e => e.Id == "minor-tricks").Render());

        var counts = ImportPipeline.CheckFamiliarCounts(spine, blocks, ranges, parsed)!;
        Assert.Equal((3, 3, 4), (counts.Bookmarks, counts.Headings, counts.Rules));
        Assert.Equal((3, 3, 3), counts.Kinds["minor"]);
        Assert.Equal((2, 2, 2), counts.Kinds["major"]);

        // A section named as a list that holds none, and a bookmark more than the headings, fail the run.
        Assert.Throws<DegreesException>(() => Parse(SpritesRow.Replace("Major Tricks: major", "Talking: major", StringComparison.Ordinal), spine, Sprites()));
        var more = SpritesSpine().Append(Node(2, "Extra", "Chapter 9: Makers", "Sprites", "Extra")).ToList();
        var (_, b2, r2, p2) = Parse(SpritesRow, more, Sprites());
        Assert.Throws<DegreesException>(() => ImportPipeline.CheckFamiliarCounts(more, b2, r2, p2));
    }

    private static List<Block> Guilds(bool hanging = true, bool secondDedication = false, bool noFeats = false, bool formula = false, bool bullets = false)
    {
        var thief = new List<Block>
        {
            Plain("section", 800, "Thief"),
            Plain("rules", 790, "A guild of those who open locks."),
            Plain("entry-header", 780, "Multiclass Thief Persons"),
            bullets ? Lines("rules", 770, (60, string.Empty, "The thief guild teaches you to open things."), (60, string.Empty, "• Mason thieves climb."), (60, string.Empty, "• Others hide.")) : Plain("rules", 770, "The thief guild teaches you to open things."),
        };
        if (!noFeats)
        {
            thief.AddRange(new[]
            {
                Plain("block-header", 760, "THIEF DEDICATION FEAT 2"),
                Plain("tag", 750, "ARCHETYPE DEDICATION MULTICLASS"),
                // The first line is the label's, set in a few points; the lines after it are a point or two apart, and the last is at the block's edge: the paragraph after the label is its own.
                Lines("body", 740, (66, "Prerequisites", "Prerequisites Dexterity +2"), (67, string.Empty, "You pick locks."), (62, string.Empty, "You gain a lock feat to open the door"), (60, string.Empty, "of your choice.")),
                Plain("block-header", 730, secondDedication ? "THIEF PLEDGE FEAT 4" : "BASIC LOCKS FEAT 4"),
                Plain("tag", 720, secondDedication ? "ARCHETYPE DEDICATION" : "ARCHETYPE"),
                formula
                    // A prerequisites line that wraps over three lines set around art: the last opens with a number, and starts left of the line that opened the paragraph.
                    ? Lines("body", 710, (66, "Prerequisites", "Prerequisites Thief Dedication, a skill of more than"), (67, string.Empty, "8 + your modifier"), (60, string.Empty, "You gain a lock."))
                    : hanging
                    ? Lines("body", 710, (60, "Prerequisites", "Prerequisites Thief Dedication and a long"), (69, string.Empty, "second line"), (60, string.Empty, "You gain a lock."))
                    : Lines("body", 710, (60, "Prerequisites", "Prerequisites Thief Dedication"), (60, string.Empty, "You gain a lock.")),
            });
        }

        thief.AddRange(new[]
        {
            Plain("section", 700, "Mason"),
            Plain("rules", 690, "Builders of walls."),
            Plain("entry-header", 680, "Multiclass Mason Persons"),
            Plain("rules", 670, "The mason guild teaches you to build."),
            Plain("block-header", 660, "MASON DEDICATION FEAT 2"),
            Plain("tag", 650, "ARCHETYPE DEDICATION MULTICLASS"),
            Lines("body", 640, (60, "Prerequisites", "Prerequisites Strength +2"), (60, string.Empty, "You lay a brick.")),
        });
        var all = new List<Block>
        {
            Plain("section", 900, "Guilds"),
            Plain("rules", 890, "Guilds are groups."),
            Plain("entry-header", 880, "Rules of Guilds"),
            Plain("rules", 870, "A guild trains you."),
            Plain("block-header", 860, "Dues"),
            Plain("rules", 850, "A guild asks for dues."),
        };
        all.AddRange(thief);
        return all;
    }

    private static List<SpineNode> GuildsSpine() => new()
    {
        Node(0, "Chapter 9: Makers", "Chapter 9: Makers"),
        Node(1, "Guilds", "Chapter 9: Makers", "Guilds"),
        Node(2, "Rules of Guilds", "Chapter 9: Makers", "Guilds", "Rules of Guilds"),
        Node(3, "Dues", "Chapter 9: Makers", "Guilds", "Rules of Guilds", "Dues"),
        Node(2, "Thief", "Chapter 9: Makers", "Guilds", "Thief"),
        Node(3, "multiclass thief persons", "Chapter 9: Makers", "Guilds", "Thief", "multiclass thief persons"),
        Node(2, "Mason", "Chapter 9: Makers", "Guilds", "Mason"),
        Node(3, "Multiclass Mason Persons", "Chapter 9: Makers", "Guilds", "Mason", "Multiclass Mason Persons"),
    };

    private const string GuildsRow = """
        rows:
          - { chapter: makers, book: pc, parser: archetypes, target: archetype, folder: archetypes, category: Classes, approved: true, columns: 1, paths: [["Chapter 9: Makers", "Guilds"]] }
        """;

    [Fact]
    public void AnArchetypeIsASectionWithATextAboutMulticlassingAsIt_AndFeatsThatNameIt_TheSentenceUnderItsTitleIsReadPast()
    {
        var parsed = Parse(GuildsRow, GuildsSpine(), Guilds()).Parsed;

        Assert.Equal(new[] { "mason", "thief" }, parsed.Where(e => e.Type == "archetype").Select(e => e.Id));
        Assert.Equal("The thief guild teaches you to open things.", parsed.Single(e => e.Id == "thief").Render());
        Assert.Equal(new[] { "dues", "guilds", "rules-of-guilds" }, parsed.Where(e => e.Type == "rule").Select(e => e.Id));
        Assert.Equal("pc > Chapter 9: Makers > Guilds > Dues", parsed.Single(e => e.Id == "dues").Source);
        // The sentence under a section's title is flavor: read past, and a flavor block of the denylist.
        Assert.DoesNotContain(parsed, e => e.Render().Contains("open locks", StringComparison.Ordinal));
        Assert.Equal(new[] { "A guild of those who open locks.", "Builders of walls." }, EntriesStage.ArchetypeFlavor(Guilds().Skip(1).ToList()).Select(b => b.Lines.Single()));

        var feats = parsed.Where(e => e.Type == "feat").OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "basic-locks", "mason-dedication", "thief-dedication" }, feats.Select(f => f.Id));
        Assert.Equal(new[] { ("basic-locks", 4, "thief"), ("mason-dedication", 2, "mason"), ("thief-dedication", 2, "thief") }, feats.Select(f => (f.Id, f.Level!.Value, f.ArchetypeOwner!.Id)));
        Assert.Equal(new[] { "archetype", "dedication", "multiclass" }, feats.Single(f => f.Id == "thief-dedication").Traits);
        Assert.Equal("pc > Chapter 9: Makers > Guilds > Thief", feats.Single(f => f.Id == "thief-dedication").Source);

        // The lines of a feat set around art start a few points apart: a line a point or two in from the label's is the next paragraph, and the label's paragraph is its prerequisites alone.
        var dedication = feats.Single(f => f.Id == "thief-dedication");
        Assert.Equal("Dexterity +2", dedication.Extracted.Single(x => x.Label == "prerequisites").Text);
        Assert.Equal("You pick locks. You gain a lock feat to open the door of your choice.", dedication.Render());
        // A label that wraps with a hanging indent (nine points in) is still one paragraph.
        Assert.Equal("Thief Dedication and a long second line", feats.Single(f => f.Id == "basic-locks").Extracted.Single(x => x.Label == "prerequisites").Text);
    }

    [Fact]
    public void AnArchetypesCounts_AreTakenFromTheDocument_AndEveryGateFailsLoud()
    {
        var spine = GuildsSpine();
        var (_, blocks, ranges, parsed) = Parse(GuildsRow, spine, Guilds());
        var counts = ImportPipeline.CheckArchetypeCounts(spine, blocks, ranges, parsed)!;

        // 3 children (a rules section and 2 archetypes), each archetype's feat headers = trait lines = level markers = feats, and exactly one dedication feat.
        Assert.Equal((3, 3, 3), (counts.Bookmarks, counts.Headings, counts.Rules));
        Assert.Equal(new[] { ("Thief", 2, 2, 2, 2, 1), ("Mason", 1, 1, 1, 1, 1) }, counts.Archetypes.Select(a => (a.Name, a.FeatHeaders, a.TraitLines, a.Markers, a.Feats, a.Dedications)));

        // A second dedication feat, an archetype with no feats, and a bookmark that names another section fail the run.
        var twice = Parse(GuildsRow, GuildsSpine(), Guilds(secondDedication: true));
        Assert.Throws<DegreesException>(() => ImportPipeline.CheckArchetypeCounts(twice.Spine, twice.Blocks, twice.Ranges, twice.Parsed));
        Assert.Throws<DegreesException>(() => Parse(GuildsRow, GuildsSpine(), Guilds(noFeats: true)));
        var renamed = GuildsSpine().Select(n => n.Title == "Mason" ? n with { Title = "Plumber" } : n).ToList();
        var (_, b2, r2, p2) = Parse(GuildsRow, GuildsSpine(), Guilds());
        Assert.Throws<DegreesException>(() => ImportPipeline.CheckArchetypeCounts(renamed, b2, r2, p2));
    }

    [Fact]
    public void APrerequisitesLineThatEndsOnAConnector_GoesOnWithALineThatOpensWithANumber_AndALabelEndingOnANumberDoesNot_AndABulletIsADash()
    {
        var feats = Parse(GuildsRow, GuildsSpine(), Guilds(formula: true, bullets: true)).Parsed.Where(e => e.Type == "feat").ToList();

        Assert.Equal("Thief Dedication, a skill of more than 8 + your modifier", feats.Single(f => f.Id == "basic-locks").Extracted.Single(x => x.Label == "prerequisites").Text);
        Assert.Equal("You gain a lock.", feats.Single(f => f.Id == "basic-locks").Render());
        // The dedication shape: a label that ends on a number, then a sentence: the paragraph stays its own.
        Assert.Equal("Dexterity +2", feats.Single(f => f.Id == "thief-dedication").Extracted.Single(x => x.Label == "prerequisites").Text);
        // A list of items with no bold name is a list of dashes in an archetype's text too.
        Assert.Equal("The thief guild teaches you to open things.\n- Mason thieves climb.\n- Others hide.", Parse(GuildsRow, GuildsSpine(), Guilds(bullets: true)).Parsed.Single(e => e.Id == "thief").Render());
    }

    [Fact]
    public void WhenNothingShipped_AnArchetypesFeatTakesTheSuffix_AndTheEarliestRowsFeatKeepsTheId()
    {
        var archetype = new ParsedEntry { Id = "thief-x", Name = "Thief", Type = "archetype" };
        var mine = new ParsedEntry { Id = "pick-lock", Type = "feat", Folder = "feats", RowNumber = 3, ArchetypeOwner = archetype };
        var classes = new ParsedEntry { Id = "pick-lock", Type = "feat", Folder = "feats", RowNumber = 2, ClassKey = "tinker" };
        EntriesStage.SuffixClassFeats(new List<ParsedEntry> { mine, classes }, null);
        Assert.Equal(("pick-lock-thief-archetype", "pick-lock"), (mine.Id, classes.Id));

        // Two class feats of one name with nothing shipped are both suffixed, as before; a group of archetype feats alone has none to keep it.
        var one = new ParsedEntry { Id = "x", Type = "feat", Folder = "feats", ClassKey = "a" };
        var two = new ParsedEntry { Id = "x", Type = "feat", Folder = "feats", ClassKey = "b" };
        EntriesStage.SuffixClassFeats(new List<ParsedEntry> { one, two }, null);
        Assert.Equal(("x-a", "x-b"), (one.Id, two.Id));
        var m1 = new ParsedEntry { Id = "y", Type = "feat", Folder = "feats", ArchetypeOwner = archetype };
        var m2 = new ParsedEntry { Id = "y", Type = "feat", Folder = "feats", ArchetypeOwner = new ParsedEntry { Name = "Mason" } };
        EntriesStage.SuffixClassFeats(new List<ParsedEntry> { m1, m2 }, null);
        Assert.Equal(("y-thief-archetype", "y-mason-archetype"), (m1.Id, m2.Id));
    }

    [Fact]
    public void AWordARowTakesOut_IsDeniedInItsOtherNumberToo_UnlessThePackNamedByPackAlreadySaysTheForm()
    {
        Assert.Equal(new[] { "zorg", "zorgs", "zorges" }, ImportPipeline.WordForms("zorg"));
        Assert.Equal(new[] { "zorgs", "zorg" }, ImportPipeline.WordForms("zorgs"));
        Assert.Equal(new[] { "boxes", "box", "boxe" }, ImportPipeline.WordForms("boxes"));

        var blocks = Book(new List<Block> { Plain("body", 100, "some plain text.") });
        var redacted = (SpanWords: (IReadOnlyList<string>)new[] { "zorgs" }, Names: (IReadOnlyList<string>)Array.Empty<string>());
        var folder = Path.Combine(Path.GetTempPath(), "codex-test-pack-" + Guid.NewGuid().ToString("N"));
        var empty = Path.Combine(Path.GetTempPath(), "codex-test-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "rules"));
        Directory.CreateDirectory(empty);
        try
        {
            File.WriteAllText(Path.Combine(folder, "rules", "rules.yaml"), "- id: \"a\"\n  description: \"A zorg walks.\"\n");
            var none = new HashSet<string>(StringComparer.Ordinal);
            // The pack the run names is the one asked: with a pack that says the singular, only the plural is denied; with a pack that says nothing, both are.
            Assert.Equal(new[] { "zorgs" }, ImportPipeline.DenylistWords(blocks, new List<TakenRange>(), none, 3, null, redacted, folder).Words);
            Assert.Equal(new[] { "zorg", "zorgs" }, ImportPipeline.DenylistWords(blocks, new List<TakenRange>(), none, 3, null, redacted, empty).Words);
        }
        finally
        {
            Directory.Delete(folder, true);
            Directory.Delete(empty, true);
        }
    }

    [Fact]
    public void AnArchetypeOfAClassThatTheBookDoesNotHave_FailsTheRun_AndAFeatNamedLikeAnotherTakesTheArchetypesSlug()
    {
        // The pipeline links each archetype to the class of its name; with no class in the pack there is none to link.
        Assert.Throws<DegreesException>(() => ImportPipeline.Run(Book(Guilds()), GuildsSpine(), TakeList.Parse(GuildsRow).Rows, RedactionTable.Parse("normalization: 2\nrows: []\n"), "pc"));

        // Two feats of one id: the one that shipped keeps it, and the other takes the slug of its archetype and the word archetype.
        var one = new ParsedEntry { Id = "basic-locks", Type = "feat", Folder = "feats", RowNumber = 2, ArchetypeOwner = new ParsedEntry { Name = "Thief" } };
        var other = new ParsedEntry { Id = "basic-locks", Type = "feat", Folder = "feats", RowNumber = 1 };
        EntriesStage.SuffixClassFeats(new List<ParsedEntry> { one, other }, new Dictionary<string, string> { ["basic-locks"] = "feats" });
        Assert.Equal(("basic-locks-thief-archetype", "basic-locks"), (one.Id, other.Id));
    }
}
