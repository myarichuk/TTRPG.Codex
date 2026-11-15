// @ts-check
// Scripted rules of the street fixture (overhaul section 4, script supplier). The host passes plain read-only data
// and reads plain data back: a value with its working, or a list of intents. Nothing here changes anything itself.
// codex.d.ts beside this file is written by Codex.SystemCheck --types; the JSDoc types below read it.
/** @typedef {import("./codex").Event} Event */
/** @typedef {import("./codex").Sheet} Sheet */
/** @typedef {import("./codex").Context} Context */
/** @typedef {import("./codex").Intent} Intent */

// Vigor: 20, plus twice Might, plus half of Grit rounded up.
/** @param {number} might @param {number} grit @returns {import("./codex").Value} */
export function vigor(might, grit) {
  const half = Math.ceil(grit / 2);
  return { value: 20 + 2 * might + half, parts: [["base", 20], ["2 x might", 2 * might], ["half of grit, rounded up", half]] };
}

// Damage to a location: the guard there stops up to its value; damage that gets through wears the guard down by two.
// A hit to the head adds 3 to what gets through.
/** @param {Event} ev @param {Sheet} sheet @returns {Intent[]} */
export function onDamage(ev, sheet) {
  const guard = sheet.tracks[`Guard_${ev.location}`] ?? 0;
  if (ev.amount <= guard) return [{ log: { key: "damage.stopped", args: [ev.amount, guard] } }];
  const through = ev.amount - guard + (ev.location === "head" ? 3 : 0);
  return [
    { adjust: { track: "Vigor", by: -through } },
    { adjust: { track: `Guard_${ev.location}`, by: -2 } },
  ];
}

// Raising a skill costs 10 experience plus 15 times the new level.
/** @param {Event} ev @param {Sheet} sheet @param {Context} ctx @returns {Intent[]} */
export function onAdvance(ev, sheet, ctx) {
  const skill = sheet.lists.skills[ev.skill];
  const next = (skill?.level ?? 0) + 1;
  const cost = ctx.call("advanceCost", next);
  if (sheet.tracks.XP < cost) return [{ log: { key: "advance.notEnoughPoints", args: [ev.skill, cost, sheet.tracks.XP] } }];
  return [
    { adjust: { track: "XP", by: -cost } },
    { set: { list: "skills", key: ev.skill, field: "level", to: next } },
  ];
}
