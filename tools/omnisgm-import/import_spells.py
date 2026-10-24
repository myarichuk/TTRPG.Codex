#!/usr/bin/env python3
"""Converts the Russian spells of OmnisGM's SRD 5.2.1 translation (CC BY 4.0, (c) 2026 Petr Radilov / OmnisGM,
https://github.com/OmnisGM-App/OmnisGM-Rules, src/dnd/srd-5.2/{en,ru}/07_Spells.md) into a Codex translation pack.

usage: import_spells.py <omnisgm srd-5.2 dir> <srd-5.2.1 pack dir> <output pack dir> [<divergent ids output file>]

The optional last argument receives the ids whose Russian has a different sentence count than OmnisGM's own English
(a sign that the translation adds or drops something); merge_with_fallback.py uses it.

Each Russian heading ends with the English name in parentheses ("Огненный шар (Fireball)"); that name is matched
to our entry id. (The Russian file is sorted by Russian name, so blocks cannot be paired by position.) Only name, description and the text properties are written;
the description is laid out like ours (header line, label lines joined by two-space line breaks, body paragraphs).
Run tools/Codex.TranslationCheck on the result: spells that fail its checks should not be shipped.
"""
import json, os, re, sys

LABELS = {"Время накладывания": "castingTime", "Время сотворения": "castingTime", "Дистанция": "range", "Длительность": "duration"}


def blocks(path):
    out, current = [], None
    for line in open(path, encoding="utf-8").read().replace("\r\n", "\n").split("\n"):
        if line.startswith("### "):
            current = [line[4:].strip(), []]
            out.append(current)
        elif current is not None:
            if line.startswith("## ") or line.startswith("# "):
                current = None
            else:
                current[1].append(line)
    return [(h, "\n".join(b).strip("\n")) for h, b in out if b and re.match(r"^\s*\*[^*]", "\n".join(b).strip("\n"))]


def our_ids(pack):
    names = {}
    for f in os.listdir(os.path.join(pack, "spells")):
        text = open(os.path.join(pack, "spells", f), encoding="utf-8").read()
        name = re.search(r"^name: (.+)$", text, re.M).group(1).strip().strip("'\"")
        has = {k: re.search(rf"^  {k}:", text, re.M) is not None for k in ("material", "higherLevel", "castingTime", "range", "duration")}
        names[norm(name)] = (f[:-5], has)
    return names


def norm(s):
    return re.sub(r"[^a-z0-9]", "", s.lower().replace("’", "'"))


def convert(heading, body, has):
    ru_name = re.sub(r"\s*\([^()]*\)\s*$", "", heading).strip()
    paras = [p for p in re.split(r"\n\s*\n", body.strip()) if p.strip()]
    header, labels, rest = paras[0].strip(), [], []
    for p in paras[1:]:
        if not rest and re.match(r"^\*\*[^*]+:\*\*", p.strip()) and "\n" not in p.strip():
            labels.append(p.strip())
        else:
            rest.append(p.strip())
    props = {}
    for label in labels:
        m = re.match(r"^\*\*([^*]+):\*\*\s*(.*)$", label)
        key = LABELS.get(m.group(1).strip())
        if key and has.get(key):
            props[key] = m.group(2).strip()
        if m.group(1).strip().startswith("Компоненты") and has.get("material"):
            mm = re.search(r"\((.*)\)\s*$", m.group(2))
            if mm:
                props["material"] = mm.group(1).strip()
    if has.get("higherLevel"):
        for p in rest:
            mm = re.match(r"^\*\*_[^*]*_\*\*\s*(.*)$", p, re.S)
            if mm:
                props["higherLevel"] = " ".join(mm.group(1).split())
    description = header + "\n\n" + "  \n".join(labels) + "\n\n" + "\n\n".join(rest)
    return ru_name, description.strip(), props


def sentences(text):
    text = re.sub(r"\*\*[^*]*\*\*|\*|_|\|[^\n]*", "", text)
    return len([x for x in re.split(r"(?<=[.!?])\s+", text) if len(x.strip()) > 3])


def scalar(value):
    return json.dumps(value, ensure_ascii=False)


def main():
    omnis, pack, out = sys.argv[1:4]
    ru, ids = blocks(os.path.join(omnis, "ru", "07_Spells.md")), our_ids(pack)
    en = {norm(h): b for h, b in blocks(os.path.join(omnis, "en", "07_Spells.md"))}
    items, missing, seen, divergent = [], [], set(), []
    for rh, rb in ru:
        m = re.search(r"\(([^()]*)\)\s*$", rh)
        eh = m.group(1) if m else rh
        hit = ids.get(norm(eh))
        if not hit:
            missing.append(eh)
            continue
        sid, has = hit
        if sid in seen:
            sys.exit(f"duplicate spell {sid} ({rh})")
        seen.add(sid)
        if norm(eh) in en and sentences(rb) != sentences(en[norm(eh)]):
            divergent.append(sid)
        name, desc, props = convert(rh, rb, has)
        items.append((sid, name, desc, props))
    os.makedirs(os.path.join(out, "spell"), exist_ok=True)
    with open(os.path.join(out, "spell", "spells.yaml"), "w", encoding="utf-8") as f:
        for sid, name, desc, props in sorted(items):
            f.write(f"- id: {sid}\n  name: {scalar(name)}\n  description: |-\n")
            f.write("".join(("    " + l if l else "") + "\n" for l in desc.split("\n")))
            if props:
                f.write("  properties:\n")
                for k, v in props.items():
                    f.write(f"    {k}: {scalar(v)}\n")
    if len(sys.argv) > 4:
        open(sys.argv[4], "w").write("\n".join(sorted(divergent)) + "\n")
    print(f"{len(items)} spells written, {len(missing)} English headings without an entry: {missing}")
    print(f"our spells not covered: {len(ids) - len(items)}")


main()
