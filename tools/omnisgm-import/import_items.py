#!/usr/bin/env python3
"""Converts the Russian magic items of OmnisGM's SRD 5.2.1 translation (CC BY 4.0, (c) 2026 Petr Radilov / OmnisGM,
https://github.com/OmnisGM-App/OmnisGM-Rules, src/dnd/srd-5.2/ru/10_MagicItems.md) into a Codex translation pack.

usage: import_items.py <omnisgm srd-5.2 dir> <srd-5.2.1 pack dir> <output pack dir> [<divergent ids output file>]

Items are the "#### Название (English name)" blocks. Only name and description are written: the properties
(category, rarity, attunement) are filtered on or read by rules, so they stay English. The description is OmnisGM's block as
written (an italic type, rarity and attunement line, then the text and any tables).
"""
import os, re, sys

from omnisgm_common import blocks, faithful, norm, scalar, sentences


def our_items(pack):
    found = {}
    folder = os.path.join(pack, "magic-items")
    for f in os.listdir(folder):
        text = open(os.path.join(folder, f), encoding="utf-8").read()
        found[norm(re.search(r"^name: (.+)$", text, re.M).group(1).strip().strip("'\""))] = f[:-5]
    return found


def main():
    omnis, pack, out = sys.argv[1:4]
    ru = blocks(os.path.join(omnis, "ru", "10_MagicItems.md"), level=4)
    en = {norm(h): b for h, b in blocks(os.path.join(omnis, "en", "10_MagicItems.md"), level=4)}
    ids = our_items(pack)
    items, missing, seen, divergent, faithful_ids = [], [], set(), [], []
    for rh, rb in ru:
        m = re.search(r"\(([^()]*)\)\s*$", rh)
        eh = m.group(1) if m else rh
        eh = re.sub(r",\s*\+1, \+2, or \+3$", "", eh)  # "Armor, +1, +2, or +3" is our "Armor"
        sid = ids.get(norm(eh))
        if not sid:
            missing.append(eh)
            continue
        if sid in seen:
            sys.exit(f"duplicate item {sid} ({rh})")
        seen.add(sid)
        if norm(eh) in en and abs(sentences(rb) - sentences(en[norm(eh)])) >= 2:
            divergent.append(sid)
        if norm(eh) in en and faithful(en[norm(eh)], rb):
            faithful_ids.append(sid)
        items.append((sid, re.sub(r"\s*\([^()]*\)\s*$", "", rh).strip(), rb.strip()))
    os.makedirs(os.path.join(out, "magic-item"), exist_ok=True)
    with open(os.path.join(out, "magic-item", "magic-items.yaml"), "w", encoding="utf-8") as f:
        for sid, name, desc in sorted(items):
            f.write(f"- id: {sid}\n  name: {scalar(name)}\n  description: |-\n")
            f.write("".join(("    " + l if l else "") + "\n" for l in desc.split("\n")))
    if len(sys.argv) > 4:
        open(sys.argv[4], "w").write("\n".join(sorted(divergent)) + "\n")
        open(sys.argv[4] + ".faithful", "w").write("\n".join(sorted(faithful_ids)) + "\n")
    notcovered = sorted(set(ids.values()) - seen)
    print(f"{len(items)} items written; OmnisGM headings without an entry ({len(missing)}): {missing}")
    print(f"our items not covered ({len(notcovered)}): {notcovered}")


main()
