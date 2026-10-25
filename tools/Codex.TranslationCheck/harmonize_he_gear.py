#!/usr/bin/env python3
"""Rewrites gear words in Hebrew pack files to the names of the equipment entries (the single source of truth).

usage: harmonize_he_gear.py <file.yaml>...   (skip entries whose id is listed in HARMONIZE_SKIP, comma-separated env var)
"""
import os, re, sys
skip = set(filter(None, os.environ.get("HARMONIZE_SKIP", "").split(",")))
RULES = [
    ("רומחים", "כידונים"), ("רומח", "כידון"),
    ("פטיש מלחמה", "פטיש קרב"),
    ("קשת צולבת כבדה", "קשת מכנית כבדה"), ("קשת צלב כבדה", "קשת מכנית כבדה"),
    ("קשת צולבת קלה", "קשת מכנית קלה"), ("קשת צלב קלה", "קשת מכנית קלה"),
    ("קשת יד", "קשת מכנית ידנית"),
    ("עור מסומר", "עור ממוסמר"),
    ("חצי לוחות", "חצי-לוחות"),
]
for path in sys.argv[1:]:
    text = open(path, encoding="utf-8").read()
    parts = re.split(r"(?m)^(?=- id: )", text)
    out = []
    for part in parts:
        m = re.match(r"- id: (\S+)", part)
        if m and m.group(1) not in skip:
            for a, b in RULES:
                part = part.replace(a, b)
        out.append(part)
    open(path, "w", encoding="utf-8").write("".join(out))
