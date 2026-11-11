namespace Codex.Web.Components.Shared;

/// <summary>A d20 roll asked for from the sheet: what it is for and the bonus to add.</summary>
public sealed record RollRequest(string Label, int Bonus);

/// <summary>A roll an action asks for (ActionsPanel): the check die with <see cref="CheckBonus"/> (an attack), or the dice of
/// <see cref="Expression"/> (damage, healing).</summary>
public sealed record ActionRoll(string Label, int? CheckBonus, string? Expression);
