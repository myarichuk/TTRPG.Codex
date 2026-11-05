#!/bin/sh
# Ashen Vellum's paper, rasterised once from the sheet the GM supplied. Each *.html here is one picture, drawn exactly as the sheet drew
# it in CSS: the grain and foxing tiles are its generated SVG noise; the paper-* pages are its page background (burn at the edges, stains,
# tint, paper colour), one for odd pages, even pages, \page {soot}, \page {reliquary} and the cover. A generated texture or a soft
# transparent gradient is drawn again into every page of a PDF as a page-sized picture (section 13: 0.5 MB a page for the grain alone);
# a picture is stored once a book, and a JPEG goes into the PDF as it is. Needs Chrome, cwebp and sips (macOS). Writes into Codex.Core.
set -e
here=$(cd "$(dirname "$0")" && pwd)
out="$here/../../../../src/Codex.Core/Book/Themes/ashen-vellum"
chrome="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
tmp=$(mktemp -d)
shot() { "$chrome" --headless=new --disable-gpu --hide-scrollbars --default-background-color=00000000 --window-size=$2,$3 --screenshot="$tmp/$1.png" "file://$here/$1.html" >/dev/null 2>&1; }
for tile in grain-light:420 foxing-light:380 grain-dark:420 foxing-dark:380; do
  name=${tile%%:*}; size=${tile##*:}
  shot "$name" "$size" "$size"
  cwebp -quiet -q 55 -alpha_q 60 -m 6 "$tmp/$name.png" -o "$out/$name.webp"
done
for name in paper-odd paper-even paper-soot paper-dark paper-cover; do
  shot "$name" 816 1056
  sips -s format jpeg -s formatOptions 82 "$tmp/$name.png" --out "$out/$name.jpg" >/dev/null
done
rm -rf "$tmp"
