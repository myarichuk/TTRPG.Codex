using System.Globalization;
using System.Text.RegularExpressions;
using Codex.Core;
using Codex.Core.Rules;
using Codex.Plugin.Abstractions;
using Codex.Plugin.Abstractions.Dice;

namespace Codex.Persistence.Runtime;

/// <summary>What is rolled or read. Attribute: an attribute's check on its own. Passive: a sheet's passive score, no die.</summary>
public enum CheckKind { Skill, Save, Attribute, Passive, ArmorClass }

/// <summary>A check the system knows: its kind and its key on the sheet (a skill key, an attribute key, a passive key; empty for AC).</summary>
public sealed record CheckSpec(CheckKind Kind, string Key, string Label);

public enum CheckBonusSource { Typed, Evaluated, Printed }

/// <summary>The number added to the die (or the passive score, or the AC) and where it came from; Parts are the feature and condition
/// bonuses that reached it (<see cref="SkillScore.Bonuses"/>, <see cref="SheetEvaluation.CheckSources"/>, the opt-ins ticked), for the log's "what counted".</summary>
public sealed record CheckBonus(int Value, CheckBonusSource Source, IReadOnlyList<RollBonus> Parts);

public sealed record CheckRequest
{
    public required string ActorId { get; init; }

    /// <summary>As content writes it: "Perception", "perception", "Dexterity", "dex", "Thieves' Tools".</summary>
    public required string Check { get; init; }

    /// <summary>Forces the reading: a hazard's effect.save is a Save of that attribute, never an attribute check. Passive with a skill's name reads that
    /// skill's passive (the same as <see cref="Passive"/>).</summary>
    public CheckKind? Kind { get; init; }

    public int? Dc { get; init; }

    /// <summary>The GM's number. Beats everything; required where the check is unresolved or the actor is printed. For a passive or an AC read it is the score itself.</summary>
    public int? Bonus { get; init; }

    /// <summary>Compare the passive score instead of rolling (a notice route, when notice.passive holds and the route allows it; see <see cref="ActorChecks.HasPassive"/>).</summary>
    public bool Passive { get; init; }

    /// <summary>The dice to roll in place of dice.check, for an edge the data has no word for (the GM picks "2d20kh1"). Null: dice.check.</summary>
    public string? Dice { get; init; }

    /// <summary>The ids of the tick boxes the roller ticked (<see cref="RollOptIn.Id"/>); a box not named is not counted.</summary>
    public IReadOnlyCollection<string> OptIns { get; init; } = Array.Empty<string>();

    /// <summary>GM only: the roll log entry is secret and the result is not for players. Default true for anything a hazard asks.</summary>
    public bool Secret { get; init; } = true;
}

/// <summary>The grade: "success" / "failure" today, the named roll's outcome name later. Mark as <see cref="RollLogEntry.Mark"/>. The keys are a content
/// contract (a route's <c>triggersOn</c> lists them): they are not renamed.</summary>
public sealed record CheckOutcome(string Key, bool Passed, string? Mark = null);

public enum CheckFailure { None, UnknownActor, Unresolved, NeedsBonus, BadDice }

public sealed record CheckResult(
    string ActorId,
    CheckSpec? Spec,
    CheckBonus? Bonus,
    int? Natural,          // the check die's face; null for a passive or AC read
    int Total,             // die + bonus, or the passive score, or the AC
    int? Dc,
    CheckOutcome? Outcome, // null when there was no DC to grade against
    RollLogEntry? Logged,  // the entry recorded, null for a passive, an AC read or a failure
    CheckFailure Failed);

/// <summary>The pure half: no dice, no runtime. Usable by the Web panel on a snapshot.</summary>
public static class ActorChecks
{
    public const string Success = "success";
    public const string Failure = "failure";

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static AttributeDefinition? AttributeNamed(SystemDefinition system, string check) =>
        system.Attributes.FirstOrDefault(a => Same(a.Key, check) || Same(a.Label, check) || Same(a.CanonicalLabel, check)
            || Same(a.Abbreviation, check) || Same(a.CanonicalAbbreviation, check));

