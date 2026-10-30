#!/usr/bin/env python3
"""Turns the PNGs capture-screenshots.mjs writes into WebP files the site can ship.

    python3 site/tools/optimize-screenshots.py <png-dir> [<out-dir>]

<png-dir>/<lang>/<name>.png  ->  <out-dir>/<lang>/<name>.webp   (default out: site/src/assets/shots)
Shots keep their captured size (desktop 1100 wide; phone 780 wide, i.e. 390 CSS px at 2x). Also writes
<out-dir>/manifest.json ({"<name>": [width, height]}, read by build.mjs so images reserve their space)
and the social-preview image <out-dir>/../og.png from the English table.
"""
import json
import sys
from pathlib import Path
from PIL import Image

src = Path(sys.argv[1])
out = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parent.parent / "src" / "assets" / "shots"

total_in = total_out = 0
manifest = {}
for png in sorted(src.glob("*/*.png")):
    lang = png.parent.name
    dest = out / lang / (png.stem + ".webp")
    dest.parent.mkdir(parents=True, exist_ok=True)
    image = Image.open(png).convert("RGB")
    image.save(dest, "WEBP", quality=84, method=6)
    manifest[png.stem] = [image.width, image.height]
    total_in += png.stat().st_size
    total_out += dest.stat().st_size
    print(f"{lang}/{png.stem}: {image.width}x{image.height}  {png.stat().st_size // 1024} KB -> {dest.stat().st_size // 1024} KB")

print(f"total {total_in // 1024} KB -> {total_out // 1024} KB")

(out / "manifest.json").write_text(json.dumps(manifest, indent=1, sort_keys=True) + "\n")

og_source = src / "en" / "table.png"
if og_source.exists():
    og = Image.open(og_source).convert("RGB")
    og = og.crop((0, 0, og.width, round(og.width * 630 / 1200))).resize((1200, 630), Image.LANCZOS)
    og.save(out.parent / "og.png", optimize=True)
