using System.Collections.Generic;

namespace Codex.Plugin.Abstractions;

/// <summary>
/// The rules a system applies to a character sheet, declared in <c>system.yaml</c> so a homebrew
/// system can have its own (or none). Everything here is evaluated by the core's sheet rule engine
/// against the sheet's field values and the content entries they reference; nothing is specific
/// to any one game.
/// </summary>
public class SheetRules
{
    /// <summary>"strict": players can't save a sheet that breaks a rule (the DM always can);
    /// "warn": violations are shown but saving is allowed; "off": rules are not evaluated.
    /// A campaign can override this (house rules).</summary>
    public string Enforcement { get; set; } = "strict";

    /// <summary>How scores are generated (point buy, standard array, typed in), capped and raised. Null: scores are typed in,
    /// kept within each attribute's <c>min</c> and <c>max</c>.</summary>
    public AttributeRules? Attributes { get; set; }

    /// <summary>Read-only fields computed from the sheet (proficiency bonus from level, ...).</summary>
    public List<DerivedField> Derived { get; set; } = new();

    /// <summary>Conditions a single-pick reference field's chosen entry must satisfy.</summary>
    public List<FieldConstraint> Constraints { get; set; } = new();

    /// <summary>Multi-pick lists of entries on the sheet (spells, feats, knacks...).</summary>
    public List<ListRule> Lists { get; set; } = new();

    /// <summary>Levels split across more than one class. Null: one class only.</summary>
    public MulticlassRules? Multiclass { get; set; }

    /// <summary>Expendable slot pools (spell slots, pact slots...) computed from class tables and
    /// tracked during play as resources.</summary>
    public List<SlotPoolRule> SlotPools { get; set; } = new();

    /// <summary>Maximum hit points computed from class hit dice and Constitution. Null: hit points are typed in.</summary>
    public HitPointRules? HitPoints { get; set; }

    /// <summary>Armor class computed from equipment, class features and bonuses. Null: typed in.</summary>
    public ArmorClassRules? ArmorClass { get; set; }

    /// <summary>What a character carries and wears. Null: no inventory.</summary>
    public InventoryRules? Inventory { get; set; }

    /// <summary>Saving throws, skills and passive scores. Null: the sheet has none.</summary>
    public SkillRules? Skills { get; set; }

    /// <summary>Spell save DC and spell attack bonus per spellcasting class. Null: not shown.</summary>
    public SpellcastingRules? Spellcasting { get; set; }

    /// <summary>Armor and weapon training, checked against what the sheet equips. Null: no warnings.</summary>
    public ProficiencyRules? Proficiencies { get; set; }

    /// <summary>Values copied from a referenced entry onto the sheet when the reference changes
    /// (a species' speed and size). Null: nothing is copied.</summary>
    public List<InheritRule> Inherits { get; set; } = new();

    /// <summary>Where a sheet's features come from (a class's and a subclass's feature rows, a species' traits, the feats
    /// picked), so what a feature says it grants reaches the sheet: numbers it changes, entries it adds to a list, and a
    /// counter of its uses. Empty: features are text only.</summary>
    public List<FeatureSource> Features { get; set; } = new();

    /// <summary>What a sheet can do with a roll: casting what it picked, attacking with what it holds. Each source names
    /// where the entries are and how their attack bonus, save DC and dice are worked out. Empty: the sheet has no actions.</summary>
    public List<ActionSource> Actions { get; set; } = new();
}

/// <summary>
/// One kind of action and the entries that give it: the entries picked in <see cref="Lists"/>, or the items carried
/// (<see cref="Inventory"/>). What an entry rolls is the map under its <see cref="Property"/>:
/// <list type="bullet">
/// <item><c>attack</c>: <c>melee</c> or <c>ranged</c>: the action starts with an attack roll (<see cref="AttackBonus"/>).</item>
/// <item><c>abilities</c>: the attributes the attack may use; the best one is <c>abilityMod</c> in the formulas.</item>
/// <item><c>save</c>: the attribute of the saving throw it asks for, against <see cref="SaveDc"/>.</item>
/// <item><c>damage</c>: the dice (<c>8d6</c>, <c>1d4+1</c>), <c>damageType</c> their kind, <c>heals: true</c> when they heal.</item>
/// <item><c>addAbility: true</c>: the dice add <see cref="AbilityBonus"/>.</item>
/// <item><c>perSlot</c>: dice added for each slot level above the entry's own; <c>atLevels</c>: <c>{ 5: 2d10 }</c>, the dice
/// from that value of <see cref="ScaleBy"/> on; <c>alternatives</c>: <c>{ two-handed: 1d10 }</c>, other dice it may roll.</item>
/// </list>
/// The formulas read the sheet's numbers and derived values, and names of their own: <c>abilityMod</c>, <c>trained</c>
/// (1 when the first class is trained with the item, see <see cref="ProficiencyRules"/>), and <c>castAttack</c>,
/// <c>castSave</c> and <c>castMod</c> (the best of the sheet's casting classes, see <see cref="SpellcastingRules"/>).
/// </summary>
public class ActionSource
{
    /// <summary>Required, unique.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The verb on the button ("Cast", "Attack").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Keys of lists in <c>lists</c> whose picked entries are actions of this kind.</summary>
    public List<string> Lists { get; set; } = new();

