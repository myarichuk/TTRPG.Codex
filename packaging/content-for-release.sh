#!/usr/bin/env bash
# Removes from a copied content/ folder every system not in content/systems/bundled.txt: those ship as their own archives
# (packaging/pack-systems.sh), not inside the app.
#
#   packaging/content-for-release.sh <copied content folder>
set -euo pipefail

content="${1:?usage: content-for-release.sh <content folder>}"
bundled="$(sed -e 's/#.*//' -e 's/[[:space:]]//g' "$content/systems/bundled.txt" | grep -v '^$' || true)"
for dir in "$content"/systems/*/; do
    [ -d "$dir" ] || continue
    name="$(basename "$dir")"
    if ! grep -qxF "$name" <<<"$bundled"; then
        echo "    leaving out $name (released on its own)"
        rm -rf "$dir"
    fi
done
