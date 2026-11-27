using Codex.Localization;

namespace Codex.Web.Services;

/// <summary>The sentence the confirm dialog asks before a prepared encounter starts (the table's picker and the editor's button share it): how many creatures and how many
/// complex hazards join the fight (ENCOUNTER_BUILDER_DESIGN.md 18).</summary>
public static class PreparedConfirm
{
    public static string Text(Localizer text, string name, int creatures, int hazards) =>
        hazards <= 0 ? text["Combat.Prepared.Confirm", name, creatures]
        : creatures <= 0 ? text["Combat.Prepared.ConfirmHazards", name, hazards]
        : text["Combat.Prepared.ConfirmBoth", name, creatures, hazards];
}
