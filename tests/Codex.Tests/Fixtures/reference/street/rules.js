// Scripted rules of the street fixture (overhaul section 4, script supplier). The host passes plain read-only data
// and reads plain data back: a value with its working, or a list of intents. Nothing here changes anything itself.

// Hit points: 10, plus 5 for each point of the average of Body and Will, rounded up.
export function hitPoints(body, will) {
  const half = Math.ceil((body + will) / 2);
  return { value: 10 + 5 * half, parts: [["base", 10], ["5 x half of BODY+WILL, rounded up", 5 * half]] };
}

// Damage to a location: armor stops up to its stopping power; damage that gets through wears the armor down by one.
// A hit to the head doubles what gets through.
export function onDamage(ev, sheet) {
  const sp = sheet.tracks[`SP_${ev.location}`] ?? 0;
  if (ev.amount <= sp) return [{ log: { key: "damage.stopped", args: [ev.amount, sp] } }];
  const through = (ev.amount - sp) * (ev.location === "head" ? 2 : 1);
  return [
    { adjust: { track: "HP", by: -through } },
    { adjust: { track: `SP_${ev.location}`, by: -1 } },
  ];
}

// Raising a skill costs 20 improvement points times the new level.
export function onAdvance(ev, sheet, ctx) {
  const skill = sheet.lists.skills[ev.skill];
  const next = (skill?.level ?? 0) + 1;
  const cost = ctx.call("advanceCost", next);
  if (sheet.tracks.IP < cost) return [{ log: { key: "advance.notEnoughPoints", args: [ev.skill, cost, sheet.tracks.IP] } }];
  return [
    { adjust: { track: "IP", by: -cost } },
    { set: { list: "skills", key: ev.skill, field: "level", to: next } },
  ];
}
