# Crew links: design

Status: designed 2026-11-14, not built. It answers open question 2 of SHEETKINDS_FEATURES_DESIGN.md ("crew-to-PC effects"),
which left these as text. It shares the opt-in tick boxes with ROLL_BONUSES_DESIGN.md (Anointed).

## What it is

In the Forged in the Dark system the crew is a sheet of its own (`sheetKinds: crew`), and some of its abilities and upgrades
change every member's sheet:

| Crew entry | What it does to each member | Shape here |
|---|---|---|
| Composed (upgrade) | +1 stress box | modifier on the stress track's size |
| Hardened (upgrade) | +1 trauma box | modifier on the trauma track's size and its retire threshold |
| Mastery (upgrade) | action ratings may go to 4 (else 3) | modifier on an action cap value, read by a sheet rule |
| Deadly (ability) | +1 action rating in Hunt, Prowl or Skirmish, each member's own pick, up to 3 | a choice on the member's sheet |
| Forged in the fire (ability) | +1d to resistance rolls | modifier on `@resistance` |
| Anointed (ability) | +1d to resistance against supernatural threats | opt-in modifier on `@resistance` (roll bonuses) |

Training, Quality, Rigging, Cohorts and the rest stay text: they act on experience, items or cohorts, not on numbers the
member's sheet holds.

Nothing here names Blades in C#. A system declares that one kind's sheet **links** to another's, and that a linked entry's
`members` grants apply to the linking sheet.

## Data

```yaml
sheetKinds:
  - kind: scoundrel
    # A scoundrel belongs to (at most) one crew of the campaign.
    links:
      - { key: crew, kind: crew, label: Crew }
    features:
      - { list: abilities }
      # The crew's abilities and upgrades, as they reach each member: each entry's `members` property is the grant.
      - { link: crew, list: abilities, property: members }
      - { link: crew, list: upgrades,  property: members }
    values:
      # Action ratings stop at 3 until the crew has Mastery.
      - { key: actionCap, label: Action rating limit, formula: "3", modifiers: actionCap }
    keyedLists:
      - key: actions
        rules:
          - { check: "max(actions, a => a.rating) <= actionCap", key: Actor.Rule.ActionCap, message: "an action rating is above {actionCap}." }
    tracks:
      - { key: stress, max: "9 + modifiers('stressBoxes')", ... }
      - { key: trauma, max: "4 + modifiers('traumaBoxes')", thresholds: [{ atOrAbove: "4 + modifiers('traumaBoxes')", condition: Retired }] }
    layout:
      - { section: Standing, widgets: [{ widget: link, key: crew }] }

  - kind: crew
    layout:
      - { section: Standing, widgets: [{ widget: members }] }    # who links to this crew, read only
```

A crew entry carries the member grants under `members`, beside its own grants:

```yaml
# crew-upgrades/composed.yaml
properties:
  cost: 3
  members:
    modifiers: [{ target: stressBoxes, amount: 1 }]

# crew-abilities/deadly.yaml
properties:
  members:
    adds: [{ to: actions, choice: deadly, choose: 1, from: [hunt, prowl, skirmish] }]   # see "Deadly" below

# crew-abilities/anointed.yaml
properties:
  members:
    modifiers: [{ target: '@resistance', amount: 1, optIn: { label: Against a supernatural threat } }]
```

**The rules:**
- `links` on a kind: each has a `key`, the `kind` of sheet it points to, and a `label`. The sheet stores the linked actor's
  id under `link__<key>`. The target must be an actor of the same campaign whose sheet is that kind. One actor per link.
- A feature source with `link: <key>` reads the **linked** sheet's picks of `list`, which must be a list of the linked kind.
  For each pick it takes the entry's `property` (a grant map: `modifiers`, `adds`, `uses`, `options`) as one feature of the
  linking sheet. The feature's name is the entry's, with the link's label: "Composed (Crew)".
- **Choices** a member grant asks for (Deadly) are stored on the **member's** sheet, scoped by the crew entry id, as every
  choice is (`choice__{entryId}__{choiceId}`). Each member picks their own.
- **Formulas** of the linking sheet may read the linked sheet's values with `link('crew', 'tier')`. It gives the linked
  sheet's worked-out value, or 0 when there is no link. That serves Tier-scaled rules later. Nothing writes across the link.
- **One step only.** The linked sheet is worked out without its own links: a crew's links (if a system gave it any) are not
  followed while it is serving as a link. No cycles are possible.
- An unknown link key, a `link:` source whose list is not on the linked kind, or `link()` with an unknown key is a load
  error.

### Deadly

Deadly adds a rating to an action the member picks, which keyed `adds` with a `choice` already do for Expertise. The
builder confirms whether those raise the rating by one or mark the key. If they only mark it, Deadly is written as
`modifiers: [{ target: actions.<pick>, amount: 1 }]` through the choice instead. Either way:
- The cap "up to a max rating of 3" is the member's `actionCap` rule. Deadly does not raise the cap.
- "It supersedes the normal starting limit" is a starting-limit rule. If the sheet has none, it stays text.

