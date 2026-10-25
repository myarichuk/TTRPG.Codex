#!/usr/bin/env python3
"""Builds the shipped Russian spell file from two sources, per spell.

usage: merge_with_fallback.py <primary spells.yaml (OmnisGM)> <fallback spells.yaml (machine-drafted by Codex)>
                              <checker report of the primary> <divergent ids file> <output spells.yaml>

The primary importer also wrote "<divergent ids file>.faithful": ids whose Russian keeps every number and dice of OmnisGM's own
English. A checker ERROR on such an entry means our English differs from OmnisGM's (our YAML came from a JSON transcription),
not that the translation lost anything, so the entry is kept, marked "differs from our English" for review.

An item comes from the primary unless the checker reported an ERROR for it or it is in the divergent list; then the
fallback's version is used. Pass "-" as the fallback to leave such items out (they stay English). Each item is preceded
by a "# source:" comment so the credit stays with the text.
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
    primary, fallback = items(sys.argv[1]), (items(sys.argv[2]) if sys.argv[2] != "-" else {})
    failing = set(re.findall(r"^ERROR [\w-]+:(\S+)", open(sys.argv[3], encoding="utf-8").read(), re.M))
    divergent = set(open(sys.argv[4]).read().split())
    try:
        faithful = set(open(sys.argv[4] + ".faithful").read().split())
    except OSError:
        faithful = set()
    differs = (failing & faithful) - divergent
    excluded = (failing - faithful) | divergent
    use_fallback = excluded & set(fallback)
    dropped = (excluded & set(primary)) - set(fallback)
    with open(sys.argv[5], "w", encoding="utf-8") as f:
        for sid in sorted((set(primary) - dropped) | set(fallback)):
            if sid in use_fallback or sid not in primary:
                f.write(f"# source: TTRPG.Codex (machine-drafted)\n{fallback[sid]}\n")
            else:
                note = "; differs from our English, see tools/omnisgm-import/README" if sid in differs else ""
                f.write(f"# source: OmnisGM (CC BY 4.0){note}\n{primary[sid]}\n")
    open(sys.argv[5] + ".known", "w").write("\n".join(sorted(differs & set(primary))) + "\n")
    print(f"{len(primary) - len(excluded & set(primary))} from OmnisGM, {len(use_fallback)} from the fallback, {len(dropped)} left out: {sorted(dropped)}; {len(differs & set(primary))} kept although our English differs")


main()
