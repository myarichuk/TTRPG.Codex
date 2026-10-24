#!/usr/bin/env python3
"""Converts classes and subclasses of OmnisGM's SRD 5.2.1 Russian translation (CC BY 4.0, (c) 2026 Petr Radilov / OmnisGM,
https://github.com/OmnisGM-App/OmnisGM-Rules, src/dnd/srd-5.2/ru/03_Classes/*.md) into Codex translation packs.

usage: import_classes.py <omnisgm srd-5.2 dir> <srd-5.2.1 pack dir> <output pack dir> [<ids output prefix>]

Writes class/classes.yaml and subclass/subclasses.yaml. A class gets its name, a description built from OmnisGM's core-traits
table (the same label lines as ours), the skill and starting-equipment lines, and its feature rows. Feature rows are paired
with OmnisGM's English by level and position, then with our rows by level and English name; if any of our rows cannot be paired,
the entry gets no feature rows (they stay English) and the id is reported. With the last argument "<prefix>.faithful" lists the
ids whose Russian keeps every number and dice of OmnisGM's English.
"""
import os, re, sys

from omnisgm_common import faithful, norm, our_names, write_items

HEADING = re.compile(r"^(#{2,5}) (.+)$")
FEATURE = re.compile(r"^(?:Уровень|Level) (\d+): (.+)$")
SUBCLASS = re.compile(r"^(?:Подкласс [^:]+|[A-Za-z' -]+ Subclass): (.+)$")


def sections(path):
    out, current = [], None
    for line in open(path, encoding="utf-8").read().replace("\r\n", "\n").split("\n"):
        m = HEADING.match(line)
        if m:
            current = [len(m.group(1)), m.group(2).replace("**", "").strip(), []]
            out.append(current)
        elif current is not None:
            current[2].append(line)
    return [(lvl, text, "\n".join(body).strip("\n")) for lvl, text, body in out]


def parse(path):
    secs = sections(path)
    title = next(text for lvl, text, _ in secs if lvl == 2)
    core = []
    for lvl, text, body in secs:
        if lvl == 2:
            for line in body.split("\n"):
                cells = [c.strip() for c in line.strip().strip("|").split("|")]
                if line.strip().startswith("|") and len(cells) == 2 and cells[0] and not set(cells[0]) <= set("-:"):
                    core.append((cells[0], cells[1]))
            break
    class_features, subclasses, current = [], {}, None
    for lvl, text, body in secs:
        sub = SUBCLASS.match(text) if lvl == 3 else None
        if sub:
            current = [sub.group(1).strip(), body, []]
            subclasses[current[0]] = current
            continue
        feat = FEATURE.match(text)
        if feat:
            row = (int(feat.group(1)), feat.group(2).strip(), body)
            (current[2] if current else class_features).append(row)
    return title, core, class_features, subclasses


def our_rows(pack, folder, sid):
    """(level, name, description with whitespace collapsed) of each feature row of our entry."""
    text = open(os.path.join(pack, folder, sid + ".yaml"), encoding="utf-8").read()
    block = text.split("\n  features:\n", 1)[1] if "\n  features:\n" in text else ""
    block = re.split(r"\n  [a-zA-Z]+:", "\n" + block, maxsplit=1)[0]
    rows = []
    for seg in re.split(r"^  - name: ", block, flags=re.M)[1:]:
        name = seg.split("\n", 1)[0].strip().strip("'\"")
        level = re.search(r"^    level: (\d+)", seg, re.M)
        desc = re.split(r"^    description:", seg, flags=re.M)
        rows.append((int(level.group(1)) if level else 0, name, " ".join(re.sub(r"^>-?|^\|-?", "", desc[1].strip()).split()) if len(desc) > 1 else ""))
    return rows


SUBCLASS_FEATURE = re.compile(r"^You gain a feature from your .+ subclass at this level\. Its features are listed on the subclass\.$")


