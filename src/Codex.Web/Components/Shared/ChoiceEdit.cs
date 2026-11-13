namespace Codex.Web.Components.Shared;

/// <summary>A change to the picks of one choice, as the picker reports it: <see cref="Key"/> is the sheet key the picks are stored under,
/// <see cref="Name"/> the feature's name, <see cref="Picks"/> the picks as they would stand (empty: cleared, to choose again),
/// <see cref="WasMade"/> whether the choice had picks before (so this changes one rather than making it).</summary>
public sealed record ChoiceEdit(string Key, string Name, IReadOnlyList<string> Picks, bool WasMade);
