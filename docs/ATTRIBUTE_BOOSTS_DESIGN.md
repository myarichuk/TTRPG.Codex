# Attribute boosts (P5)

A second way to build attribute scores, beside point buy, standard array, manual and printed. Every attribute starts at its default (a
modifier of 0); sources each give boosts. All numbers in the fixtures are invented.

## Shape (`sheetRules.attributes`)

```yaml
attributes:
  methods: [manual, boosts]
  default: manual
  boosts:
    label: Boosts
    property: boosts      # entry property listing a source's boosts (default)
    partialFrom: 4        # modifier at which a boost is half a step (default 4)
    sources:
      - { key: ancestry, label: Ancestry, field: ancestry, trade: { fixed: 2, free: 1 } }
      - { key: class,    label: Class,    field: class }
      - { key: lvl5,     label: Level 5,  level: 5, free: 4 }
```

An entry (the one an EntityReference field points at) lists its boosts as words: `con` fixed, `str|dex` one of those, `free` any,
`-int` a flaw. A milestone source (`level` + `free`) gives that many free boosts from that level.

Rules:
- One source's boosts (fixed and chosen) go to different attributes; flaws are not boosts.
- A boost adds a step (+1 modifier, 2 score points); at `partialFrom` or above it adds 1 score point, so two make a step. The sheet shows
  the odd one as `+4½`. A flaw takes a step away. Sources apply in listed order, flaws first within a source.
- Trade (field sources only): the player gives up `fixed` of the source's fixed boosts for `free` free ones.
- Scores stay on the normal scale (default 10, `half-minus-five`), so every formula and ranked list reads them unchanged.

## Where it runs

Boosts are final in `EvaluateAttributes` and `ApplyAttributeTotals` (both call `EvaluateBoosts`, `SheetRuleEngine.Boosts.cs`),
before derived values, ranks and features read an attribute. They are not feature grants and not ranks (`ApplyChoice` is untouched).
Totals = default + boost change (+ increases). `bonuses` and the stored `base_` scores are ignored while the method is `boosts`.

## Choice storage

Reuses the feature-choice key: `choice__boost-{source}__{slot}`, a list of one attribute key. Slots: `b1, b2...` (an entry's choices in
order), `m1...` (milestone), `t1...` (from a trade). The trade itself is `choice__boost-{source}__trade`: the fixed attributes given up.
The stale-choice sweep skips keys whose entry id starts `boost-`.

## Loader checks

Boosts offered (in `methods` or as `default`) need a `boosts` block; every attribute `half-minus-five` with default 10; at least one
source; unique keys without `__` or `#`; exactly one of `field` and `level`; `field` an EntityReference actor field; `free` from 0 to the
attribute count and only on a level source; `trade` only on a field source, both numbers at least 1; `partialFrom` at least 1; no `bonuses`
beside boosts.

## Messages a player sees (en, ru, he)

`Actor.Rule.BoostOpen` (n boosts still to place), `BoostSame` (each boost needs a different attribute), `BoostCant` (not one of the
choices), `BoostTrade` (give up exactly n fixed boosts). Duplicates still count in the score, so the sheet shows what was chosen.

## Spec expectations (degrees fixture)

`degrees.boosts-raise-scores-from-ten`, `a-flaw-lowers-the-score`, `two-fixed-boosts-trade-for-one-free`, `a-boost-above-plus-four-is-half`,
`two-half-boosts-make-a-full-step`, `four-different-boosts-are-required`, `an-open-boost-is-reported`.

## Built

- Types `BoostRules`, `BoostSource`, `BoostTrade` (`SheetRules.cs`); engine `SheetRuleEngine.Boosts.cs`; `AttributeSummary.Boosts`.
- Loader checks; schema and reference regenerated.
- Sheet panel (`AttributesPanel.razor`): the Boosts method in the picker, scores read-only, per source a fieldset with fixed boosts, flaws,
  the trade (checkboxes) and one labelled select per choice; problems in a polite live region; the half step shown as `½` with a spoken
  label. Strings in en, ru and he.
- Degrees fixture uses the method (ancestry, class, four milestones); 7 spec expectations; `AttributeBoostTests` for the loader.
- Not done: no cap or floor on boosted scores; one source's choices are not cleared when its entry changes (stale picks are ignored when
  they no longer fit and reported as "not one of the choices").