    /// <summary>Or the inventory: <c>equipped</c> for what is worn or wielded, <c>carried</c> for everything.</summary>
    public string? Inventory { get; set; }

    /// <summary>Only inventory items with this tag (<c>weapons</c>). None: every item that has <see cref="Property"/>.</summary>
    public string? Tag { get; set; }

    /// <summary>The entry property holding what it rolls. Default <c>use</c>. A picked entry without it is still listed
    /// (casting it spends a slot and rolls nothing); an item without it is not.</summary>
    public string Property { get; set; } = "use";

    /// <summary>The entry property with its level; an entry of level 1 or more spends a slot of <see cref="Spends"/>.</summary>
    public string? LevelProperty { get; set; }

    /// <summary>Keys of the <c>slotPools</c> an action may spend from: one slot of the entry's level or higher.</summary>
    public List<string> Spends { get; set; } = new();

    /// <summary>Formula for the number added to the attack roll.</summary>
    public string? AttackBonus { get; set; }

    /// <summary>Formula for the DC of the saving throw.</summary>
    public string? SaveDc { get; set; }

    /// <summary>Formula for a number added to every entry's dice.</summary>
    public string? DamageBonus { get; set; }

    /// <summary>Formula for the number added to the dice of an entry that says <c>addAbility</c>.</summary>
    public string? AbilityBonus { get; set; }

    /// <summary>The sheet value an entry's <c>atLevels</c> is read by. Default: the system's level field.</summary>
    public string? ScaleBy { get; set; }
}

/// <summary>
/// One place features come from. Either <see cref="Field"/> (the entry a reference field names) or <see cref="List"/> (every
/// entry picked in a list). With <see cref="Property"/> the features are that property's rows (<c>name</c>, <c>description</c>,
/// and <see cref="LevelColumn"/>); without it the entry itself is one feature. A feature, row or entry, may carry:
/// <list type="bullet">
/// <item><c>modifiers</c>: <c>[{ target, amount, type, when }]</c>, as everywhere else. <c>target</c> is the armor class or
/// hit point key, a derived value, or a Number actor field; <c>amount</c> is a number or a formula; a <c>type</c> the system
/// stacks as <c>best</c> sets a floor ("at least 120") instead of adding.</item>
/// <item><c>adds</c>: <c>[{ to, entries, level }]</c>: entries put on a pick list (by id or name) or values on a tag list,
/// from <c>level</c> on. They leave again when the feature does.</item>
/// <item><c>uses</c>: <c>{ max, recoversOn, key }</c>: a counter of uses, <c>max</c> a number or a formula, refilled by the
/// rests named.</item>
/// </list>
/// </summary>
public class FeatureSource
{
    /// <summary>The EntityReference actor field whose entry carries the features (<c>class</c>, <c>subclass</c>, <c>species</c>).
    /// With multiclass rules, the class and subclass fields are read once per class.</summary>
    public string? Field { get; set; }

    /// <summary>Or: the key of a list in <c>lists</c> whose picked entries carry them (<c>feats</c>).</summary>
    public string? List { get; set; }

    /// <summary>The entry property holding feature rows (<c>features</c>, <c>traits</c>). None: the entry is one feature and
    /// carries <c>modifiers</c>, <c>adds</c> and <c>uses</c> among its own properties.</summary>
    public string? Property { get; set; }

    /// <summary>A row's column saying from which level it applies. Default <c>level</c>; a row without it always applies.</summary>
    public string LevelColumn { get; set; } = "level";

    /// <summary>The sheet value a row's level is compared with (<c>classLevel</c>, <c>level</c>). None: rows are not gated.</summary>
    public string? ByField { get; set; }
}

/// <summary>
/// Copies properties of the entry <see cref="Field"/> names onto sheet keys the moment the player
/// picks a different entry, then leaves them alone so a hand-edited speed survives later saves.
/// <see cref="AppliedKey"/> remembers which entry was last copied from.
/// </summary>
public class InheritRule
{
    /// <summary>Required: the EntityReference actor field whose chosen entry the values are copied from (<c>species</c>).</summary>
    public string Field { get; set; } = string.Empty;
    /// <summary>Required: a sheet key, not an actor field, where the id of the entry last copied from is kept
    /// (<c>speciesApplied</c>).</summary>
    public string AppliedKey { get; set; } = string.Empty;

    /// <summary>Entry property → sheet key.</summary>
    public Dictionary<string, string> Copy { get; set; } = new();
}

/// <summary>One skill: the attribute whose modifier it uses.</summary>
public class SkillDefinition
{
    /// <summary>Required, unique: the skill's key (<c>athletics</c>). Sheets and entries may name a skill by its key or its
    /// label.</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The skill's name as the sheet shows it ("Athletics").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The label as system.yaml wrote it. Sheets and backgrounds list skills by it ("Athletics"), so the rules
    /// match on it even when a localized copy shows <see cref="Label"/> in another language.</summary>
    public string? CanonicalLabel { get; set; }
    /// <summary>Required: the attribute key whose modifier the skill adds (<c>str</c>).</summary>
    public string Attribute { get; set; } = string.Empty;
}

/// <summary>A score of the form base + a skill's bonus (passive Perception is 10 + Perception).</summary>
public class PassiveRule
{
    /// <summary>The passive score's key (<c>passivePerception</c>).</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>Its name as the sheet shows it ("Passive Perception").</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>Required: the key of the skill in <c>list</c> whose bonus is added.</summary>
    public string Skill { get; set; } = string.Empty;
    /// <summary>The number the skill's bonus is added to. Default 10.</summary>
    public int Base { get; set; } = 10;
}

