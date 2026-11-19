// @ts-check
// Scripted rules of this system: the recovery check of a dying creature (rule dying-rule). The host hands plain read-only data and reads
// a list of intents back; nothing here changes anything itself, and the host's dice are the only dice (ctx.d).
// codex.d.ts beside this file is written by Codex.SystemCheck --types; the JSDoc types below read it.
/** @typedef {import("./codex").Event} Event */
/** @typedef {import("./codex").Sheet} Sheet */
/** @typedef {import("./codex").Context} Context */
/** @typedef {import("./codex").Intent} Intent */

// What a recovery check does to the dying value, by degree (0 critical failure, 1 failure, 2 success, 3 critical success).
const STEP = [2, 1, -1, -2];

// The dying value at which a creature dies.
const DEATH = 4;

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

  // Doomed lowers the dying value a creature dies at; one that lowers it to 0 kills at once, with no check.
  const deathAt = Math.max(0, DEATH - (values.Doomed ?? 0));
  if (deathAt === 0) return die();

  const dc = 10 + dying;
  const natural = ctx.d(20);
  // A flat check: the die is the whole total.
  const next = dying + STEP[ctx.call("degree", natural, dc, natural)];
  /** @type {Intent} */
  const said = { log: { key: "Recovery check", args: [natural, dc] } };
  if (next >= deathAt) return [said, ...die()];

  if (next <= 0) return [said, ...recover()];
  return [said, { condition: { add: "Dying", value: next } }];
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