## Storage and hosts

- **Storage.** `link__<key>` is an ordinary sheet key, saved with the sheet. A **vault** character has no campaign, so its
  link widget is hidden and a stored link is ignored. The **join review** drops `link__*` keys from the campaign copy,
  because the id belongs to another campaign. `SheetReferences` ignores them, since they are actor ids, not Library entries.
- **Evaluation needs the linked sheet.** A new `ILinkedSheets` (resolves an actor id to that actor's sheet, or null) is
  passed, optionally, wherever a sheet is evaluated. These hosts supply it; every other caller passes none, and link
  features are then simply absent:
  - `SheetModel`: a new optional constructor argument. `KindFeatures` reads it.
  - `SheetRuleEngine.Evaluate`, `EvaluateModel` and `ApplyTracks`.
  - `SheetRolls.Roll`.
  - `SheetView`.
  - `SheetIntents` and `SheetEvents`, where they build a model.
  - `TableEvents`.
- **An unknown link is not an empty link.** Many write paths evaluate a sheet without supplying linked sheets: a save, the
  runtime's `ReplaceActorCommand`, intents and events applied off the page, and the AI. If such a pass treated a missing
  crew as "no crew", it would sweep the member's link-made `uses_*` counters, prune their `choice__` picks (Deadly), and
  clamp tracks whose size reads link modifiers (stress 10 back to 9). So:
  - The engine tells three states apart: no `link__<key>` on the sheet (no link), a link whose sheet was supplied (known),
    and a link whose sheet was not supplied (unknown).
  - When a link is unknown, the pass keeps every counter and choice key scoped to link features as it is. It does not clamp
    a track whose maximum reads a modifier that a link feature could give. Those keys and values are left exactly as stored.
  - Only a known link whose crew no longer holds the entry makes those keys stale.
  - A test saves a member through a path with no linked sheets, and checks that the Deadly pick, a link-made counter and
    stress 10 are all still there.
- **Where the linked sheet comes from:**
  - The live table: `CampaignRuntime`'s cached actor documents. The live crew sheet is the source.
  - The sheet page: `ActorSheet` loads the linked actor through the actor repository, once on load and again when the link changes.
  - The Table cards: `CombatConsole` uses the runtime when live, else the campaign context's actors.
  - The AI paths and the vault: none.
- **Who sees what.** The rules never depend on who is looking. Link features always apply: the host loads the linked
  sheet with the campaign's own access (the runtime's documents, or a GM-level read on the sheet page). Only the crew's
  name and the link to its sheet follow the viewer's access. A player who cannot see the crew reads "Crew not visible" in
  the widget and in the feature's name. A crew sheet's role is `shared`. Today `CastService.RoleOf` never
  yields `shared`, so a crew is made with the kind picker. That stays. Its owner and visibility are the GM's, as for any
  non-player actor.
- **Live updates.** When the crew actor changes on the live table, every actor whose sheet links to it is re-worked-out. The
  runtime keeps a reverse index (crew id to member ids) built from `link__*` keys on hydrate and on each sheet replace.
  `StateChanged` then names the members too. On the sheet page (not live), a change to the crew shows on the member's next
  load.
- **Counters** a member grant makes (`uses`) are the member's own `uses_*` counters, refilled at the member's moments, as any
  feature's are.

## Sheet and table

- **Link widget** (`{ widget: link, key: crew }`):
  - A select of the campaign's actors of the linked kind that the viewer can see, plus "No crew".
  - It shows the linked crew's name as a link to its sheet.
  - It can be edited by whoever can edit the sheet.
  - It is labelled and works by keyboard and right to left.
- **Members widget** (`{ widget: members }`, on the linked kind): a read-only list of the actors that link here, each a link
  to its sheet, with their names as the viewer may see them.
- **The Features panel** lists link features under the crew's name. A feature that asks for a choice (Deadly) shows in the
  member's choices block, like any choice.
- Strings in en, ru, he: the widget's labels, "No crew", "Crew not visible" and the action-cap rule message. ru and he need
  native review.

## Data to write (Forged in the Dark)

Through the fitd importer, never by hand in the pack:
- The scoundrel kind:
  - `links`;
  - the two link sources;
  - `actionCap`;
  - the action cap rule;
  - stress and trauma sizes from modifiers;
  - the trauma threshold from the same formula;
  - the link widget;
  - the crew's members widget.
- The `members` grants: Composed, Hardened, Mastery (`actionCap` +1), Deadly, Forged in the fire, Anointed (opt-in, after
  RB1a).
- Translations: the new labels in the ru and he system translations (machine drafts).