/// <summary>
/// Saving throws and skills. A skill is proficient when its key is in <see cref="ProficienciesKey"/>
/// (a list on the sheet) or in the <see cref="BackgroundProperty"/> of the entry <see cref="BackgroundField"/>
/// names; it is expert when it is also in <see cref="ExpertiseKey"/>. Saving throws are proficient for the
/// first class's <see cref="SavingThrowsProperty"/>. Bonuses add the derived field <see cref="BonusKey"/>.
/// </summary>
public class SkillRules
{
    /// <summary>What the sheet calls the skills, and the word its messages about them start with. Default "Skills".</summary>
    public string Label { get; set; } = "Skills";

    /// <summary>Required: the derived value skill and save bonuses add (5e: "proficiencyBonus").</summary>
    public string BonusKey { get; set; } = string.Empty;
    /// <summary>The sheet list of skills the player marked proficient, by key or label. Default <c>skillProficiencies</c>.</summary>
    public string ProficienciesKey { get; set; } = "skillProficiencies";
    /// <summary>The sheet list of skills with expertise (the bonus counted twice); expertise without proficiency is a warning.
    /// Default <c>skillExpertise</c>.</summary>
    public string ExpertiseKey { get; set; } = "skillExpertise";
    /// <summary>The sheet field naming the first class, whose saving throws and skill choices count. Default <c>class</c>.</summary>
    public string ClassField { get; set; } = "class";
    /// <summary>The class entry property listing the attributes (keys, abbreviations or labels) whose saving throws are
    /// proficient. Default <c>savingThrows</c>.</summary>
    public string SavingThrowsProperty { get; set; } = "savingThrows";
    /// <summary>The sheet field naming the entry that grants skills on its own (a background). Default <c>background</c>.</summary>
    public string BackgroundField { get; set; } = "background";
    /// <summary>The property on that entry listing the skills it grants. Default <c>skillProficiencies</c>.</summary>
    public string BackgroundProperty { get; set; } = "skillProficiencies";

    /// <summary>The first class's property saying how many skills it lets a character choose: a number, or text whose first
    /// number is it ("Choose 2: Acrobatics, …"). More chosen than that (beyond the background's) is a warning, since a
    /// species or feat may grant more.</summary>
    public string ClassChoicesProperty { get; set; } = "skillChoices";
    /// <summary>The skills, in the order the sheet shows them.</summary>
    public List<SkillDefinition> List { get; set; } = new();
    /// <summary>Scores worked out from a skill's bonus (passive Perception).</summary>
    public List<PassiveRule> Passives { get; set; } = new();
}

/// <summary>
/// Each class that has a <see cref="AbilityProperty"/> casts with that attribute: save DC is
/// <see cref="SaveBase"/> + the derived <see cref="BonusKey"/> + the modifier, the attack bonus is the
/// bonus + the modifier.
/// </summary>
public class SpellcastingRules
{
    /// <summary>The class entry property naming the attribute it casts with (key, abbreviation or label). A class without it
    /// does not cast. Default <c>spellcastingAbility</c>.</summary>
    public string AbilityProperty { get; set; } = "spellcastingAbility";

    /// <summary>Required: the derived value save DCs and attack bonuses add (5e: "proficiencyBonus").</summary>
    public string BonusKey { get; set; } = string.Empty;
    /// <summary>The number a spell save DC starts from. Default 8.</summary>
    public int SaveBase { get; set; } = 8;
}

/// <summary>
/// Warnings (never violations: a feat or feature may grant what the class lacks) for equipped armor and
/// weapons the first class's training doesn't cover, plus the armor's Strength minimum and stealth penalty.
/// Class training is a list of names such as "Light Armor", "Shields", "Martial Weapons" or "All armor".
/// </summary>
public class ProficiencyRules
{
    /// <summary>The class entry property listing its armor training. Default <c>armorProficiencies</c>.</summary>
    public string ArmorProperty { get; set; } = "armorProficiencies";
    /// <summary>The class entry property listing its weapon training, by weapon tag or name. Default
    /// <c>weaponProficiencies</c>.</summary>
    public string WeaponProperty { get; set; } = "weaponProficiencies";
    /// <summary>The sheet field naming the first class, whose training counts. Default <c>class</c>.</summary>
    public string ClassField { get; set; } = "class";

    /// <summary>Equipment property → the training name that covers it: "light" → "Light Armor".</summary>
    public Dictionary<string, string> ArmorTraining { get; set; } = new();

    /// <summary>Item property holding the Strength an armor needs, and the one flagging Stealth disadvantage.</summary>
    public string StrengthProperty { get; set; } = "strength";
    /// <summary>The attribute an armor's Strength minimum is checked against (5e: "str"). No default: a system without
    /// strength minimums leaves it out and none is checked.</summary>
    public string? StrengthAttribute { get; set; }
    /// <summary>The armor property that, when true, brings a warning of disadvantage on Stealth. Default <c>stealth</c>.</summary>
    public string StealthProperty { get; set; } = "stealth";

    /// <summary>Equipment tags that mark a weapon.</summary>
    public string WeaponTag { get; set; } = "weapons";

