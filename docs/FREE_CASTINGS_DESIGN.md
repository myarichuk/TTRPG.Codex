# Free castings: design

Status: designed 2026-11-13, not built. It is built at the end of the Step 10 branch (`SRD_GRANTS_IMPORT_DESIGN.md`), which
freezes the `free-<spell>` counter keys this design adopts. Piece B of `FEATURE_GRANTS_DESIGN.md`.

## What it is

A feature lets a character cast a spell without a slot: once per rest, a number of times per rest, or at will. Today casting
always spends a slot, and Step 10 writes such features as a plain `free-<spell>` counter that does not cast.

After this, a free casting is **one more way to pay for the spell's action**, beside its slots. The player picks it in the
same cost picker the action already has. Nothing else about casting changes.

## Data

`free` goes on an `adds` item whose `to` is the granted-spells list:

```yaml
adds:
  - to: grantedSpells
    entries: [misty-step]
    free: { max: 1, recoversOn: [Long Rest] }      # once per Long Rest
  - to: grantedSpells
    entries: [speak-with-animals]
    free: { max: proficiencyBonus, recoversOn: [Long Rest] }
  - to: grantedSpells
    entries: [detect-magic]
    free: atWill
```

Checked in the code (2026-11-13):
- `adds` to `grantedSpells` are written onto the sheet's list by `ApplyGranted` (and remembered in
  `granted__grantedSpells`). The list is then read like any list into `ListPicks`, and srd5e's `cast` action source lists
  `[cantrips, spells, grantedSpells]`. So every granted spell already has an action to attach a cost to.
- `FeatureGrantCheck` refuses a choice on a list of Library entries (only ranked lists and a kind's keyed lists take
  choices). So **player-chosen free spells are not in this design**; see the last section.

- `free` takes the same map as `uses`: `max`, `recoversOn`, `recovers`, `regain`, `key`, `label`. It is parsed by the same
  code (`FeatureUses`), so every refill rule, moment and GM-only Regain already works.
- `free: atWill` is the one other value.
- **One counter per spell.** Each entry in `entries` gets its own counter. A lineage with two free spells gets two counters,
  which is the shape the SRD text has ("once each per Long Rest").
- **Keys, exactly.** The counter id is `free-<spell id>` (for example `free-misty-step`), the id Step 10 froze. Its sheet
  keys are therefore `uses_free-<spell id>` (left), `uses_free-<spell id>_Max`, `uses_free-<spell id>_Rest` and
  `uses_free-<spell id>_Label`. Existing counters keep their values when Step 10's rows move from a plain `uses` to `free`.
  `free.key` overrides the id. A `key` with more than one entry makes **one shared counter** for all of them (rare; no SRD
  feature needs it now).
- Adding a spell another row already added is harmless (the list keeps one). So Dragon Companion can write
  `{ to: grantedSpells, entries: [summon-dragon], free: {...} }` although Draconic Spells adds the spell.

## Engine

1. **Parse.** `FeatureAdd` gets `Free: FeatureUses?` and `AtWill: bool`. `FeatureGrantData` reads and writes them, so the
   homebrew editor round-trips them. `FeatureGrantCheck` warns when `free` is on an add that is not a spell list, and when
   it names a cantrip (cantrips are at will already; the `free` is ignored).
2. **Counters.** In `EvaluateFeatures`, each spell with a counted `free` becomes a `FeatureCounter` with the id above and
   the label "<feature>: <spell name>". The label goes through Step 10's `uses.label` (E3), so `_Label` holds it. From
   there the counters panel, Use/Regain, rests, moments, the table's seeding (`SeedNewCounters`) and the delta merge all
   work unchanged.
   When the feature goes (a lineage changed, a feat removed), the existing sweep at the end of counter syncing removes its
   `uses_free-*` keys like any stale counter. A new counter starts full, as every counter does today.
3. **A record of free castings.** `SheetEvaluation.FreeCastings`: `(ActiveFeature, string SpellId, FeatureCounter? Counter)`.
   A null counter means at will.
