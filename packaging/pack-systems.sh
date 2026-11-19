#!/usr/bin/env bash
# Writes each system under content/systems/ that is not in content/systems/bundled.txt as the archive the app's install takes
# (Codex.SystemCheck --pack): <out>/system-<id>-<version>.zip. Each is checked first, as the server loads it; one that fails
# stops the script. A system with compiled code cannot be an archive (it is installed by copying its folder), and fails too.
#
#   packaging/pack-systems.sh <out folder>
#
# It also writes <out>/systems.json, the catalog of those archives (id, name, version, description, licence, scripts, file, size,
# sha256). With no separate systems it writes the archives of none, and a catalog with an empty list.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:?usage: pack-systems.sh <out folder>}"
mkdir -p "$out"
bundled="$(sed -e 's/#.*//' -e 's/[[:space:]]//g' "$repo/content/systems/bundled.txt" | grep -v '^$' || true)"

unreleased=""
[ -f "$repo/content/systems/unreleased.txt" ] && unreleased="$(sed -e 's/#.*//' -e 's/[[:space:]]//g' "$repo/content/systems/unreleased.txt" | grep -v '^$' || true)"

dotnet build "$repo/tools/Codex.SystemCheck" -c Release -v q -nologo >/dev/null
entries="$(mktemp -d)"
trap 'rm -rf "$entries"' EXIT
for dir in "$repo"/content/systems/*/; do
    name="$(basename "$dir")"
    grep -qxF "$name" <<<"$bundled" && continue
    grep -qxF "$name" <<<"$unreleased" && { echo "skipping $name (listed in unreleased.txt)"; continue; }
    [ -f "$dir/system.yaml" ] || continue

    archive="$out/.$name.zip"
    report="$(dotnet run --project "$repo/tools/Codex.SystemCheck" -c Release --no-build -- "$dir" --pack "$archive" --catalog-entry "$entries/$name.json")"
    echo "$report"
    # "LOADED  <id> <version> (<name>)"
    read -r _ id version _ <<<"$(grep -m1 '^LOADED' <<<"$report")"
    mv "$archive" "$out/system-$id-$version.zip"
    echo "==> $out/system-$id-$version.zip"
done

# The list of those archives the app's "Get more game systems" page reads (always written; empty when nothing is separate).
shopt -s nullglob
dotnet run --project "$repo/tools/Codex.SystemCheck" -c Release --no-build -- --catalog "$out/systems.json" "$entries"/*.json
