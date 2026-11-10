using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

// Roll definitions (docs/SYSTEM_IMPLEMENTATION_OVERHAUL.md, section 3, "Rolls"), as system.yaml declares them. The engine is
// Codex.Core.Rules.SheetRolls; the vocabulary is in tests/Codex.Tests/Fixtures/reference/README.md.

/// <summary>
/// A named roll: how its dice are built (<see cref="Dice"/>, or <see cref="Pool"/> dice of <see cref="Die"/>), what is
/// added, what it is compared against and the named outcome it comes to. A group roll (<see cref="Participants"/>) has no
/// dice of its own: each participant makes another roll and the best result counts.
/// </summary>
public sealed class RollDefinition
{
    /// <summary>Required, unique: the roll's name, as a <c>roll</c> widget, a <c>then</c> line and <c>participants</c> name it.</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The roll's name on its button; the key when unset.</summary>
    public string? Label { get; set; }

    /// <summary>A fixed dice expression: <c>1d20</c>, <c>1d10</c>, <c>3d6</c>.</summary>
    public string? Dice { get; set; }

    /// <summary>A formula for how many dice of <see cref="Die"/>: <c>actions[input.action].rating + input.bonusDice</c>.</summary>
    public string? Pool { get; set; }

    /// <summary>The die a <see cref="Pool"/> rolls: <c>d6</c>.</summary>
    public string? Die { get; set; }

    /// <summary>What a pool of no dice (or fewer) rolls instead. Null: such a pool is a problem with the roll.</summary>
    public ZeroDiceDefinition? ZeroDice { get; set; }

    /// <summary>Which dice count. Null: all of them.</summary>
    public KeepDefinition? Keep { get; set; }

    /// <summary>A die showing <see cref="RerollDefinition.On"/> adds another die.</summary>
    public RerollDefinition? Explode { get; set; }

    /// <summary>A die showing <see cref="RerollDefinition.On"/> takes another die away.</summary>
    public RerollDefinition? Implode { get; set; }

    /// <summary>Formulas added to the dice: values, <c>modifiers('attack')</c>.</summary>
    public List<string> Add { get; set; } = new();

    /// <summary>The parameters the roll takes, which its formulas read by key (<c>target</c>) and as <c>input.key</c>.</summary>
    public List<RollInputDefinition> Inputs { get; set; } = new();

    /// <summary>The first whose <c>when</c> holds is the outcome; one without <c>when</c> always holds.</summary>
    public List<RollOutcomeDefinition> Outcomes { get; set; } = new();

    /// <summary>Inputs copied onto the result for whatever follows (position and effect).</summary>
    public List<string> Carries { get; set; } = new();

    /// <summary>What follows the roll: another roll (a confirmation) or intents (stress to pay).</summary>
    public List<RollFollowUpDefinition> Then { get; set; } = new();

    /// <summary>A group roll: who rolls, and which roll each of them makes.</summary>
    public RollParticipantsDefinition? Participants { get; set; }

    /// <summary>For a group roll, which participant's outcome is the group's: <c>best</c> (the earliest in the participants'
    /// roll's <see cref="Outcomes"/>) or <c>worst</c>.</summary>
    public string? Result { get; set; }
}

/// <summary><c>{ roll: 2, keep: lowest }</c>: the dice a pool of none rolls, and which one counts.</summary>
public sealed class ZeroDiceDefinition
{
    /// <summary>How many dice to roll instead. Default 2.</summary>
    public int Roll { get; set; } = 2;

    /// <summary><c>lowest</c> or <c>highest</c>.</summary>
    public string Keep { get; set; } = "lowest";
}

/// <summary><c>{ highest: 1 }</c> or <c>{ lowest: 1 }</c>.</summary>
public sealed class KeepDefinition
{
    /// <summary>Keep this many of the highest dice.</summary>
    public int? Highest { get; set; }
    /// <summary>Keep this many of the lowest dice (when <c>highest</c> is not set).</summary>
    public int? Lowest { get; set; }
}

/// <summary><c>{ on: 10, times: 1 }</c>: the face that rolls another die, and how many times in a row it may.</summary>
public sealed class RerollDefinition
{
    /// <summary>The face that brings another die (<c>10</c> on a d10).</summary>
    public int On { get; set; }

    /// <summary>How many more dice one die may bring; null: as many as come up (within the engine's limit).</summary>
    public int? Times { get; set; }
}

/// <summary>A named parameter supplied when the roll is made.</summary>
public sealed class RollInputDefinition
{
    /// <summary>Required: the input's name, as formulas read it.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Who supplies it: <c>roller</c> (the default), <c>gm</c>, <c>weapon</c> (a property of the item the
    /// <c>weapon</c> input names) or <c>parent</c> (the roll this one follows).</summary>
    public string From { get; set; } = "roller";

    /// <summary>What the roll widget calls the input; the key when unset.</summary>
    public string? Label { get; set; }

    /// <summary>For <c>from: weapon</c>, the item property it reads.</summary>
    public string? Property { get; set; }

    /// <summary>The value when nobody supplies one. With neither a value nor a default the roll fails, naming the input.</summary>
    public object? Default { get; set; }
    /// <summary>The values allowed, offered as a dropdown; any other fails the roll.</summary>
    public List<string>? Options { get; set; }
}

/// <summary>A named result a roll can come to (<c>critical</c>, <c>success</c>, <c>partial</c>); a roll's outcomes are tried in
/// order.</summary>
public sealed class RollOutcomeDefinition
{
    /// <summary>Required: the outcome's name, as the result, <c>then</c> lines and a group roll's <c>result</c> read it.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>A formula over the dice and total (<c>natural</c>, <c>total</c>, <c>kept</c>, <c>dice</c>, <c>zeroDice</c>)
    /// that must be true. None: always, so it belongs last.</summary>
    public string? When { get; set; }

    /// <summary>A follow-up roll's outcome that becomes the roll it follows (a confirmed threat is the attack's critical).</summary>
    public bool ReplacesParent { get; set; }

    /// <summary>How the dice display marks it: <c>critical</c>, <c>fumble</c> or none. Unset, an outcome named
    /// <c>critical</c> or <c>fumble</c> is marked so.</summary>
    public string? Mark { get; set; }
}

/// <summary><c>{ when, roll }</c> or <c>{ when, intents }</c>.</summary>
public sealed class RollFollowUpDefinition
{
    /// <summary>A formula over the result (<c>outcome</c>) that must be true for the line to act. None: always.</summary>
    public string? When { get; set; }
    /// <summary>The key of the roll to make next, which sees this roll's inputs (a confirmation roll).</summary>
    public string? Roll { get; set; }

    /// <summary>Intents as overhaul section 3 names them: <c>{ adjust: { track, by, actor } }</c>.</summary>
    public List<Dictionary<string, object>> Intents { get; set; } = new();
}

/// <summary>A group roll's participants (<c>{ from, roll }</c>): who chooses them, and the roll each makes.</summary>
public sealed class RollParticipantsDefinition
{
    /// <summary>Who chooses the participants: <c>gm</c> or <c>roller</c>.</summary>
    public string From { get; set; } = "gm";

    /// <summary>The roll each participant makes.</summary>
    public string Roll { get; set; } = string.Empty;
}
