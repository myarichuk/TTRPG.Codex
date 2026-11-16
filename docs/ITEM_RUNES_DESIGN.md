# Item runes (P10)

Homebrew magic items and runes for the Four Degrees system. Every name, number and line is invented; the generic words (flaming, icy) carry our own mechanics.

## Shape

- `magic-item` entries are carried in the inventory (`sheetRules.inventory.types`). Equipped, an item's `modifiers` reach the sheet as typed modifiers.
- `rune` entries are never carried on their own. A rune is attached to an inventory pick as the choice `rune.{id}`:
  `ledge-axe|equipped:1,rune.honed-edge-1:1,rune.flaming:1`. `sheetRules.inventory.runeType` names the entity type (`rune`).
- A rune carries `modifiers` (the same shape as a feature's), and optionally:
  - `appliesTo`: the item's entity type (`weapon` or `armor`); anything else is a violation.
  - `group`: a fundamental rune (`potency`, `striking`, `resilience`). An item holds one rune per group.
  - `slots`: places for property runes this rune opens (the potency runes: +1 is one place, +2 two, +3 three).
  - `extraDice`: copies of the weapon's own damage dice added to its Strikes.
  - A rune with no `group` is a property rune and takes one place.
- On a weapon the targets `attack` and `damage` of its own and its runes' modifiers are narrowed to `attack.entry.{weaponId}` / `damage.entry.{weaponId}`, so a rune
  reaches its weapon alone. Armor runes name `armorClass`, `save`, `save.{attribute}` or `check.skill.{key}` and apply while the armor is equipped.
- Every rune and item modifier is `type: item`, which the system stacks as `highest`: a +2 rune and a +1 glove give +2, not +3.

## Built

- `InventoryRules.RuneType` and the YAML key `runeType`.
- `SheetRuleEngine.Runes.cs`: `InventoryFeatures` (equipped items and their runes become features, collected with every other feature, so stacking, working lists
  and roll sites need no new path), `WeaponDice` (`extraDice` on a Strike's damage, applied in `EvaluateActions`), `CheckRunes` (rune exists, fits the item,
  one per group, property runes within the room the item's `slots` give), `WithRune`, `RuneIds` and the `FormatItem` overload that keeps a pick's runes
  when its quantity or equipped state changes (used by the inventory panel and by item moves).
- Violations: `Actor.Rule.RuneNotFound`, `RuneWrongItem`, `RuneGroup`, `RuneSlots` (en, ru and he; ru and he are drafts for native review).
- Pack `four-degrees-core`: 12 magic items, 15 runes (5 weapon fundamental, 3 weapon property, 5 armor fundamental, 2 armor property), all tagged `homebrew`.
- Tests: `tests/Codex.Tests/FourDegreesRunesTests.cs`.

## Not built

- No sheet or inventory-panel control to attach or remove a rune; today the pick string is edited by data. The panel keeps the runes it finds.
- Item and rune modifiers apply only in a system that names a `runeType`; other systems' items (srd5e) keep working as before.
- Moving or trading a weapon to another sheet (`InventoryMoves.Add`) rebuilds the pick without its runes, and renaming a rune entry does not update picks
  (`SheetReferences` does not track rune ids).
- The two Four Degrees test classes share an xunit collection (`FourDegreesContent`) because they load the same content folder at once; the attribute is a workaround.
- A weapon carried twice under one id shares its runes' `attack` and `damage` modifiers between the copies (the roll targets name the entry id).
- Extra damage dice from a rune do not carry a damage type; the rune's name is the source shown on the roll.
