namespace Codex.Plugin.Abstractions;

public enum TriggerType
{
    Passive,
    Action,
    BonusAction,
    Reaction,
    TurnStart,
    TurnEnd,
    OnHit,
    OnDamageReceived,
    Complex
}