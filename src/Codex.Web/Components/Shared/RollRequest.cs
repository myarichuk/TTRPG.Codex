namespace Codex.Web.Components.Shared;

/// <summary>A d20 roll asked for from the sheet: what it is for and the bonus to add.</summary>
public sealed record RollRequest(string Label, int Bonus);