    /// <summary>Finds the check by key or label, ignoring case: a skill, then (only when <paramref name="kind"/> is Save) a save, then an attribute, then a
    /// passive. A given kind looks nowhere else. Null: the system does not know it (Thieves' Tools, Spellcasting).</summary>
    public static CheckSpec? Resolve(SystemDefinition system, string check, CheckKind? kind = null)
    {
        if (string.IsNullOrWhiteSpace(check)) return null;
        var rules = system.SheetRules?.Skills;
        if (kind is null or CheckKind.Skill && rules != null)
        {
            var skill = rules!.List.FirstOrDefault(s => Same(s.Key, check) || Same(s.Label, check) || Same(s.CanonicalLabel, check));
            if (skill != null) return new CheckSpec(CheckKind.Skill, skill.Key, skill.Label);
        }

        if (kind == CheckKind.Save && AttributeNamed(system, check) is { } saved) return new CheckSpec(CheckKind.Save, saved.Key, saved.Label);
        if (kind is null or CheckKind.Attribute && AttributeNamed(system, check) is { } attribute) return new CheckSpec(CheckKind.Attribute, attribute.Key, attribute.Label);

        if (kind is null or CheckKind.Passive && rules != null)
        {
            var passive = rules!.Passives.FirstOrDefault(p => Same(p.Key, check) || Same(p.Label, check));
            // Kind Passive with a skill's name ("Perception") is that skill's passive; a skill with none is not found.
            if (passive == null && kind == CheckKind.Passive)
            {
                var named = rules.List.FirstOrDefault(s => Same(s.Key, check) || Same(s.Label, check) || Same(s.CanonicalLabel, check));
                passive = named == null ? null : rules.Passives.FirstOrDefault(p => Same(p.Skill, named.Key));
            }

            if (passive != null)
            {
                var skill = rules.List.FirstOrDefault(s => Same(s.Key, passive.Skill));
                return new CheckSpec(CheckKind.Passive, passive.Key, skill?.Label ?? passive.Label);
            }
        }

        return kind == CheckKind.ArmorClass && system.SheetRules?.ArmorClass != null ? new CheckSpec(CheckKind.ArmorClass, string.Empty, "Armor Class") : null;
    }

    /// <summary>Whether the sheet has a passive score for the check (Perception: yes; Investigation, an attribute, a tool: no), so a caller asks for a
    /// passive only where there is one and rolls otherwise.</summary>
    public static bool HasPassive(SystemDefinition system, CheckSpec spec) => PassiveOf(system, spec) != null;

    /// <summary>The passive rule that scores the check: the spec's own for a passive, the one over the skill for a skill.</summary>
    public static PassiveRule? PassiveOf(SystemDefinition system, CheckSpec spec)
    {
        var passives = system.SheetRules?.Skills?.Passives;
        if (passives == null) return null;
        return spec.Kind switch
        {
            CheckKind.Passive => passives.FirstOrDefault(p => Same(p.Key, spec.Key)),
            CheckKind.Skill => passives.FirstOrDefault(p => Same(p.Skill, spec.Key)),
            _ => null
        };
    }

    /// <summary>Printed iff the sheet was seeded from a stat block (the printed method stamp <see cref="ActorSheetSync.SeedFromBlueprint"/> sets, homebrew
    /// creatures included, whose <c>BlueprintId</c> is null) and the evaluation has no class levels. A cast NPC with a full sheet is evaluated.</summary>
    public static bool IsPrinted(SystemDefinition system, ActorDocument actor, SheetEvaluation evaluation) =>
        string.Equals(YamlValues.GetString(actor.Sheet, SheetRuleEngine.MethodKey), SheetRuleEngine.PrintedMethod, StringComparison.OrdinalIgnoreCase)
        && evaluation.Classes.Count == 0;

