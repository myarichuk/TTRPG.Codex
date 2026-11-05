#!/usr/bin/env python3
"""Draws the placeholder pictures the screenshot campaign uses (a cover and three portraits), so the book shots show
pictures placed. They are simple ink-and-wash style illustrations made with Pillow, stand-ins for a table's real art.

    python3 site/tools/demo-art.py <out-dir>

Writes cover.png (3:4), aria.png, odessa.png, bell-ringer.png (3:4). Deterministic: the same pictures every run.
"""
import math
import random
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

W, H = 900, 1200


def gradient(top, bottom):
    image = Image.new("RGB", (W, H))
    draw = ImageDraw.Draw(image)
    for y in range(H):
        t = y / (H - 1)
        draw.line([(0, y), (W, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)))
    return image


def wash(image, seed):
    """Paper grain and a soft vignette, so flat colour reads as wash."""
    rnd = random.Random(seed)
    grain = Image.effect_noise((W, H), 18).convert("RGB")
    image = Image.blend(image, grain, 0.07)
    vignette = Image.new("L", (W, H), 0)
    d = ImageDraw.Draw(vignette)
    d.ellipse((-W * 0.3, -H * 0.2, W * 1.3, H * 1.2), fill=255)
    vignette = vignette.filter(ImageFilter.GaussianBlur(160))
    dark = Image.new("RGB", (W, H), (30, 25, 22))
    return Image.composite(image, dark, vignette).filter(ImageFilter.GaussianBlur(0.6))


def cover():
    image = gradient((226, 196, 150), (92, 98, 112))
    d = ImageDraw.Draw(image, "RGBA")
    d.ellipse((W * 0.58, 180, W * 0.58 + 210, 390), fill=(250, 236, 200, 255))      # a low moon
    for i, y in enumerate(range(560, H, 38)):                                      # the sea, in bands
        shade = 70 + i * 6
        pts = [(0, y)] + [(x, y + 10 * math.sin(x / 55 + i)) for x in range(0, W + 20, 20)] + [(W, H), (0, H)]
        d.polygon(pts, fill=(40 + i * 2, shade, 96 + i * 3, 255))
    d.polygon([(0, 560), (W, 540), (W, 600), (0, 620)], fill=(52, 56, 68, 255))
    # a chapel roof showing above the water, and the bell tower
    d.polygon([(330, 600), (450, 470), (570, 600)], fill=(36, 34, 40, 255))
    d.rectangle((425, 380, 475, 520), fill=(36, 34, 40, 255))
    d.polygon([(415, 380), (450, 320), (485, 380)], fill=(36, 34, 40, 255))
    d.ellipse((438, 410, 462, 440), fill=(214, 176, 90, 255))
    for k in range(5):                                                              # ripples about the chapel
        d.ellipse((280 - k * 30, 596 - k * 5, 620 + k * 30, 620 + k * 9), outline=(235, 235, 230, 120 - k * 18), width=2)
    return wash(image, 1)


def figure(name, skin, cloth, top, bottom, hood=False, hair=None):
    image = gradient(top, bottom)
    d = ImageDraw.Draw(image, "RGBA")
    d.ellipse((W * 0.15, H * 0.62, W * 0.85, H * 1.25), fill=cloth + (255,))        # shoulders
    d.rectangle((W * 0.43, H * 0.5, W * 0.57, H * 0.68), fill=skin + (255,))        # neck
    if hood:
        d.ellipse((W * 0.27, H * 0.16, W * 0.73, H * 0.62), fill=tuple(c // 2 for c in cloth) + (255,))
    if hair and not hood:
        d.ellipse((W * 0.3, H * 0.16, W * 0.7, H * 0.52), fill=hair + (255,))
    d.ellipse((W * 0.34, H * 0.22, W * 0.66, H * 0.56), fill=skin + (255,))         # face
    if hood:
        d.ellipse((W * 0.36, H * 0.27, W * 0.64, H * 0.5), fill=(20, 20, 26, 200))  # shadow under the hood
    else:
        for x in (0.43, 0.57):
            d.ellipse((W * x - 14, H * 0.37, W * x + 14, H * 0.385), fill=(40, 30, 28, 255))
        d.arc((W * 0.44, H * 0.43, W * 0.56, H * 0.49), 20, 160, fill=(120, 60, 56, 255), width=4)
    return wash(image, hash(name) % 1000)


out = Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)
cover().save(out / "cover.png")
figure("aria", (214, 168, 138), (120, 124, 132), (190, 170, 140), (96, 92, 98), hair=(96, 60, 38)).save(out / "aria.png")
figure("odessa", (196, 150, 124), (58, 84, 96), (170, 186, 190), (70, 86, 98), hair=(150, 150, 154)).save(out / "odessa.png")
figure("bell-ringer", (150, 160, 158), (44, 52, 62), (70, 84, 98), (24, 30, 40), hood=True).save(out / "bell-ringer.png")
print("wrote", out)