    /// <summary>A class's armor training that covers every armor but shields ("All armor"), matched at the start of the
    /// training's name; null when the system has none.</summary>
    public string? AllArmor { get; set; }

    /// <summary>The plural ending dropped when a weapon is matched to a class's weapon training by name, so "Longswords"
    /// covers a Longsword ("s" in English); null to match names as they are.</summary>
    public string? PluralSuffix { get; set; }
}

/// <summary>
/// Maximum hit points from the sheet's classes. Levels are taken first class first, then the other
/// classes in order. The very first level gives what <see cref="FirstLevel"/> says (the full hit die by default), every
/// later level the die's <see cref="Average"/> (or the player's roll in "rolled" mode), plus the <see cref="Attribute"/> modifier, never less than
/// <see cref="MinimumPerLevel"/>. Because the modifier is read from the final score, raising
/// Constitution raises maximum hit points for every level at once. The result is written to
/// <see cref="Key"/> only while the sheet's <see cref="ModeKey"/> is not "manual" (a sheet without the
/// key is manual, so existing characters and imported creatures keep their numbers).
/// </summary>
public class HitPointRules
{
    /// <summary>Sheet key that receives the maximum (the primary resource's max key).</summary>
    public string Key { get; set; } = "HP_Max";

    /// <summary>Sheet key of current hit points: it follows the maximum when it was at full health.</summary>
    public string CurrentKey { get; set; } = "HP";

    /// <summary>Must be an EntityReference actor field: the first class, whose entry has the hit die. Default <c>class</c>.</summary>
    public string ClassField { get; set; } = "class";
    /// <summary>Must be an actor field: the character's total level. Default <c>level</c>.</summary>
    public string LevelField { get; set; } = "level";

    /// <summary>Class entry property holding the hit die, like "d10".</summary>
    public string DieProperty { get; set; } = "hitDie";

    /// <summary>Required: the attribute whose modifier is added per level (5e: "con").</summary>
    public string Attribute { get; set; } = string.Empty;

    /// <summary>The least a level gives once the modifier is added. Default 1.</summary>
    public int MinimumPerLevel { get; set; } = 1;

    public const string FullDie = "full-die";
    public const string HalfPlusOne = "half-plus-one";
    public const string Half = "half";

    /// <summary>What the character's very first level gives: <see cref="FullDie"/>, or "average" for the die's average.</summary>
    public string FirstLevel { get; set; } = FullDie;

    /// <summary>The gain a level takes in place of a roll: <see cref="HalfPlusOne"/> (a d10 gives 6) or <see cref="Half"/> (a d10
    /// gives 5).</summary>
    public string Average { get; set; } = HalfPlusOne;

    /// <summary>The die's average by <see cref="Average"/>.</summary>
    public int AverageOf(int die) => Average == Half ? die / 2 : die / 2 + 1;

    /// <summary>What the character's first level gives for a die.</summary>
    public int FirstLevelGain(int die) => FirstLevel == FullDie ? die : AverageOf(die);

    /// <summary>"average", "rolled" or "manual".</summary>
    public List<string> Modes { get; set; } = new() { "average", "rolled", "manual" };
    /// <summary>The mode a new sheet starts in, and the one used when a sheet names a mode not in <c>modes</c>; must be one of
    /// <c>modes</c>. Default <c>average</c>.</summary>
    public string Default { get; set; } = "average";
    /// <summary>The sheet key holding the sheet's mode; must not be an actor field. Default <c>hpMode</c>.</summary>
    public string ModeKey { get; set; } = "hpMode";

    /// <summary>Sheet key holding the player's rolls for levels 2 and up, in level order.</summary>
    public string RollsKey { get; set; } = "hpRolls";

    /// <summary>Entries whose number adds to every level's gain (Dwarven Toughness): the sheet field that
    /// names the entry and the property holding the bonus.</summary>
    public FieldProperty? PerLevelBonus { get; set; }
}

/// <summary>
/// Armor class. With body armor worn: its base plus the Dexterity modifier (limited by the armor's cap,
/// none for a cap of 0). Without: the best of <see cref="Unarmored"/> and the classes' own formulas
/// (<see cref="ClassFormulaProperty"/>, such as 10 + Dex + Con). Then every equipped item's bonus, the
/// bonus of picks in <see cref="BonusLists"/> and the sheet's <see cref="MiscField"/> are added. Written
/// to <see cref="Key"/> while <see cref="ModeKey"/> is "auto"; a sheet without that key is manual.
/// </summary>
public class ArmorClassRules
{
    /// <summary>The sheet key that receives the armor class. Default <c>armorClass</c>.</summary>
    public string Key { get; set; } = "armorClass";
    /// <summary>The sheet key holding <c>auto</c> or <c>manual</c>: new sheets start <c>auto</c>; must not be an actor field.
    /// Default <c>acMode</c>.</summary>
    public string ModeKey { get; set; } = "acMode";

    /// <summary>Required: the formula over the sheet's numbers when nothing is worn and no class formula is better
    /// (5e: "10 + mod(dex)").</summary>
    public string Unarmored { get; set; } = string.Empty;

    /// <summary>Required: the attribute added to body armor's base, up to its cap (5e: "dex").</summary>
    public string DexAttribute { get; set; } = string.Empty;

