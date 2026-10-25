#!/usr/bin/env python3
"""Adds batch-NN/flavor.md (the English flavor of each batch's entries) to batches made by `Codex.TranslationCheck --prepare`.

usage: prepare_flavor.py <type> <batches-dir> [<codex-flavor pack dir>]
"""
import glob, os, re, sys

type_, batches = sys.argv[1], sys.argv[2]
pack = sys.argv[3] if len(sys.argv) > 3 else os.path.join(os.path.dirname(__file__), "../../content/systems/srd5e/packs/codex-flavor")
flavor = {}
for f in sorted(glob.glob(os.path.join(pack, "flavor", type_, "*.yaml"))):
    cur = key = None
    for line in open(f, encoding="utf-8"):
        m = re.match(r"- id: (\S+)", line)
        if m:
            cur = m.group(1); flavor[cur] = {}; key = None
            continue
        m = re.match(r"  (flavor|habitat|behavior): ?(.*)", line)
        if m and cur:
            key = m.group(1); flavor[cur][key] = m.group(2).replace(">-", "").strip(); continue
        if re.match(r"  \w+:", line): key = None
        elif cur and key and line.startswith("    "):
            flavor[cur][key] = (flavor[cur][key] + " " + line.strip()).strip()

for d in sorted(glob.glob(os.path.join(batches, "batch-*"))):
    ids = re.findall(r"^## id: (\S+)", open(os.path.join(d, "input.md"), encoding="utf-8").read(), re.M)
    with open(os.path.join(d, "flavor.md"), "w", encoding="utf-8") as out:
        for i in ids:
            if i in flavor:
                out.write(f"## id: {i}\n" + "".join(f"{k}: {v}\n" for k, v in flavor[i].items()) + "\n")
print(f"{len(flavor)} flavor entries, {len(glob.glob(os.path.join(batches, 'batch-*')))} batches")
