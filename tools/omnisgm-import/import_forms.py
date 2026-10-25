#!/usr/bin/env python3
"""Builds the Russian stat blocks of the lycanthrope and vampire forms from OmnisGM's single block per creature
(CC BY 4.0, (c) 2026 Petr Radilov / OmnisGM, https://github.com/OmnisGM-App/OmnisGM-Rules, src/dnd/srd-5.2/{en,ru}/12_MonstersA-Z.md).

usage: import_forms.py <omnisgm srd-5.2 dir> <srd-5.2.1 pack dir> <output pack dir>

Our pack stores each form of a shape-shifter as its own entry ("Werewolf, Hybrid Form"), OmnisGM has one block per creature whose rows
say in which form they apply ("Bite (only in wolf or hybrid form)"). For each of our forms the OmnisGM header is kept, the size line and
the speed are replaced by the form's own (translated with a small dictionary), and only the rows our English form lists are copied,
with the "(only in ... form)" remark dropped from their names. Rows are paired with their English names by position.
Writes monster/forms.yaml.
"""
import os, re, sys

from omnisgm_common import norm, scalar

PARENTS = {"vampire": "Vampire", "werebear": "Werebear", "wereboar": "Wereboar", "wererat": "Wererat", "weretiger": "Weretiger", "werewolf": "Werewolf"}
FORMS = {"Human Form": "человеческая форма", "Hybrid Form": "гибридная форма", "Bat Form": "форма летучей мыши", "Mist Form": "форма тумана",
         "Vampire Form": "форма вампира", "Wolf Form": "форма волка", "Bear Form": "форма медведя", "Boar Form": "форма кабана",
         "Rat Form": "форма крысы", "Tiger Form": "форма тигра"}
SIZE = {"Tiny": ("Крошечное", "Крошечная"), "Small": ("Маленькое", "Маленькая"), "Medium": ("Среднее", "Средняя"), "Large": ("Большое", "Большая")}
SPEED = {"ft.": "футов", "climb": "лазание", "fly": "полёт", "swim": "плавание", "burrow": "копание"}


def speed_ru(text):
    parts = []
    for p in text.split(","):
        p = p.strip()
        if p.endswith("hover True"):
            parts.append("парение")
            continue
        m = re.match(r"(?:(\w+) )?(\d+) ft\.$", p)
        parts.append(f"{SPEED[m.group(1)] + ' ' if m.group(1) else ''}{m.group(2)} футов")
    # a trailing "hover" goes in brackets after the fly speed
    out = ", ".join(x for x in parts if x != "парение")
    return out + (" (парение)" if "парение" in parts else "")


def rows(lines):
    """(header lines, [(section heading, [row lines])]) of a stat block's lines; a row starts at a '**_' paragraph."""
    header, sections, current = [], [], None
    for l in lines:
        if l.startswith("#### "):
            sections.append((l, []))
            current = None
        elif not sections:
            header.append(l)
        else:
            if l.startswith("**_"):
                current = [l]
                sections[-1][1].append(current)
            elif current is not None:
                current.append(l)
    return header, sections


def block_lines(path, heading_en):
    text = open(path, encoding="utf-8").read().replace("\r\n", "\n").split("\n")
    out, on = [], False
    for l in text:
        if l.startswith("### "):
            if on:
                break
            on = re.search(rf"(?:^### {re.escape(heading_en)}$|\({re.escape(heading_en)}\)\s*$)", l) is not None
            if on:
                out.append(l)
            continue
        if on:
            out.append(l)
    return out


def row_name(line):
    m = re.match(r"\*\*_?(.+?)_?\*\*", line)
    return re.sub(r"\s*\(.*", "", m.group(1)).strip(" ._") if m else ""


