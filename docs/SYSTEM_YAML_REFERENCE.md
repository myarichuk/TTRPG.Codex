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
| `id` | text (a number is text too) |  |
| `name` | text (a number is text too) |  |
| `description` | text (a number is text too) |  |
| `version` | text (a number is text too) |  |
| `compatibility` | text (a number is text too) | What the system may call itself in relation to a trademarked product, e.g. "5E compatible". |
| `license` | [LicenseInfo](#licenseinfo) |  |
| `attributes` | list of [AttributeDefinition](#attributedefinition) |  |
| `resources` | list of [ResourceDefinition](#resourcedefinition) |  |
| `actorFields` | list of [FieldDefinition](#fielddefinition) | Extra sheet fields beyond attributes and resources (level, class, alignment, speed...). |
| `fieldRoles` | [FieldRoles](#fieldroles) | Which of the ActorFields the app reads for what, outside the rules engine. |
| `entityTypes` | list of [ContentTypeDefinition](#contenttypedefinition) |  |
| `conditions` | list of text (a number is text too) | Condition names the combat console offers in its picker. |
| `dice` | [DiceConventions](#diceconventions) |  |
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
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `abbreviation` | text (a number is text too) |  |
| `canonicalLabel` | text (a number is text too) | The label and abbreviation as system.yaml wrote them. A localized copy shows translated text in Label, but sheet data and pack entries refer to the original words ("STR 13+", "Strength"), so rules match on these as well. |
| `canonicalAbbreviation` | text (a number is text too) |  |
| `default` | whole number |  |
| `min` | whole number |  |
| `max` | whole number |  |
| `modifier` | text (a number is text too) | Optional derived-modifier rule. Supported: `"half-minus-five"` (floor((v-10)/2), the d20 family) or `"none"`. Systems that need anything else define a derived field instead. |

## ResourceDefinition

A depletable pool (hit points, mana, strain, luck) on an actor.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `maxKey` | text (a number is text too) | Pool key that holds the maximum, e.g. "HP_Max". Null for pools without a cap. |
| `default` | whole number |  |
| `isPrimary` | true or false | True for the pool damage/healing target by default (usually hit points). |

## FieldDefinition

One field of a schema-driven form. A plain class (not a positional record) so it can be read straight out of `system.yaml`; the positional constructor is kept for code that builds schemas by hand.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `type` | one of: `Text`, `LongText`, `Number`, `Boolean`, `Dropdown`, `TagList`, `ImagePicker`, `EntityReference`, `Collection` |  |
| `description` | text (a number is text too) |  |
| `options` | list of text (a number is text too) |  |
| `defaultValue` | any |  |
| `isRequired` | true or false |  |
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
| `subclass` | text (a number is text too) |  |
| `speed` | text (a number is text too) | Shown on the sheet's plate beside the derived numbers. |
| `summary` | list of text (a number is text too) | The fields Cast's one-line summary of a character lists, in order. Level reads "Level 3"; a reference field reads its entry's name. |

## ContentTypeDefinition

One kind of content a system's packs can carry (class, feat, spell, monster, item, ...), with the field schema the Library browser and homebrew editor render it with.

| Key | Type | What it is |
|---|---|---|
| `type` | text (a number is text too) | Stable key, singular, lower-case: "spell". |
| `label` | text (a number is text too) |  |
| `pluralLabel` | text (a number is text too) |  |
| `folder` | text (a number is text too) | Pack folder holding entries of this type; defaults to Type. |
| `icon` | text (a number is text too) |  |
| `description` | text (a number is text too) |  |
| `fields` | list of [FieldDefinition](#fielddefinition) |  |
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
| `initiative` | text (a number is text too) |  |
| `check` | text (a number is text too) |  |
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
| `down` | [DownRules](#downrules) |  |
| `deathSaves` | [DeathSaveRules](#deathsaverules) |  |
| `damage` | [DamageTypeRules](#damagetyperules) |  |
| `exhaustion` | [ExhaustionRules](#exhaustionrules) |  |
| `hitDice` | [HitDiceRules](#hitdicerules) |  |
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

| Key | Type | What it is |
|---|---|---|
| `die` | text (a number is text too) |  |
| `successOn` | whole number |  |
| `successesToStabilize` | whole number |  |
| `failuresToDie` | whole number |  |
| `criticalSuccess` | whole number | A natural roll this high regains one hit point. Null: no critical success. |
| `criticalFailure` | whole number | A natural roll this low counts as two failures. Null: no critical failure. |
| `successesKey` | text (a number is text too) | Pool keys the running tally is kept in. |
| `failuresKey` | text (a number is text too) |  |

## DamageTypeRules

Damage types and the sheet lists that halve, cancel or double them.

| Key | Type | What it is |
|---|---|---|
| `types` | list of text (a number is text too) |  |
| `resistancesField` | text (a number is text too) |  |
| `immunitiesField` | text (a number is text too) |  |
| `vulnerabilitiesField` | text (a number is text too) |  |

## ExhaustionRules

Exhaustion as a level counter kept in a pool, not a timed condition.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `maxLevel` | whole number |  |
| `condition` | text (a number is text too) | The condition name in Conditions this replaces in the picker. |

## HitDiceRules

A pool of hit dice: one per level for characters, the stat block's count for creatures.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `levelField` | text (a number is text too) |  |
| `classField` | text (a number is text too) |  |
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
| `maximumDc` | whole number |  |

## RestRule

| Key | Type | What it is |
|---|---|---|
| `name` | text (a number is text too) |  |
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
| `attributes` | [AttributeRules](#attributerules) |  |
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

## AttributeRules

How ability scores are generated and adjusted.

| Key | Type | What it is |
|---|---|---|
| `methods` | list of text (a number is text too) | Methods a player may choose from: "point-buy", "standard-array", "manual". The DM can always use "manual" (typed in, capped at Max) and "printed" (a stat block's scores as the block prints them: no cap, no bonuses or increases added); imported creatures get "printed". |
| `default` | text (a number is text too) |  |
| `max` | whole number | Upper bound on a final score (base plus bonuses), except on a "printed" sheet. |
| `pointBuy` | [PointBuyRules](#pointbuyrules) |  |
| `standardArray` | list of whole number | The fixed set of scores to assign, one per attribute, for "standard-array". |
| `bonuses` | [BonusRules](#bonusrules) | Bonuses granted by a referenced entry (a background's "+2/+1 among these abilities"). |
| `increases` | [IncreaseRules](#increaserules) | Score increases granted by entries picked in a list (an Ability Score Improvement feat). |

## PointBuyRules

| Key | Type | What it is |
|---|---|---|
| `budget` | whole number |  |
| `min` | whole number |  |
| `max` | whole number |  |
| `costs` | map of whole number | Cost of each score from Min to Max. |

## BonusRules

| Key | Type | What it is |
|---|---|---|
| `label` | text (a number is text too) |  |
| `fromField` | text (a number is text too) | The sheet field whose referenced entry lists the eligible attributes. |
| `property` | text (a number is text too) | The property on that entry holding the eligible attribute keys, abbreviations or labels. |
| `allocations` | list of list of whole number | Allowed distributions, e.g. [[2, 1], [1, 1, 1]]: one bonus per distinct attribute. |

## IncreaseRules

Score increases that come from picks in a ListRule: each picked entry whose Property is a positive number grants that many points, spread by the player over the attributes its OptionsProperty lists (any attribute when it lists none), and can raise a score no higher than its CapProperty (or Max). The player's choice is stored on the pick itself: `"feat-id|str:1,dex:1"`.

| Key | Type | What it is |
|---|---|---|
| `label` | text (a number is text too) |  |
| `fromList` | text (a number is text too) | The Key whose picks can grant increases. |
| `property` | text (a number is text too) | Entry property holding how many points the entry grants. |
| `optionsProperty` | text (a number is text too) | Entry property listing the attributes (keys, abbreviations or labels) the points may go to. |
| `capProperty` | text (a number is text too) | Entry property holding the highest score the increase may reach. |

## DerivedField

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `formula` | text (a number is text too) | A C#-style expression over the sheet's numeric fields and attribute scores, e.g. `2 + (level - 1) / 4`. Integer arithmetic; `floor`, `ceil`, `max`, `min` and `mod(key)` (an attribute's modifier) are available. |
| `category` | text (a number is text too) |  |

## FieldConstraint

| Key | Type | What it is |
|---|---|---|
| `field` | text (a number is text too) |  |
| `where` | list of [RuleCondition](#rulecondition) |  |

## RuleCondition

One condition on a candidate entry. Exactly one comparison is set; Property names the candidate's property ("level", "classes", "category"...), or "id"/"name"/"tags".

| Key | Type | What it is |
|---|---|---|
| `property` | text (a number is text too) |  |
| `equals` | any | Candidate value equals this constant. |
| `in` | list of text (a number is text too) | Candidate value is one of these. |
| `gte` | number | Candidate numeric value >= / <= a constant. |
| `lte` | number |  |
| `equalsField` | text (a number is text too) | Candidate value equals the raw value of this sheet field (an entry id, say). |
| `equalsPropertyOfField` | [FieldProperty](#fieldproperty) | Candidate value equals a property of the entry a sheet field references (e.g. the feat named by the chosen background). |
| `containsNameOfField` | text (a number is text too) | Candidate list property contains the display name of the entry a sheet field references. |
| `lteField` | text (a number is text too) | Candidate numeric value <= / >= a numeric sheet field. |
| `gteField` | text (a number is text too) |  |
| `lteLookup` | [LookupRule](#lookuprule) | Candidate numeric value <= a value looked up in a referenced entry's table. |
| `message` | text (a number is text too) | Shown when the condition fails. |

## FieldProperty

| Key | Type | What it is |
|---|---|---|
| `field` | text (a number is text too) |  |
| `property` | text (a number is text too) |  |

## LookupRule

Reads a value out of a row table on a referenced entry: the sheet's Field names an entry; that entry's Table property is a list of rows; the row whose RowKey equals the sheet's ByField value (or the highest row not above it) supplies Column.

| Key | Type | What it is |
|---|---|---|
| `field` | text (a number is text too) |  |
| `table` | text (a number is text too) |  |
| `byField` | text (a number is text too) |  |
| `rowKey` | text (a number is text too) |  |
| `column` | text (a number is text too) |  |

## ListRule

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `description` | text (a number is text too) |  |
| `targetEntityType` | text (a number is text too) |  |
| `where` | list of [RuleCondition](#rulecondition) |  |
| `maxCount` | [CountRule](#countrule) |  |
| `category` | text (a number is text too) |  |
| `repeatableProperty` | text (a number is text too) | Candidate property that, when true, lets the same entry be picked more than once. |

## CountRule

| Key | Type | What it is |
|---|---|---|
| `value` | whole number |  |
| `lookup` | [LookupRule](#lookuprule) |  |
| `plus` | whole number |  |
| `also` | list of [LookupRule](#lookuprule) | More tables added to Lookup for every class (a subclass's own grants). |
| `fromField` | [FieldProperty](#fieldproperty) | A number on the entry a sheet field references, added once (a species' extra feat). |

## MulticlassRules

Multiclassing: the sheet's ClassField is the first class, Key holds the others as `"class-id:levels"`, and LevelField stays the total character level. Every class gets its own ClassLevelKey value, so table lookups written with `byField: classLevel` read the right row for each class; list limits are summed across classes and a list entry is allowed when any one class allows it.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `classField` | text (a number is text too) |  |
| `levelField` | text (a number is text too) |  |
| `classLevelKey` | text (a number is text too) |  |
| `prerequisitesProperty` | text (a number is text too) | Class entry property listing prerequisite options, each like `"STR 13+"` or `"DEX 13+, WIS 13+"` (all of these). Any one option is enough. |
| `requireForFirstClass` | true or false | Whether the first class must also meet its own prerequisites once you multiclass. |
| `subclassField` | text (a number is text too) | The sheet field naming the first class's subclass. Each other class stores its own after its levels: `"class-id:levels:subclass-id"`. Null: subclasses are not tracked per class. |
| `subclassClassProperty` | text (a number is text too) | The subclass entry's property naming the class it belongs to; the sheet offers a class only its own subclasses. |
| `subclassLevel` | whole number | The class level at which a subclass can be chosen; a subclass before it is a violation. |

## SlotPoolRule

One pool of expendable slots. For each of the sheet's classes, the class entry's Table row at that class's level supplies Column: either `"4/3/2"` (slots per slot level from 1 up) or, when SlotLevelColumn is set, a count of slots that are all of the level in that column. Classes are filtered by their CasterTypeProperty. When two or more classes contribute and Combine is set, their weighted levels are added up and looked up in the combined table instead. During play each slot level is a resource: `{Key}_{level}` (remaining) and `{Key}_{level}_Max`.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `unit` | text (a number is text too) | One slot, as a button's label and the table log name it ("spell slot": "Use a level 3 spell slot"). The pool's label when not set. Translated through the system's values, like the label. |
| `field` | text (a number is text too) | The sheet field naming the class entry, and the sheet value whose row is read (with multiclass rules, use their ClassLevelKey). |
| `byField` | text (a number is text too) |  |
| `table` | text (a number is text too) |  |
| `column` | text (a number is text too) |  |
| `slotLevelColumn` | text (a number is text too) |  |
| `casterTypeProperty` | text (a number is text too) |  |
| `includeCasterTypes` | list of text (a number is text too) | Only classes with one of these caster types count (empty: any). |
| `excludeCasterTypes` | list of text (a number is text too) |  |
| `combine` | [SlotCombineRule](#slotcombinerule) |  |
| `recoversOn` | list of text (a number is text too) | Rest names that refill this pool ("Short Rest", "Long Rest"); the table offers one button per name. |

## SlotCombineRule

How several classes' slots combine: each class adds its level times its caster type's weight (rounded per Rounding), and the sum picks a row of Table (row 1 is caster level 1).

| Key | Type | What it is |
|---|---|---|
| `weights` | map of number |  |
| `rounding` | text (a number is text too) | "up", "down" or "nearest", applied once to the weighted sum of every class's levels. |
| `table` | list of text (a number is text too) | Slots per caster level, each like `"4/3/2"`. |

## HitPointRules

Maximum hit points from the sheet's classes. Levels are taken first class first, then the other classes in order. The very first level gives what FirstLevel says (the full hit die by default), every later level the die's Average (or the player's roll in "rolled" mode), plus the Attribute modifier, never less than MinimumPerLevel. Because the modifier is read from the final score, raising Constitution raises maximum hit points for every level at once. The result is written to Key only while the sheet's ModeKey is not "manual" (a sheet without the key is manual, so existing characters and imported creatures keep their numbers).

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | Sheet key that receives the maximum (the primary resource's max key). |
| `currentKey` | text (a number is text too) | Sheet key of current hit points: it follows the maximum when it was at full health. |
| `classField` | text (a number is text too) |  |
| `levelField` | text (a number is text too) |  |
| `dieProperty` | text (a number is text too) | Class entry property holding the hit die, like "d10". |
| `attribute` | text (a number is text too) | Required: the attribute whose modifier is added per level (5e: "con"). |
| `minimumPerLevel` | whole number |  |
| `firstLevel` | text (a number is text too) | What the character's very first level gives: FullDie, or "average" for the die's average. |
| `average` | text (a number is text too) | The gain a level takes in place of a roll: HalfPlusOne (a d10 gives 6) or Half (a d10 gives 5). |
| `modes` | list of text (a number is text too) | "average", "rolled" or "manual". |
| `default` | text (a number is text too) |  |
| `modeKey` | text (a number is text too) |  |
| `rollsKey` | text (a number is text too) | Sheet key holding the player's rolls for levels 2 and up, in level order. |
| `perLevelBonus` | [FieldProperty](#fieldproperty) | Entries whose number adds to every level's gain (Dwarven Toughness): the sheet field that names the entry and the property holding the bonus. |

## ArmorClassRules

Armor class. With body armor worn: its base plus the Dexterity modifier (limited by the armor's cap, none for a cap of 0). Without: the best of Unarmored and the classes' own formulas (ClassFormulaProperty, such as 10 + Dex + Con). Then every equipped item's bonus, the bonus of picks in BonusLists and the sheet's MiscField are added. Written to Key while ModeKey is "auto"; a sheet without that key is manual.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `modeKey` | text (a number is text too) |  |
| `unarmored` | text (a number is text too) | Required: the formula over the sheet's numbers when nothing is worn and no class formula is better (5e: "10 + mod(dex)"). |
| `dexAttribute` | text (a number is text too) | Required: the attribute added to body armor's base, up to its cap (5e: "dex"). |
| `classFormulaProperty` | text (a number is text too) | Class entry property with an unarmored formula, and the one saying a shield is still allowed. |
| `classShieldProperty` | text (a number is text too) |  |
| `typeProperty` | text (a number is text too) | Item properties: the armor type ("light", "medium", "heavy", "shield"), the base AC of body armor, its Dexterity cap (absent: uncapped; 0: no Dexterity), and the flat bonus any item gives. |
| `baseProperty` | text (a number is text too) |  |
| `dexCapProperty` | text (a number is text too) |  |
| `bonusProperty` | text (a number is text too) |  |
| `shieldType` | text (a number is text too) | The TypeProperty value that marks a shield. |
| `bonusLists` | list of text (a number is text too) | Lists whose picks may add BonusProperty (a fighting style feat), and the pick property that limits the bonus to wearers of body armor. |
| `requiresArmorProperty` | text (a number is text too) |  |
| `miscField` | text (a number is text too) | Numeric sheet field for everything else (a spell, a magic effect). |

## InventoryRules

The items a sheet carries, stored under Key as `"item-id|qty:2,equipped:1"`. Items may come from any of Types. At most one suit of armor and one shield may be equipped.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `types` | list of text (a number is text too) |  |
| `quantityChoice` | text (a number is text too) |  |
| `equippedChoice` | text (a number is text too) |  |
| `weightProperty` | text (a number is text too) | The item property holding one item's weight; the sheet adds them up, times their quantities. |
| `weightUnit` | text (a number is text too) | The word the total is shown with ("lb"), translated like the system's other words. None shows the number alone. |
| `capacity` | text (a number is text too) | A formula for how much a character can carry ("str * 15"); carrying more is a warning. None: no limit. |

## SkillRules

Saving throws and skills. A skill is proficient when its key is in ProficienciesKey (a list on the sheet) or in the BackgroundProperty of the entry BackgroundField names; it is expert when it is also in ExpertiseKey. Saving throws are proficient for the first class's SavingThrowsProperty. Bonuses add the derived field BonusKey.

| Key | Type | What it is |
|---|---|---|
| `label` | text (a number is text too) |  |
| `bonusKey` | text (a number is text too) | Required: the derived value skill and save bonuses add (5e: "proficiencyBonus"). |
| `proficienciesKey` | text (a number is text too) |  |
| `expertiseKey` | text (a number is text too) |  |
| `classField` | text (a number is text too) |  |
| `savingThrowsProperty` | text (a number is text too) |  |
| `backgroundField` | text (a number is text too) |  |
| `backgroundProperty` | text (a number is text too) |  |
| `classChoicesProperty` | text (a number is text too) | The first class's property saying how many skills it lets a character choose: a number, or text whose first number is it ("Choose 2: Acrobatics, …"). More chosen than that (beyond the background's) is a warning, since a species or feat may grant more. |
| `list` | list of [SkillDefinition](#skilldefinition) |  |
| `passives` | list of [PassiveRule](#passiverule) |  |

## SkillDefinition

One skill: the attribute whose modifier it uses.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `canonicalLabel` | text (a number is text too) | The label as system.yaml wrote it. Sheets and backgrounds list skills by it ("Athletics"), so the rules match on it even when a localized copy shows Label in another language. |
| `attribute` | text (a number is text too) |  |

## PassiveRule

A score of the form base + a skill's bonus (passive Perception is 10 + Perception).

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `skill` | text (a number is text too) |  |
| `base` | whole number |  |

## SpellcastingRules

Each class that has a AbilityProperty casts with that attribute: save DC is SaveBase + the derived BonusKey + the modifier, the attack bonus is the bonus + the modifier.

| Key | Type | What it is |
|---|---|---|
| `abilityProperty` | text (a number is text too) |  |
| `bonusKey` | text (a number is text too) | Required: the derived value save DCs and attack bonuses add (5e: "proficiencyBonus"). |
| `saveBase` | whole number |  |

## ProficiencyRules

Warnings (never violations: a feat or feature may grant what the class lacks) for equipped armor and weapons the first class's training doesn't cover, plus the armor's Strength minimum and stealth penalty. Class training is a list of names such as "Light Armor", "Shields", "Martial Weapons" or "All armor".

| Key | Type | What it is |
|---|---|---|
| `armorProperty` | text (a number is text too) |  |
| `weaponProperty` | text (a number is text too) |  |
| `classField` | text (a number is text too) |  |
| `armorTraining` | map of text (a number is text too) | Equipment property → the training name that covers it: "light" → "Light Armor". |
| `strengthProperty` | text (a number is text too) | Item property holding the Strength an armor needs, and the one flagging Stealth disadvantage. |
| `strengthAttribute` | text (a number is text too) | The attribute an armor's Strength minimum is checked against (5e: "str"). No default: a system without strength minimums leaves it out and none is checked. |
| `stealthProperty` | text (a number is text too) |  |
| `weaponTag` | text (a number is text too) | Equipment tags that mark a weapon. |
| `allArmor` | text (a number is text too) | A class's armor training that covers every armor but shields ("All armor"), matched at the start of the training's name; null when the system has none. |
| `pluralSuffix` | text (a number is text too) | The plural ending dropped when a weapon is matched to a class's weapon training by name, so "Longswords" covers a Longsword ("s" in English); null to match names as they are. |

## InheritRule

Copies properties of the entry Field names onto sheet keys the moment the player picks a different entry, then leaves them alone so a hand-edited speed survives later saves. AppliedKey remembers which entry was last copied from.

| Key | Type | What it is |
|---|---|---|
| `field` | text (a number is text too) |  |
| `appliedKey` | text (a number is text too) |  |
| `copy` | map of text (a number is text too) | Entry property → sheet key. |

## SheetKindDefinition

A kind of sheet (`character`, `crew`): its values, modifiers, lists and keyed lists.

| Key | Type | What it is |
|---|---|---|
| `kind` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `role` | text (a number is text too) | The host role: `player`, `npc`, `creature` or `shared`. |
| `values` | list of [ValueDefinition](#valuedefinition) |  |
| `modifiers` | list of [ModifierDefinition](#modifierdefinition) |  |
| `lists` | list of [PickListDefinition](#picklistdefinition) |  |
| `keyedLists` | list of [KeyedListDefinition](#keyedlistdefinition) |  |
| `tracks` | list of [TrackDefinition](#trackdefinition) |  |
| `layout` | list of [LayoutSection](#layoutsection) | How the sheet is laid out: sections of widgets bound to keys. Empty: the host's generic layout (every value, track, list and keyed list in turn). |

## ValueDefinition

A number (or a choice) on the sheet: worked out by Formula, or entered (Input).

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `formula` | text (a number is text too) |  |
| `modifiers` | any | A target or a list of targets (`AC`, `'@saves'`) whose modifiers are added on top of the formula. |
| `without` | list of any | Modifier types left out: a type name, or `{ type, positiveOnly: true }` to leave out only its bonuses. |
| `input` | true or false | Entered by the player rather than worked out. |
| `options` | list of text (a number is text too) |  |
| `default` | any |  |

## ModifierDefinition

A typed contribution to a target: `{ target: AC, amount: 2, type: dodge, source: Dodge }`.

| Key | Type | What it is |
|---|---|---|
| `target` | text (a number is text too) | A value key, a keyed-list entry (`skills.balance`) or a group (`'@armorPenalty'`). |
| `amount` | text (a number is text too) | A number or a formula. |
| `type` | text (a number is text too) | The modifier type; none is `untyped`. |
| `source` | text (a number is text too) |  |
| `when` | text (a number is text too) | A formula; the modifier counts only while it holds. |

## PickListDefinition

Picks of an entry type, each with its own fields.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `type` | text (a number is text too) | The entry type the picks are of; none for a list of plain records (`hpRolls`). |
| `description` | text (a number is text too) |  |
| `itemFields` | list of [ItemFieldDefinition](#itemfielddefinition) |  |
| `modifiersFrom` | [ModifiersFromDefinition](#modifiersfromdefinition) | Each pick's entry property holding modifiers, and when a pick's count (`"i => i.equipped"`). |
| `prerequisites` | text (a number is text too) | The entry property holding prerequisites, checked on the sheet without the pick. |
| `rules` | list of [SheetCheckDefinition](#sheetcheckdefinition) |  |

## ItemFieldDefinition

A field each pick or keyed entry carries (`classLevel`, `equipped`, `points`).

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `type` | text (a number is text too) |  |
| `default` | any |  |
| `min` | number |  |
| `max` | number |  |
| `options` | list of text (a number is text too) |  |
| `display` | text (a number is text too) |  |

## ModifiersFromDefinition

| Key | Type | What it is |
|---|---|---|
| `property` | text (a number is text too) |  |
| `when` | text (a number is text too) |  |

## SheetCheckDefinition

A problem with key Key when Check is false; a lambda checks every item.

| Key | Type | What it is |
|---|---|---|
| `check` | text (a number is text too) |  |
| `key` | text (a number is text too) |  |
| `message` | text (a number is text too) |  |

## KeyedListDefinition

A fixed set of keys (skills, actions), each with fields the player sets and values worked out per key.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `itemFields` | list of [ItemFieldDefinition](#itemfielddefinition) |  |
| `keys` | list of [KeyedEntryDefinition](#keyedentrydefinition) |  |
| `derive` | map of text (a number is text too) | Per-key values as lambdas over the entry (`ranks: "s => s.points"`). `value` is the entry's own value, with the modifiers that target it added. |
| `rules` | list of [SheetCheckDefinition](#sheetcheckdefinition) |  |

## KeyedEntryDefinition

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `attribute` | text (a number is text too) | The attribute the entry reads, if any (formulas see it as `s.attribute`). |
| `group` | text (a number is text too) |  |
| `groups` | list of text (a number is text too) |  |
| `parameter` | text (a number is text too) | One entry per subject (`knowledge.arcana`) instead of one for the key. |
| `trainedOnly` | true or false |  |

## TrackDefinition

A resource with a current value (overhaul section 3): hit points, stress, armor per location, spell slots. Its current value is a number on the sheet under Key, or for a slot track the list of what its slots hold. Formulas read the current value by the key and the maximum as `{key}_Max`. Overflow, WhenFull and Beyond act when the track changes (Codex.Core.Rules.SheetIntents).

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) | The key, or for Each a template: `"slots_{item.id}_{level}"`. |
| `label` | text (a number is text too) |  |
| `max` | text (a number is text too) | A number or a formula; none for an open-ended track (improvement points). |
| `min` | text (a number is text too) |  |
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
| `atOrBelow` | text (a number is text too) |  |
| `below` | text (a number is text too) |  |
| `atOrAbove` | text (a number is text too) |  |
| `above` | text (a number is text too) |  |
| `equals` | text (a number is text too) | Equal to (YAML `equals`; named so as not to hide Object) for callers). |
| `condition` | text (a number is text too) |  |

## TrackHoldsDefinition

What a slot holds: entries of Type (a spell id) that pass Where, or `text`.

| Key | Type | What it is |
|---|---|---|
| `type` | text (a number is text too) |  |
| `where` | text (a number is text too) |  |
| `message` | text (a number is text too) |  |

## TrackOverflowDefinition

`{ into, by, then, ask }`: a rise past the maximum adds By to Into, and the track is then `reset` to its minimum (stress), `carry`-s what went past (heat), or `keep`-s its maximum.

| Key | Type | What it is |
|---|---|---|
| `into` | text (a number is text too) |  |
| `by` | text (a number is text too) | A number or a formula (reading `excess`, how far past the maximum); 1 when unset. |
| `then` | text (a number is text too) | `reset`, `carry` or `keep`. |
| `ask` | [TrackAskDefinition](#trackaskdefinition) | A choice the host asks for once it overflowed (which trauma). |

## TrackAskDefinition

| Key | Type | What it is |
|---|---|---|
| `who` | text (a number is text too) | `player` or `gm`. |
| `choices` | list of text (a number is text too) |  |

## TrackBeyondDefinition

| Key | Type | What it is |
|---|---|---|
| `condition` | text (a number is text too) |  |

## LayoutSection

A section of a sheet's layout: a tab of its own, or with Aside a column shown beside every tab.

| Key | Type | What it is |
|---|---|---|
| `section` | text (a number is text too) | The section's name, also its key (`Actions`, `attributes`). |
| `label` | text (a number is text too) | What the tab says; none is the host's word for a section it knows, else Section. |
| `icon` | text (a number is text too) | A Lucide icon name for the tab. |
| `aside` | true or false | Shown beside every tab rather than as a tab. |
| `widgets` | list of [LayoutWidget](#layoutwidget) |  |

## LayoutWidget

One widget in a section. Generic widgets: `values` (Keys), `track` (Key, which may end in `*` for every generated track it starts), `list` and `table` (Key, Columns, GroupBy), `roll` (Roll), `text` (Key) and `modifiers` (Target). Any other name asks for a widget the host builds in (5e's `armor-class`, `hit-points`...).

| Key | Type | What it is |
|---|---|---|
| `widget` | text (a number is text too) |  |
| `key` | text (a number is text too) |  |
| `keys` | list of text (a number is text too) |  |
| `columns` | list of text (a number is text too) |  |
| `groupBy` | text (a number is text too) |  |
| `roll` | text (a number is text too) |  |
| `target` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |

## CampaignObjectsDefinition

What a system lets a campaign keep beside its sheets (overhaul section 3, "Campaign objects"): clocks, and tables of named rows with fields (Blades' faction status). The campaign holds the state; this says what is allowed.

| Key | Type | What it is |
|---|---|---|
| `clocks` | [ClockRulesDefinition](#clockrulesdefinition) | Clocks: null when the system has none. |
| `tables` | list of [CampaignTableDefinition](#campaigntabledefinition) |  |

## ClockRulesDefinition

The clocks a campaign may keep: the sizes allowed (empty: any size from 2), and who may see one.

| Key | Type | What it is |
|---|---|---|
| `segments` | list of whole number |  |
| `visibility` | list of text (a number is text too) | `gm` (the GM alone) and `players` (everyone at the table); the first is a new clock's. |

## CampaignTableDefinition

A table of named rows, each with the ItemFields (status from -3 to 3). Who may see it is the table's: `players` (the default) or `gm`.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `itemFields` | list of [ItemFieldDefinition](#itemfielddefinition) |  |
| `visibility` | text (a number is text too) |  |

## ModifierTypesDefinition

Stacking: `sum`, `highest` or `lowest`, by modifier type; penalties (negative amounts) apart.

| Key | Type | What it is |
|---|---|---|
| `default` | text (a number is text too) |  |
| `penalties` | text (a number is text too) |  |
| `types` | map of text (a number is text too) |  |

## FunctionDefinition

A named function: a one-line Formula (`"(a, b) => a + b"`), a Script export (`rules.js#hitPoints`), or a compiled Type (`"Acme.Street.HitPoints, Acme.Street"`, an ICodexFunction). One of them.

| Key | Type | What it is |
|---|---|---|
| `formula` | text (a number is text too) |  |
| `script` | text (a number is text too) |  |
| `type` | text (a number is text too) |  |
| `reads` | list of text (a number is text too) |  |

## WidgetDefinition

A widget a DLL supplies: Type names an ICodexWidget (`"Acme.Street.Cyberware, Acme.Street"`).

| Key | Type | What it is |
|---|---|---|
| `type` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |

## ConditionEffectDefinition

| Key | Type | What it is |
|---|---|---|
| `modifiers` | list of [ModifierDefinition](#modifierdefinition) |  |

## RollDefinition

A named roll: how its dice are built (Dice, or Pool dice of Die), what is added, what it is compared against and the named outcome it comes to. A group roll (Participants) has no dice of its own: each participant makes another roll and the best result counts.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `label` | text (a number is text too) |  |
| `dice` | text (a number is text too) | A fixed dice expression: `1d20`, `1d10`, `3d6`. |
| `pool` | text (a number is text too) | A formula for how many dice of Die: `actions[input.action].rating + input.bonusDice`. |
| `die` | text (a number is text too) | The die a Pool rolls: `d6`. |
| `zeroDice` | [ZeroDiceDefinition](#zerodicedefinition) | What a pool of no dice (or fewer) rolls instead. Null: such a pool is a problem with the roll. |
| `keep` | [KeepDefinition](#keepdefinition) | Which dice count. Null: all of them. |
| `explode` | [RerollDefinition](#rerolldefinition) | A die showing On adds another die. |
| `implode` | [RerollDefinition](#rerolldefinition) | A die showing On takes another die away. |
| `add` | list of text (a number is text too) | Formulas added to the dice: values, `modifiers('attack')`. |
| `inputs` | list of [RollInputDefinition](#rollinputdefinition) |  |
| `outcomes` | list of [RollOutcomeDefinition](#rolloutcomedefinition) | The first whose `when` holds is the outcome; one without `when` always holds. |
| `carries` | list of text (a number is text too) | Inputs copied onto the result for whatever follows (position and effect). |
| `then` | list of [RollFollowUpDefinition](#rollfollowupdefinition) | What follows the roll: another roll (a confirmation) or intents (stress to pay). |
| `participants` | [RollParticipantsDefinition](#rollparticipantsdefinition) | A group roll: who rolls, and which roll each of them makes. |
| `result` | text (a number is text too) | For a group roll, which participant's outcome is the group's: `best` (the earliest in the participants' roll's Outcomes) or `worst`. |

## ZeroDiceDefinition

`{ roll: 2, keep: lowest }`: the dice a pool of none rolls, and which one counts.

| Key | Type | What it is |
|---|---|---|
| `roll` | whole number |  |
| `keep` | text (a number is text too) | `lowest` or `highest`. |

## KeepDefinition

`{ highest: 1 }` or `{ lowest: 1 }`.

| Key | Type | What it is |
|---|---|---|
| `highest` | whole number |  |
| `lowest` | whole number |  |

## RerollDefinition

`{ on: 10, times: 1 }`: the face that rolls another die, and how many times in a row it may.

| Key | Type | What it is |
|---|---|---|
| `on` | whole number |  |
| `times` | whole number | How many more dice one die may bring; null: as many as come up (within the engine's limit). |

## RollInputDefinition

A named parameter supplied when the roll is made.

| Key | Type | What it is |
|---|---|---|
| `key` | text (a number is text too) |  |
| `from` | text (a number is text too) | Who supplies it: `roller` (the default), `gm`, `weapon` (a property of the item the `weapon` input names) or `parent` (the roll this one follows). |
| `label` | text (a number is text too) |  |
| `property` | text (a number is text too) | For `from: weapon`, the item property it reads. |
| `default` | any |  |
| `options` | list of text (a number is text too) |  |

## RollOutcomeDefinition

| Key | Type | What it is |
|---|---|---|
| `name` | text (a number is text too) |  |
| `when` | text (a number is text too) |  |
| `replacesParent` | true or false | A follow-up roll's outcome that becomes the roll it follows (a confirmed threat is the attack's critical). |
| `mark` | text (a number is text too) | How the dice display marks it: `critical`, `fumble` or none. Unset, an outcome named `critical` or `fumble` is marked so. |

## RollFollowUpDefinition

`{ when, roll }` or `{ when, intents }`.

| Key | Type | What it is |
|---|---|---|
| `when` | text (a number is text too) |  |
| `roll` | text (a number is text too) |  |
| `intents` | list of map of any | Intents as overhaul section 3 names them: `{ adjust: { track, by, actor } }`. |

## RollParticipantsDefinition

| Key | Type | What it is |
|---|---|---|
| `from` | text (a number is text too) | Who chooses the participants: `gm` or `roller`. |
| `roll` | text (a number is text too) | The roll each participant makes. |
