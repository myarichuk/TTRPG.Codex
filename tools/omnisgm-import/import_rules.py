#!/usr/bin/env python3
"""Converts the rule entries (skills, abilities, alignments, damage types, schools, languages, weapon properties and masteries) and the
conditions of OmnisGM's Russian SRD 5.2.1 (CC BY 4.0, (c) 2026 Petr Radilov / OmnisGM, https://github.com/OmnisGM-App/OmnisGM-Rules,
src/dnd/srd-5.2/{en,ru}) into a Codex translation pack.

usage: import_rules.py <omnisgm srd-5.2 dir> <srd-5.2.1 pack dir> <output pack dir>

The English and Russian files are line for line the same, so headings and table rows are paired by position. Skill names follow our
own glossary (system.ru.yaml), and Thunder stays "Звук" (our decision), everything else is OmnisGM's.
"""
import os, re, sys

from omnisgm_common import norm, scalar

ABILITY_ABBR = {"strength": "Сил.", "dexterity": "Лвк.", "constitution": "Тел.", "intelligence": "Инт.", "wisdom": "Мдр.", "charisma": "Хар."}
ABILITY_ID = {"strength": "str", "dexterity": "dex", "constitution": "con", "intelligence": "int", "wisdom": "wis", "charisma": "cha"}


def lines(path):
    return open(path, encoding="utf-8").read().replace("\r\n", "\n").split("\n")


def table_after(ls, marker):
    """Rows (cells) of the first pipe table after the first line containing marker, header and separator dropped."""
    start = next(i for i, l in enumerate(ls) if marker in l)
    rows, seen = [], False
    for l in ls[start:]:
        if l.startswith("|"):
            seen = True
            rows.append([c.strip() for c in l.strip().strip("|").split("|")])
        elif seen:
            break
    return rows[2:]


def slug(s):
    return re.sub(r"[^a-z0-9]+", "-", s.lower().replace("'", "").replace("’", "")).strip("-")


def section_blocks(ls, level, until=None):
    """(heading, body) pairs for the headings of exactly this level, body up to the next heading of any level."""
    marker, out, current = "#" * level + " ", [], None
    for l in ls:
        if l.startswith("#"):
            current = [l[len(marker):].strip(), []] if l.startswith(marker) else None
            if current:
                out.append(current)
        elif current is not None:
            current[1].append(l)
    return [(h, "\n".join(b).strip("\n")) for h, b in out]