    /// <summary>Class entry property with an unarmored formula, and the one saying a shield is still allowed.</summary>
    public string ClassFormulaProperty { get; set; } = "unarmoredDefense";
    /// <summary>Default <c>unarmoredDefenseShield</c>: a class entry property that, when false, means the class's unarmored
    /// formula does not count while a shield is carried. Absent: it does.</summary>
    public string ClassShieldProperty { get; set; } = "unarmoredDefenseShield";

    /// <summary>Item properties: the armor type ("light", "medium", "heavy", "shield"), the base AC of body
    /// armor, its Dexterity cap (absent: uncapped; 0: no Dexterity), and the flat bonus any item gives.</summary>
    public string TypeProperty { get; set; } = "armorType";
    /// <summary>The item property with body armor's base armor class; an item that has it is body armor. Default
    /// <c>armorBase</c>.</summary>
    public string BaseProperty { get; set; } = "armorBase";
    /// <summary>The item property capping the Dexterity added to body armor (absent: no cap; 0: none added). Default
    /// <c>armorDexCap</c>.</summary>
    public string DexCapProperty { get; set; } = "armorDexCap";
    /// <summary>The property with a flat bonus, on any equipped item (a shield, a ring) or on a pick in <c>bonusLists</c>.
    /// Default <c>armorClassBonus</c>.</summary>
    public string BonusProperty { get; set; } = "armorClassBonus";

    /// <summary>The <see cref="TypeProperty"/> value that marks a shield.</summary>
    public string ShieldType { get; set; } = "shield";

    /// <summary>Lists whose picks may add <see cref="BonusProperty"/> (a fighting style feat), and the pick
    /// property that limits the bonus to wearers of body armor.</summary>
    public List<string> BonusLists { get; set; } = new();
    /// <summary>The pick property that, when true, gives its bonus only while body armor is worn. Default
    /// <c>armorClassRequiresArmor</c>.</summary>
    public string RequiresArmorProperty { get; set; } = "armorClassRequiresArmor";

    /// <summary>Numeric sheet field for everything else (a spell, a magic effect).</summary>
    public string MiscField { get; set; } = "armorClassMisc";
}

/// <summary>
/// The items a sheet carries, stored under <see cref="Key"/> as <c>"item-id|qty:2,equipped:1"</c>. Items
/// may come from any of <see cref="Types"/>. At most one suit of armor and one shield may be equipped.
/// </summary>
public class InventoryRules
{
    /// <summary>The sheet key the items are stored under; must not be an actor field. Default <c>inventory</c>.</summary>
    public string Key { get; set; } = "inventory";
    /// <summary>What the sheet calls the inventory, and the word its messages start with. Default "Equipment".</summary>
    public string Label { get; set; } = "Equipment";
    /// <summary>The entity types items may be of; each must be one the system declares. Default <c>[equipment]</c>.</summary>
    public List<string> Types { get; set; } = new() { "equipment" };
    /// <summary>The name the item's quantity is stored under after its id (<c>qty:2</c>); none stored is 1. Default <c>qty</c>.</summary>
    public string QuantityChoice { get; set; } = "qty";
    /// <summary>The name that marks an item worn or wielded (<c>equipped:1</c>). Default <c>equipped</c>.</summary>
    public string EquippedChoice { get; set; } = "equipped";

    /// <summary>The item property holding one item's weight; the sheet adds them up, times their quantities.</summary>
    public string WeightProperty { get; set; } = "weight";

    /// <summary>The word the total is shown with ("lb"), translated like the system's other words. None shows the number alone.</summary>
    public string? WeightUnit { get; set; }

    /// <summary>A formula for how much a character can carry ("str * 15"); carrying more is a warning. None: no limit.</summary>
    public string? Capacity { get; set; }
}

/// <summary>How ability scores are generated and adjusted.</summary>
public class AttributeRules
{
    /// <summary>Methods a player may choose from: "point-buy", "standard-array", "manual". The DM can always use "manual" (typed in,
    /// capped at <see cref="Max"/>) and "printed" (a stat block's scores as the block prints them: no cap, no bonuses or increases added); imported creatures get
    /// "printed".</summary>
    public List<string> Methods { get; set; } = new() { "manual" };

    /// <summary>The method a new sheet starts with, and the one a sheet that names none uses. Default <c>manual</c>.</summary>
    public string Default { get; set; } = "manual";

    /// <summary>Upper bound on a final score (base plus bonuses), except on a "printed" sheet.</summary>
    public int? Max { get; set; }

    /// <summary>The budget and costs for <c>point-buy</c>; required when <c>methods</c> offers it.</summary>
    public PointBuyRules? PointBuy { get; set; }

    /// <summary>The fixed set of scores to assign, one per attribute, for "standard-array".</summary>
    public List<int> StandardArray { get; set; } = new();

    /// <summary>Bonuses granted by a referenced entry (a background's "+2/+1 among these abilities").</summary>
    public BonusRules? Bonuses { get; set; }

    /// <summary>Score increases granted by entries picked in a list (an Ability Score Improvement feat).</summary>
    public IncreaseRules? Increases { get; set; }
}

/// <summary>
/// Score increases that come from picks in a <see cref="ListRule"/>: each picked entry whose
/// <see cref="Property"/> is a positive number grants that many points, spread by the player over
/// the attributes its <see cref="OptionsProperty"/> lists (any attribute when it lists none), and
/// can raise a score no higher than its <see cref="CapProperty"/> (or <see cref="AttributeRules.Max"/>).
/// The player's choice is stored on the pick itself: <c>"feat-id|str:1,dex:1"</c>.
/// </summary>
public class IncreaseRules
{
    /// <summary>What the sheet calls these increases, and the word its messages about them start with. Default "Score
    /// increases".</summary>
    public string Label { get; set; } = "Score increases";

