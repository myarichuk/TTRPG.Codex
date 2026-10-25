#!/usr/bin/env python3
"""Merges reviewed batches into the Hebrew packs.

usage: merge_he_batches.py <type> <batches-dir> <rules-file-name> <srd-he pack dir> <flavor-he pack dir>
  <batches-dir>/batch-NN/out/<type>/<file>.yaml  -> <srd-he>/<type>/<rules-file-name> (one file, id order)
  <batches-dir>/batch-NN/out-flavor/<type>/*.yaml -> <flavor-he>/<type>/flavor-NN.yaml (one per batch)
"""
import glob, os, shutil, sys, re

type_, batches, rules_name, srd, fl = sys.argv[1:6]
items = []
for d in sorted(glob.glob(os.path.join(batches, "batch-*"))):
    for f in sorted(glob.glob(os.path.join(d, "out", type_, "*.yaml"))):
        text = open(f, encoding="utf-8").read().rstrip("\n")
        items.append(text)
    for i, f in enumerate(sorted(glob.glob(os.path.join(d, "out-flavor", type_, "*.yaml"))), 1):
        n = re.search(r"batch-(\d+)", d).group(1)
        dst = os.path.join(fl, type_, f"flavor-{n}.yaml")
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copyfile(f, dst)
if items:
    dst = os.path.join(srd, type_, rules_name)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    open(dst, "w", encoding="utf-8").write("\n".join(items) + "\n")
print(f"{len(items)} rule files merged")
