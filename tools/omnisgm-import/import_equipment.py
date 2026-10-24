#!/usr/bin/env python3
"""Converts OmnisGM's Russian equipment (SRD 5.2.1, CC BY 4.0, (c) 2026 Petr Radilov / OmnisGM,
https://github.com/OmnisGM-App/OmnisGM-Rules, src/dnd/srd-5.2/{en,ru}/06_Equipment.md) into a Codex translation pack.

usage: import_equipment.py <omnisgm srd-5.2 dir> <srd-5.2.1 pack dir> <output pack dir>

Names come from the "#### Название (English name) (cost)" headings and from the first column of the weapon/armor/ammunition
tables (the English and Russian files are line for line the same, so rows are paired by position). The text under a heading is
OmnisGM's. The stat lines (category, damage, properties, cost, weight ...) are rebuilt from our own English entry with a small
dictionary, so the numbers are exactly ours; entries with a body text but no OmnisGM block stay English.
"""
import os, re, sys

from omnisgm_common import norm, scalar

CATEGORIES = {
    "weapons": "Оружие", "melee weapons": "Оружие ближнего боя", "ranged weapons": "Дальнобойное оружие",
    "simple weapons": "Простое оружие", "simple melee weapons": "Простое оружие ближнего боя", "simple ranged weapons": "Простое дальнобойное оружие",
    "martial weapons": "Воинское оружие", "martial melee weapons": "Воинское оружие ближнего боя", "martial ranged weapons": "Воинское дальнобойное оружие",
    "armor": "Доспехи", "light armor": "Лёгкие доспехи", "medium armor": "Средние доспехи", "heavy armor": "Тяжёлые доспехи", "shields": "Щиты",
    "adventuring gear": "Снаряжение искателя приключений", "equipment packs": "Наборы снаряжения", "ammunition": "Боеприпасы",
    "tools": "Инструменты", "artisan's tools": "Инструменты ремесленника", "other tools": "Прочие инструменты",
    "musical instruments": "Музыкальные инструменты", "gaming sets": "Игровые наборы",
    "arcane foci": "Магические фокусы", "druidic foci": "Друидические фокусы", "holy symbols": "Священные символы",
}
DAMAGE = {"Bludgeoning": "дробящий", "Piercing": "колющий", "Slashing": "рубящий"}
COINS = {"gp": "зм", "sp": "см", "cp": "мм", "ep": "эм", "pp": "пм"}


def tables(path):
    """Every pipe table of the file as a list of rows (each a list of cells), header and separator rows included."""
    out, current = [], None
    for line in open(path, encoding="utf-8").read().replace("\r\n", "\n").split("\n"):
        if line.startswith("|"):
            if current is None:
                current = []
                out.append(current)
            current.append([c.strip() for c in line.strip().strip("|").split("|")])
        else:
            current = None
    return out


def headings(path):
    return [l for l in open(path, encoding="utf-8").read().replace("\r\n", "\n").split("\n") if l.startswith("#")]