def pair_rows(ours, en_feats, ru_feats):
    """Our rows -> [{name, level, description}] in our order, or None when any row cannot be paired.

    Rows are matched to OmnisGM's by level and English name. Rows the SRD lists once but our data repeats at later levels
    ("Ability Score Improvement" at 8, 12, 16) reuse the translation of an earlier row with the same text; the
    "Subclass Feature" template row gets a short Russian line built from OmnisGM's class-table wording.
    """
    if len(en_feats) != len(ru_feats) or any(e[0] != r[0] for e, r in zip(en_feats, ru_feats)):
        return None
    by_key = {(e[0], norm(e[1])): r for e, r in zip(en_feats, ru_feats)}
    genitive = next((r[1].split(" ", 1)[1] for e, r in zip(en_feats, ru_feats) if e[1].endswith("Subclass") and " " in r[1]), None)
    rows, done, suspect = [], {}, []
    for level, name, desc in ours:
        hit = by_key.get((level, norm(name)))
        if not hit:  # a row whose name swallowed the first sentence of its text
            hit = next((r for (lvl, key), r in by_key.items() if lvl == level and len(key) > 6 and norm(name).startswith(key)), None)
        if hit:
            row = {"name": hit[1], "level": level, "description": hit[2].strip()}
        elif (name, desc) in done:
            row = dict(done[(name, desc)], level=level)
        elif name == "Subclass Feature" and SUBCLASS_FEATURE.match(desc) and genitive:
            row = {"name": "Умение подкласса", "level": level,
                   "description": f"На этом уровне вы получаете умение вашего подкласса {genitive}. Умения подкласса перечислены в его описании."}
        else:
            return None
        if not row["name"].startswith("Умение подкласса") and not faithful(desc, row["description"]):
            suspect.append(f"{level}:{name}")
        done.setdefault((name, desc), row)
        rows.append(row)
    SUSPECT.extend(suspect)
    return rows


SUSPECT = []  # rows whose Russian lacks a number or dice expression our English has (the checker does not look inside rows)


def main():
    omnis, pack, out = sys.argv[1:4]
    ok = [] if len(sys.argv) > 4 else None
    classes, subs = our_names(pack, "classes"), our_names(pack, "subclasses")
    class_items, sub_items, unpaired, missing = [], [], [], []
    folder_ru, folder_en = os.path.join(omnis, "ru", "03_Classes"), os.path.join(omnis, "en", "03_Classes")
    for f in sorted(os.listdir(folder_ru)):
        if not re.match(r"\d\d_", f) or f.startswith("00_"):
            continue
        ru_title, core, ru_feats, ru_subs = parse(os.path.join(folder_ru, f))
        en_title, _, en_feats, en_subs = parse(os.path.join(folder_en, f))
        sid = classes.get(norm(en_title))
        if not sid:
            missing.append(en_title)
            continue
        labels = {label: value for label, value in core}
        description = "  \n".join(f"**{label}:** {value}" for label, value in core)
        props = {}
        for label, key in (("Владение навыками", "skillChoices"), ("Начальное снаряжение", "startingEquipment")):
            if label in labels:
                props[key] = labels[label]
        rows = pair_rows(our_rows(pack, "classes", sid), en_feats, ru_feats)
        if rows:
            props["features"] = rows
        else:
            unpaired.append(sid)
        class_items.append((sid, ru_title, description, props))
        if ok is not None and faithful("\n".join(b for _, _, b in en_feats), "\n".join(b for _, _, b in ru_feats)):
            ok.append(sid)
        for (en_name, en_sub), (ru_name, ru_sub) in zip(en_subs.items(), ru_subs.items()):
            ssid = subs.get(norm(en_name))
            if not ssid:
                missing.append(en_name)
                continue
            intro = ru_sub[1].strip()
            summary = re.match(r"^\*([^*]+)\*", intro)
            sprops = {"summary": summary.group(1).strip()} if summary else {}
            srows = pair_rows(our_rows(pack, "subclasses", ssid), en_sub[2], ru_sub[2])
            if srows:
                sprops["features"] = srows
            else:
                unpaired.append(ssid)
            sub_items.append((ssid, ru_name, intro, sprops))
            if ok is not None and faithful(en_sub[1] + "\n".join(b for _, _, b in en_sub[2]), ru_sub[1] + "\n".join(b for _, _, b in ru_sub[2])):
                ok.append(ssid)
    write_items(os.path.join(out, "class", "classes.yaml"), class_items)
    write_items(os.path.join(out, "subclass", "subclasses.yaml"), sub_items)
    if ok is not None:
        open(sys.argv[4] + ".faithful", "w").write("\n".join(sorted(ok)) + "\n")
    print(f"rows to review (numbers or dice of our English not found in the Russian): {len(SUSPECT)}: {SUSPECT[:40]}")
    print(f"{len(class_items)} classes, {len(sub_items)} subclasses written; feature rows left English for: {unpaired}; unmatched: {missing}")


main()
