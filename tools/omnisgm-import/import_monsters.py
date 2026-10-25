#!/usr/bin/env python3
"""Converts the Russian creatures of OmnisGM's SRD 5.2.1 translation (CC BY 4.0, (c) 2026 Petr Radilov / OmnisGM,
https://github.com/OmnisGM-App/OmnisGM-Rules, src/dnd/srd-5.2/ru/{12_MonstersA-Z,13_Animals}.md) into a Codex translation pack.

usage: import_monsters.py <omnisgm srd-5.2 dir> <srd-5.2.1 pack dir> <output pack dir> [<divergent ids output file>]

Each Russian heading ends with the English name in parentheses; that name is matched to our entry id. The description is
OmnisGM's stat block as written (it is the full SRD 5.2.1 block, with initiative and per-ability saves). Of the properties
only speed, senses, languages, skills and savingThrows are translated: they are plain text. Size, type and alignment stay
English because the Library filters on them, and rows (traits, actions) are left to the description (the registry drops the
English rows a translated description makes redundant).
"""
import os, re, sys

from omnisgm_common import blocks, faithful, norm, scalar, sentences

LABELS = {"Скорость": "speed", "Чувства": "senses", "Языки": "languages", "Навыки": "skills"}
ABILITIES = {"STR": "СИЛ", "DEX": "ЛОВ", "CON": "ТЕЛ", "INT": "ИНТ", "WIS": "МДР", "CHA": "ХАР"}  # as system.ru.yaml writes them
TEXT_PROPS = ("speed", "senses", "languages", "skills", "savingThrows")


def our_monsters(pack):
    found = {}
    folder = os.path.join(pack, "monsters")
    for f in os.listdir(folder):
        text = open(os.path.join(folder, f), encoding="utf-8").read()
        name = re.search(r"^name: (.+)$", text, re.M).group(1).strip().strip("'\"")
        props = {}
        for k in TEXT_PROPS:
            m = re.search(rf"^  {k}: (.*)$", text, re.M)
            if m:
                props[k] = m.group(1).strip().strip("'\"")
        found[norm(name)] = (f[:-5], props)
    return found


def convert(heading, body, ours):
    name = re.sub(r"\s*\([^()]*\)\s*$", "", heading).strip()
    props = {}
    for line in body.split("\n"):
        m = re.match(r"^- \*\*([^*:]+):\*\*\s*(.*)$", line.strip())
        if m and LABELS.get(m.group(1).strip()) in ours:
            props[LABELS[m.group(1).strip()]] = m.group(2).strip()
    if "savingThrows" in ours:
        props["savingThrows"] = re.sub(r"\b(STR|DEX|CON|INT|WIS|CHA)\b", lambda m: ABILITIES[m.group(1)], ours["savingThrows"])
    return name, body.strip(), props


def main():
    omnis, pack, out = sys.argv[1:4]
    ru = blocks(os.path.join(omnis, "ru", "12_MonstersA-Z.md")) + blocks(os.path.join(omnis, "ru", "13_Animals.md"))
    en = {}
    for f in ("12_MonstersA-Z.md", "13_Animals.md"):
        en.update({norm(h): b for h, b in blocks(os.path.join(omnis, "en", f))})
    ids = our_monsters(pack)
    items, missing, seen, divergent, faithful_ids = [], [], set(), [], []
    for rh, rb in ru:
        m = re.search(r"\(([^()]*)\)\s*$", rh)
        eh = m.group(1) if m else rh
        hit = ids.get(norm(eh))
        if not hit:
            missing.append(eh)
            continue
        sid, ours = hit
        if sid in seen:
            sys.exit(f"duplicate creature {sid} ({rh})")
        seen.add(sid)
        # The Russian block normally counts one sentence fewer than the English (the stat-line punctuation differs), so only a
        # bigger gap suggests a sentence was added or dropped.
        if norm(eh) in en and abs(sentences(rb) - sentences(en[norm(eh)]) + 1) >= 2:
            divergent.append(sid)
        if norm(eh) in en and faithful(en[norm(eh)], rb):
            faithful_ids.append(sid)
        items.append((sid,) + convert(rh, rb, ours))
    os.makedirs(os.path.join(out, "monster"), exist_ok=True)
    with open(os.path.join(out, "monster", "monsters.yaml"), "w", encoding="utf-8") as f:
        for sid, name, desc, props in sorted(items):
            f.write(f"- id: {sid}\n  name: {scalar(name)}\n  description: |-\n")
            f.write("".join(("    " + l if l else "") + "\n" for l in desc.split("\n")))
            if props:
                f.write("  properties:\n")
                for k, v in props.items():
                    f.write(f"    {k}: {scalar(v)}\n")
    if len(sys.argv) > 4:
        open(sys.argv[4], "w").write("\n".join(sorted(divergent)) + "\n")
        open(sys.argv[4] + ".faithful", "w").write("\n".join(sorted(faithful_ids)) + "\n")
    notcovered = sorted(set(v[0] for v in ids.values()) - seen)
    print(f"{len(items)} creatures written; OmnisGM headings without an entry: {missing}")
    print(f"our creatures not covered ({len(notcovered)}): {notcovered}")


main()