    /// <summary>The <see cref="ListRule.Key"/> whose picks can grant increases.</summary>
    public string FromList { get; set; } = string.Empty;

    /// <summary>Entry property holding how many points the entry grants.</summary>
    public string Property { get; set; } = "abilityIncrease";

    /// <summary>Entry property listing the attributes (keys, abbreviations or labels) the points may go to.</summary>
    public string OptionsProperty { get; set; } = "abilityIncreaseOptions";

    /// <summary>Entry property holding the highest score the increase may reach.</summary>
    public string CapProperty { get; set; } = "abilityIncreaseCap";
}

/// <summary>
/// Multiclassing: the sheet's <see cref="ClassField"/> is the first class, <see cref="Key"/> holds
/// the others as <c>"class-id:levels"</c>, and <see cref="LevelField"/> stays the total character
/// level. Every class gets its own <see cref="ClassLevelKey"/> value, so table lookups written with
/// <c>byField: classLevel</c> read the right row for each class; list limits are summed across
/// classes and a list entry is allowed when any one class allows it.
/// </summary>
public class MulticlassRules
{
    /// <summary>The sheet key holding the classes after the first, each as <c>"class-id:levels"</c>; must not be an actor
    /// field. Default <c>multiclass</c>.</summary>
    public string Key { get; set; } = "multiclass";
    /// <summary>What the sheet calls the other classes, and the word its messages about them start with. Default "Additional
    /// classes".</summary>
    public string Label { get; set; } = "Additional classes";
    /// <summary>Must be an EntityReference actor field: the first class. Default <c>class</c>.</summary>
    public string ClassField { get; set; } = "class";
    /// <summary>Must be an actor field: the total character level, which the other classes' levels come out of. Default
    /// <c>level</c>.</summary>
    public string LevelField { get; set; } = "level";
    /// <summary>The name each class's own level goes by in table lookups (<c>byField: classLevel</c>); must not be an actor
    /// field. Default <c>classLevel</c>.</summary>
    public string ClassLevelKey { get; set; } = "classLevel";

    /// <summary>Class entry property listing prerequisite options, each like <c>"STR 13+"</c> or
    /// <c>"DEX 13+, WIS 13+"</c> (all of these). Any one option is enough.</summary>
    public string PrerequisitesProperty { get; set; } = "multiclassPrerequisites";

    /// <summary>Whether the first class must also meet its own prerequisites once you multiclass.</summary>
    public bool RequireForFirstClass { get; set; } = true;

    /// <summary>The sheet field naming the first class's subclass. Each other class stores its own
    /// after its levels: <c>"class-id:levels:subclass-id"</c>. Null: subclasses are not tracked per class.</summary>
    public string? SubclassField { get; set; }

    /// <summary>The subclass entry's property naming the class it belongs to; the sheet offers a class only its own subclasses.</summary>
    public string SubclassClassProperty { get; set; } = "class";

    /// <summary>The class level at which a subclass can be chosen; a subclass before it is a violation.</summary>
    public int SubclassLevel { get; set; } = 1;
}

/// <summary>
/// One pool of expendable slots. For each of the sheet's classes, the class entry's
/// <see cref="Table"/> row at that class's level supplies <see cref="Column"/>: either
/// <c>"4/3/2"</c> (slots per slot level from 1 up) or, when <see cref="SlotLevelColumn"/> is set, a
/// count of slots that are all of the level in that column. Classes are filtered by their
/// <see cref="CasterTypeProperty"/>. When two or more classes contribute and <see cref="Combine"/> is
/// set, their weighted levels are added up and looked up in the combined table instead.
/// During play each slot level is a resource: <c>{Key}_{level}</c> (remaining) and
/// <c>{Key}_{level}_Max</c>.
/// </summary>
public class SlotPoolRule
{
    /// <summary>Required, unique: the prefix of the pool's sheet keys (<c>slots_1</c>, <c>slots_1_Max</c>). Default
    /// <c>slots</c>.</summary>
    public string Key { get; set; } = "slots";
    /// <summary>The pool's name as the sheet and the table show it. Default "Spell slots".</summary>
    public string Label { get; set; } = "Spell slots";

    /// <summary>One slot, as a button's label and the table log name it ("spell slot": "Use a level 3 spell slot"). The
    /// pool's label when not set. Translated through the system's values, like the label.</summary>
    public string? Unit { get; set; }

    public string UnitName => Unit ?? Label;

    /// <summary>The sheet field naming the class entry, and the sheet value whose row is read
    /// (with multiclass rules, use their <see cref="MulticlassRules.ClassLevelKey"/>).</summary>
    public string Field { get; set; } = "class";
    /// <summary>The sheet value whose row of the table is read. Default <c>level</c>.</summary>
    public string ByField { get; set; } = "level";

