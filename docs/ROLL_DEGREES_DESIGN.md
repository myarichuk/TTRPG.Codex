# Degrees of success and the multiple attack penalty on hybrid sheets (P6)

A hybrid system builds the character with `sheetRules` and keeps play-time numbers on a sheet kind (docs/SHEET_CONDITIONS_DESIGN.md). Its
rolls come from two places: the Actions panel and the skill buttons roll a plain d20 with a bonus the sheet worked out, and the named rolls
(`rolls:`) have inputs, outcomes and a log entry. P6 gives the first kind a degree and an attack number, and says how the two kinds share
condition modifiers. Nothing here changes a system that does not name the new keys: srd5e and fitd read none of them.

## The shape in system.yaml

    dice:
      checkRoll: degreeCheck          # a rolls: entry with inputs bonus and target; its outcomes are the degrees

    sheetRules:
      multipleAttack:
        penalties: [0, -5, -10]       # what the 1st, 2nd and 3rd-or-later attack of a turn add; the last row holds for later ones
        variants:                     # a weapon trait that lowers it: the first variant the weapon has wins
          - { property: traits, has: featherlight, penalties: [0, -4, -8] }

    rolls:
      - key: strike
        add: ["bonus", "attackPenalty(attackNumber)"]    # the same table, for a roll made from a definition
        inputs: [bonus, target, { key: attackNumber, default: 1 }, { key: weapon, default: "" }]

## Decisions

1. **One degree function: the system's own outcomes.** There is no degree logic in C#. The degrees are the `outcomes` of the roll
   `dice.checkRoll` names, over the system's `degree()` formula function (the natural 20 and 1 shift is data). `SheetRolls.Grade` evaluates
   those outcomes for a total, the check die's natural face and a DC, so a roll made through a definition and a roll the sheet makes (an
   attack, a skill check) cannot disagree. `RollOutcomeDefinition.Passes` says whether an outcome meets the DC; unset, `success` and
   `criticalSuccess` do. A system with no `checkRoll` has no degrees, and nothing about its rolls changes.
2. **A roll with no DC has no degree.** The panel's "against" box is optional. Empty, the roll is made and logged as before; filled, the log
   entry carries `Outcome` and `Mark` (the same fields a named roll sets). The grade is worked out where the roll is recorded
   (`CheckGrading`), at the table and off it.
3. **The multiple attack penalty is a roll input, in one table.** `sheetRules.multipleAttack` is the only place the numbers live.
   `MultipleAttack.Penalty` reads it for the Actions panel (which adds the penalty to the attack bonus and passes the attack number) and
   for `attackPenalty(n)` in a roll definition (which reads the roll's `weapon` input). A weapon's trait picks a variant row; the trait
   and its numbers are the system's, never the engine's.
4. **A named roll on a hybrid gets condition modifiers through `bonus`, not through `check`.** The numbers the sheet hands a roll as its
   `bonus` (a skill's, a save's, an action's attack bonus) already carry the conditions, once, through the `sheetRules` targets `check`,
   `save` and `attack`. A named roll therefore never reads `modifiers('check')`, `modifiers('save')` or `modifiers('attack')`: doing so
   would make them kind targets too, and the loader refuses a target both sides claim. A condition effect that should reach a named roll
   beyond the bonus names the roll's own key as its target, and the roll reads `modifiers('<its key>')`. A Frightened character's strike
   total drops by its value exactly once (tested on the attack bonus the panel passes).
5. **Words, not colour.** The roll result shows the degree as text ("Critical success", "Success", "Failure", "Critical failure"; en, ru,
   he) in the pill beside the roll, and the dice mark is a second cue, never the only one. Outcome keys the system adds beyond the four
   fall back to the system's own words.

## Not built

- Hazard checks (`CheckService.Make`) still grade success or failure only; `ActorChecks.Grade` has no system to read `checkRoll` from.
  The skipped `ActorCheckTests` case stands for it and is P12's (degrees through the outcome keys).
- The attack number does not advance by itself after a roll; the player sets it, and the panel says to set it back each turn.

## Built

Files touched (the plan asks that the next step to touch the Actions panel, H4b, list its files first):

- `src/Codex.Plugin.Abstractions`: `DiceConventions.CheckRoll`, `RollOutcomeDefinition.Passes`, `SheetRules.MultipleAttack` (`MultipleAttackRules`, `MultipleAttackVariant`).
- `src/Codex.Core/Rules`: `MultipleAttack.cs` (new), `SheetRolls.cs` (`Grade`, `CheckGrade`, loader checks), `SheetModel.cs` (`AttackPenalty`, `Weapon`), `SheetFormulas.cs` (`attackPenalty`).
- `src/Codex.Persistence/Runtime`: `CheckGrading.cs` (new), `RuntimeCommands.cs` (`RollDiceCommand.Dc`).
- `src/Codex.Web/Components`: `Shared/ActionsPanel.razor` (attack number and "against" controls), `Shared/RollRequest.cs` (`ActionRoll.AttackNumber`, `Dc`), `Shared/DegreeWords.cs` (new), `Shared/TableCard.razor`, `Shared/CombatConsole.razor`, `Pages/ActorSheet.razor`.
- `src/Codex.Localization/Resources`: `Strings.Actor` (en, ru, he).
- Tests: `tests/Codex.Tests/Components/AttackNumberAndDegreesTests.cs`; the degrees fixture (`system.yaml`, weapons pack, three new expectations in `spec.yaml` and `progress.txt`).
- Generated: `docs/schema/system.schema.json`, `docs/SYSTEM_YAML_REFERENCE.md`.
