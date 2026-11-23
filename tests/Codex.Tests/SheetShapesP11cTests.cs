using Codex.Core.Rules;
using Codex.Core.Systems;
using Codex.Plugin.Abstractions;

namespace Codex.Tests;

/// <summary>
/// P11c of docs/COMPANIONS_DESIGN.md, the engine shapes on an invented system: the generic sheet's members widget, a pick list's <c>changesOn</c>
/// (guidance a row choice's own <c>changesOn</c> gives, here for a whole list) and a derived number the sheet leaves out (<c>hidden</c>).
/// </summary>
public class SheetShapesP11cTests
{
    private const string Text = """
        id: shapes
        name: Shapes
        moments:
          - { key: prep, label: Preparations }
        entityTypes:
          - { type: gadget, label: Gadget }
        sheetRules:
          derived:
            - { key: shown,  label: Shown,  formula: "3", signed: false }
            - { key: secret, label: Secret, formula: "4", signed: false, hidden: true }
        sheetKinds:
          - kind: hero
            label: Hero
            values:
              - { key: grit, label: Grit, formula: "2" }
            lists:
              - { key: kit, label: Kit, type: gadget, changesOn: [prep] }
              - { key: spare, label: Spare, type: gadget }
          - kind: pet
            label: Pet
            links:
              - { key: owner, kind: hero, label: Owner }
            values:
              - { key: bite, label: Bite, formula: "1" }
          - kind: rock
            label: Rock
            values:
              - { key: weight, label: Weight, formula: "9" }
        """;

    private static SystemDefinition System(string? text = null) => SystemDefinitionLoader.Parse(text ?? Text);

    private static SheetRuleEngine Engine(SystemDefinition system) => new(system, new AttributeBoostTests.Resolver());

    private static Dictionary<string, object> Sheet(string kind) => new() { ["kind"] = kind };

    // ---- Members on the generic sheet ----

    [Fact]
    public void TheGenericSheet_EndsWithMembers_OnlyForAKindThatOtherKindsLinkTo()
    {
        var system = System();

        Assert.Equal("members", Assert.Single(SheetLayout.For(system, Sheet("hero"))).Widgets[^1].Widget);
        Assert.DoesNotContain(Assert.Single(SheetLayout.For(system, Sheet("pet"))).Widgets, w => w.Widget == "members"); // nothing links to a pet
        Assert.DoesNotContain(Assert.Single(SheetLayout.For(system, Sheet("rock"))).Widgets, w => w.Widget == "members");
        Assert.Empty(SheetLayout.Problems(system));
    }

    [Fact]
    public void WithoutALinkAnywhere_TheGenericSheetIsAsItWasAndTheKindOnlyOverloadStillWorks()
    {
        var unlinked = System(Text.Replace("kind: hero, label: Owner", "kind: rock, label: Owner"));
        Assert.DoesNotContain(Assert.Single(SheetLayout.For(unlinked, Sheet("hero"))).Widgets, w => w.Widget == "members");
        Assert.Contains(Assert.Single(SheetLayout.For(unlinked, Sheet("rock"))).Widgets, w => w.Widget == "members"); // the kind that is linked to now

        // The old one-argument call is the sheet of the kind alone.
        Assert.DoesNotContain(Assert.Single(SheetLayout.Generic(System().SheetKinds[0])).Widgets, w => w.Widget == "members");
    }

    [Fact]
    public void ASystemWithNoSheetKinds_IsGivenTheBuiltInSheetAsBefore()
    {
        var plain = System("id: bare\nname: Bare\n");
        Assert.Same(SheetLayout.Builtin, SheetLayout.For(plain, new Dictionary<string, object>()));
    }

    // ---- changesOn on a pick list ----

    [Fact]
    public void AListsChangesOn_IsReadByTheLoader_AndAMomentTheSystemLacksIsRefused()
    {
        Assert.Equal(new[] { "prep" }, System().SheetKinds[0].Lists[0].ChangesOn);
        Assert.Empty(System().SheetKinds[0].Lists[1].ChangesOn);

        var typo = Assert.Throws<InvalidDataException>(() => System(Text.Replace("changesOn: [prep]", "changesOn: [prp]")));
        Assert.Contains("sheetKinds.hero.lists.kit: changesOn 'prp' is not one of the system's moments", typo.Message);
        // A rest, initiative and dawn are moments too.
        Assert.NotNull(System(Text.Replace("changesOn: [prep]", "changesOn: [dawn, initiative]")));
    }

    [Fact]
    public void AListKeyThatStartsLikeARowChoicesKey_IsRefused_SoTheTwoKindsOfChangesOnKeyNeverCollide()
    {
        var clash = Assert.Throws<InvalidDataException>(() => System(Text.Replace("key: spare,", "key: choice__spare,")));
        Assert.Contains("sheetKinds.hero.lists.choice__spare: a list key cannot start with 'choice__'", clash.Message);
    }

    [Fact]
    public void Recompute_WritesTheListsMoment_UnderTheListKey_AndTakesItOffWhenTheListNoLongerHasOne()
    {
        var sheet = Sheet("hero");
        Engine(System()).Recompute(sheet);

        Assert.Equal("prep", sheet["changes_on__kit"]);
        Assert.Equal("Kit", sheet["changes_on__kit_Label"]);
        Assert.DoesNotContain("changes_on__spare", sheet.Keys);
        Assert.Equal(new[] { ("kit", "Kit") }, SheetRuleEngine.ChoicesToChangeAfter(sheet, "prep"));
        Assert.Empty(SheetRuleEngine.ChoicesToChangeAfter(sheet, "dawn"));

        // The moment matters to a hero, and to no kind that has no such list.
        var system = System();
        Assert.True(SystemMoments.Matters(system, sheet, "prep"));
        Assert.False(SystemMoments.Matters(system, Sheet("rock"), "prep"));

        Engine(System(Text.Replace(", changesOn: [prep]", ""))).Recompute(sheet);
        Assert.DoesNotContain(sheet.Keys, k => k.StartsWith(SheetRuleEngine.ChangesOnPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void TheListsPicks_AreNeverTouchedByTheMoment_SoNothingIsLocked()
    {
        var sheet = Sheet("hero");
        sheet["kit"] = new List<object> { "a", "b" };
        Engine(System()).Recompute(sheet);
        Assert.Equal(new List<object> { "a", "b" }, sheet["kit"]);

        // The list can be edited at any time: the same picks, changed, recompute without a word.
        sheet["kit"] = new List<object> { "c" };
        var evaluation = Engine(System()).Recompute(sheet);
        Assert.Equal(new List<object> { "c" }, sheet["kit"]);
        Assert.DoesNotContain(evaluation.Violations, v => v.Key == "kit");
    }

    // ---- hidden on a derived number ----

    [Fact]
    public void AHiddenDerivedNumber_IsLoadedAsHidden_AndStillWorkedOut()
    {
        var system = System();
        Assert.True(system.SheetRules!.Derived.Single(d => d.Key == "secret").Hidden);
        Assert.False(system.SheetRules.Derived.Single(d => d.Key == "shown").Hidden);

        var evaluation = Engine(system).Recompute(Sheet("hero"));
        Assert.Equal(4L, Convert.ToInt64(evaluation.Derived["secret"], global::System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(3L, Convert.ToInt64(evaluation.Derived["shown"], global::System.Globalization.CultureInfo.InvariantCulture));
    }
}