**Shapes CL1 confirms or adapts** (none is verified against the formula engine yet):
- `max(actions, a => a.rating)`: formulas use `count` and `sum` with a lambda, but `max` is only known with two numbers. If
  there is no lambda form, CL1 adds one, or writes the rule another way (for example `count(actions, a => a.rating > actionCap) == 0`).
- `rules:` on a keyed list: so far only pick lists have `rules`. CL1 puts the rule where rules are read, or adds them to keyed lists.
- `thresholds.atOrAbove` as a formula: it may be a number only today. CL1 makes it a formula (as `max` is), or leaves
  Hardened's threshold as text and says so.

## Tests

- Load: `links`; a `link:` source; `link()`; each refused shape.
- A scoundrel linked to a crew with Composed has 10 stress boxes; unlinked, 9.
- Hardened moves the trauma size and the Retired threshold to 5.
- Mastery:
  - without it, a rating of 4 breaks the cap rule;
  - with it, the cap is 4.
- Deadly:
  - two members pick different actions;
  - each member's pick raises their own rating;
  - a pick that would pass 3 breaks the cap rule.
- Forged in the fire: the resistance pool is one die larger.
- Anointed is offered as a tick box on resistance only (after RB1a).
- One step: a crew sheet linked from two members is worked out once each, with no recursion, and a link to a missing actor
  gives no features and no error.
- Live: changing the crew's upgrades on the live table refreshes both members' cards. The `StateChanged` ids include them.
- Join review: `link__crew` is dropped from the campaign copy. The vault hides the widget.
- Golden: the fitd golden changes only for the scoundrel and crew kinds and the written entries.

## Build order and who builds it

| # | Step | Who | Notes |
|---|---|---|---|
| CL1 | Engine: `links`, `link:` sources, `link()`, `ILinkedSheets` threaded through, the unknown-link rule, load checks | Sonnet session | After RB1a is reviewed, since both touch `SheetModel`'s modifiers. In parallel with RB1b (RB1b works in the sheet-rules engine, CL1 in `SheetModel` and the kind path) |
| CL2 | Hosts and UI: the runtime's reverse index and live refresh, the sheet page and cards supplying linked sheets, the link and members widgets, the join review drop, strings | Sonnet session | After CL1 |
| CL3 | Data: fitd importer and system.yaml, the `members` grants, translations | Sonnet session | After CL1. Can run with CL2 (different files) |

## CL1 as built (engine)

- `SheetKindDefinition.Links` (`key`, `kind`, `label`), `FeatureSource.Link`, `ILinkedSheets.Sheet(actorId)`, `LinkState` (`None`, `Known`, `Unknown`), `SheetLinks`
  (`LinkedSheets.cs`). A link source needs `list` (of the linked kind) and `property`; `choose` is refused on it (a grant map's own `options` and `choose` do that).
- The linked sheet is checked by its own `kind` string, not by the system's default kind. A linked actor that is missing, or of another kind, is a known, empty link.
- A link to a **missing actor counts as known and empty**: a deleted crew drops its members' link-made counters and choice picks on the next save that supplies the resolver.
- The unknown-link rule lives in `SheetEvaluation.LinksUnknown` (set by `Evaluate`) and `SheetModel.LinksUnknown`. Sweeps that stand down while a link is unknown:
  `ApplyCounters` (`uses_*`), `ApplyChoiceMoments` (`changes_on__*`), `PruneChoices` (`choice__*`). Track maxima that read `modifiers(...)`, `link(...)`, or a value
  that does are unknown (null): `TrackOf`, so intents do not clamp and `ApplyTracks` does not write `{key}_Max`; the model's `{key}_Max` value; and the range check.
  A kind rule that reads such a value (the action cap) is not judged either.
- Shapes: `max(list, x => ..., default)` already exists, so the rule is written `max(actions, a => a.rating, 0) <= actionCap` (a two-argument lambda `max` would clash with
  `max(a, b)`). `rules:` on a keyed list was already read. A threshold is a formula already; but it could not read `modifiers(...)` (the conditions it brings were still being
  worked out), so such a formula now reads the modifiers of everything except conditions.
- Deadly: a keyed `adds` choice still only marks a key (`SheetRuleEngine.KeyedChoices.cs`) and changes no rating. CL3 must write it as a modifier on the picked
  key, or add the hook.
- Left for CL2: the hosts that supply `ILinkedSheets`, live refresh, the widgets, the join review, strings.

## Decisions (2026-11-14)

1. A link is a sheet key holding an actor id, declared per kind. There is no new actor relation in the database.
2. Member grants live on the crew entry under `members`, read through a `link:` source with `property`.
3. Member choices are stored on the member's sheet.
4. Links are one step deep and read-only. Nothing a member does writes to the crew sheet.
5. Link features apply whoever is looking. Only the crew's name and link follow the viewer's access.
