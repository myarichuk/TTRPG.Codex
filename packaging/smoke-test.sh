#!/usr/bin/env bash
# Starts a packaged release through its launcher and checks that it serves pages, loaded the SRD
# system and started the embedded database on the bundled runtime.
#   packaging/smoke-test.sh dist/linux-x64/app
set -euo pipefail
app="${1:?usage: smoke-test.sh <app-dir>}"

# The tray launcher cannot open a window on a CI machine, so check what it can: that it runs on the
# bundled runtime alone (no DOTNET_ROOT, no installed .NET) and finds the server, runtime and content.
env -u DOTNET_ROOT -u DOTNET_ROOT_X64 "$app/launcher/ttrpg-codex-tray" --self-test

log="$(mktemp -d)/smoke.log"
data="$(mktemp -d)"

CODEX_NO_BROWSER=1 CODEX_URL=http://127.0.0.1:5299 Codex__DataDirectory="$data" "$app/ttrpg-codex" > "$log" 2>&1 &
pid=$!
trap 'kill $pid 2>/dev/null || true; pkill -f Raven.Server 2>/dev/null || true' EXIT

for i in $(seq 1 90); do
    if curl -sf -o /dev/null http://127.0.0.1:5299/login; then
        echo "Server responded to /login after ${i}s"
        break
    fi
    if ! kill -0 $pid 2>/dev/null || [ "$i" -eq 90 ]; then
        echo "Server never responded to /login" >&2
        cat "$log"
        exit 1
    fi
    sleep 1
done

sleep 5 # the scheduled-backup setup (the first database call) runs right after start-up
fail=0
grep -q "was skipped" "$log" && { echo "A content pack was skipped: $(grep "was skipped" "$log")" >&2; fail=1; }
grep -q "Loaded data-driven system srd5e" "$log" || { echo "The SRD system was not loaded" >&2; fail=1; }
# The backup set-up is the first database call; a licence that doesn't cover backups is fine (the
# database answered), anything else means the embedded server didn't start.
if grep -q "Scheduled backup configuration failed" "$log" && ! grep -q "LicenseLimitException" "$log"; then
    echo "The embedded database did not start" >&2; fail=1
fi
curl -sf -o /dev/null http://127.0.0.1:5299/app.css || { echo "Static files are not served" >&2; fail=1; }
if [ $fail -ne 0 ]; then cat "$log"; exit 1; fi
echo "Smoke test passed"
