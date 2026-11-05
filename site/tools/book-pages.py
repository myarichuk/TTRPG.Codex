#!/usr/bin/env python3
"""Lays pages of a printed book side by side as one PNG, for the website.

    python3 site/tools/book-pages.py <book.pdf> <out.png> <page> [<page> ...] [--height 760]

Pages are 1-based. Needs pdftoppm (poppler). The pages are rendered at the requested height and joined with a small gap on a
neutral background, so a two-page spread reads like an open book.
"""
import subprocess
import sys
import tempfile
from pathlib import Path
from PIL import Image

args = sys.argv[1:]
height = 760
if "--height" in args:
    i = args.index("--height")
    height = int(args[i + 1])
    del args[i:i + 2]
pdf, out, pages = Path(args[0]), Path(args[1]), [int(p) for p in args[2:]]

tiles = []
with tempfile.TemporaryDirectory() as tmp:
    for page in pages:
        subprocess.run(["pdftoppm", "-r", "110", "-png", "-f", str(page), "-l", str(page), str(pdf), f"{tmp}/p"], check=True)
        file = next(Path(tmp).glob(f"p-*{page}.png"))
        image = Image.open(file).convert("RGB")
        tiles.append(image.resize((round(image.width * height / image.height), height), Image.LANCZOS))

gap, pad = 14, 22
sheet = Image.new("RGB", (sum(t.width for t in tiles) + gap * (len(tiles) - 1) + pad * 2, height + pad * 2), (58, 54, 50))
x = pad
for tile in tiles:
    sheet.paste(tile, (x, pad))
    x += tile.width + gap
sheet.save(out)
print("wrote", out, sheet.size)
