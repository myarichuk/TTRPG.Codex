// The script third of the supplier contract fixture (system.yaml). Each export mirrors a formula there and a type in
// tests/Codex.ContractFixture.

export function hitPoints(body, will) {
  return { value: 10 + 5 * Math.ceil((body + will) / 2), parts: [["base", 10], ["BODY+WILL", body + will]] };
}

// The host's div rounds down; so does Math.floor. (Math.trunc would make -7 halve to -3.)
export function half(n) {
  return Math.floor(n / 2);
}

export function signed(n, flag) {
  const v = typeof n === "string" ? n.length : n;
  return flag ? v : -v;
}

export function boom() {
  throw new Error("boom");
}

export function onHit(ev, sheet) {
  const through = Math.max(0, ev.amount - sheet.tracks.SP);
  const out = [{ adjust: { track: "HP", by: -through } }];
  if (through > 0) out.push({ adjust: { track: "SP", by: -1 } });
  if (through >= 5) out.push({ condition: { add: "Wounded" } });
  return out;
}

export function locate(ev, sheet, ctx) {
  return [{ log: { key: "Contract.Located", args: [ctx.d(6), ctx.call("half", ev.amount)] } }];
}
