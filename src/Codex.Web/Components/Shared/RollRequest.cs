using Codex.Core.Rules;

namespace Codex.Web.Components.Shared;

/// <summary>A d20 roll asked for from the sheet: what it is for and the bonus to add. <paramref name="Kind"/> and <paramref name="Key"/> say which
/// roll it is (a save or a check of an attribute, a skill's key, the sheet's own spell attack) so the host can add what features give that roll
/// (<see cref="RollBonusTray"/>); without them nothing is added. <paramref name="AddSiteNumber"/>: the bonus does not hold the always-on numbers of the roll's
/// targets (the sheet's spell attack), so the host adds them.</summary>
public sealed record RollRequest(string Label, int Bonus, RollKind? Kind = null, string? Key = null, bool AddSiteNumber = false);

/// <summary>A roll an action asks for (ActionsPanel): the check die with <see cref="CheckBonus"/> (an attack), or the dice of
/// <see cref="Expression"/> (damage, healing). <paramref name="Kind"/> and <paramref name="Action"/> say which roll of which action it is, so the host can
/// add what features give it.</summary>
public sealed record ActionRoll(string Label, int? CheckBonus, string? Expression, RollKind? Kind = null, SheetAction? Action = null);
