using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Persistence.Runtime;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>A sheet kind's tracks at a live table: a sheet edit merges the numeric ones by delta (<see cref="SheetCounters"/>), and an
/// intent for a track the sheet's kind does not declare is left, not written (<see cref="SheetIntents"/>).</summary>
public class KindTrackCountersTests
{
    private static readonly SystemDefinition System = SystemDefinitionLoader.Parse("""
        id: kind-tracks
        name: Kind tracks
        sheetKinds:
          - kind: scoundrel
            tracks:
              - { key: stress, max: 9 }
              - { key: armor, max: 3, display: boxes }
              - { key: coin, display: number }
              - { key: wounds, display: slots, holds: { type: text }, max: 2 }
              - { key: luck, max: 5, min: -2 }
          - kind: crew
            tracks:
              - { key: rep, max: 12 }
        events:
          downtime:
            - { intents: [{ set: { track: armor, to: 0 } }, { set: { track: rep, to: 0 } }] }
        """);

    private static Dictionary<string, object> Sheet(string kind, params (string Key, object Value)[] more)
    {
        var sheet = new Dictionary<string, object> { ["kind"] = kind };
        foreach (var (key, value) in more) sheet[key] = value;
        return sheet;
    }

    [Fact]
    public void Of_ListsTheKindsNumericTracks_WithTheirMax_NotSlotTracksNorOnesThatCanGoBelowZero()
    {
        var counters = SheetCounters.Of(System, Array.Empty<string>(), Sheet("scoundrel")).ToList();

        Assert.Equal(new (string, string?)[] { ("stress", "stress_Max"), ("armor", "armor_Max"), ("coin", null) }, counters);
        Assert.Equal(new (string, string?)[] { ("rep", "rep_Max") }, SheetCounters.Of(System, Array.Empty<string>(), Sheet("crew")).ToList());
    }

    [Fact]
    public void Of_WithoutASheet_OrForASystemWithNoKinds_IsAsItWas()
    {
        Assert.Empty(SheetCounters.Of(System, Array.Empty<string>()));
        Assert.Equal(new (string, string?)[] { ("HP", "HP_Max") }, SheetCounters.Of(null, Array.Empty<string>()).ToList());
    }

    [Fact]
    public void ASheetEditToAKindTrack_MergesByHowMuchItMoved_NotByWhatTheEditorSaw()
    {
        // The sheet opened with stress 2; at the table it rose to 5; the editor marked one more on the sheet (3): the live 5 moves to 6.
        var baseline = Sheet("scoundrel", ("stress", 2L), ("stress_Max", 9L));
        var edited = Sheet("scoundrel", ("stress", 3L), ("stress_Max", 9L));
        var live = Sheet("scoundrel", ("stress", 5L), ("stress_Max", 9L));
        var merged = new Dictionary<string, object>(edited);

        SheetCounters.MergeByDelta(merged, edited, baseline, live, System);

        Assert.Equal(6L, merged["stress"]);
    }

    [Fact]
    public void AKindTrackTheEditorLeftAlone_KeepsItsLiveValue_AndAMergeStaysWithinTheMax()
    {
        var baseline = Sheet("scoundrel", ("stress", 2L), ("stress_Max", 9L), ("armor", 1L), ("armor_Max", 3L));
        var edited = Sheet("scoundrel", ("stress", 2L), ("stress_Max", 9L), ("armor", 3L), ("armor_Max", 3L));
        var live = Sheet("scoundrel", ("stress", 7L), ("stress_Max", 9L), ("armor", 2L), ("armor_Max", 3L));
        var merged = new Dictionary<string, object>(edited);

        SheetCounters.MergeByDelta(merged, edited, baseline, live, System);

        Assert.Equal(7L, merged["stress"]);
        Assert.Equal(3L, merged["armor"]); // 2 + (3 - 1) = 4, held at the maximum
    }

    [Theory]
    [InlineData("set")]
    [InlineData("adjust")]
    [InlineData("mark")]
    [InlineData("clear")]
    public void AnIntentForATrackTheKindLacks_IsLeft_AndWritesNothing(string kind)
    {
        var sheet = Sheet("scoundrel");
        var fields = kind is "set" ? new[] { ("track", (object?)"rep"), ("to", 0d) } : new[] { ("track", (object?)"rep"), ("by", 1d) };

        var result = SheetIntents.Apply(System, NullEntryResolver.Instance, sheet, new[] { new SheetIntent(kind, null, fields.ToDictionary(f => f.Item1, f => f.Item2, StringComparer.Ordinal)) });

        Assert.DoesNotContain("rep", sheet.Keys);
        Assert.Single(result.Left);
        Assert.Empty(result.Changed);
    }

    [Fact]
    public void AHandlerLineThatNamesACrewTrackAndAScoundrelTrack_ChangesOnlyWhatEachSheetHas()
    {
        var scoundrel = Sheet("scoundrel", ("armor", 2L));
        var crew = Sheet("crew", ("rep", 4L));

        SheetEvents.Raise(System, NullEntryResolver.Instance, scoundrel, SheetEvent.Of("downtime"));
        SheetEvents.Raise(System, NullEntryResolver.Instance, crew, SheetEvent.Of("downtime"));

        Assert.Equal(0L, scoundrel["armor"]);
        Assert.DoesNotContain("rep", scoundrel.Keys);
        Assert.Equal(0L, crew["rep"]);
        Assert.DoesNotContain("armor", crew.Keys);
    }

    [Fact]
    public void ASystemWithNoSheetKinds_StillWritesAnyTrack()
    {
        var plain = SystemDefinitionLoader.Parse("id: plain\nname: Plain\n");
        var sheet = new Dictionary<string, object>();

        SheetIntents.Apply(plain, NullEntryResolver.Instance, sheet, new[] { new SheetIntent("set", null, new Dictionary<string, object?> { ["track"] = "HP", ["to"] = 4d }) });

        Assert.Equal(4L, sheet["HP"]);
    }
}