    /// <summary>Null: no source has it, the caller must supply a typed bonus. Evaluated from the sheet, or (AC only, and every number of a printed
    /// creature's skills and saves is left out) printed. Never typed: that is the request's.</summary>
    /// <param name="liveSheet">The sheet as the table has it (<c>LiveSheetOf</c>), where a printed or manual armor class is read; null: the stored one.</param>
    public static CheckBonus? BonusOf(SystemDefinition system, ActorDocument actor, SheetEvaluation live, CheckSpec spec, IReadOnlyDictionary<string, object>? liveSheet = null)
    {
        var printed = IsPrinted(system, actor, live);
        if (spec.Kind == CheckKind.ArmorClass) return ArmorClassOf(system, liveSheet ?? actor.Sheet, live, printed);
        // A printed creature's evaluation would come to the bare attribute modifier for a skill or a save, and a passive over it.
        if (printed && spec.Kind is CheckKind.Skill or CheckKind.Save or CheckKind.Passive) return null;

        switch (spec.Kind)
        {
            case CheckKind.Skill:
                return live.Skills.FirstOrDefault(s => Same(s.Key, spec.Key)) is { } skill
                    ? new CheckBonus(skill.Bonus, CheckBonusSource.Evaluated, skill.Bonuses) : null;
            case CheckKind.Save:
                return live.SavingThrows.FirstOrDefault(s => Same(s.Key, spec.Key)) is { } save
                    ? new CheckBonus(save.Bonus, CheckBonusSource.Evaluated, save.Bonuses) : null;
            case CheckKind.Attribute:
                if (!live.AttributeModifiers.TryGetValue(spec.Key, out var modifier)) return null;
                return new CheckBonus(modifier + live.CheckBonuses.GetValueOrDefault(spec.Key), CheckBonusSource.Evaluated,
                    live.CheckSources.GetValueOrDefault(spec.Key) ?? Array.Empty<RollBonus>());
            case CheckKind.Passive:
                return live.Passives.FirstOrDefault(p => Same(p.Key, spec.Key)) is { } passive
                    ? new CheckBonus(passive.Value, CheckBonusSource.Evaluated, Array.Empty<RollBonus>()) : null;
            default:
                return null;
        }
    }

    private static CheckBonus? ArmorClassOf(SystemDefinition system, IReadOnlyDictionary<string, object> sheet, SheetEvaluation live, bool printed)
    {
        // An armor class the rules work out (auto), else the number the sheet carries: a stat block's, or a manual one.
        if (!printed && live.ArmorClass is { Active: true } worked) return new CheckBonus(worked.Total, CheckBonusSource.Evaluated, Array.Empty<RollBonus>());
        return PrintedArmorClass(system, sheet);
    }

    /// <summary>The armor class a sheet carries (a stat block's, or a manual one), read from the live sheet so a status the table put on it counts.</summary>
    public static CheckBonus? PrintedArmorClass(SystemDefinition system, IReadOnlyDictionary<string, object> sheet)
    {
        var key = system.SheetRules?.ArmorClass?.Key;
        return key != null && YamlValues.GetInt(sheet, key) is { } own ? new CheckBonus(own, CheckBonusSource.Printed, Array.Empty<RollBonus>()) : null;
    }

    /// <summary>The prefill for a printed creature: a "Label +N" match of the spec's key or label in the printed text field the system names
    /// (<see cref="ContentTypeDefinition.PrintedSkills"/>, <see cref="ContentTypeDefinition.PrintedSaves"/>). Never used without the GM.</summary>
    public static int? Suggest(SystemDefinition system, ActorDocument actor, CheckSpec spec)
    {
        var names = new List<string?>();
        string? field = null;
        foreach (var type in system.EntityTypes.Where(t => t.ActorBlueprint))
        {
            field = spec.Kind switch { CheckKind.Skill => type.PrintedSkills, CheckKind.Save => type.PrintedSaves, _ => null };
            if (!string.IsNullOrWhiteSpace(field)) break;
        }

        if (string.IsNullOrWhiteSpace(field) || YamlValues.GetString(actor.Sheet, field) is not { Length: > 0 } text) return null;
        if (spec.Kind == CheckKind.Skill)
        {
            var skill = system.SheetRules?.Skills?.List.FirstOrDefault(s => Same(s.Key, spec.Key));
            names.AddRange(new[] { spec.Key, spec.Label, skill?.CanonicalLabel });
        }
        else
        {
            var attribute = system.Attributes.FirstOrDefault(a => Same(a.Key, spec.Key));
            names.AddRange(new[] { spec.Key, spec.Label, attribute?.Abbreviation, attribute?.CanonicalLabel, attribute?.CanonicalAbbreviation });
        }

        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var match = Regex.Match(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(name!.Trim()) + @"\s*(?<n>[+-]\s*[0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success) return int.Parse(match.Groups["n"].Value.Replace(" ", string.Empty), CultureInfo.InvariantCulture);
        }

        return null;
    }