def main():
    omnis, pack, out = sys.argv[1:4]
    en_path, ru_path = os.path.join(omnis, "en", "06_Equipment.md"), os.path.join(omnis, "ru", "06_Equipment.md")

    names = {}  # norm(English) -> Russian
    en_t, ru_t = tables(en_path), tables(ru_path)
    if len(en_t) != len(ru_t):
        sys.exit("the English and Russian files have different numbers of tables")
    for te, tr in zip(en_t, ru_t):
        if len(te) != len(tr):
            continue
        for re_, rr in zip(te[2:], tr[2:]):
            if re_ and rr and re_[0] and rr[0]:
                names.setdefault(norm(re.sub(r"\s*\(.*", "", re_[0])), re.sub(r"\s*\(.*", "", rr[0]))

    blocks = {}  # norm(English) -> (Russian name, body)
    lines = open(ru_path, encoding="utf-8").read().replace("\r\n", "\n").split("\n")
    current = None
    for line in lines:
        m = re.match(r"^#### (.+?) \(([^()]+)\)(?: \([^()]*\))?\s*$", line)
        if line.startswith("#"):
            current = None
        if m:
            current = [m.group(1).strip(), []]
            blocks[norm(m.group(2))] = current
        elif current is not None:
            current[1].append(line)
    blocks = {k: (n, "\n".join(b).strip("\n")) for k, (n, b) in blocks.items()}
    for k, (n, _) in blocks.items():
        names.setdefault(k, n)

    # instruments and gaming sets are listed by name in the "**Variants:**" line of their block
    def variants(path):
        found = []
        for line in open(path, encoding="utf-8").read().split("\n"):
            m = re.match(r"\*\*(?:Variants|Варианты):\*\*\s*(.*)$", line)
            if m:
                found.append([re.sub(r"\s*\(.*?\)", "", v).strip().rstrip(".") for v in re.split(r",\s*(?![^()]*\))", m.group(1))])
        return found
    for ve, vr in zip(variants(en_path), variants(ru_path)):
        if len(ve) == len(vr):
            for e, r in zip(ve, vr):
                names.setdefault(norm(e), r[:1].upper() + r[1:])

    # sample poisons live in the Gameplay Toolbox: "#### Name (cost)" followed by an italic type line
    poisons = {}
    toolbox = [open(os.path.join(omnis, lang, "09_GameplayToolbox.md"), encoding="utf-8").read().split("\n") for lang in ("en", "ru")]
    heads = [[i for i, l in enumerate(t) if l.startswith("#")] for t in toolbox]
    if heads[0] == heads[1]:
        for n, i in enumerate(heads[0]):
            if toolbox[0][i].startswith("#### ") and re.search(r"\(\d[\d,]* GP\)$", toolbox[0][i]):
                end = heads[0][n + 1] if n + 1 < len(heads[0]) else len(toolbox[0])
                body = "\n".join(toolbox[1][i + 1:end]).strip("\n")
                kind = re.match(r"\*([^*]+)\*\s*\n", body)
                poisons[norm(re.sub(r"\s*\(.*", "", toolbox[0][i][5:]))] = (re.sub(r"\s*\([^()]*\)\s*$", "", toolbox[1][i][5:]).strip(), kind.group(1) if kind else "", body[kind.end():].strip("\n") if kind else body)

    # weapon properties and masteries: the "####" headings of the Свойства sections
    props = {}
    for he, hr in zip(headings(en_path), headings(ru_path)):
        if he.startswith("#### ") and hr.startswith("#### "):
            props[norm(he[5:])] = hr[5:].strip()

    def damage(value):
        value = re.sub(r"\(two-handed: ([^)]*)\)", r"(двуручное: \1)", value)
        for e, r in DAMAGE.items():
            value = value.replace(e, r)
        return value

    def stat(label, value):
        value = value.strip()
        if label == "Cost":
            m = re.match(r"([\d,.]+) (\w\w)$", value)
            return "Стоимость", f"{m.group(1)} {COINS[m.group(2)]}"
        if label == "Weight":
            return "Вес", re.sub(r"lb\.", "фнт.", value)
        if label == "Damage":
            return "Урон", damage(value)
        if label == "Properties":
            return "Свойства", ", ".join(props[norm(p)].lower() if False else props[norm(p)] for p in value.split(", "))
        if label == "Mastery":
            return "Мастерство", props[norm(value)]
        if label == "Range":
            return "Дистанция", value.replace("ft.", "фт.")
        if label == "Armor Class":
            return "Класс Доспеха", value.replace("Dex modifier", "модификатор Лов").replace("max", "макс.")
        if label == "Strength":
            return "Сила", value
        if label == "Stealth":
            return "Скрытность", value.replace("Disadvantage", "Помеха")
        raise KeyError(label)

    items, skipped, missing = [], [], []
    folder = os.path.join(pack, "equipment")
    for f in sorted(os.listdir(folder)):
        sid = f[:-5]
        text = open(os.path.join(folder, f), encoding="utf-8").read()
        en_name = re.search(r"^name: (.+)$", text, re.M).group(1).strip().strip("'\"")
        key = norm(en_name)
        desc = re.search(r"^description: >-\n((?:  .*\n|\n)+?)(?=\S)", text, re.M)
        raw = "\n".join(l[2:] if l.startswith("  ") else l for l in desc.group(1).split("\n")).strip("\n") if desc else ""
        parts = re.split(r"\n\n\n(?=\S)", raw)
        head_line = parts[0].strip("\n")
        category = head_line.strip("*")
        stats_part = parts[1] if len(parts) > 1 else ""
        body_en = "\n\n\n".join(parts[2:]).strip() if len(parts) > 2 else ""
        ru_name = names.get(key)
        block = blocks.get(key)
        if key in poisons:
            pname, pkind, pbody = poisons[key]
            cost = re.search(r"\*\*Cost:\*\*\s*([\d,.]+) (\w\w)", stats_part)
            lines_ = [f"**Стоимость:** {cost.group(1)} {COINS[cost.group(2)]}"] if cost else []
            items.append((sid, pname, f"*{pkind}*\n\n\n" + "  \n".join(lines_) + "\n\n\n" + pbody))
            continue
        if not ru_name:
            missing.append(sid)
            continue
        if body_en and not block:
            skipped.append(sid)
            continue
        try:
            cats = ", ".join(CATEGORIES[c.strip().lower()] for c in category.split(","))
            stat_lines = []
            for l in filter(str.strip, stats_part.strip().split("\n")):
                m = re.match(r"\*\*([A-Za-z ]+):\*\*\s*(.*?)\s*$", l)
                label, value = stat(m.group(1), m.group(2))
                stat_lines.append(f"**{label}:** {value}")
        except KeyError as e:
            print(f"{sid}: no dictionary entry for {e}")
            continue
        except AttributeError:
            # a category line like "Poison (ingested)" or an unusual shape: leave it English
            skipped.append(sid)
            continue
        body_ru = ""
        if block:
            # the block's own cost/weight/ability lines are rebuilt or kept: drop only a duplicated weight line
            body_ru = "\n".join(l for l in block[1].split("\n") if not l.startswith("**Вес:**")).strip("\n")
            body_ru = re.sub(r"\n{3,}", "\n\n", body_ru)
        desc_ru = f"*{cats}*\n\n\n" + "  \n".join(stat_lines) + (("\n\n\n" + body_ru) if body_ru else "")
        items.append((sid, ru_name.strip(), desc_ru))

    os.makedirs(os.path.join(out, "equipment"), exist_ok=True)
    with open(os.path.join(out, "equipment", "equipment.yaml"), "w", encoding="utf-8") as f:
        for sid, name, desc in items:
            f.write(f"- id: {sid}\n  name: {scalar(name)}\n  description: |-\n")
            f.write("".join(("    " + l if l else "") + "\n" for l in desc.split("\n")))
    print(f"{len(items)} equipment entries written; no Russian name for ({len(missing)}): {missing}")
    print(f"left English because of body text without an OmnisGM block or an unusual shape ({len(skipped)}): {skipped}")


main()