    /// <summary>Required: the class entry property holding the table, one row per level (each row's <c>level</c> is its key).
    /// Default <c>spellcasting</c>.</summary>
    public string Table { get; set; } = "spellcasting";
    /// <summary>Required: the row's column with the slots. Default <c>slots</c>.</summary>
    public string Column { get; set; } = "slots";
    /// <summary>When set, the row's column giving the level of every slot, and <c>column</c> is then a plain count (pact
    /// magic). None: <c>column</c> reads <c>"4/3/2"</c>.</summary>
    public string? SlotLevelColumn { get; set; }
    /// <summary>The class entry property with its caster type, which <c>includeCasterTypes</c>, <c>excludeCasterTypes</c> and
    /// <c>combine.weights</c> read. Default <c>casterType</c>.</summary>
    public string CasterTypeProperty { get; set; } = "casterType";

    /// <summary>Only classes with one of these caster types count (empty: any).</summary>
    public List<string> IncludeCasterTypes { get; set; } = new();
    /// <summary>Classes with one of these caster types do not count.</summary>
    public List<string> ExcludeCasterTypes { get; set; } = new();

    /// <summary>How two or more contributing classes combine into one table. Null: each class's slots are added up level by
    /// level.</summary>
    public SlotCombineRule? Combine { get; set; }

    /// <summary>Rest names that refill this pool ("Short Rest", "Long Rest"); the table offers one button per name.</summary>
    public List<string> RecoversOn { get; set; } = new();

    public string KeyFor(int slotLevel) => $"{Key}_{slotLevel}";
    public string MaxKeyFor(int slotLevel) => $"{Key}_{slotLevel}_Max";

    /// <summary>True for "{Key}_{n}" and "{Key}_{n}_Max".</summary>
    public bool Owns(string sheetKey)
    {
        if (!sheetKey.StartsWith(Key + "_", System.StringComparison.Ordinal)) return false;
        var rest = sheetKey.Substring(Key.Length + 1);
        if (rest.EndsWith("_Max", System.StringComparison.Ordinal)) rest = rest.Substring(0, rest.Length - 4);
        return rest.Length > 0 && int.TryParse(rest, out _);
    }
}

/// <summary>How several classes' slots combine: each class adds its level times its caster type's
/// weight (rounded per <see cref="Rounding"/>), and the sum picks a row of <see cref="Table"/>
/// (row 1 is caster level 1).</summary>
public class SlotCombineRule
{
    /// <summary>Caster type -> how much each of its class levels counts (<c>full: 1, half: 0.5</c>); a type not listed counts
    /// 0.</summary>
    public Dictionary<string, double> Weights { get; set; } = new();

    /// <summary>"up", "down" or "nearest", applied once to the weighted sum of every class's levels.</summary>
    public string Rounding { get; set; } = "down";

    /// <summary>Slots per caster level, each like <c>"4/3/2"</c>.</summary>
    public List<string> Table { get; set; } = new();
}

/// <summary>Point buy (<c>sheetRules.attributes.pointBuy</c>): scores bought from a budget at a cost each.</summary>
public class PointBuyRules
{
    /// <summary>The points to spend; spending more is a violation. Default 27.</summary>
    public int Budget { get; set; } = 27;
    /// <summary>The lowest score that may be bought, and where each score of a new point-buy sheet starts. Default 8.</summary>
    public int Min { get; set; } = 8;
    /// <summary>The highest score that may be bought, before bonuses. Default 15.</summary>
    public int Max { get; set; } = 15;

    /// <summary>Cost of each score from <see cref="Min"/> to <see cref="Max"/>.</summary>
    public Dictionary<int, int> Costs { get; set; } = new();
}

/// <summary>Bonuses a referenced entry lets the player spread over attributes (<c>sheetRules.attributes.bonuses</c>), such as a
/// background's +2/+1.</summary>
public class BonusRules
{
    /// <summary>What the sheet calls these bonuses, and the word its messages about them start with. Default "Bonuses".</summary>
    public string Label { get; set; } = "Bonuses";

    /// <summary>The sheet field whose referenced entry lists the eligible attributes.</summary>
    public string FromField { get; set; } = string.Empty;

    /// <summary>The property on that entry holding the eligible attribute keys, abbreviations or labels.</summary>
    public string Property { get; set; } = string.Empty;

    /// <summary>Allowed distributions, e.g. [[2, 1], [1, 1, 1]]: one bonus per distinct attribute.</summary>
    public List<List<int>> Allocations { get; set; } = new();
}

/// <summary>A read-only number worked out from the sheet (<c>sheetRules.derived</c>): proficiency bonus, initiative.</summary>
public class DerivedField
{
    /// <summary>Required: the name formulas and other rules read the value by (<c>proficiencyBonus</c>); must not be an actor
    /// field.</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The value's name as the sheet shows it.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>A C#-style expression over the sheet's numeric fields and attribute scores, e.g.
    /// <c>2 + (level - 1) / 4</c>. Integer arithmetic; <c>floor</c>, <c>ceil</c>, <c>max</c>, <c>min</c>
    /// and <c>mod(key)</c> (an attribute's modifier) are available.</summary>
    public string Formula { get; set; } = string.Empty;

    /// <summary>A group heading for the value. Translated with the system's other headings, but this version does not group
    /// derived values by it.</summary>
    public string? Category { get; set; }
}

/// <summary>Conditions the entry chosen in a single-pick reference field must meet (<c>sheetRules.constraints</c>); the field's
/// picker offers only entries that do.</summary>
public class FieldConstraint
{
    /// <summary>Required: the EntityReference actor field the rule is on (<c>subclass</c>).</summary>
    public string Field { get; set; } = string.Empty;
    /// <summary>The conditions; each one that fails is a violation with its <c>message</c>.</summary>
    public List<RuleCondition> Where { get; set; } = new();
}