def main():
    omnis, pack, out = sys.argv[1:4]
    en = lambda f: lines(os.path.join(omnis, "en", f))
    ru = lambda f: lines(os.path.join(omnis, "ru", f))
    rules, conditions = [], []

    # skill names follow our glossary
    glossary_skills = {}
    in_skills = False
    for l in lines(os.path.join(os.path.dirname(os.path.dirname(pack)), "system.ru.yaml")):
        if l.startswith("  skills:"):
            in_skills = True
        elif in_skills and l.startswith("    "):
            k, v = l.strip().split(": ", 1)
            glossary_skills[k] = v
        elif in_skills:
            break

    # skills
    for te, tr in zip(table_after(en("01_PlayingTheGame.md"), "Table: Skills"), table_after(ru("01_PlayingTheGame.md"), "Таблица: Навыки")):
        sid = "skill-" + slug(te[0])
        name = glossary_skills[slug(te[0])]
        rules.append((sid, name, f"**Характеристика:** {ABILITY_ABBR[te[1].lower()]}\n\n\n{tr[2]}"))

    # abilities
    for te, tr in zip(table_after(en("01_PlayingTheGame.md"), "Ability Descriptions"), table_after(ru("01_PlayingTheGame.md"), "Описания характеристик")):
        rules.append((f"ability-score-{ABILITY_ID[te[0].lower()]}", tr[0], tr[1]))

    # damage types (Thunder stays "Звук")
    for te, tr in zip(table_after(en("08_RulesGlossary.md"), "Table: Damage Types"), table_after(ru("08_RulesGlossary.md"), "Таблица: Типы урона")):
        name = "Звук" if te[0].lower() == "thunder" else tr[0]
        rules.append(("damage-type-" + slug(te[0]), name, tr[1]))

    # schools of magic
    for te, tr in zip(table_after(en("07_Spells.md"), "Table: Schools of Magic"), table_after(ru("07_Spells.md"), "Таблица: Школы магии")):
        rules.append(("school-of-magic-" + slug(te[0]), tr[0], tr[1]))

    # languages: the two tables of "Choose Languages"
    for marker_e, marker_r in (("Table: Standard Languages", "Таблица: Стандартные языки"), ("Table: Rare Languages", "Таблица: Редкие языки")):
        for te, tr in zip(table_after(en("02_CharacterCreation.md"), marker_e), table_after(ru("02_CharacterCreation.md"), marker_r)):
            for ce, cr in zip(te[-2:] if marker_e.endswith("Rare Languages") else te[-1:], tr[-2:] if marker_e.endswith("Rare Languages") else tr[-1:]):
                ce, cr = ce.strip("*").strip(), cr.strip("*").strip()
                if ce:
                    rules.append(("language-" + slug(ce), cr, None))

    # the footnote of the rare languages table describes Primordial
    for l in ru("02_CharacterCreation.md"):
        if l.startswith("\\*Первичный"):
            rules = [(sid, name, l.lstrip("\\*").strip() if sid == "language-primordial" else desc) for sid, name, desc in rules]

    # alignments: the nine paragraphs of step 4 plus the sidebar for unaligned creatures
    en_al = [l for l in en("02_CharacterCreation.md") if re.match(r"^\*\*[A-Z][A-Za-z\- ]+ \([A-Z]+\)\.\*\*", l)]
    ru_al = [l for l in ru("02_CharacterCreation.md") if re.match(r"^\*\*[^*]+ \([^()]+\)\.\*\*", l)]
    for le, lr in zip(en_al, ru_al):
        ne = re.match(r"^\*\*([^*(]+) \(", le).group(1).strip()
        m = re.match(r"^\*\*([^*(]+) \([^()]+\)\.\*\*\s*(.*)$", lr)
        rules.append(("alignment-" + slug(ne), m.group(1).strip(), m.group(2).strip()))
    for i, l in enumerate(ru("02_CharacterCreation.md")):
        if l.startswith("> ### Существа без мировоззрения"):
            rules.append(("alignment-unaligned", "Без мировоззрения", ru("02_CharacterCreation.md")[i + 2].lstrip("> ").strip()))

    # weapon properties and masteries: "####" headings of the "Свойства" and "Свойства мастерства" sections of the equipment chapter
    he, hr = en("06_Equipment.md"), ru("06_Equipment.md")
    section = None
    for i, (a, b) in enumerate(zip(he, hr)):
        if a.startswith("### "):
            section = {"Properties": "weapon-property-", "Mastery Properties": "weapon-mastery-"}.get(a[4:].strip())
        elif a.startswith("#### ") and section:
            j = i + 1
            body = []
            while j < len(hr) and not hr[j].startswith("#"):
                body.append(hr[j])
                j += 1
            rules.append((section + slug(a[5:]), b[5:].strip(), "\n".join(body).strip("\n")))

    # conditions: the glossary entries named like ours
    ours = {}
    folder = os.path.join(pack, "conditions")
    for f in os.listdir(folder):
        ours[norm(re.search(r"^name: (.+)$", open(os.path.join(folder, f), encoding="utf-8").read(), re.M).group(1).strip())] = f[:-5]
    ge, gr = en("08_RulesGlossary.md"), ru("08_RulesGlossary.md")
    for i, a in enumerate(ge):
        if a.startswith("#### ") and norm(re.sub(r"\s*\[.*", "", a[5:])) in ours and gr[i].startswith("#### "):
            j = i + 1
            body = []
            while j < len(gr) and not gr[j].startswith("#"):
                body.append(gr[j])
                j += 1
            conditions.append((ours[norm(re.sub(r"\s*\[.*", "", a[5:]))], re.sub(r"\s*\[.*", "", gr[i][5:]).strip(), "\n".join(body).strip("\n")))

    have = {f[:-5] for f in os.listdir(os.path.join(pack, "rules"))}
    unknown = [r[0] for r in rules if r[0] not in have]
    rules = [r for r in rules if r[0] in have]

    def write(path, items):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            for sid, name, desc in sorted(items):
                f.write(f"- id: {sid}\n  name: {scalar(name)}\n")
                if desc:
                    f.write("  description: |-\n" + "".join(("    " + l if l else "") + "\n" for l in desc.split("\n")))

    write(os.path.join(out, "rule", "rules.yaml"), rules)
    write(os.path.join(out, "condition", "conditions.yaml"), conditions)
    print(f"{len(rules)} rule entries, {len(conditions)} conditions written; ids OmnisGM gave that we do not have: {unknown}")
    print("our rules not covered:", sorted(have - {r[0] for r in rules}))
    print("our conditions not covered:", sorted(set(ours.values()) - {c[0] for c in conditions}))


main()
