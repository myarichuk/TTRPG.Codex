# system.yaml reference

Generated from the app's `SystemDefinition` by `Codex.SystemCheck --schema`; do not edit by hand. The same model is the
JSON schema beside it ([schema/system.schema.json](schema/system.schema.json)), which an editor can check a system.yaml against as you
type: put this first line in the file:

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/myarichuk/TTRPG.Codex/main/docs/schema/system.schema.json
```

How the pieces fit together is in [CONTENT_PACKS.md](CONTENT_PACKS.md) and the worked systems under
`tests/Codex.Tests/Fixtures/reference`. `id` and `name` are required; everything else has a default. A key this
version does not read is ignored, and the loader says so.

## system.yaml

A whole game system described as data (`system.yaml`). This is what lets a homebrew system exist without compiling a plugin: attributes, resources, the actor sheet, the content types its packs may contain, conditions, and the licence the rules text is under.

| Key | Type | What it is |
|---|---|---|
| `id` | text (a number is text too) | Required: the system's key, lower-case letters, digits, `-` or `_`, starting with a letter or digit. Campaigns and packs name the system by it, so it should not change. |
| `name` | text (a number is text too) | Required: the system's name as the app shows it. |
| `description` | text (a number is text too) | A paragraph about the system, shown on the About page. |
| `version` | text (a number is text too) | The system's version. A campaign records the version it plays under and says when it changes. Default `1.0.0`. |
| `compatibility` | text (a number is text too) | What the system may call itself in relation to a trademarked product, e.g. "5E compatible". |
| `license` | [LicenseInfo](#licenseinfo) | The licence the system's rules text is under, shown with its attribution on the About page. None: no licence is shown. |
| `attributes` | list of [AttributeDefinition](#attributedefinition) | The attributes every actor has (ability scores, characteristics), in the order the sheet shows them. |
| `resources` | list of [ResourceDefinition](#resourcedefinition) | The pools every actor has (hit points, mana). The one marked `isPrimary`, else the first, is the one damage and healing reach. |
| `actorFields` | list of [FieldDefinition](#fielddefinition) | Extra sheet fields beyond attributes and resources (level, class, alignment, speed...). |
| `fieldRoles` | [FieldRoles](#fieldroles) | Which of the ActorFields the app reads for what, outside the rules engine. |
| `entityTypes` | list of [ContentTypeDefinition](#contenttypedefinition) | The kinds of content the system's packs may carry (class, spell, monster), each with its own fields and pack folder. |
| `conditions` | list of text (a number is text too) | Condition names the combat console offers in its picker. |
| `dice` | [DiceConventions](#diceconventions) | The dice the table rolls for initiative, and how initiative ties are broken. |
| `ai` | [AiVocabulary](#aivocabulary) | How the system's own words are put to AI models, so prompts carry no assumptions about any one game. |
| `setting` | [SystemSetting](#systemsetting) | The kind of world play assumes, in general terms. Null: none (the campaign's own description is the world). |
| `vitals` | [VitalsRules](#vitalsrules) | Table rules for hit points, death, damage types, exhaustion, hit dice and rests. Null: none. |
| `sheetRules` | [SheetRules](#sheetrules) | Character-sheet rules (attribute generation, pick constraints, lists, derived fields). Null means "no rules": any value goes, which is what a loose homebrew wants. |
| `sheetKinds` | list of [SheetKindDefinition](#sheetkinddefinition) | The kinds of sheet the system has, each with its own values, modifiers, lists and keyed lists. Empty for a system that only has SheetRules. |
| `layout` | list of [LayoutSection](#layoutsection) | The sheet's layout for a system with no SheetKinds (each kind has its own). Empty: the host's built-in widgets, in the order the sheet has always had them (Codex.Core.Rules.SheetLayout). |
| `campaign` | [CampaignObjectsDefinition](#campaignobjectsdefinition) | State the campaign owns rather than a sheet: clocks and tables (faction status, a party treasure). Null: none. The state itself is the campaign's (Codex.Core.Rules.CampaignObjects). |
| `modifierTypes` | [ModifierTypesDefinition](#modifiertypesdefinition) | How bonuses of one type combine on one target. |
| `progressions` | map of text (a number is text too) | Named per-level formulas (`bab.good: "level"`), read by `progression(name, level)`. |
| `functions` | map of [FunctionDefinition](#functiondefinition) | Named functions, callable from any formula by name. |
| `widgets` | map of [WidgetDefinition](#widgetdefinition) | Widgets a DLL supplies (`{ type: "Acme.Street.Cyberware, Acme.Street" }`), placed by name in a layout like the host's own (`{ widget: cyberware }`). |
| `conditionEffects` | map of [ConditionEffectDefinition](#conditioneffectdefinition) | What a condition does while it holds, by condition name. |
| `rolls` | list of [RollDefinition](#rolldefinition) | Named rolls with inputs and outcomes (see Rolls.cs). |
| `moments` | list of [MomentDefinition](#momentdefinition) | The system's own moments (`downtime`, `loadChosen`): named times at which counters refill and an `events:` handler of the same name runs. Rests (`vitals.rests`), `initiative` and `dawn` are moments too and need no declaring. |
| `events` | map of any | Event handlers by event name (`damage`, `turnStart`, `roll`): `{ builtin: name, ...options }`, `{ script: file#export }`, or a list of `{ when, intents }` lines. Read as YAML gives it, and parsed by Codex.Core.Rules.SheetEvents, since a handler is written in either shape. |

## LicenseInfo

Licence and attribution metadata for a system or a content pack. Content under CC-BY (the 5E SRD), ORC, or similar must be displayed with its attribution wherever it is used, so this travels with the data rather than living in a README nobody at the table ever sees.

| Key | Type | What it is |
|---|---|---|
| `name` | text (a number is text too) | Short licence identifier, e.g. "CC-BY-4.0", "ORC", "MIT", "All rights reserved". |
| `url` | text (a number is text too) | Link to the licence's legal text. |
| `attribution` | text (a number is text too) | The verbatim attribution statement the licensor requires, if any. |
| `sourceUrl` | text (a number is text too) | Where the material came from (the SRD page, the upstream repository, ...). |
| `modified` | true or false | CC-BY §3(a)(1)(B): say so if the material was changed on the way in. |
| `notices` | list of text (a number is text too) | Free text for additional notices (e.g. an upstream MIT notice for a data transcription). |

## AttributeDefinition

A character attribute (ability score, characteristic, stat) the system tracks per actor.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required, unique: the key the sheet stores the score under and formulas name it by (`str`, as in `mod(str)`). |
| `label` | text (a number is text too) | The attribute's name as the sheet shows it ("Strength"). |
| `abbreviation` | text (a number is text too) | A short name ("STR") the sheet shows in place of the label. Text that names attributes (a background's eligible scores, `"STR 13+"`) may use the key, this or the label. |
| `canonicalLabel` | text (a number is text too) | The label and abbreviation as system.yaml wrote them. A localized copy shows translated text in Label, but sheet data and pack entries refer to the original words ("STR 13+", "Strength"), so rules match on these as well. |
| `canonicalAbbreviation` | text (a number is text too) | The abbreviation as system.yaml wrote it (see `canonicalLabel`). Normally left out: a localized copy fills it in from `abbreviation`. |
| `default` | whole number | The score a sheet has until one is entered. Default 10. |
| `min` | whole number | The lowest score that can be typed in on a sheet when the system has no `sheetRules.attributes`; also given to the AI as the attribute's range. None: no floor. |
| `max` | whole number | The highest score that can be typed in on a sheet when the system has no `sheetRules.attributes` (which caps scores with its own `max`); also given to the AI as the attribute's range. None: no cap. |
| `modifier` | text (a number is text too) | Optional derived-modifier rule. Supported: `"half-minus-five"` (floor((v-10)/2), the d20 family) or `"none"`. Systems that need anything else define a derived field instead. |

## ResourceDefinition

A depletable pool (hit points, mana, strain, luck) on an actor.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required, unique: the key the current value is kept under on the sheet and in the table's pools ("HP"). |
| `label` | text (a number is text too) | The pool's name as the sheet shows it ("Hit Points"). |
| `maxKey` | text (a number is text too) | Pool key that holds the maximum, e.g. "HP_Max". Null for pools without a cap. |
| `default` | whole number | The value a new sheet starts with, for the pool and for its maximum. Default 0. |
| `isPrimary` | true or false | True for the pool damage/healing target by default (usually hit points). |

## FieldDefinition

One field of a schema-driven form. A plain class (not a positional record) so it can be read straight out of `system.yaml`; the positional constructor is kept for code that builds schemas by hand.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required, unique within its list: the key the value is stored under on the sheet or entry. |
| `label` | text (a number is text too) | The field's name as the form shows it. |
| `type` | one of: `Text`, `LongText`, `Number`, `Boolean`, `Dropdown`, `TagList`, `ImagePicker`, `EntityReference`, `Collection` | How the value is entered and kept. Default `Text`; an `EntityReference` needs `targetEntityType`. |
| `description` | text (a number is text too) | A line of help the form shows under the field. |
| `options` | list of text (a number is text too) | The choices of a `Dropdown`. |
| `defaultValue` | any | The value the form shows until one is set; for an actor field, also the value a new character starts with. |
| `isRequired` | true or false | Marks the field as required in the form. Only a mark: nothing refuses an empty value. |
| `targetEntityType` | text (a number is text too) | For EntityReference: the content type the field points at. |
| `category` | text (a number is text too) | Grouping heading in the rendered form. |
| `min` | number | Lower bound for Number. |
| `max` | number | Upper bound for Number. |
| `abbreviation` | text (a number is text too) | A short name where space is tight (the sheet's plate): "AC" for Armor Class. The label when unset. |
| `unit` | text (a number is text too) | The unit a number is measured in ("ft"), written after it where it stands alone. |

## FieldRoles

The actor fields the app reads by what they mean rather than by name, so a system calls them what it likes. A role left empty turns off what reads it: without Level there is no level up, without Speed the sheet's plate shows no speed. Each names a key of ActorFields.

| Key | Type | What it is |
|---|---|---|
| `level` | text (a number is text too) | The character's level: the level up, the AI's level-up suggestion, Cast's summary. |
| `class` | text (a number is text too) | The character's (first) class and its subclass, for the AI's level-up suggestion and the table's hit dice. |
| `subclass` | text (a number is text too) | The character's subclass, for the AI's level-up suggestion, which also lists the subclasses its class offers. None: the suggestion knows of no subclass. |
| `speed` | text (a number is text too) | Shown on the sheet's plate beside the derived numbers. |
| `summary` | list of text (a number is text too) | The fields Cast's one-line summary of a character lists, in order. Level reads "Level 3"; a reference field reads its entry's name. |

## ContentTypeDefinition

One kind of content a system's packs can carry (class, feat, spell, monster, item, ...), with the field schema the Library browser and homebrew editor render it with.

| Key | Type | What it is |
|---|---|---|
| `type` | text (a number is text too) | Stable key, singular, lower-case: "spell". |
| `label` | text (a number is text too) | The type's name, singular, as the Library and editors show it ("Spell"). |
| `pluralLabel` | text (a number is text too) | The plural name ("Spells"); `label` when unset. |
| `folder` | text (a number is text too) | Pack folder holding entries of this type; defaults to Type. |
| `icon` | text (a number is text too) | An icon name for the type. Reserved; not read by this version (the Library picks its own icon by the type key). |
| `description` | text (a number is text too) | A line of help the homebrew editor shows when this type is chosen. |
| `fields` | list of [FieldDefinition](#fielddefinition) | The properties an entry of this type carries, in the order the editor and the entry's page show them. |
| `summaryFields` | list of text (a number is text too) | Property keys shown in list rows next to the name (e.g. level, school for spells). |
| `filterFields` | list of text (a number is text too) | Property keys offered as facet filters in the browser (e.g. level, school, class). |
| `actorBlueprint` | true or false | True when entries of this type describe creatures the DM can drop into an encounter. They are registered as actor blueprints as well as library entries. |
| `hitPointsProperty` | text (a number is text too) | For actor blueprints: the property holding starting hit points (default "hitPoints"). |
| `attributeProperties` | map of text (a number is text too) | For actor blueprints: property -> attribute key map (e.g. "strength" -> "str"). |
| `playerFacing` | true or false | True for types a player may pick on their own sheet (class, species, background...). |

## DiceConventions

Dice conventions the runtime uses when it needs to roll something for a system.

| Key | Type | What it is |
|---|---|---|
| `initiative` | text (a number is text too) | The dice the combat console rolls for initiative. Default `1d20`. |
| `check` | text (a number is text too) | The dice for an ordinary check. The sheet's check buttons roll a d20; the Actions panel reads this die's top face to decide whether an action's `critical` face is worth showing. |
| `initiativeBonus` | text (a number is text too) | The derived field (see `sheetRules.derived`) added to an initiative roll, e.g. an `initiative` field with the formula `mod(dex)`. Null: the bare die. |
| `initiativeTieBreak` | text (a number is text too) | Attribute whose score breaks initiative ties (higher first). Null: ties fall to actor id. |

## AiVocabulary

The words and hints the AI features take from a system instead of assuming one game (`ai:` in system.yaml). Every part is optional; a system that says nothing gets neutral wording, so a game with no levels, classes or hit points still reads right.

| Key | Type | What it is |
|---|---|---|
| `creature` | text (a number is text too) | What the game calls a creature with a stat block, singular and lower-case ("monster", "foe"). Null: "creature". |
| `creatureExamples` | list of text (a number is text too) | Plain roles whose stat blocks suit an ordinary person ("guard", "priest"), offered to the model as examples. Null: a neutral few. |
| `advancementChoices` | list of text (a number is text too) | Decisions a character makes on advancing ("subclass", "feats", "spells"), listed for the level-up advice. |
| `advancementChanges` | list of text (a number is text too) | Numbers that change on advancing ("hit points", "proficiency bonus"), listed for the level-up advice. |
| `primaryResourceBasis` | text (a number is text too) | What a starting value of the primary resource should depend on ("the level, class and constitution"). Null: the sheet as a whole. |
| `bookShape` | text (a number is text too) | How this game's own published books read, for the campaign book's AI edits to take after (docs/BOOK_EXPORT.md 41.4): their chapters, their furniture (read-aloud boxes, sidebars, stat blocks) and their voice, finishing the sentence "The book should read like this game's own published books, which are …". Shape and voice only: no book's title, words or trademarks. Null: neutral words. |
| `playerBookShape` | text (a number is text too) | The same for the books the players read (the party edition, docs/BOOK_EXPORT.md 42): a player's guide or a gazetteer, in the world's voice, with nothing addressed to the GM. Null: neutral words for a players' book, never BookShape. |

## SystemSetting

What kind of world a system's play assumes, in general terms (`setting:` in system.yaml): genre, tone, the kinds of factions and places it is built for. It is for systems that ship no setting content of their own, or whose rules presume one, so a GM (and the AI features) can homebrew in its spirit. Write genre words, never a published setting's names or text: "criminal crews in a haunted industrial city", not the city's name. Every part is optional.

| Key | Type | What it is |
|---|---|---|
| `summary` | text (a number is text too) | One short paragraph: the world the game is built to be played in. |
| `genres` | list of text (a number is text too) | Genre words ("heist", "gothic horror", "industrial fantasy"). |
| `tone` | list of text (a number is text too) | How play should feel ("grim", "desperate", "darkly funny"). |
| `themes` | list of text (a number is text too) | What stories in it tend to be about ("ambition and its cost", "loyalty under pressure"). |
| `technology` | text (a number is text too) | How advanced the world is ("early industrial: rail, gas light, black powder"). |
| `supernatural` | text (a number is text too) | Magic, psionics, ghosts, gods: what exists beyond the ordinary, and how common it is. |
| `factions` | list of text (a number is text too) | Kinds of factions, never named ones ("criminal gangs", "a decadent nobility", "a magocracy"). |
| `places` | list of text (a number is text too) | Kinds of places ("a crowded port city", "slums along the canals"). |
| `play` | list of text (a number is text too) | What play looks like ("a crew takes jobs, then deals with the fallout"). |
| `avoid` | list of text (a number is text too) | What does not belong ("high-tech gadgets", "noble, untroubled heroes"). |
| `notes` | text (a number is text too) | Anything else a GM homebrewing for the system should know. |

## VitalsRules

What happens to a creature's vitals at the table (`vitals:` in system.yaml): dropping to zero, death saves, damage types, exhaustion, hit dice and rests. Every part is optional; a system that leaves one out gets the plain behaviour (damage stops at zero, rests refill only what slot pools say).

| Key | Type | What it is |
|---|---|---|
| `temporaryResource` | text (a number is text too) | Resource that absorbs damage before the primary one ("TempHP"); granted as "take the higher". |
| `down` | [DownRules](#downrules) | What reaching zero on the primary resource does. Null: damage stops at zero and nothing else happens. |
| `deathSaves` | [DeathSaveRules](#deathsaverules) | Death saves for a creature at zero. Null: none. |
| `damage` | [DamageTypeRules](#damagetyperules) | Damage types, and the sheet lists that resist, ignore or double them. Null: damage has no type. |
| `exhaustion` | [ExhaustionRules](#exhaustionrules) | Exhaustion as a level counter. Null: none (an exhaustion condition, if listed, is an ordinary condition). |
| `hitDice` | [HitDiceRules](#hitdicerules) | A pool of hit dice spent to heal during a rest. Null: none. |
| `concentration` | [ConcentrationRules](#concentrationrules) | Concentration: a condition a creature keeps while it holds an effect up, a save when it is hurt, and the held effects ending with it. Null: the table has no concentration. |
| `rests` | list of [RestRule](#restrule) | Rests the table offers, by name; a slot pool's `recoversOn` names one of these. |

## DownRules

What reaching zero on the primary resource does.

| Key | Type | What it is |
|---|---|---|
| `condition` | text (a number is text too) | Condition applied to a player character at zero ("Unconscious"). |
| `deadCondition` | text (a number is text too) | Condition that marks a dead creature; monsters and NPCs get it straight away at zero. |
| `stableCondition` | text (a number is text too) | Condition for a creature at zero that is no longer making death saves. |
| `instantDeathAtMax` | true or false | When the damage left over after reaching zero is at least the maximum, the creature dies outright. |
| `nonPlayersDieAtZero` | true or false | Non-player creatures die at zero instead of falling unconscious. |

## DeathSaveRules

Death saves for a creature at zero on the primary resource (`vitals.deathSaves`). They need `vitals.down` as well; without this block a creature at zero simply stays down.

| Key | Type | What it is |
|---|---|---|
| `die` | text (a number is text too) | The dice a death save rolls. Default `1d20`. |
| `successOn` | whole number | The lowest roll that is a success. Default 10. |
| `successesToStabilize` | whole number | Successes that make the creature stable. Default 3. |
| `failuresToDie` | whole number | Failures that kill it (a hit while down counts as one, a critical hit as two). Default 3. |
| `criticalSuccess` | whole number | A natural roll this high regains one hit point. Null: no critical success. |
| `criticalFailure` | whole number | A natural roll this low counts as two failures. Null: no critical failure. |
| `successesKey` | text (a number is text too) | Pool keys the running tally is kept in. |
| `failuresKey` | text (a number is text too) | The pool key the failures are counted in. Default `DeathSaveFailures`. |

## DamageTypeRules

Damage types and the sheet lists that halve, cancel or double them.

| Key | Type | What it is |
|---|---|---|
| `types` | list of text (a number is text too) | The damage types the table offers when it deals damage ("fire", "slashing"). Empty: the table offers none. |
| `resistancesField` | text (a number is text too) | The sheet field listing the types the creature resists: that damage is halved, rounded down. Default `resistances`. |
| `immunitiesField` | text (a number is text too) | The sheet field listing the types the creature is immune to: that damage does nothing. Default `immunities`. |
| `vulnerabilitiesField` | text (a number is text too) | The sheet field listing the types the creature is vulnerable to: that damage is doubled. Default `vulnerabilities`. |

## ExhaustionRules

Exhaustion as a level counter kept in a pool, not a timed condition.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | The pool key the level is kept in. Default `Exhaustion`. |
| `maxLevel` | whole number | The highest level; reaching it kills the creature when the system has `vitals.down`. Default 6. |
| `condition` | text (a number is text too) | The condition name in Conditions this replaces in the picker. |

## HitDiceRules

A pool of hit dice: one per level for characters, the stat block's count for creatures.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | The pool key the remaining dice are kept in; the total is kept under the same key plus `_Max`. Default `HitDice`. |
| `levelField` | text (a number is text too) | The sheet field whose number is a character's count of hit dice. Default `level`. |
| `classField` | text (a number is text too) | The sheet field naming the class whose die a character spends. Default `class`. |
| `dieProperty` | text (a number is text too) | Class entry property with the die ("d10"). |
| `creatureDiceProperty` | text (a number is text too) | Creature property with the dice expression ("2d6") that gives count and die. |
| `attribute` | text (a number is text too) | Required: the attribute whose modifier is added to each die spent (5e: "con"). |

## ConcentrationRules

Holding an effect up by concentrating (most 5e spells with a duration).

| Key | Type | What it is |
|---|---|---|
| `condition` | text (a number is text too) | The condition a concentrating creature carries. |
| `saveAttribute` | text (a number is text too) | Required: the attribute whose save keeps concentration when the creature takes damage (5e: "con"). |
| `minimumDc` | whole number | The save's difficulty: half the damage taken, never below MinimumDc or above MaximumDc. |
| `maximumDc` | whole number | The highest the save's difficulty goes, however much damage was taken. Default 30. |

## RestRule

A rest the table offers (`vitals.rests`): what it refills, clears and lifts.

| Key | Type | What it is |
|---|---|---|
| `name` | text (a number is text too) | The rest's name, as the table's button shows it and a slot pool's `recoversOn` names it ("Long Rest"). |
| `restores` | list of text (a number is text too) | Resources refilled to their maximum ("HP"). |
| `clears` | list of text (a number is text too) | Resources set to zero ("TempHP"). |
| `hitDiceRegain` | text (a number is text too) | Hit dice regained: "all", "half" (rounded down, at least one) or "none". |
| `spendHitDice` | true or false | Whether the table offers spending hit dice during this rest. |
| `exhaustionDelta` | whole number | Change to the exhaustion level (-1 for a long rest). |
| `clearsConditions` | list of text (a number is text too) | Conditions removed by the rest. |

## SheetRules

The rules a system applies to a character sheet, declared in `system.yaml` so a homebrew system can have its own (or none). Everything here is evaluated by the core's sheet rule engine against the sheet's field values and the content entries they reference; nothing is specific to any one game.

| Key | Type | What it is |
|---|---|---|
| `enforcement` | text (a number is text too) | "strict": players can't save a sheet that breaks a rule (the DM always can); "warn": violations are shown but saving is allowed; "off": rules are not evaluated. A campaign can override this (house rules). |
| `attributes` | [AttributeRules](#attributerules) | How scores are generated (point buy, standard array, typed in), capped and raised. Null: scores are typed in, kept within each attribute's `min` and `max`. |
| `derived` | list of [DerivedField](#derivedfield) | Read-only fields computed from the sheet (proficiency bonus from level, ...). |
| `constraints` | list of [FieldConstraint](#fieldconstraint) | Conditions a single-pick reference field's chosen entry must satisfy. |
| `lists` | list of [ListRule](#listrule) | Multi-pick lists of entries on the sheet (spells, feats, knacks...). |
| `multiclass` | [MulticlassRules](#multiclassrules) | Levels split across more than one class. Null: one class only. |
| `slotPools` | list of [SlotPoolRule](#slotpoolrule) | Expendable slot pools (spell slots, pact slots...) computed from class tables and tracked during play as resources. |
| `hitPoints` | [HitPointRules](#hitpointrules) | Maximum hit points computed from class hit dice and Constitution. Null: hit points are typed in. |
| `armorClass` | [ArmorClassRules](#armorclassrules) | Armor class computed from equipment, class features and bonuses. Null: typed in. |
| `inventory` | [InventoryRules](#inventoryrules) | What a character carries and wears. Null: no inventory. |
| `skills` | [SkillRules](#skillrules) | Saving throws, skills and passive scores. Null: the sheet has none. |
| `spellcasting` | [SpellcastingRules](#spellcastingrules) | Spell save DC and spell attack bonus per spellcasting class. Null: not shown. |
| `proficiencies` | [ProficiencyRules](#proficiencyrules) | Armor and weapon training, checked against what the sheet equips. Null: no warnings. |
| `inherits` | list of [InheritRule](#inheritrule) | Values copied from a referenced entry onto the sheet when the reference changes (a species' speed and size). Null: nothing is copied. |
| `features` | list of [FeatureSource](#featuresource) | Where a sheet's features come from (a class's and a subclass's feature rows, a species' traits, the feats picked), so what a feature says it grants reaches the sheet: numbers it changes, entries it adds to a list, and a counter of its uses. Empty: features are text only. |
| `actions` | list of [ActionSource](#actionsource) | What a sheet can do with a roll: casting what it picked, attacking with what it holds. Each source names where the entries are and how their attack bonus, save DC and dice are worked out. Empty: the sheet has no actions. |

## AttributeRules

How ability scores are generated and adjusted.

| Key | Type | What it is |
|---|---|---|
| `methods` | list of text (a number is text too) | Methods a player may choose from: "point-buy", "standard-array", "manual". The DM can always use "manual" (typed in, capped at Max) and "printed" (a stat block's scores as the block prints them: no cap, no bonuses or increases added); imported creatures get "printed". |
| `default` | text (a number is text too) | The method a new sheet starts with, and the one a sheet that names none uses. Default `manual`. |
| `max` | whole number | Upper bound on a final score (base plus bonuses), except on a "printed" sheet. |
| `pointBuy` | [PointBuyRules](#pointbuyrules) | The budget and costs for `point-buy`; required when `methods` offers it. |
| `standardArray` | list of whole number | The fixed set of scores to assign, one per attribute, for "standard-array". |
| `bonuses` | [BonusRules](#bonusrules) | Bonuses granted by a referenced entry (a background's "+2/+1 among these abilities"). |
| `increases` | [IncreaseRules](#increaserules) | Score increases granted by entries picked in a list (an Ability Score Improvement feat). |

## PointBuyRules

Point buy (`sheetRules.attributes.pointBuy`): scores bought from a budget at a cost each.

| Key | Type | What it is |
|---|---|---|
| `budget` | whole number | The points to spend; spending more is a violation. Default 27. |
| `min` | whole number | The lowest score that may be bought, and where each score of a new point-buy sheet starts. Default 8. |
| `max` | whole number | The highest score that may be bought, before bonuses. Default 15. |
| `costs` | map of whole number | Cost of each score from Min to Max. |

## BonusRules

Bonuses a referenced entry lets the player spread over attributes (`sheetRules.attributes.bonuses`), such as a background's +2/+1.

| Key | Type | What it is |
|---|---|---|
| `label` | text (a number is text too) | What the sheet calls these bonuses, and the word its messages about them start with. Default "Bonuses". |
| `fromField` | text (a number is text too) | The sheet field whose referenced entry lists the eligible attributes. |
| `property` | text (a number is text too) | The property on that entry holding the eligible attribute keys, abbreviations or labels. |
| `allocations` | list of list of whole number | Allowed distributions, e.g. [[2, 1], [1, 1, 1]]: one bonus per distinct attribute. |

## IncreaseRules

Score increases that come from picks in a ListRule: each picked entry whose Property is a positive number grants that many points, spread by the player over the attributes its OptionsProperty lists (any attribute when it lists none), and can raise a score no higher than its CapProperty (or Max). The player's choice is stored on the pick itself: `"feat-id|str:1,dex:1"`.

| Key | Type | What it is |
|---|---|---|
| `label` | text (a number is text too) | What the sheet calls these increases, and the word its messages about them start with. Default "Score increases". |
| `fromList` | text (a number is text too) | The Key whose picks can grant increases. |
| `property` | text (a number is text too) | Entry property holding how many points the entry grants. |
| `optionsProperty` | text (a number is text too) | Entry property listing the attributes (keys, abbreviations or labels) the points may go to. |
| `capProperty` | text (a number is text too) | Entry property holding the highest score the increase may reach. |

## DerivedField

A read-only number worked out from the sheet (`sheetRules.derived`): proficiency bonus, initiative.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required: the name formulas and other rules read the value by (`proficiencyBonus`); must not be an actor field. |
| `label` | text (a number is text too) | The value's name as the sheet shows it. |
| `formula` | text (a number is text too) | A C#-style expression over the sheet's numeric fields and attribute scores, e.g. `2 + (level - 1) / 4`. Integer arithmetic; `floor`, `ceil`, `max`, `min` and `mod(key)` (an attribute's modifier) are available. |
| `category` | text (a number is text too) | A group heading for the value. Translated with the system's other headings, but this version does not group derived values by it. |
| `signed` | true or false | Whether the sheet shows the value with its sign, as a bonus (`+2`). Default true; false for a count or a threshold that is not added to anything (attacks per action `2`, critical hit from `19`). |

## FieldConstraint

Conditions the entry chosen in a single-pick reference field must meet (`sheetRules.constraints`); the field's picker offers only entries that do.

| Key | Type | What it is |
|---|---|---|
| `field` | text (a number is text too) | Required: the EntityReference actor field the rule is on (`subclass`). |
| `where` | list of [RuleCondition](#rulecondition) | The conditions; each one that fails is a violation with its `message`. |

## RuleCondition

One condition on a candidate entry. Exactly one comparison is set; Property names the candidate's property ("level", "classes", "category"...), or "id"/"name"/"tags".

| Key | Type | What it is |
|---|---|---|
| `property` | text (a number is text too) | Required: the candidate's property the comparison reads. |
| `equals` | any | Candidate value equals this constant. |
| `in` | list of text (a number is text too) | Candidate value is one of these. |
| `gte` | number | Candidate numeric value >= / <= a constant. |
| `lte` | number | Candidate numeric value <= this constant (with `gte`, both must hold). |
| `equalsField` | text (a number is text too) | Candidate value equals the raw value of this sheet field (an entry id, say). |
| `equalsPropertyOfField` | [FieldProperty](#fieldproperty) | Candidate value equals a property of the entry a sheet field references (e.g. the feat named by the chosen background). |
| `containsNameOfField` | text (a number is text too) | Candidate list property contains the display name of the entry a sheet field references. |
| `lteField` | text (a number is text too) | Candidate numeric value <= / >= a numeric sheet field. |
| `gteField` | text (a number is text too) | Candidate numeric value >= this numeric sheet field. A candidate without the property passes. |
| `lteLookup` | [LookupRule](#lookuprule) | Candidate numeric value <= a value looked up in a referenced entry's table. |
| `message` | text (a number is text too) | Shown when the condition fails. |

## FieldProperty

A property of the entry a sheet field references: `{ field: background, property: feat }`.

| Key | Type | What it is |
|---|---|---|
| `field` | text (a number is text too) | The EntityReference actor field naming the entry. |
| `property` | text (a number is text too) | The property read from that entry. |

## LookupRule

Reads a value out of a row table on a referenced entry: the sheet's Field names an entry; that entry's Table property is a list of rows; the row whose RowKey equals the sheet's ByField value (or the highest row not above it) supplies Column.

| Key | Type | What it is |
|---|---|---|
| `field` | text (a number is text too) | Required: the EntityReference actor field naming the entry whose table is read. |
| `table` | text (a number is text too) | Required: the entry property holding the table, a list of rows. |
| `byField` | text (a number is text too) | The sheet value the row is chosen by (with multiclass rules, their `classLevelKey`). Default `level`. |
| `rowKey` | text (a number is text too) | The column each row is keyed by. Default `level`. |
| `column` | text (a number is text too) | Required: the column whose number is read. |

## ListRule

A list of entries picked on the sheet (`sheetRules.lists`): spells, feats, fighting styles. The sheet stores the picks under Key.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required, unique: the sheet key the picks are stored under, and the name other rules use for the list; must not be an actor field. |
| `label` | text (a number is text too) | The list's name as the sheet shows it, and the word its messages start with. |
| `description` | text (a number is text too) | A line of help the sheet shows with the list. |
| `targetEntityType` | text (a number is text too) | Required: the entity type the picks are of; one the system declares. |
| `where` | list of [RuleCondition](#rulecondition) | Conditions every pick must meet. With several classes, a pick is allowed when it meets them for any one class. |
| `maxCount` | [CountRule](#countrule) | How many may be picked. Null: any number. |
| `category` | text (a number is text too) | A group heading for the list. Translated with the system's other headings, but this version does not group lists by it. |
| `repeatableProperty` | text (a number is text too) | Candidate property that, when true, lets the same entry be picked more than once. |

## CountRule

A limit on a list (`maxCount`): a constant, a table lookup per class, or both, plus a number from a referenced entry.

| Key | Type | What it is |
|---|---|---|
| `value` | whole number | A fixed limit, counted once however many classes the sheet has. |
| `lookup` | [LookupRule](#lookuprule) | A limit read from each class's table and added up across classes. Unknown (no limit) until an entry is chosen. |
| `plus` | whole number | A number added to the limit. Default 0. |
| `also` | list of [LookupRule](#lookuprule) | More tables added to Lookup for every class (a subclass's own grants). |
| `fromField` | [FieldProperty](#fieldproperty) | A number on the entry a sheet field references, added once (a species' extra feat). |

## MulticlassRules

Multiclassing: the sheet's ClassField is the first class, Key holds the others as `"class-id:levels"`, and LevelField stays the total character level. Every class gets its own ClassLevelKey value, so table lookups written with `byField: classLevel` read the right row for each class; list limits are summed across classes and a list entry is allowed when any one class allows it.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | The sheet key holding the classes after the first, each as `"class-id:levels"`; must not be an actor field. Default `multiclass`. |
| `label` | text (a number is text too) | What the sheet calls the other classes, and the word its messages about them start with. Default "Additional classes". |
| `classField` | text (a number is text too) | Must be an EntityReference actor field: the first class. Default `class`. |
| `levelField` | text (a number is text too) | Must be an actor field: the total character level, which the other classes' levels come out of. Default `level`. |
| `classLevelKey` | text (a number is text too) | The name each class's own level goes by in table lookups (`byField: classLevel`); must not be an actor field. Default `classLevel`. |
| `prerequisitesProperty` | text (a number is text too) | Class entry property listing prerequisite options, each like `"STR 13+"` or `"DEX 13+, WIS 13+"` (all of these). Any one option is enough. |
| `requireForFirstClass` | true or false | Whether the first class must also meet its own prerequisites once you multiclass. |
| `subclassField` | text (a number is text too) | The sheet field naming the first class's subclass. Each other class stores its own after its levels: `"class-id:levels:subclass-id"`. Null: subclasses are not tracked per class. |
| `subclassClassProperty` | text (a number is text too) | The subclass entry's property naming the class it belongs to; the sheet offers a class only its own subclasses. |
| `subclassLevel` | whole number | The class level at which a subclass can be chosen; a subclass before it is a violation. |

## SlotPoolRule

One pool of expendable slots. For each of the sheet's classes, the class entry's Table row at that class's level supplies Column: either `"4/3/2"` (slots per slot level from 1 up) or, when SlotLevelColumn is set, a count of slots that are all of the level in that column. Classes are filtered by their CasterTypeProperty. When two or more classes contribute and Combine is set, their weighted levels are added up and looked up in the combined table instead. During play each slot level is a resource: `{Key}_{level}` (remaining) and `{Key}_{level}_Max`.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required, unique: the prefix of the pool's sheet keys (`slots_1`, `slots_1_Max`). Default `slots`. |
| `label` | text (a number is text too) | The pool's name as the sheet and the table show it. Default "Spell slots". |
| `unit` | text (a number is text too) | One slot, as a button's label and the table log name it ("spell slot": "Use a level 3 spell slot"). The pool's label when not set. Translated through the system's values, like the label. |
| `field` | text (a number is text too) | The sheet field naming the class entry, and the sheet value whose row is read (with multiclass rules, use their ClassLevelKey). |
| `byField` | text (a number is text too) | The sheet value whose row of the table is read. Default `level`. |
| `table` | text (a number is text too) | Required: the class entry property holding the table, one row per level (each row's `level` is its key). Default `spellcasting`. |
| `column` | text (a number is text too) | Required: the row's column with the slots. Default `slots`. |
| `slotLevelColumn` | text (a number is text too) | When set, the row's column giving the level of every slot, and `column` is then a plain count (pact magic). None: `column` reads `"4/3/2"`. |
| `casterTypeProperty` | text (a number is text too) | The class entry property with its caster type, which `includeCasterTypes`, `excludeCasterTypes` and `combine.weights` read. Default `casterType`. |
| `includeCasterTypes` | list of text (a number is text too) | Only classes with one of these caster types count (empty: any). |
| `excludeCasterTypes` | list of text (a number is text too) | Classes with one of these caster types do not count. |
| `combine` | [SlotCombineRule](#slotcombinerule) | How two or more contributing classes combine into one table. Null: each class's slots are added up level by level. |
| `recoversOn` | list of text (a number is text too) | Rest names that refill this pool ("Short Rest", "Long Rest"); the table offers one button per name. |

## SlotCombineRule

How several classes' slots combine: each class adds its level times its caster type's weight (rounded per Rounding), and the sum picks a row of Table (row 1 is caster level 1).

| Key | Type | What it is |
|---|---|---|
| `weights` | map of number | Caster type -> how much each of its class levels counts (`full: 1, half: 0.5`); a type not listed counts 0. |
| `rounding` | text (a number is text too) | "up", "down" or "nearest", applied once to the weighted sum of every class's levels. |
| `table` | list of text (a number is text too) | Slots per caster level, each like `"4/3/2"`. |

## HitPointRules

Maximum hit points from the sheet's classes. Levels are taken first class first, then the other classes in order. The very first level gives what FirstLevel says (the full hit die by default), every later level the die's Average (or the player's roll in "rolled" mode), plus the Attribute modifier, never less than MinimumPerLevel. Because the modifier is read from the final score, raising Constitution raises maximum hit points for every level at once. The result is written to Key only while the sheet's ModeKey is not "manual" (a sheet without the key is manual, so existing characters and imported creatures keep their numbers).

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Sheet key that receives the maximum (the primary resource's max key). |
| `currentKey` | text (a number is text too) | Sheet key of current hit points: it follows the maximum when it was at full health. |
| `classField` | text (a number is text too) | Must be an EntityReference actor field: the first class, whose entry has the hit die. Default `class`. |
| `levelField` | text (a number is text too) | Must be an actor field: the character's total level. Default `level`. |
| `dieProperty` | text (a number is text too) | Class entry property holding the hit die, like "d10". |
| `attribute` | text (a number is text too) | Required: the attribute whose modifier is added per level (5e: "con"). |
| `minimumPerLevel` | whole number | The least a level gives once the modifier is added. Default 1. |
| `firstLevel` | text (a number is text too) | What the character's very first level gives: FullDie, or "average" for the die's average. |
| `average` | text (a number is text too) | The gain a level takes in place of a roll: HalfPlusOne (a d10 gives 6) or Half (a d10 gives 5). |
| `modes` | list of text (a number is text too) | "average", "rolled" or "manual". |
| `default` | text (a number is text too) | The mode a new sheet starts in, and the one used when a sheet names a mode not in `modes`; must be one of `modes`. Default `average`. |
| `modeKey` | text (a number is text too) | The sheet key holding the sheet's mode; must not be an actor field. Default `hpMode`. |
| `rollsKey` | text (a number is text too) | Sheet key holding the player's rolls for levels 2 and up, in level order. |
| `perLevelBonus` | [FieldProperty](#fieldproperty) | Entries whose number adds to every level's gain (Dwarven Toughness): the sheet field that names the entry and the property holding the bonus. |

## ArmorClassRules

Armor class. With body armor worn: its base plus the Dexterity modifier (limited by the armor's cap, none for a cap of 0). Without: the best of Unarmored and the classes' own formulas (ClassFormulaProperty, such as 10 + Dex + Con). Then every equipped item's bonus, the bonus of picks in BonusLists and the sheet's MiscField are added. Written to Key while ModeKey is "auto"; a sheet without that key is manual.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | The sheet key that receives the armor class. Default `armorClass`. |
| `modeKey` | text (a number is text too) | The sheet key holding `auto` or `manual`: new sheets start `auto`; must not be an actor field. Default `acMode`. |
| `unarmored` | text (a number is text too) | Required: the formula over the sheet's numbers when nothing is worn and no class formula is better (5e: "10 + mod(dex)"). |
| `dexAttribute` | text (a number is text too) | Required: the attribute added to body armor's base, up to its cap (5e: "dex"). |
| `classFormulaProperty` | text (a number is text too) | Class entry property with an unarmored formula, and the one saying a shield is still allowed. |
| `classShieldProperty` | text (a number is text too) | Default `unarmoredDefenseShield`: a class entry property that, when false, means the class's unarmored formula does not count while a shield is carried. Absent: it does. |
| `typeProperty` | text (a number is text too) | Item properties: the armor type ("light", "medium", "heavy", "shield"), the base AC of body armor, its Dexterity cap (absent: uncapped; 0: no Dexterity), and the flat bonus any item gives. |
| `baseProperty` | text (a number is text too) | The item property with body armor's base armor class; an item that has it is body armor. Default `armorBase`. |
| `dexCapProperty` | text (a number is text too) | The item property capping the Dexterity added to body armor (absent: no cap; 0: none added). Default `armorDexCap`. |
| `bonusProperty` | text (a number is text too) | The property with a flat bonus, on any equipped item (a shield, a ring) or on a pick in `bonusLists`. Default `armorClassBonus`. |
| `shieldType` | text (a number is text too) | The TypeProperty value that marks a shield. |
| `bonusLists` | list of text (a number is text too) | Lists whose picks may add BonusProperty (a fighting style feat), and the pick property that limits the bonus to wearers of body armor. |
| `requiresArmorProperty` | text (a number is text too) | The pick property that, when true, gives its bonus only while body armor is worn. Default `armorClassRequiresArmor`. |
| `miscField` | text (a number is text too) | Numeric sheet field for everything else (a spell, a magic effect). |

## InventoryRules

The items a sheet carries, stored under Key as `"item-id|qty:2,equipped:1"`. Items may come from any of Types. At most one suit of armor and one shield may be equipped.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | The sheet key the items are stored under; must not be an actor field. Default `inventory`. |
| `label` | text (a number is text too) | What the sheet calls the inventory, and the word its messages start with. Default "Equipment". |
| `types` | list of text (a number is text too) | The entity types items may be of; each must be one the system declares. Default `[equipment]`. |
| `quantityChoice` | text (a number is text too) | The name the item's quantity is stored under after its id (`qty:2`); none stored is 1. Default `qty`. |
| `equippedChoice` | text (a number is text too) | The name that marks an item worn or wielded (`equipped:1`). Default `equipped`. |
| `weightProperty` | text (a number is text too) | The item property holding one item's weight; the sheet adds them up, times their quantities. |
| `weightUnit` | text (a number is text too) | The word the total is shown with ("lb"), translated like the system's other words. None shows the number alone. |
| `capacity` | text (a number is text too) | A formula for how much a character can carry ("str * 15"); carrying more is a warning. None: no limit. |

## SkillRules

| Key | Type | What it is |
|---|---|---|
| `label` | text (a number is text too) | What the sheet calls the skills, and the word its messages about them start with. Default "Skills". |
| `bonusKey` | text (a number is text too) | The derived value skill and save bonuses add (5e: "proficiencyBonus"). Required unless Ranks and `saves.ranks` are both named, since it is what a default rank is worth. |
| `proficienciesKey` | text (a number is text too) | The sheet list of skills the player marked proficient, by key or label. Default `skillProficiencies`. |
| `expertiseKey` | text (a number is text too) | The sheet list of skills with expertise (the bonus counted twice); expertise without proficiency is a warning. Default `skillExpertise`. |
| `classField` | text (a number is text too) | The sheet field naming the first class, whose saving throws and skill choices count. Default `class`. |
| `savingThrowsProperty` | text (a number is text too) | The class entry property listing the attributes (keys, abbreviations or labels) whose saving throws are proficient. Default `savingThrows`. |
| `backgroundField` | text (a number is text too) | The sheet field naming the entry that grants skills on its own (a background). Default `background`. |
| `backgroundProperty` | text (a number is text too) | The property on that entry listing the skills it grants. Default `skillProficiencies`. |
| `classChoicesProperty` | text (a number is text too) | The first class's property saying how many skills it lets a character choose: a number, or text whose first number is it ("Choose 2: Acrobatics, …"). More chosen than that (beyond the background's) is a warning, since a species or feat may grant more. |
| `list` | list of [SkillDefinition](#skilldefinition) | The skills, in the order the sheet shows them. |
| `passives` | list of [PassiveRule](#passiverule) | Scores worked out from a skill's bonus (passive Perception). |
| `ranks` | list of [RankDefinition](#rankdefinition) | The ranks a skill can hold, lowest first, each with the sheet list that holds the skills the player set at it and what it is worth. A skill's rank is the highest list that holds it, or that a feature, the background or a choice gives. Left out, the ranks are the two lists above: `proficient` (ProficienciesKey, worth BonusKey) and `expert` (ExpertiseKey, worth twice it). Naming ranks makes ProficienciesKey and ExpertiseKey unused. |
| `saves` | [SaveRules](#saverules) | Saving throws as a ranked list over the attributes; see SaveRules. Left out: one rank, as described there. |

## SkillDefinition

One skill: the attribute whose modifier it uses.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required, unique: the skill's key (`athletics`). Sheets and entries may name a skill by its key or its label. |
| `label` | text (a number is text too) | The skill's name as the sheet shows it ("Athletics"). |
| `canonicalLabel` | text (a number is text too) | The label as system.yaml wrote it. Sheets and backgrounds list skills by it ("Athletics"), so the rules match on it even when a localized copy shows Label in another language. |
| `attribute` | text (a number is text too) | Required: the attribute key whose modifier the skill adds (`str`). |

## PassiveRule

A score of the form base + a skill's bonus (passive Perception is 10 + Perception).

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | The passive score's key (`passivePerception`). |
| `label` | text (a number is text too) | Its name as the sheet shows it ("Passive Perception"). |
| `skill` | text (a number is text too) | Required: the key of the skill in `list` whose bonus is added. |
| `base` | whole number | The number the skill's bonus is added to. Default 10. |

## RankDefinition

Saving throws and skills, as ranked lists. A skill's rank is the highest of: the sheet list that holds it (Ranks; by default ProficienciesKey is rank 1 and ExpertiseKey rank 2), the BackgroundProperty of the entry BackgroundField names (rank 1), a feature's grant, and the player's choices. Saving throws are ranked the same way, over the attributes: the first class's SavingThrowsProperty puts each at rank 1. What a rank adds is its Value (by default the derived field BonusKey, twice it for rank 2).

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required, unique within the list: the rank's key (`proficient`, `expert`). A grant names a rank by it, or by its number (1 is the first rank listed). |
| `label` | text (a number is text too) | The rank's name as the sheet shows it ("Expert"). Default: the key. |
| `sheetKey` | text (a number is text too) | Required, unique across every ranked list: the sheet list holding the keys the player set at this rank (`skillExpertise`). |
| `value` | text (a number is text too) | What a key at this rank adds to its check: a formula over the sheet's numbers and derived values (`proficiencyBonus * 2`, `level + 4`). Empty: the rank is worth its number (1 for the first rank), which a roll formula can read. |

## SaveRules

Saving throws as a ranked list over the system's attributes (the first class's `savingThrows` put each of its attributes at the first rank). Without it a saving throw has one rank, `proficient`, kept in the sheet list `saveProficiencies` and worth the skills' `bonusKey`.

| Key | Type | What it is |
|---|---|---|
| `ranks` | list of [RankDefinition](#rankdefinition) | The ranks a saving throw can hold, lowest first. |

## SpellcastingRules

Each class that has a AbilityProperty casts with that attribute: save DC is SaveBase + the derived BonusKey + the modifier, the attack bonus is the bonus + the modifier.

| Key | Type | What it is |
|---|---|---|
| `abilityProperty` | text (a number is text too) | The class entry property naming the attribute it casts with (key, abbreviation or label). A class without it does not cast. Default `spellcastingAbility`. |
| `bonusKey` | text (a number is text too) | Required: the derived value save DCs and attack bonuses add (5e: "proficiencyBonus"). |
| `saveBase` | whole number | The number a spell save DC starts from. Default 8. |

## ProficiencyRules

Warnings (never violations: a feat or feature may grant what the class lacks) for equipped armor and weapons the first class's training doesn't cover, plus the armor's Strength minimum and stealth penalty. Class training is a list of names such as "Light Armor", "Shields", "Martial Weapons" or "All armor".

| Key | Type | What it is |
|---|---|---|
| `armorProperty` | text (a number is text too) | The class entry property listing its armor training. Default `armorProficiencies`. |
| `weaponProperty` | text (a number is text too) | The class entry property listing its weapon training, by weapon tag or name. Default `weaponProficiencies`. |
| `classField` | text (a number is text too) | The sheet field naming the first class, whose training counts. Default `class`. |
| `armorTraining` | map of text (a number is text too) | Equipment property → the training name that covers it: "light" → "Light Armor". |
| `strengthProperty` | text (a number is text too) | Item property holding the Strength an armor needs, and the one flagging Stealth disadvantage. |
| `strengthAttribute` | text (a number is text too) | The attribute an armor's Strength minimum is checked against (5e: "str"). No default: a system without strength minimums leaves it out and none is checked. |
| `stealthProperty` | text (a number is text too) | The armor property that, when true, brings a warning of disadvantage on Stealth. Default `stealth`. |
| `weaponTag` | text (a number is text too) | Equipment tags that mark a weapon. |
| `allArmor` | text (a number is text too) | A class's armor training that covers every armor but shields ("All armor"), matched at the start of the training's name; null when the system has none. |
| `pluralSuffix` | text (a number is text too) | The plural ending dropped when a weapon is matched to a class's weapon training by name, so "Longswords" covers a Longsword ("s" in English); null to match names as they are. |

## InheritRule

Copies properties of the entry Field names onto sheet keys the moment the player picks a different entry, then leaves them alone so a hand-edited speed survives later saves. AppliedKey remembers which entry was last copied from.

| Key | Type | What it is |
|---|---|---|
| `field` | text (a number is text too) | Required: the EntityReference actor field whose chosen entry the values are copied from (`species`). |
| `appliedKey` | text (a number is text too) | Required: a sheet key, not an actor field, where the id of the entry last copied from is kept (`speciesApplied`). |
| `copy` | map of text (a number is text too) | Entry property → sheet key. |

## FeatureSource

One place features come from. Either Field (the entry a reference field names) or List (every entry picked in a list). With Property the features are that property's rows (`name`, `description`, and LevelColumn); without it the entry itself is one feature. A feature, row or entry, may carry: `modifiers`: `[{ target, amount, type, when }]`, as everywhere else. `target` is the armor class or hit point key, a derived value, or a Number actor field; `amount` is a number or a formula; a `type` the system stacks as `max` (or `best`) sets a floor ("at least 120"), as `min` a ceiling ("at most 18"), instead of adding; `highest` and `lowest` add only the largest or smallest bonus of the type. The rule is declared in the system's `modifierTypes`; a type it does not name, or none, adds. `adds`: `[{ to, entries, level }]`: entries put on a pick list (by id or name) or values on a tag list, from `level` on. They leave again when the feature does. On a ranked list (`to: skills` or `to: saves`, or a rank's own sheet list; see Ranks) an item takes more: `rank` (a rank key or number; none means the first rank) puts the entries at that rank, locked, with the feature as its source; `raise: 1` moves them that many ranks up, stopping at the top (`rank` and `raise` exclude each other). Such grants are worked out in the evaluation and never written to the sheet. Instead of `entries`, an item may leave the choice to the player: `{ to, choice, choose, from, minRank, maxRank, rank | raise }`. `choice` names the choice within its entry (required; it must be unique there, and the sheet stores the picks under `choice__{entryId}__{choice}`, `choice__{entryId}#2__{choice}` for the second copy of a repeatable feat); `choose` is how many keys; `from` is a list of keys, `any` (the default) or a filter on a key's own fields (`{ attribute: [str, dex] }`); `minRank` and `maxRank` are the ranks a key must already hold to be offered ("a skill you are proficient in"), checked against the ranks the sheet has so far without this choice's own picks. A choice not yet made is a warning, never a violation; a pick that no longer fits is a violation that says to choose again. On an item to a list of Library entries (the granted spells), `free` lets the character cast each spell without a slot: `free: atWill`, or a counter of uses written as `uses` is (`{ max, recoversOn, recovers, regain, key, label }`). Each spell gets its own counter, `free-{spell id}` (the `key` makes one shared counter instead), and the spell's action gets one more way to pay, after its slots, at the spell's own level. `uses`: `{ max, key, recoversOn, recovers, regain }`: a counter of uses, `max` a number or a formula, refilled by the rests named in `recoversOn` (a full refill on each). `key` names the pool, so features that name one share it: the highest maximum wins and the sheet warns. `recovers` is `[{ on, amount, when }]`: `on` a moment (a rest, a moment the system declares in `moments`, `initiative` or `dawn`) or `manual`; `amount` is `all`, a number, a formula or dice; `when` is `empty` or a formula. `regain: gm` lets only the GM add a use back by hand. `label` names the counter on the sheet, the Table and the log when that is not the feature's own name (a Breath Weapon shared by ten lineage rows). `spends`: `{ key, amount }`: the feature gets a Use button that spends `amount` (a number, a formula, or `ask` for the player to choose) from the pool `key`, a feature counter's `key` or a pool's own sheet key. `options` and `choose` ("choose one of the following"): `options: [{ name, description, and any key above }]` with `choose: { id, count, onChange, changesOn }` (see RowChoose; `id` is required). Only the options picked become features, named "{feature}: {option}". The pick is stored as `choice__{entryId}__{id}`; a choice inside a picked row is stored under the parent's id first, `choice__{entryId}__{parentId}__{id}`. The same shape, one level up, is Choose.

| Key | Type | What it is |
|---|---|---|
| `field` | text (a number is text too) | The EntityReference actor field whose entry carries the features (`class`, `subclass`, `species`). With multiclass rules, the class and subclass fields are read once per class. |
| `list` | text (a number is text too) | Or: the key of a list in `lists` whose picked entries carry them (`feats`). On a sheet kind (`sheetKinds[].features`) a source is this alone, with `property`, `entries`, `choose` and `levelColumn`: `list` names a list of the kind's own `lists`; `field` and `byField` are refused. |
| `property` | text (a number is text too) | The entry property holding feature rows (`features`, `traits`). None: the entry is one feature and carries `modifiers`, `adds` and `uses` among its own properties. |
| `levelColumn` | text (a number is text too) | A row's column saying from which level it applies. Default `level`; a row without it always applies. |
| `firstOnly` | true or false | With Field on a source read once per class (`class`, `subclass` under multiclass rules): read for the first class only. A grant on the class entry itself (the skills its first level gives, which a second class does not repeat) is such a source. |
| `label` | text (a number is text too) | The name of the feature an entry becomes when the source has no Property (the entry is one feature). Default: the entry's own name. A class entry that carries a grant of its own is not best named "Rogue" in "Rogue (Rogue): choose 4 more skills". |
| `byField` | text (a number is text too) | The sheet value a row's level is compared with (`classLevel`, `level`). None: rows are not gated. |
| `entries` | list of text (a number is text too) | Only the entries with these ids carry features through this source. Empty: every entry the source reaches. Lets a Choose apply to one species and not the others whose entries have the same property. |
| `choose` | [RowChoose](#rowchoose) | Makes the player pick one row (or `count` rows) of the entry's Property: only the rows picked become features, the others give nothing, counters included. Needs a Property and a Field. Written `choose: { id, label, count, onChange, changesOn }` or, for the id alone, `choose: lineage`. The pick is stored on the sheet as `choice__{entryId}__{id}`, a list holding the rows' names as written in the entry's own language. |

## RowChoose

How the player picks among rows: of a feature source's property (Choose) or of a feature row's own `options` (`choose: { id, count, onChange, changesOn }` beside `options`). Id names the choice within its entry (required; unique there); a choice nested in a chosen row is stored under `{parentId}__{id}`.

| Key | Type | What it is |
|---|---|---|
| `id` | text (a number is text too) | Required: the choice's name within its entry. The sheet stores the pick under `choice__{entryId}__{id}`. |
| `label` | text (a number is text too) | What the picker calls the choice ("Lineage"). Default: the feature's or the entry's name. |
| `count` | whole number | How many rows the player picks. Default 1. |
| `onChange` | text (a number is text too) | What happens to counters when the pick changes: `keep` (default), `refill` or `empty`. A change is never blocked, and is logged at a live table. |
| `changesOn` | list of text (a number is text too) | Moments (a rest, a moment the system declares in `moments`, `initiative` or `dawn`) at which the pick is usually changed. The picker says so, and the rest flow offers the change after that rest. Never a limit. |

## ActionSource

One kind of action and the entries that give it: the entries picked in Lists, or the items carried (Inventory). What an entry rolls is the map under its Property: `attack`: `melee` or `ranged`: the action starts with an attack roll (AttackBonus). `abilities`: the attributes the attack may use; the best one is `abilityMod` in the formulas. `save`: the attribute of the saving throw it asks for, against SaveDc. `damage`: the dice (`8d6`, `1d4+1`), `damageType` their kind, `heals: true` when they heal. `addAbility: true`: the dice add AbilityBonus. `perSlot`: dice added for each slot level above the entry's own; `atLevels`: `{ 5: 2d10 }`, the dice from that value of ScaleBy on; `alternatives`: `{ two-handed: 1d10 }`, other dice it may roll. The formulas read the sheet's numbers and derived values, and names of their own: `abilityMod`, `trained` (1 when the first class is trained with the item, see ProficiencyRules), and `castAttack`, `castSave` and `castMod` (the best of the sheet's casting classes, see SpellcastingRules).

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required, unique. |
| `label` | text (a number is text too) | The verb on the button ("Cast", "Attack"). |
| `lists` | list of text (a number is text too) | Keys of lists in `lists` whose picked entries are actions of this kind. |
| `inventory` | text (a number is text too) | Or the inventory: `equipped` for what is worn or wielded, `carried` for everything. |
| `tag` | text (a number is text too) | Only inventory items with this tag (`weapons`). None: every item that has Property. |
| `property` | text (a number is text too) | The entry property holding what it rolls. Default `use`. A picked entry without it is still listed (casting it spends a slot and rolls nothing); an item without it is not. |
| `levelProperty` | text (a number is text too) | The entry property with its level; an entry of level 1 or more spends a slot of Spends. |
| `spends` | list of text (a number is text too) | Keys of the `slotPools` an action may spend from: one slot of the entry's level or higher. |
| `attackBonus` | text (a number is text too) | Formula for the number added to the attack roll. |
| `saveDc` | text (a number is text too) | Formula for the DC of the saving throw. |
| `damageBonus` | text (a number is text too) | Formula for a number added to every entry's dice. |
| `abilityBonus` | text (a number is text too) | Formula for the number added to the dice of an entry that says `addAbility`. |
| `scaleBy` | text (a number is text too) | The sheet value an entry's `atLevels` is read by. Default: the system's level field. |
| `count` | text (a number is text too) | A derived value or formula for how many of this action a turn allows (`attacks`). The Actions panel shows "×N" when it works out to more than one. Null: not shown. The formulas read the same names as AttackBonus. |
| `critical` | text (a number is text too) | A derived value or formula for the lowest die face of the system's check die (`dice.check`) that is a critical hit (`critFrom`). The panel shows "crit 19–20" only when it is below the die's top face. Null: not shown. A critical's extra dice are not rolled. |

## SheetKindDefinition

A kind of sheet (`character`, `crew`): its values, modifiers, lists and keyed lists.

| Key | Type | What it is |
|---|---|---|
| `kind` | text (a number is text too) | Required: the kind's key (`character`, `crew`), which each sheet of it records. |
| `label` | text (a number is text too) | The kind's name as the app shows it (where a new sheet's kind is chosen); `kind` when empty. |
| `role` | text (a number is text too) | The host role: `player`, `npc`, `creature` or `shared`. |
| `values` | list of [ValueDefinition](#valuedefinition) | The sheet's numbers and choices, worked out or entered. Their order does not matter: each is worked out when first read. |
| `modifiers` | list of [ModifierDefinition](#modifierdefinition) | Modifiers every sheet of the kind has, beside those from picks and conditions. |
| `lists` | list of [PickListDefinition](#picklistdefinition) | Lists of picks (classes, feats, items), each with fields of its own per pick. |
| `features` | list of [FeatureSource](#featuresource) | Where the kind's sheets get features (special abilities, upgrades) from, as `sheetRules.features` does for a system with sheet rules; on a sheet kind a source names a `list` of the kind's `lists` and nothing else (no `field`, no `byField`). Each pick's own properties carry the grants: `modifiers` (targets are the kind's values, keyed entries or groups), `uses` and `spends` (counters), `options` with `choose`, and `adds` with a `choice` over a keyed list. A list that is a source may not also have `modifiersFrom`, or its modifiers would count twice. |
| `keyedLists` | list of [KeyedListDefinition](#keyedlistdefinition) | Fixed sets of keys (skills, actions), each key with its own fields and worked-out values. |
| `tracks` | list of [TrackDefinition](#trackdefinition) | Values that go up and down in play (hit points, stress, slots), with their bounds and what they bring. |
| `layout` | list of [LayoutSection](#layoutsection) | How the sheet is laid out: sections of widgets bound to keys. Empty: the host's generic layout (every value, track, list and keyed list in turn). |

## ValueDefinition

A number (or a choice) on the sheet: worked out by Formula, or entered (Input).

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required: the name the sheet and formulas know the value by. |
| `label` | text (a number is text too) | The value's name as the sheet shows it; `key` when unset. |
| `formula` | text (a number is text too) | How the value is worked out, over the sheet's values, attributes, lists and functions. None: 0 (or, with `input`, what was entered). |
| `modifiers` | any | A target or a list of targets (`AC`, `'@saves'`) whose modifiers are added on top of the formula. |
| `without` | list of any | Modifier types left out: a type name, or `{ type, positiveOnly: true }` to leave out only its bonuses. |
| `input` | true or false | Entered by the player rather than worked out. |
| `options` | list of text (a number is text too) | For an `input` value: the choices the sheet offers in a dropdown. Not checked: a value set another way is kept. |
| `default` | any | For an `input` value: its value until the player sets one. |

## ModifierDefinition

A typed contribution to a target: `{ target: AC, amount: 2, type: dodge, source: Dodge }`.

| Key | Type | What it is |
|---|---|---|
| `target` | text (a number is text too) | A value key, a keyed-list entry (`skills.balance`) or a group (`'@armorPenalty'`). |
| `amount` | text (a number is text too) | A number or a formula. |
| `type` | text (a number is text too) | The modifier type; none is `untyped`. |
| `source` | text (a number is text too) | Where the modifier comes from, as the `modifiers` widget shows it. A condition's modifier without one is credited to the condition, a pick's to the entry. |
| `when` | text (a number is text too) | A formula; the modifier counts only while it holds. |

## PickListDefinition

Picks of an entry type, each with its own fields.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required: the sheet key the picks are stored under, and the name formulas and layouts use for the list. |
| `label` | text (a number is text too) | The list's name as the sheet shows it. |
| `type` | text (a number is text too) | The entry type the picks are of; none for a list of plain records (`hpRolls`). |
| `description` | text (a number is text too) | A line about the list. Reserved; not read by this version. |
| `itemFields` | list of [ItemFieldDefinition](#itemfielddefinition) | The fields each pick carries besides its entry (`classLevel`, `equipped`). |
| `modifiersFrom` | [ModifiersFromDefinition](#modifiersfromdefinition) | Each pick's entry property holding modifiers, and when a pick's count (`"i => i.equipped"`). |
| `prerequisites` | text (a number is text too) | The entry property holding prerequisites, checked on the sheet without the pick. |
| `rules` | list of [SheetCheckDefinition](#sheetcheckdefinition) | Checks on the list; one with a lambda (`"c => ..."`) checks every pick. |

## ItemFieldDefinition

A field each pick or keyed entry carries (`classLevel`, `equipped`, `points`).

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required: the field's name, as formulas read it on the pick or row (`item.classLevel`). |
| `label` | text (a number is text too) | The column heading; the key when unset. |
| `type` | text (a number is text too) | `Number` (the default), `Boolean` (a checkbox), or anything else for text. |
| `default` | any | The value a new pick, keyed entry or row has (none: 0 for a number, false for a flag). |
| `min` | number | Lower bound for a number: an intent's change stops at it, and a campaign table reports a value below it. |
| `max` | number | Upper bound for a number: an intent's change stops at it, and a campaign table reports a value above it. |
| `options` | list of text (a number is text too) | The values a text field may take. Not checked; this version uses them only to type the field in the generated `codex.d.ts`. |
| `display` | text (a number is text too) | How the field is drawn (`dots`). Reserved; not read by this version. |

## ModifiersFromDefinition

Where a list's picks carry modifiers (`modifiersFrom`): each pick's entry property holding a list of `{ target, amount, type, source, when }`.

| Key | Type | What it is |
|---|---|---|
| `property` | text (a number is text too) | The entry property holding the modifiers. Default `modifiers`. |
| `when` | text (a number is text too) | A lambda over the pick (`"i => i.equipped"`): only picks it holds for bring their modifiers. None: every pick does. |

## SheetCheckDefinition

A problem with key Key when Check is false; a lambda checks every item.

| Key | Type | What it is |
|---|---|---|
| `check` | text (a number is text too) | Required: a formula that must be true; a lambda (`"s => s.ranks <= s.maxRanks"`) must be true for every item of the list. |
| `key` | text (a number is text too) | Required: the problem's translation key (`Actor.Rule.RankMax`); also its text when there is no `message`. |
| `message` | text (a number is text too) | The problem as shown, with `{name}` filled in from the item or the sheet's values (`{label}`, `{maxRanks}`). |

## KeyedListDefinition

A fixed set of keys (skills, actions), each with fields the player sets and values worked out per key.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required: the sheet key the entries are stored under, and the list's name in formulas (`skills.balance`) and layouts. |
| `label` | text (a number is text too) | The list's name as the sheet shows it. |
| `itemFields` | list of [ItemFieldDefinition](#itemfielddefinition) | The fields the player sets on each entry (`points`, `rating`). |
| `keys` | list of [KeyedEntryDefinition](#keyedentrydefinition) | The fixed entries the list has. |
| `derive` | map of text (a number is text too) | Per-key values as lambdas over the entry (`ranks: "s => s.points"`). `value` is the entry's own value, with the modifiers that target it added. |
| `rules` | list of [SheetCheckDefinition](#sheetcheckdefinition) | Checks on the list; one with a lambda (`"s => ..."`) checks every entry. |

## KeyedEntryDefinition

One entry of a keyed list (a skill, an action): its key, label, attribute and groups.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required: the entry's key (`balance`), as formulas reach it (`skills.balance`). |
| `label` | text (a number is text too) | The entry's name as the sheet shows it. |
| `attribute` | text (a number is text too) | The attribute the entry reads, if any (formulas see it as `s.attribute`). |
| `group` | text (a number is text too) | A group the entry belongs to: formulas read it as `group`, a modifier on `'@group'` reaches the entry, and a table can group rows by it. |
| `groups` | list of text (a number is text too) | More groups, as `group` (formulas read all of them as `groups`). |
| `parameter` | text (a number is text too) | One entry per subject (`knowledge.arcana`) instead of one for the key. |
| `trainedOnly` | true or false | A flag formulas read as `trainedOnly` (3.5's trained-only skills); nothing else acts on it. |

## TrackDefinition

A resource with a current value (overhaul section 3): hit points, stress, armor per location, spell slots. Its current value is a number on the sheet under Key, or for a slot track the list of what its slots hold. Formulas read the current value by the key and the maximum as `{key}_Max`. Overflow, WhenFull and Beyond act when the track changes (Codex.Core.Rules.SheetIntents).

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | The key, or for Each a template: `"slots_{item.id}_{level}"`. |
| `label` | text (a number is text too) | The track's name as the sheet shows it; the key when unset. |
| `max` | text (a number is text too) | A number or a formula; none for an open-ended track (improvement points). |
| `min` | text (a number is text too) | The lowest the current value may be: a number or a formula. Default 0; it may be negative (3.5's -10). |
| `start` | text (a number is text too) | Where a new sheet starts: a number, or `max`. |
| `display` | text (a number is text too) | `number` (default), `boxes` or `slots`. |
| `thresholds` | list of [TrackThresholdDefinition](#trackthresholddefinition) | Each adds its condition while it holds; several can hold at once. |
| `bands` | list of [TrackThresholdDefinition](#trackthresholddefinition) | The first that matches adds its condition; exactly one holds at a time. |
| `holds` | [TrackHoldsDefinition](#trackholdsdefinition) | For a slot track, what a slot may hold. |
| `levels` | any | With Each, the levels to make a track for: `"0..9"`. On a slot track, its levels of slots. |
| `each` | text (a number is text too) | A list formula: one track per item (and per level), its formulas reading `item` and `level`. |
| `overflow` | [TrackOverflowDefinition](#trackoverflowdefinition) | What a rise past the maximum does: stress into trauma, heat into wanted level. Null: the track stops at its maximum. |
| `whenFull` | text (a number is text too) | For slots in levels: `pushUp`, a full level puts what it is given in the next level up. Null: a full level takes nothing more. |
| `beyond` | [TrackBeyondDefinition](#trackbeyonddefinition) | For slots in levels: what pushing past the last level brings (Blades' fatal harm). |

## TrackThresholdDefinition

A comparison of the track's current value with a number or formula, and the condition it brings. No comparison matches always (a band's last, catch-all line).

| Key | Type | What it is |
|---|---|---|
| `atOrBelow` | text (a number is text too) | Holds when the current value is at or below this number or formula. |
| `below` | text (a number is text too) | Holds when the current value is below this number or formula. |
| `atOrAbove` | text (a number is text too) | Holds when the current value is at or above this number or formula. |
| `above` | text (a number is text too) | Holds when the current value is above this number or formula. |
| `equals` | text (a number is text too) | Equal to (YAML `equals`; named so as not to hide Object) for callers). |
| `condition` | text (a number is text too) | The condition the line brings while it holds, with its `conditionEffects`. |

## TrackHoldsDefinition

What a slot holds: entries of Type (a spell id) that pass Where, or `text`.

| Key | Type | What it is |
|---|---|---|
| `type` | text (a number is text too) | The entity type a slot holds (`spell`), checked against the Library, or `text` (the default) for free text, which is not checked. |
| `where` | text (a number is text too) | A lambda over the held entry that must be true (`"s => levelFor(s, item.name) == level"`). None: any entry of the type. |
| `message` | text (a number is text too) | The problem shown for something the slots cannot hold, with `{item.<property>}` and `{level}` filled in. |

## TrackOverflowDefinition

`{ into, by, then, ask }`: a rise past the maximum adds By to Into, and the track is then `reset` to its minimum (stress), `carry`-s what went past (heat), or `keep`-s its maximum.

| Key | Type | What it is |
|---|---|---|
| `into` | text (a number is text too) | Required: the track the overflow adds to (stress into `trauma`). |
| `by` | text (a number is text too) | A number or a formula (reading `excess`, how far past the maximum); 1 when unset. |
| `then` | text (a number is text too) | `reset`, `carry` or `keep`. |
| `ask` | [TrackAskDefinition](#trackaskdefinition) | A choice the host asks for once it overflowed (which trauma). |

## TrackAskDefinition

A choice the host asks for after an overflow (`{ who, choices }`): which trauma, say.

| Key | Type | What it is |
|---|---|---|
| `who` | text (a number is text too) | `player` or `gm`. |
| `choices` | list of text (a number is text too) | The options offered. |

## TrackBeyondDefinition

What pushing a slot track past its last level brings (`beyond`).

| Key | Type | What it is |
|---|---|---|
| `condition` | text (a number is text too) | The condition put on the sheet (Blades' fatal harm: `Dead`). |

## LayoutSection

A section of a sheet's layout: a tab of its own, or with Aside a column shown beside every tab.

| Key | Type | What it is |
|---|---|---|
| `section` | text (a number is text too) | The section's name, also its key (`Actions`, `attributes`). |
| `label` | text (a number is text too) | What the tab says; none is the host's word for a section it knows, else Section. |
| `icon` | text (a number is text too) | A Lucide icon name for the tab. |
| `aside` | true or false | Shown beside every tab rather than as a tab. |
| `widgets` | list of [LayoutWidget](#layoutwidget) | The section's widgets, top to bottom; a section needs at least one. |

## LayoutWidget

One widget in a section. Generic widgets: `values` (Keys), `track` (Key, which may end in `*` for every generated track it starts), `list` and `table` (Key, Columns, GroupBy), `roll` (Roll), `text` (Key) and `modifiers` (Target). Any other name asks for a widget the host builds in (5e's `armor-class`, `hit-points`...).

| Key | Type | What it is |
|---|---|---|
| `widget` | text (a number is text too) | Required: the widget's name: a generic widget (listed with this type), a widget the host builds in, or one of the system's own `widgets`. |
| `key` | text (a number is text too) | The key a `track`, `list`, `table` or `text` widget shows; for a `track`, a prefix ending in `*` shows every generated track it starts. |
| `keys` | list of text (a number is text too) | The keys a `values` widget shows: values, attributes, `{track}_Max` or a keyed-list entry (`list.id`). |
| `columns` | list of text (a number is text too) | For `list` and `table`: the columns after each row's name, as per-pick fields (editable in place), entry properties or derived values. |
| `groupBy` | text (a number is text too) | For `list` and `table`: the property rows are grouped under headings by (`group`). None: no headings. |
| `roll` | text (a number is text too) | For `roll`: the key of the roll in `rolls` its button makes. |
| `target` | text (a number is text too) | For `modifiers`: the target whose modifiers it lists, each with whether it counted. |
| `label` | text (a number is text too) | The text a `roll` button or a `text` field shows, or the label handed to one of the system's own widgets, in place of its default. |

## CampaignObjectsDefinition

What a system lets a campaign keep beside its sheets (overhaul section 3, "Campaign objects"): clocks, and tables of named rows with fields (Blades' faction status). The campaign holds the state; this says what is allowed.

| Key | Type | What it is |
|---|---|---|
| `clocks` | [ClockRulesDefinition](#clockrulesdefinition) | Clocks: null when the system has none. |
| `tables` | list of [CampaignTableDefinition](#campaigntabledefinition) | Tables of named rows a campaign may keep (faction status). |

## ClockRulesDefinition

The clocks a campaign may keep: the sizes allowed (empty: any size from 2), and who may see one.

| Key | Type | What it is |
|---|---|---|
| `segments` | list of whole number | The clock sizes allowed (`[4, 6, 8]`), each at least 2. Empty: any size from 2. |
| `visibility` | list of text (a number is text too) | `gm` (the GM alone) and `players` (everyone at the table); the first is a new clock's. |

## CampaignTableDefinition

A table of named rows, each with the ItemFields (status from -3 to 3). Who may see it is the table's: `players` (the default) or `gm`.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required, unique: the table's name, as intents (`table: factions`) and handler targets (`campaign.factions`) name it; not `clocks`. |
| `label` | text (a number is text too) | The table's heading; the key when unset. |
| `itemFields` | list of [ItemFieldDefinition](#itemfielddefinition) | The fields each row has besides its name; a row's name is its own, so no field is called `name`. |
| `visibility` | text (a number is text too) | Who sees the table: `players` (everyone at the table, the default) or `gm`. |

## ModifierTypesDefinition

Stacking by modifier type: the modifiers of a type form a group that keeps one member or all. Bonus rules add what they keep to the value: `sum` (every one), `highest` / `lowest` (the largest or smallest bonus; penalties apart). Value rules compete with the value's starting amount and replace it when they win: `max` (the greatest, a floor; `best` is its older name) and `min` (the least, a ceiling). Extra Attack 2, 3, 4 on 1 attack under `max` is 4; critical 19 and 18 on a start of 20 under `min` is 18; +1 and +2 status under `highest` plus +1 untyped on 10 is 13.

| Key | Type | What it is |
|---|---|---|
| `default` | text (a number is text too) | The rule for a type `types` does not name: `sum` (the default), `highest`, `lowest`, `max`, `min` or `best`. |
| `penalties` | text (a number is text too) | The rule for the penalties (negative amounts) of one type, whatever the type's own rule, except a `best` type. Default `sum`. |
| `types` | map of text (a number is text too) | Modifier type -> its rule (`dodge: sum`, `armor: highest`, `base: max`, `critFrom: min`). |

## FunctionDefinition

A named function: a one-line Formula (`"(a, b) => a + b"`), a Script export (`rules.js#hitPoints`), or a compiled Type (`"Acme.Street.HitPoints, Acme.Street"`, an ICodexFunction). One of them.

| Key | Type | What it is |
|---|---|---|
| `formula` | text (a number is text too) | A one-line lambda (`"(a, b) => a + b"`), up to four parameters. |
| `script` | text (a number is text too) | A script export, `file#export` (`rules.js#hitPoints`), read from the system's folder. |
| `type` | text (a number is text too) | A compiled type, `"Namespace.Type, Assembly"`, from a DLL beside system.yaml. |
| `reads` | list of text (a number is text too) | For a script function, the parameters it takes; only their count is used, and only when the script's own parameters cannot be read. |

## WidgetDefinition

A widget a DLL supplies: Type names an ICodexWidget (`"Acme.Street.Cyberware, Acme.Street"`).

| Key | Type | What it is |
|---|---|---|
| `type` | text (a number is text too) | The compiled type, `"Namespace.Type, Assembly"`, an `ICodexWidget` in a DLL beside system.yaml. |
| `label` | text (a number is text too) | The label handed to the widget when its layout entry gives none. |

## ConditionEffectDefinition

What a condition does while it holds (`conditionEffects.<name>`).

| Key | Type | What it is |
|---|---|---|
| `modifiers` | list of [ModifierDefinition](#modifierdefinition) | Modifiers that apply while the condition holds; one without a `source` is credited to the condition. |

## RollDefinition

A named roll: how its dice are built (Dice, or Pool dice of Die), what is added, what it is compared against and the named outcome it comes to. A group roll (Participants) has no dice of its own: each participant makes another roll and the best result counts.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required, unique: the roll's name, as a `roll` widget, a `then` line and `participants` name it. |
| `label` | text (a number is text too) | The roll's name on its button; the key when unset. |
| `dice` | text (a number is text too) | A fixed dice expression: `1d20`, `1d10`, `3d6`. |
| `pool` | text (a number is text too) | A formula for how many dice of Die: `actions[input.action].rating + input.bonusDice`. |
| `die` | text (a number is text too) | The die a Pool rolls: `d6`. |
| `zeroDice` | [ZeroDiceDefinition](#zerodicedefinition) | What a pool of no dice (or fewer) rolls instead. Null: such a pool is a problem with the roll. |
| `keep` | [KeepDefinition](#keepdefinition) | Which dice count. Null: all of them. |
| `explode` | [RerollDefinition](#rerolldefinition) | A die showing On adds another die. |
| `implode` | [RerollDefinition](#rerolldefinition) | A die showing On takes another die away. |
| `add` | list of text (a number is text too) | Formulas added to the dice: values, `modifiers('attack')`. |
| `inputs` | list of [RollInputDefinition](#rollinputdefinition) | The parameters the roll takes, which its formulas read by key (`target`) and as `input.key`. |
| `outcomes` | list of [RollOutcomeDefinition](#rolloutcomedefinition) | The first whose `when` holds is the outcome; one without `when` always holds. |
| `carries` | list of text (a number is text too) | Inputs copied onto the result for whatever follows (position and effect). |
| `then` | list of [RollFollowUpDefinition](#rollfollowupdefinition) | What follows the roll: another roll (a confirmation) or intents (stress to pay). |
| `participants` | [RollParticipantsDefinition](#rollparticipantsdefinition) | A group roll: who rolls, and which roll each of them makes. |
| `result` | text (a number is text too) | For a group roll, which participant's outcome is the group's: `best` (the earliest in the participants' roll's Outcomes) or `worst`. |

## ZeroDiceDefinition

`{ roll: 2, keep: lowest }`: the dice a pool of none rolls, and which one counts.

| Key | Type | What it is |
|---|---|---|
| `roll` | whole number | How many dice to roll instead. Default 2. |
| `keep` | text (a number is text too) | `lowest` or `highest`. |

## KeepDefinition

`{ highest: 1 }` or `{ lowest: 1 }`.

| Key | Type | What it is |
|---|---|---|
| `highest` | whole number | Keep this many of the highest dice. |
| `lowest` | whole number | Keep this many of the lowest dice (when `highest` is not set). |

## RerollDefinition

`{ on: 10, times: 1 }`: the face that rolls another die, and how many times in a row it may.

| Key | Type | What it is |
|---|---|---|
| `on` | whole number | The face that brings another die (`10` on a d10). |
| `times` | whole number | How many more dice one die may bring; null: as many as come up (within the engine's limit). |

## RollInputDefinition

A named parameter supplied when the roll is made.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required: the input's name, as formulas read it. |
| `from` | text (a number is text too) | Who supplies it: `roller` (the default), `gm`, `weapon` (a property of the item the `weapon` input names) or `parent` (the roll this one follows). |
| `label` | text (a number is text too) | What the roll widget calls the input; the key when unset. |
| `property` | text (a number is text too) | For `from: weapon`, the item property it reads. |
| `default` | any | The value when nobody supplies one. With neither a value nor a default the roll fails, naming the input. |
| `options` | list of text (a number is text too) | The values allowed, offered as a dropdown; any other fails the roll. |

## RollOutcomeDefinition

A named result a roll can come to (`critical`, `success`, `partial`); a roll's outcomes are tried in order.

| Key | Type | What it is |
|---|---|---|
| `name` | text (a number is text too) | Required: the outcome's name, as the result, `then` lines and a group roll's `result` read it. |
| `when` | text (a number is text too) | A formula over the dice and total (`natural`, `total`, `kept`, `dice`, `zeroDice`) that must be true. None: always, so it belongs last. |
| `replacesParent` | true or false | A follow-up roll's outcome that becomes the roll it follows (a confirmed threat is the attack's critical). |
| `mark` | text (a number is text too) | How the dice display marks it: `critical`, `fumble` or none. Unset, an outcome named `critical` or `fumble` is marked so. |

## RollFollowUpDefinition

`{ when, roll }` or `{ when, intents }`.

| Key | Type | What it is |
|---|---|---|
| `when` | text (a number is text too) | A formula over the result (`outcome`) that must be true for the line to act. None: always. |
| `roll` | text (a number is text too) | The key of the roll to make next, which sees this roll's inputs (a confirmation roll). |
| `intents` | list of map of any | Intents as overhaul section 3 names them: `{ adjust: { track, by, actor } }`. |

## RollParticipantsDefinition

A group roll's participants (`{ from, roll }`): who chooses them, and the roll each makes.

| Key | Type | What it is |
|---|---|---|
| `from` | text (a number is text too) | Who chooses the participants: `gm` or `roller`. |
| `roll` | text (a number is text too) | The roll each participant makes. |

## MomentDefinition

A moment the system declares: a named time at which feature counters refill (`recovers: [{ on: downtime }]`) and the system's `events:` handler of the same name runs.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Required: the moment's name. It may not repeat a rest's name, `initiative`, `dawn` or `manual`, nor another moment's. |
| `label` | text (a number is text too) | The moment's name as the app shows it; `key` when empty. |
| `onSet` | text (a number is text too) | The key of an `input` value of a sheet kind. Setting that value on the sheet fires the moment. |