def main():
    omnis, pack, out = sys.argv[1:4]
    folder = os.path.join(pack, "monsters")
    entries = []
    for f in sorted(os.listdir(folder)):
        sid = f[:-5]
        parent = next((p for p in PARENTS if sid.startswith(p + "-")), None)
        if not parent:
            continue
        text = open(os.path.join(folder, f), encoding="utf-8").read()
        name = re.search(r"^name: (.+)$", text, re.M).group(1).strip().strip("'\"")
        form = name.split(", ", 1)[1] if ", " in name else ""
        if form not in FORMS:
            continue
        desc = re.search(r"description: >-\n((?:  .*\n|\n)+)", text).group(1)
        desc = "\n".join(l[2:] for l in desc.split("\n"))
        entries.append((sid, parent, name, form, desc))

    result, problems = [], []
    for sid, parent, name, form, desc in entries:
        en_lines = block_lines(os.path.join(omnis, "en", "12_MonstersA-Z.md"), PARENTS[parent])
        ru_lines = block_lines(os.path.join(omnis, "ru", "12_MonstersA-Z.md"), PARENTS[parent])
        eh, es = rows(en_lines)
        rh, rs = rows(ru_lines)
        en_rows = [r for _, rr in es for r in rr]
        ru_sections = [(h, rr) for h, rr in rs]
        if len(en_rows) != sum(len(rr) for _, rr in rs):
            problems.append(f"{sid}: OmnisGM en/ru row counts differ")
            continue
        wanted = {norm(m) for m in re.findall(r"^\*\*([^*]+?)\.\*\*", desc, re.M)}
        wanted_short = {norm(re.sub(r"\s*\(.*", "", m)) for m in re.findall(r"^\*\*([^*]+?)\.\*\*", desc, re.M)}
        # header: OmnisGM's, with the form's own size line and speed
        size_en = re.match(r"\*(\w+)", desc.strip()).group(1)
        header = [l for l in rh if not l.startswith("### ")]
        ital = next(i for i, l in enumerate(header) if l.startswith("*"))
        m = re.match(r"\*(\S+)(?: или \S+)? (.*)\*$", header[ital])
        fem = m.group(1).endswith(("ая", "яя"))
        header[ital] = f"*{SIZE[size_en][1 if fem else 0]} {m.group(2)}*"
        speed_en = re.search(r"\*\*Speed\*\*\s*(.+?)\s*$", desc, re.M).group(1)
        sp = speed_ru(speed_en)
        header = [re.sub(r"^(- \*\*Скорость:\*\*).*", rf"\1 {sp}", l) for l in header]
        props = {"speed": sp}
        for l in header:
            mm = re.match(r"^- \*\*(Навыки|Чувства|Языки):\*\*\s*(.*)$", l)
            if mm:
                props[{"Навыки": "skills", "Чувства": "senses", "Языки": "languages"}[mm.group(1)]] = mm.group(2).strip()
        body = list(header)
        kept = 0
        idx = 0
        for h, rr in ru_sections:
            chosen = []
            for r in rr:
                en_name = row_name(en_rows[idx][0])
                idx += 1
                if norm(en_name) in wanted_short:
                    first = re.sub(r"(\*\*_[^_]*?)\s*\((?:только|Только)[^)]*\)(\._\*\*)", r"\1\2", r[0])
                    first = re.sub(r"\.\s*\(", ".", first) if False else first
                    chosen.append([first] + r[1:])
                    kept += 1
            if chosen:
                body += [h, ""]
                for r in chosen:
                    body += r
                    if r[-1].strip():
                        body.append("")
        if kept != len(re.findall(r"^\*\*[^*]+?\.\*\*", desc, re.M)) - len(re.findall(r"^\*\*(Forbiddance|Running Water|Stake to the Heart|Sunlight)\.\*\*", desc, re.M)):
            problems.append(f"{sid}: kept {kept} rows, English has a different number (check names)")
        ru_name = re.sub(r"\s*\([^()]*\)\s*$", "", next(l for l in ru_lines if l.startswith("### "))[4:]).strip()
        result.append((sid, f"{ru_name}, {FORMS[form]}", "\n".join(body).strip("\n"), props))

    os.makedirs(os.path.join(out, "monster"), exist_ok=True)
    with open(os.path.join(out, "monster", "forms.yaml"), "w", encoding="utf-8") as f:
        f.write("# source: OmnisGM (CC BY 4.0), split into our forms by tools/omnisgm-import/import_forms.py\n")
        for sid, name, desc, props in sorted(result):
            f.write(f"- id: {sid}\n  name: {scalar(name)}\n  description: |-\n")
            f.write("".join(("    " + l if l else "") + "\n" for l in re.sub(r"\n{3,}", "\n\n", desc).split("\n")))
            f.write("  properties:\n" + "".join(f"    {k}: {scalar(v)}\n" for k, v in props.items()))
    print(f"{len(result)} forms written")
    for p in problems:
        print("PROBLEM", p)


main()