    /// <summary>The default grade: a total at or above the DC passes. Replaced by the named check roll when <c>dice.checkRoll</c> is set (P6);
    /// <paramref name="natural"/> is for that, and is not read here.</summary>
    public static CheckOutcome Grade(int total, int natural, int dc) => total >= dc ? new CheckOutcome(Success, true) : new CheckOutcome(Failure, false);
}

/// <summary>The rolling half, on the command loop only (it reads entities and records rolls). <c>runtime.Checks</c>. It writes no
/// <see cref="SessionEvent"/>: the command that calls it says what happened, with <see cref="EventTerm.GmOnly"/> parts when the check is secret.</summary>
public sealed class CheckService(CampaignRuntime runtime)
{
    public CheckResult Make(CheckRequest request, string rollerUserId)
    {
        var system = runtime.System;
        if (system == null || runtime.GetActorDocument(request.ActorId) is not { } actor) return Fail(request, null, CheckFailure.UnknownActor);

        var spec = ActorChecks.Resolve(system, request.Check, request.Kind);
        if (request.Passive)
        {
            // A passive of a check the sheet has none for (Investigation, a tool) is not read, and nothing is rolled in its place.
            var rule = spec == null ? null : ActorChecks.PassiveOf(system, spec);
            if (spec == null || rule == null) return Fail(request, spec, CheckFailure.Unresolved);
            spec = ActorChecks.Resolve(system, rule.Key, CheckKind.Passive);
        }

        // A check the system does not know is rolled only on the GM's number.
        var known = spec != null;
        spec ??= new CheckSpec(request.Kind ?? CheckKind.Skill, string.Empty, request.Check);
        var reading = spec.Kind is CheckKind.Passive or CheckKind.ArmorClass;

        SheetEvaluation? live = null;
        SheetRuleEngine? engine = null;
        Dictionary<string, object>? liveSheet = null;
        var evaluated = false;

        bool Evaluate()
        {
            if (evaluated) return live != null;
            evaluated = true;
            try
            {
                engine = new SheetRuleEngine(system, runtime.Resolver);
                liveSheet = runtime.LiveSheetOf(actor);
                live = engine.EvaluateLive(liveSheet, runtime.LinkedFor(actor.Sheet));
            }
            catch (Exception ex) when (ex is FormulaException or InvalidOperationException)
            {
                live = null;
            }

            return live != null;
        }

        CheckBonus? bonus = null;
        List<RollBonus> ticked = new();
        var extraDice = new List<string>();
        if (request.Bonus is { } typed)
        {
            bonus = new CheckBonus(typed, CheckBonusSource.Typed, Array.Empty<RollBonus>());
        }
        else if (known)
        {
            if (Evaluate()) bonus = ActorChecks.BonusOf(system, actor, live!, spec, liveSheet);
            // A printed armor class needs no evaluation, so a sheet the rules cannot work out still has one.
            else if (spec.Kind == CheckKind.ArmorClass) bonus = ActorChecks.PrintedArmorClass(system, liveSheet ?? actor.Sheet);
        }

        // What the table adds to a die: ticked boxes and always-on dice (Bane's -1d4 on a save), on a typed number as on an evaluated one.
        // Read inside the same guard as the evaluation, so a formula that cannot be worked out never fails a MakeAll halfway, after rolls are recorded.
        if (bonus is { Source: CheckBonusSource.Typed or CheckBonusSource.Evaluated } && !reading && known && Evaluate())
        {
            try
            {
                var site = SiteOf(engine!, spec);
                var offered = engine!.OptIns(liveSheet!, site, runtime.LinkedFor(actor.Sheet));
                var chosen = offered.Where(o => request.OptIns.Contains(o.Id, StringComparer.Ordinal)).ToList();
                var added = 0;
                var tickedHere = new List<RollBonus>();
                var diceHere = new List<string>();
                foreach (var optIn in chosen)
                {
                    if (int.TryParse(optIn.Amount, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
                    {
                        added += amount;
                        tickedHere.Add(new RollBonus(optIn.Id, optIn.Source, optIn.Label, amount, true));
                    }
                    else
                    {
                        // Dice (Guidance's 1d4) are rolled with the die, not folded into the number.
                        diceHere.Add(optIn.Amount.StartsWith('+') || optIn.Amount.StartsWith('-') ? optIn.Amount : "+" + optIn.Amount);
                        tickedHere.Add(new RollBonus(optIn.Id, optIn.Source, optIn.Label, 0, true));
                    }
                }

                diceHere.AddRange(engine.Bonuses(liveSheet!, site, runtime.LinkedFor(actor.Sheet)).Dice.Select(d => d.Dice));
                ticked.AddRange(tickedHere);
                extraDice.AddRange(diceHere);
                bonus = bonus with { Value = bonus.Value + added, Parts = bonus.Parts.Concat(tickedHere).ToList() };
            }
            catch (Exception ex) when (ex is FormulaException or InvalidOperationException)
            {
                // No tick boxes and no table dice: the number stands on its own.
            }
        }

        if (bonus == null) return Fail(request, spec, CheckFailure.NeedsBonus);

        if (reading)
        {
            // No die: the score is the passive (or the AC), compared to the DC.
            var outcome = request.Dc is { } readDc ? ActorChecks.Grade(bonus.Value, 0, readDc) : null;
            return new CheckResult(request.ActorId, spec, bonus, null, bonus.Value, request.Dc, outcome, null, CheckFailure.None);
        }

        var expression = (request.Dice ?? system.Dice.Check) + bonus.Value.ToString("+#;-#;+0", CultureInfo.InvariantCulture) + string.Concat(extraDice);
        DiceRollResult rolled;
        try
        {
            rolled = runtime.DiceRoller.Roll(expression);
        }
        catch (FormatException)
        {
            return Fail(request, spec, CheckFailure.BadDice);
        }

        int? natural = rolled.CheckDieIndex is { } at && at >= 0 && at < rolled.Kept.Count ? rolled.Kept[at] : null;
        var graded = request.Dc is { } dc ? ActorChecks.Grade(rolled.Total, natural ?? 0, dc) : null;
        var label = ticked.Count == 0 ? spec.Label : RollBonusLabel.Of(spec.Label, ticked, false);
        var entry = new RollLogEntry
        {
            ActorId = request.ActorId,
            RollerUserId = rollerUserId,
            Expression = expression,
            Result = rolled.ToString(),
            Total = rolled.Total,
            IsSecret = request.Secret,
            Label = label,
            Name = spec.Label,
            Bonuses = ticked.Select(b => new RollLogBonus { Source = b.Source, Label = b.Label, Amount = b.Amount }).ToList(),
            Kept = rolled.Kept.ToList(),
            Dropped = rolled.Dropped.ToList(),
            Modifier = bonus.Value,
            CheckDieIndex = rolled.CheckDieIndex,
            Outcome = graded?.Key,
            Mark = graded?.Mark
        };
        runtime.RecordRoll(entry);
        return new CheckResult(request.ActorId, spec, bonus, natural, rolled.Total, request.Dc, graded, entry, CheckFailure.None);
    }

    /// <summary>The roll site the check's tick boxes and always-on dice are read at.</summary>
    private static RollSite SiteOf(SheetRuleEngine engine, CheckSpec spec) => spec.Kind switch
    {
        CheckKind.Skill => engine.SkillSite(spec.Key),
        CheckKind.Save => engine.SaveSite(spec.Key),
        _ => engine.CheckSite(spec.Key)
    };

    private static CheckResult Fail(CheckRequest request, CheckSpec? spec, CheckFailure failure) =>
        new(request.ActorId, spec, null, null, 0, request.Dc, null, null, failure);

    /// <summary>The checks in order, each made as <see cref="Make"/> does.</summary>
    public IReadOnlyList<CheckResult> MakeAll(IEnumerable<CheckRequest> requests, string rollerUserId) =>
        requests.Select(r => Make(r, rollerUserId)).ToList();

    /// <summary>A target's AC for an attack: evaluated, else printed; <see cref="CheckFailure.NeedsBonus"/> when neither (the GM types it).</summary>
    public CheckResult ArmorClassOf(string actorId)
    {
        var request = new CheckRequest { ActorId = actorId, Check = "Armor Class", Kind = CheckKind.ArmorClass };
        return Make(request, string.Empty);
    }
}
