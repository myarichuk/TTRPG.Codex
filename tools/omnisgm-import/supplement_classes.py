#!/usr/bin/env python3
"""Adds the translated `multiclassing` summary line ("Prerequisite: STR 13+. Proficiencies gained: ...") to class/classes.yaml of the
Russian pack, built from our English entry with a small dictionary (the line is plain text with ability abbreviations and proficiencies).

usage: supplement_classes.py <srd-5.2.1 pack dir> <srd-5.2.1-ru pack dir>      (running it twice changes nothing)
"""
import os, re, sys

ABILITY = {"STR": "СИЛ", "DEX": "ЛОВ", "CON": "ТЕЛ", "INT": "ИНТ", "WIS": "МДР", "CHA": "ХАР"}
PROF = {"Light Armor": "Лёгкие доспехи", "Medium Armor": "Средние доспехи", "Heavy Armor": "Тяжёлые доспехи", "Shields": "Щиты",
        "Martial Weapons": "Воинское оружие", "Simple Weapons": "Простое оружие", "Tool: Thieves' Tools": "Инструмент: воровские инструменты"}


def translate(text):
    m = re.match(r"Prerequisite: (.+?)(?:\. Proficiencies gained: (.+))?$", text)
    pre = m.group(1)
    if " or " in pre:
        pre = " или ".join(ABILITY[a] + " " + n for a, n in (p.split(" ") for p in pre.split(" or ")))
    else:
        pre = ", ".join(ABILITY[a] + " " + n for a, n in (p.split(" ") for p in pre.split(", ")))
    out = f"Требование: {pre}"
    if m.group(2):
        out += ". Получаемые владения: " + ", ".join(PROF[p.strip()] for p in m.group(2).split(", "))
    return out


def main():
    en_dir, ru_dir = sys.argv[1:3]
    path = os.path.join(ru_dir, "class", "classes.yaml")
    text = open(path, encoding="utf-8").read()
    for f in sorted(os.listdir(os.path.join(en_dir, "classes"))):
        sid = f[:-5]
        en = open(os.path.join(en_dir, "classes", f), encoding="utf-8").read()
        m = re.search(r"^  multiclassing: (.+)$", en, re.M)
        if not m:
            continue
        value = m.group(1).strip()
        value = value[1:-1].replace("''", "'") if value.startswith("'") else value.strip('"')
        line = f'    multiclassing: "{translate(value)}"\n'
        block = re.search(rf"- id: {re.escape(sid)}\n.*?(?=\n- id: |\Z)", text, re.S)
        if not block or "    multiclassing:" in block.group(0):
            continue
        new = re.sub(r"(    startingEquipment: .*\n)", lambda mm: mm.group(1) + line, block.group(0), count=1)
        text = text.replace(block.group(0), new)
    open(path, "w", encoding="utf-8").write(text)
    print(text.count("    multiclassing:"), "classes have the line")


main()
