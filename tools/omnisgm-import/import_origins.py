#!/usr/bin/env python3
"""Converts feats, species and backgrounds of OmnisGM's SRD 5.2.1 Russian translation (CC BY 4.0, (c) 2026 Petr Radilov / OmnisGM,
https://github.com/OmnisGM-App/OmnisGM-Rules, src/dnd/srd-5.2/ru/{05_Feats,04_CharacterOrigins}.md) into Codex translation packs.

usage: import_origins.py <omnisgm srd-5.2 dir> <srd-5.2.1 pack dir> <output pack dir> [<ids output prefix>]

With the last argument, "<prefix>.faithful" lists the ids whose Russian keeps every number and dice of OmnisGM's English.

Writes feat/feats.yaml, species/species.yaml and background/backgrounds.yaml. Name and description are translated, plus the
background's equipment and tool proficiency lines (plain text); everything the Library filters on stays English.
"""
import os, re, sys

from omnisgm_common import blocks, english_name, faithful, norm, our_names, russian_name, write_items


def labelled(body, label):
    m = re.search(rf"^\*\*{label}:\*\*\s*(.*)$", body, re.M)
    return m.group(1).strip() if m else None


def has_rows(pack_folder, sid, key):
    return re.search(rf"^  {key}:", open(os.path.join(pack_folder, sid + ".yaml"), encoding="utf-8").read(), re.M) is not None


def convert(ru, en, folder, ours, out, filename, background=False, faithful_ids=None, pack_folder=None):
    items, missing = [], []
    for h, b in ru:
        sid = ours.get(norm(english_name(h)))
        if not sid:
            missing.append(english_name(h))
            continue
        props = {}
        if background:
            for key, label in (("toolProficiency", "Владение инструментами"), ("equipment", "Снаряжение")):
                value = labelled(b, label)
                if value:
                    props[key] = re.sub(r"[*_]", "", value) if key == "equipment" else value
        if pack_folder and has_rows(pack_folder, sid, "lineages"):
            props["lineages"] = []  # the lineage table is in the translated description; the English rows would repeat it
        if faithful_ids is not None and norm(english_name(h)) in en and faithful(en[norm(english_name(h))], b):
            faithful_ids.append(sid)
        items.append((sid, russian_name(h), b.strip(), props))
    write_items(os.path.join(out, folder, filename), items)
    print(f"{folder}: {len(items)} written, {len(ours) - len(items)} of ours not covered, unmatched OmnisGM headings: {missing}")


def main():
    omnis, pack, out = sys.argv[1:4]
    base, english = os.path.join(omnis, "ru"), os.path.join(omnis, "en")
    ok = [] if len(sys.argv) > 4 else None
    feats_en = {norm(h): b for h, b in blocks(os.path.join(english, "05_Feats.md"), level=4)}
    convert(blocks(os.path.join(base, "05_Feats.md"), level=4), feats_en, "feat", our_names(pack, "feats"), out, "feats.yaml", faithful_ids=ok)

    def split(path, named):
        text = open(path, encoding="utf-8").read()
        at = text.index("### Описания видов") if named else text.index("## Character Species")
        found = blocks(path, level=4, italic=False, named=named)
        marks = {h: text.index("#### " + h) for h, _ in found}
        return [(h, b) for h, b in found if marks[h] > at], [(h, b) for h, b in found if marks[h] < at]

    species, backgrounds = split(os.path.join(base, "04_CharacterOrigins.md"), True)
    species_en, backgrounds_en = split(os.path.join(english, "04_CharacterOrigins.md"), False)
    convert(species, {norm(h): b for h, b in species_en}, "species", our_names(pack, "species"), out, "species.yaml", faithful_ids=ok, pack_folder=os.path.join(pack, "species"))
    convert(backgrounds, {norm(h): b for h, b in backgrounds_en}, "background", our_names(pack, "backgrounds"), out, "backgrounds.yaml", background=True, faithful_ids=ok)
    if ok is not None:
        open(sys.argv[4] + ".faithful", "w").write("\n".join(sorted(ok)) + "\n")


main()
