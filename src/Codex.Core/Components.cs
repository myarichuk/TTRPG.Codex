namespace Codex.Core.Components;

public struct StatusEffectComponent
{
    public string EffectId { get; init; }
    public string PackId { get; init; }

    public StatusEffectComponent(string effectId, string packId)
    {
        EffectId = effectId;
        PackId = packId;
    }
}

public struct DurationComponent
{
    public float RoundsRemaining { get; set; }

    public DurationComponent(float roundsRemaining)
    {
        RoundsRemaining = roundsRemaining;
    }

    public DurationComponent(double roundsRemaining)
    {
        RoundsRemaining = (float)roundsRemaining;
    }
}