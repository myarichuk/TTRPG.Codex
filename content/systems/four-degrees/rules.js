// @ts-check
// Scripted rules of this system: the dying condition, from the recovery check to what damage, healing and a hero point do to it (rules
// dying-rule, getting-knocked-out, wounded, instant-death and hero-points). The host hands plain read-only data and reads
// a list of intents back; nothing here changes anything itself, and the host's dice are the only dice (ctx.d).
// codex.d.ts beside this file is written by Codex.SystemCheck --types; the JSDoc types below read it.
/** @typedef {import("./codex").Event} Event */
/** @typedef {import("./codex").Sheet} Sheet */
/** @typedef {import("./codex").Context} Context */
/** @typedef {import("./codex").Intent} Intent */

// What a recovery check does to the dying value, by degree (0 critical failure, 1 failure, 2 success, 3 critical success).
const STEP = [2, 1, -1, -2];

// The words for those degrees, as the log says them.
const DEGREES = ["Critical failure", "Failure", "Success", "Critical success"];

// The dying value at which a creature dies.
const DEATH = 4;

// The flat check that may end persistent damage (conditions persistent-damage).
const PERSISTENT_DC = 15;

// The start of a turn: a dying creature at 0 hit points makes a flat check against 10 plus its dying value. The dying value moves by the
// degree. At the value it dies at (4, less its doomed value) it dies; at 0 it loses the condition. Losing the condition, whatever the
// cause, adds 1 to wounded, and a creature that has 1 hit point or more loses it with no check. A creature that is not dying does nothing,
// and rolls nothing. Every creature's turn raises this event, so most calls return at once.
/** @param {Event} ev @param {Sheet} sheet @param {Context} ctx @returns {Intent[]} */
export function onTurnStart(ev, sheet, ctx) {
  const values = sheet.conditionValues;
  const dying = values.Dying ?? 0;
  if (dying <= 0 || sheet.conditions.includes("Dead")) return [];
  if ((sheet.resources.HP ?? 0) > 0) return recover();

  // One already at the value it dies at (set by hand, or doomed rose) dies with no check.
  const dead = deathAt(values);
  if (dying >= dead) return die();

  const dc = 10 + dying;
  const natural = ctx.d(20);
  // A flat check: the die is the whole total.
  const degree = ctx.call("degree", natural, dc, natural);
  const next = dying + STEP[degree];
  // The log reads "Recovery check DC 12, Failure" (the die is said by the host); the words are the system's own, so they translate.
  /** @type {Intent} */
  const said = { log: { key: "Recovery check", args: [`DC ${dc}`, DEGREES[degree]] } };
  if (next >= dead) return [said, ...die()];

  if (next <= 0) return [said, ...recover()];
  return [said, { condition: { add: "Dying", value: next } }];
}

// The end of a turn: a creature with the persistent damage condition (conditions persistent-damage, and the rules text on persistent
// damage and on ending a turn) rolls a DC 15 flat check after it has taken the damage, and the condition ends on a success. The damage itself stays
// the GM's (the condition carries its amount and type in the text, not in a value), so it is dealt before the turn is advanced. A
// creature without the condition does nothing, and rolls nothing. The built-in lowering of valued conditions (frightened) still runs
// after this handler.
/** @param {Event} ev @param {Sheet} sheet @param {Context} ctx @returns {Intent[]} */
export function onTurnEnd(ev, sheet, ctx) {
  if (!sheet.conditions.includes("Persistent Damage") || sheet.conditions.includes("Dead")) return [];
  const natural = ctx.d(20);
  // A flat check: the die is the whole total.
  const degree = ctx.call("degree", natural, PERSISTENT_DC, natural);
  /** @type {Intent} */
  const said = { log: { key: "Persistent damage check", args: [`DC ${PERSISTENT_DC}`, DEGREES[degree]] } };
  return degree >= 2 ? [said, { condition: { remove: "Persistent Damage" } }] : [said];
}

// Damage was taken (the table raises this after its own damage, once per blow; rules dying-rule, getting-knocked-out, wounded and
// instant-death). A creature that died, or is dead, is left alone. Damage of twice the maximum or more in the one blow kills (counted
// before temporary hit points take any of it: the rule does not say, and this is the plainer reading). A creature that is dying has its
// value raised by 1, or by 2 for a critical hit or its own critical failure on a save; a creature that is not dying and has been brought
// to 0 hit points (or was hit while there) becomes dying 1, or 2 for a critical, and its wounded value is added on top. A blow that
// temporary hit points take in full still counts as taking damage, so it raises the value of a dying creature (the rule says "take damage"
// and does not say otherwise). Only the hit point pool is read here.
/** @param {Event} ev @param {Sheet} sheet @returns {Intent[]} */
export function onDamaged(ev, sheet) {
  if (ev.pool !== "HP") return [];
  if (ev.died || sheet.conditions.includes("Dead")) return [];
  const values = sheet.conditionValues;
  if (ev.max > 0 && ev.dealt >= 2 * ev.max) return [{ log: { key: "Massive damage" } }, ...die()];

  const step = ev.critical || ev.save === "criticalFailure" ? 2 : 1;
  const dying = values.Dying ?? 0;
  if (dying > 0) return setDying(dying + step, values);
  if (ev.after === 0) return setDying(step + (values.Wounded ?? 0), values);
  return [];
}

// Hit points came back (the table raises this after its own healing). A dying creature with 1 hit point or more loses the condition by
// itself, and is wounded for it (dying-rule). The table's own healing has already woken it.
/** @param {Event} ev @param {Sheet} sheet @returns {Intent[]} */
export function onHealed(ev, sheet) {
  if (ev.pool !== "HP") return [];
  if ((sheet.conditionValues.Dying ?? 0) > 0 && ev.after >= 1) return recover();
  return [];
}

// Heroic recovery (instant-death, hero-points): a dying creature with a hero point spends all it has, loses the dying condition and stays
// at 0 hit points. It gains no wounded, and keeps the wounded it had. With no hero point nothing happens but the line saying so.
/** @param {Event} ev @param {Sheet} sheet @returns {Intent[]} */
export function onHeroicRecovery(ev, sheet) {
  if ((sheet.conditionValues.Dying ?? 0) <= 0 || sheet.conditions.includes("Dead")) return [];
  if ((sheet.tracks.heroPoints ?? 0) < 1) return [{ log: { key: "No hero points" } }];
  return [{ set: { track: "heroPoints", to: 0 } }, { condition: { remove: "Dying" } }];
}

// The dying value a creature dies at: 4, less its doomed value (the doomed condition).
/** @param {Sheet["conditionValues"]} values @returns {number} */
function deathAt(values) {
  return Math.max(0, DEATH - (values.Doomed ?? 0));
}

// The dying value becomes `next`; at the value the creature dies at, it dies instead.
/** @param {number} next @param {Sheet["conditionValues"]} values @returns {Intent[]} */
function setDying(next, values) {
  if (next >= deathAt(values)) return die();
  return [{ condition: { add: "Dying", value: next } }];
}

// The creature dies, and is no longer doomed.
/** @returns {Intent[]} */
function die() {
  return [{ condition: { remove: "Dying" } }, { condition: { remove: "Doomed" } }, { kill: {} }];
}

// The creature stops dying and is wounded, or more wounded.
/** @returns {Intent[]} */
function recover() {
  return [{ condition: { remove: "Dying" } }, { condition: { add: "Wounded", by: 1 } }];
}