/// <summary>A list of entries picked on the sheet (<c>sheetRules.lists</c>): spells, feats, fighting styles. The sheet stores
/// the picks under <see cref="Key"/>.</summary>
public class ListRule
{
    /// <summary>Required, unique: the sheet key the picks are stored under, and the name other rules use for the list; must not
    /// be an actor field.</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>The list's name as the sheet shows it, and the word its messages start with.</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>A line of help the sheet shows with the list.</summary>
    public string? Description { get; set; }
    /// <summary>Required: the entity type the picks are of; one the system declares.</summary>
    public string TargetEntityType { get; set; } = string.Empty;
    /// <summary>Conditions every pick must meet. With several classes, a pick is allowed when it meets them for any one class.</summary>
    public List<RuleCondition> Where { get; set; } = new();
    /// <summary>How many may be picked. Null: any number.</summary>
    public CountRule? MaxCount { get; set; }
    /// <summary>A group heading for the list. Translated with the system's other headings, but this version does not group
    /// lists by it.</summary>
    public string? Category { get; set; }

    /// <summary>Candidate property that, when true, lets the same entry be picked more than once.</summary>
    public string? RepeatableProperty { get; set; }
}

/// <summary>
/// One condition on a candidate entry. Exactly one comparison is set; <see cref="Property"/> names
/// the candidate's property ("level", "classes", "category"...), or "id"/"name"/"tags".
/// </summary>
public class RuleCondition
{
    /// <summary>Required: the candidate's property the comparison reads.</summary>
    public string Property { get; set; } = string.Empty;

    /// <summary>Candidate value equals this constant.</summary>
    public new object? Equals { get; set; }

    /// <summary>Candidate value is one of these.</summary>
    public List<string>? In { get; set; }

    /// <summary>Candidate numeric value &gt;= / &lt;= a constant.</summary>
    public double? Gte { get; set; }
    /// <summary>Candidate numeric value &lt;= this constant (with <c>gte</c>, both must hold).</summary>
    public double? Lte { get; set; }

    /// <summary>Candidate value equals the raw value of this sheet field (an entry id, say).</summary>
    public string? EqualsField { get; set; }

    /// <summary>Candidate value equals a property of the entry a sheet field references
    /// (e.g. the feat named by the chosen background).</summary>
    public FieldProperty? EqualsPropertyOfField { get; set; }

    /// <summary>Candidate list property contains the display name of the entry a sheet field references.</summary>
    public string? ContainsNameOfField { get; set; }

    /// <summary>Candidate numeric value &lt;= / &gt;= a numeric sheet field.</summary>
    public string? LteField { get; set; }
    /// <summary>Candidate numeric value &gt;= this numeric sheet field. A candidate without the property passes.</summary>
    public string? GteField { get; set; }

    /// <summary>Candidate numeric value &lt;= a value looked up in a referenced entry's table.</summary>
    public LookupRule? LteLookup { get; set; }

    /// <summary>Shown when the condition fails.</summary>
    public string? Message { get; set; }
}

/// <summary>A property of the entry a sheet field references: <c>{ field: background, property: feat }</c>.</summary>
public class FieldProperty
{
    /// <summary>The EntityReference actor field naming the entry.</summary>
    public string Field { get; set; } = string.Empty;
    /// <summary>The property read from that entry.</summary>
    public string Property { get; set; } = string.Empty;
}

/// <summary>
/// Reads a value out of a row table on a referenced entry: the sheet's <see cref="Field"/> names an
/// entry; that entry's <see cref="Table"/> property is a list of rows; the row whose
/// <see cref="RowKey"/> equals the sheet's <see cref="ByField"/> value (or the highest row not above
/// it) supplies <see cref="Column"/>.
/// </summary>
public class LookupRule
{
    /// <summary>Required: the EntityReference actor field naming the entry whose table is read.</summary>
    public string Field { get; set; } = string.Empty;
    /// <summary>Required: the entry property holding the table, a list of rows.</summary>
    public string Table { get; set; } = string.Empty;
    /// <summary>The sheet value the row is chosen by (with multiclass rules, their <c>classLevelKey</c>). Default <c>level</c>.</summary>
    public string ByField { get; set; } = "level";
    /// <summary>The column each row is keyed by. Default <c>level</c>.</summary>
    public string RowKey { get; set; } = "level";
    /// <summary>Required: the column whose number is read.</summary>
    public string Column { get; set; } = string.Empty;
}

/// <summary>A limit on a list (<c>maxCount</c>): a constant, a table lookup per class, or both, plus a number from a referenced
/// entry.</summary>
public class CountRule
{
    /// <summary>A fixed limit, counted once however many classes the sheet has.</summary>
    public int? Value { get; set; }
    /// <summary>A limit read from each class's table and added up across classes. Unknown (no limit) until an entry is chosen.</summary>
    public LookupRule? Lookup { get; set; }
    /// <summary>A number added to the limit. Default 0.</summary>
    public int Plus { get; set; }

    /// <summary>More tables added to <see cref="Lookup"/> for every class (a subclass's own grants).</summary>
    public List<LookupRule> Also { get; set; } = new();

    /// <summary>A number on the entry a sheet field references, added once (a species' extra feat).</summary>
    public FieldProperty? FromField { get; set; }
}
