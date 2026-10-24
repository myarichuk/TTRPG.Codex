#!/usr/bin/env python3
"""Builds the shipped Russian spell file from two sources, per spell.

usage: merge_with_fallback.py <primary spells.yaml (OmnisGM)> <fallback spells.yaml (machine-drafted by Codex)>
                              <checker report of the primary> <divergent ids file> <output spells.yaml>

A spell comes from the primary unless the checker reported an ERROR for it or it is in the divergent list; then the
fallback's version is used. Each item is preceded by a "# source:" comment so the credit stays with the text.
"""
import re, sys


def items(path):
    out, current, lines = {}, None, []
    for line in open(path, encoding="utf-8").read().split("\n"):
        m = re.match(r"^- id: (\S+)$", line)
        if m:
            if current:
                out[current] = "\n".join(lines).rstrip("\n")
            current, lines = m.group(1), [line]
        elif current is not None:
            lines.append(line)
    if current:
        out[current] = "\n".join(lines).rstrip("\n")
    return out


def main():
    primary, fallback = items(sys.argv[1]), items(sys.argv[2])
    failing = set(re.findall(r"^ERROR spell:(\S+)", open(sys.argv[3], encoding="utf-8").read(), re.M))
    divergent = set(open(sys.argv[4]).read().split())
    use_fallback = (failing | divergent) & set(fallback)
    with open(sys.argv[5], "w", encoding="utf-8") as f:
        for sid in sorted(set(primary) | set(fallback)):
            if sid in use_fallback or sid not in primary:
                f.write(f"# source: TTRPG.Codex (machine-drafted)\n{fallback[sid]}\n")
            else:
                f.write(f"# source: OmnisGM (CC BY 4.0)\n{primary[sid]}\n")
    print(f"{len(primary) - len(use_fallback & set(primary))} from OmnisGM, {len(use_fallback)} from the fallback")


main()
