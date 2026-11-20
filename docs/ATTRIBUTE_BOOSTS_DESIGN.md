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
- `trade.flaws: keep | drop` (default `keep`): whether the source's own flaws still apply once the trade is made. four-degrees uses `drop`, because its
  rules entry `alternate-ancestry-boosts` replaces the ancestry's boosts and flaws both with two free boosts. The reference spec's trade is unchanged.
- `voluntaryFlaws: true` (field sources only): the player may add flaws of their own. Stored at `boost__{source}__{entryId}.flaws`, one attribute each; each
  lowers its attribute and gives nothing back. Two flaws from one source on one attribute, the entry's own included, is the violation `FlawTwice`
  (rules entry `voluntary-flaws`: no more than one flaw on any one attribute). The text sets no number and no extra boost. Changing the entry drops them.
- Scores stay on the normal scale (default 10, `half-minus-five`), so every formula and ranked list reads them unchanged.

## Where it runs

Boosts are final in `EvaluateAttributes` and `ApplyAttributeTotals` (both call `EvaluateBoosts`, `SheetRuleEngine.Boosts.cs`),
before derived values, ranks and features read an attribute. They are not feature grants and not ranks (`ApplyChoice` is untouched).
Totals = default + boost change (+ increases). `bonuses` and the stored `base_` scores are ignored while the method is `boosts`.

## Choice storage

Own key prefix, so the feature-choice sweep never sees them: `boost__{source}__{entryId}.{slot}` for a source that reads an entry,
`boost__{source}__{slot}` for a milestone; a list of one attribute key. Slots: `b1, b2...` (an entry's choices in order), `m1...`
(milestone), `t1...` (from a trade). The trade is `boost__{source}__{entryId}.trade`: the fixed attributes given up. The entry id in the key
means picks made for one ancestry stop counting when it changes, and `ApplyAttributeTotals` removes them (and any key of an unknown source).
The panel's boxes and `SetTrade` use the stored list (`Given`), so a trade can be built one box at a time; `Traded` is what was applied.

## Loader checks

Boosts offered (in `methods` or as `default`) need a `boosts` block; every attribute `half-minus-five` with default 10; at least one
source; unique keys without `__` or `#`; exactly one of `field` and `level`; `field` an EntityReference actor field; `free` from 0 to the
attribute count and only on a level source; `trade` only on a field source, both numbers at least 1, `trade.flaws` `keep` or `drop`; `voluntaryFlaws` only on a field source; `partialFrom` at least 1; no `bonuses` or
`increases` beside boosts (a boost reads the score it lands on).

## Messages a player sees (en, ru, he)

`BoostOpen` (boosts still to place; a **warning**, so a sheet in progress saves, and a level that opens a milestone offers its boosts as
a `LevelChoice` `boosts:{source}`), and the violations `BoostSame` (each boost needs a different attribute), `BoostCant` (not one of the
choices), `BoostTrade` (give up exactly n fixed boosts), `FlawTwice` (an attribute with two flaws from one source). The panel also says the flaw goes with a `drop` trade,
and a source with voluntary flaws shows an add button per attribute (named for it) and a remove button per flaw taken. Duplicates still count in the score, so the sheet shows what was chosen.

## Spec expectations (degrees fixture)

`degrees.boosts-raise-scores-from-ten`, `a-flaw-lowers-the-score`, `two-fixed-boosts-trade-for-one-free`, `a-boost-above-plus-four-is-half`,
`two-half-boosts-make-a-full-step`, `four-different-boosts-are-required`, `an-open-boost-is-a-warning`.

## Built

- Types `BoostRules`, `BoostSource`, `BoostTrade` (`SheetRules.cs`); engine `SheetRuleEngine.Boosts.cs`; `AttributeSummary.Boosts`.
- Loader checks; schema and reference regenerated.
- Sheet panel (`AttributesPanel.razor`): the Boosts method in the picker, scores read-only, per source a fieldset with fixed boosts, flaws,
  the trade (checkboxes) and one labelled select per choice; problems in a polite live region; the half step shown as `½` with a spoken
  label. Strings in en, ru and he.
- Degrees fixture uses the method (ancestry, class, four milestones); 7 spec expectations; `AttributeBoostTests` for the loader.
- `attributes.max` still caps a boosted score (a violation). There is no floor.
- Not done: an entry word that matches nothing is ignored; the milestone level is the `level` field (a multiclass total is not summed);
  flaws apply before boosts within a source, and a later source's flaw can change a half step (flaws belong to creation).
