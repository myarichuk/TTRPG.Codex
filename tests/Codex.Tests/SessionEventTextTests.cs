using System.Text.RegularExpressions;
using Codex.Localization;
using Codex.Persistence;

namespace Codex.Tests;

public class SessionEventTextTests
{
    private static SessionEvent Damage() => SessionEvent.From("Damage",
        new[]
        {
            EventMessage.Of("Damage.TookTyped", "Aria", 7, "fire", "goblin"),
            EventMessage.Of("Damage.Absorbed", 3),
            EventMessage.Of("Damage.Down", "Aria")
        },
        new[] { "a1" },
        new[] { EventMessage.Of("Damage.PlayerDown", "Aria") });

    [Fact]
    public void An_event_is_read_in_the_readers_language_and_stays_English_in_the_stored_description()
    {
        var evt = Damage();

        Assert.Equal("Aria took 7 fire damage from goblin. 3 absorbed by temporary hit points. Aria dropped to 0 and is down.", evt.Description);
        Assert.Equal(evt.Description, evt.Text(Localizer.For("en")));
        Assert.Contains("Aria", evt.Text(Localizer.For("ru")));
        Assert.DoesNotContain("took", evt.Text(Localizer.For("ru")));
        Assert.DoesNotContain("took", evt.Text(Localizer.For("he")));
        Assert.Matches("[֐-׿]", evt.Text(Localizer.For("he")));
    }

    [Fact]
    public void A_player_who_may_not_see_the_numbers_reads_the_player_wording_in_their_language()
    {
        var shown = SessionEventRedactor.ForPlayers(new[] { Damage() },
            new[] { new ActorDocument { Id = "a1", Name = "Aria", Visibility = ActorVisibility.Known } }).Single();

        Assert.Equal("Aria took damage and went down.", shown.Description);
        Assert.Matches("[Ѐ-ӿ]", shown.Text(Localizer.For("ru")));
        Assert.DoesNotContain("7", shown.Text(Localizer.For("ru")));
    }

    [Fact]
    public void An_event_logged_before_messages_existed_shows_its_stored_text()
    {
        var old = new SessionEvent { Type = "Damage", Description = "Bram took 4 damage." };

        Assert.Equal("Bram took 4 damage.", old.Text(Localizer.For("he")));
    }

    [Fact]
    public void Nested_messages_are_listed_inside_the_sentence()
    {
        var rest = EventMessage.Of("Rest.TookWith", "Aria", "long rest")
            .With(new[] { EventMessage.Of("Rest.Slots"), EventMessage.Of("Rest.HitDice", 2) });

        Assert.Equal("Aria took a long rest: slots refilled, 2 hit dice regained.", rest.Render(Localizer.For("en")));
    }

    [Fact]
    public void Every_event_key_used_in_code_exists_in_all_three_languages()
    {
        var en = Localizer.Keys("en");
        Assert.Contains("Event.Damage.Took", en);
        Assert.Equal(en.Where(k => k.StartsWith("Event.")).OrderBy(k => k), Localizer.Keys("ru").Where(k => k.StartsWith("Event.")).OrderBy(k => k));
        Assert.Equal(en.Where(k => k.StartsWith("Event.")).OrderBy(k => k), Localizer.Keys("he").Where(k => k.StartsWith("Event.")).OrderBy(k => k));
    }

    [Fact]
    public void Every_event_key_written_in_the_runtime_exists()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "TTRPG.Codex.sln")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var defined = Localizer.Keys("en").ToHashSet();
        var keys = new Regex(@"""((?:Damage|Healing|TempHp|DeathSave|Stabilized|Exhaustion|HitDice|Status|Turn|Initiative|Encounter|Actor|Sheet|Roll|Resource|Rest|Note)\.[A-Za-z.]+)""");
        var missing = new List<string>();
        foreach (var file in new[] { "Runtime/RuntimeCommands.cs", "Runtime/TableRules.cs" })
            foreach (Match m in keys.Matches(File.ReadAllText(Path.Combine(root!, "src/Codex.Persistence", file))))
                if (!defined.Contains("Event." + m.Groups[1].Value))
                    missing.Add(m.Groups[1].Value);

        Assert.Empty(missing.Distinct());
        // The note verbs are joined from a verb and an optional "Secret", so check each combination.
        foreach (var verb in new[] { "Added", "Corrected", "Removed", "Unchanged", "InPlace" })
            foreach (var suffix in new[] { "", "Secret" })
                Assert.Contains($"Event.Note.{verb}{suffix}", defined);
    }
}