4. **Costs.** In `EvaluateActions` (it runs after features), an action whose entry has free castings gets extra costs after
   its slot costs:
   - `ActionCost.OfCounter(counter, 1, baseDice)` for each counted free casting;
   - `ActionCost.Nothing("At will", baseDice)` for an at-will one.

   `baseDice` is the spell's dice at its own level (no upcasting): a free casting is cast at the spell's level. Spell
   Mastery's "at the lowest level" is the same thing.
5. **A spell with no slots.** A Forest Gnome with no spellcasting has `speak-with-animals` in `grantedSpells` but no slot
   pool. Today the action has nothing to pay with. With this change the free cost is its only cost, which is correct.
   When the counter is empty, the action shows but cannot be cast, like an action whose slots are spent.

## Sheet and table

- **The cost picker** lists the free casting as "<feature> (1 of 1 left)" or "At will". When a free casting has a use
  left, it is **selected by default**, because that is what a player almost always wants. Slots stay one click away.
- **Spending** goes through the existing cost path (`ActionCost.Key` → the pool), on the sheet and at the table. The
  session log line names the feature: "Cast Misty Step (Fey Step, free)".
- **The Features panel** shows the counters like any other, so a player can see what is left and the GM can regain them.
- **Strings** (en, ru, he): the at-will label, the "free" log marker, and the check warnings. ru and he need native review.

## Step 10 follow-up (same branch, after this is built)

- Move the `free-<spell>` counters in section 5 of `SRD_GRANTS_IMPORT_DESIGN.md` from a row's `uses` to the spell's
  `adds` item with `free`. The keys stay the same.
- **Lineage free castings become grants.** The rows section 6 left as text because "a row holds one `uses`" (Elven
  Lineage, Fiendish Legacy, Gnomish Lineage) now fit: each lineage spell from level 3 gets
  `free: { max: 1, recoversOn: [Long Rest] }`.
- Player-chosen spells (Magic Initiate, Mystic Arcanum, Spell Mastery, Signature Spells) stay text; see below.
- Divine Intervention and Natural Recovery stay text. Their "free" depends on what is cast and its level, not on a counter
  per spell.
- Update the coverage numbers.

## Not in this design

- **Player-chosen free spells** (Magic Initiate, Mystic Arcanum, Spell Mastery, Signature Spells, about a third of the SRD's
  free castings). They need a choice over a list of Library entries (pick 1 level-1 wizard spell), which the engine refuses
  today. That is its own design: choices over entry lists. When it comes, a picked spell's counter must be keyed by the
  choice slot (`free-<choice id>-<n>`), not by the spell, so re-picking cannot refill it.

- **Rituals** (Ritual Adept, ritual casting without a slot). They are a source-level rule, not a per-spell grant.
- **Casting at a fixed higher level** for free. No SRD feature needs it.
- **A free casting that spends another pool** (Sorcery Points instead of a slot). That is `spends`-to-action and belongs
  with the action `on` bonuses design.

## Tests

- Parse and round-trip: `free` map, `free: atWill`, and a shared `key`. `FeatureGrantCheck` warns on `free` outside a spell
  list and on a cantrip.
- Two free spells on one row make two counters, keyed `free-<spell>`.
- The action of a granted spell has its slot costs plus the free cost; a character without slots has only the free cost.
- Spending the free cost lowers the counter and no slot. A Long Rest refills it. At will never runs out.
- A Step 10 sheet that already holds `uses_free-hunters-mark` keeps its value after the row moves to `free`.
- The cost picker defaults to the free casting when it has a use left, and to the lowest slot when it does not.

## Decisions (2026-11-13)

- **Default cost:** when a free casting has a use left, the cost picker selects it; when it is spent, the lowest slot.
- **Lineage free spells become grants:** each lineage spell from level 3 (Elven Lineage, Fiendish Legacy, Gnomish Lineage)
  gets `free: { max: 1, recoversOn: [Long Rest] }`. Step 10's coverage numbers are updated to match.
