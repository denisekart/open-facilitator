#!/usr/bin/env bash
# Leg timer for `make coverage`: runs the leg, records wall seconds into
# artifacts/coverage/timings.json under the tier key, exits with the leg's
# status. First argument: tier tag; second: timings file; rest: the command.
set -u
tier="$1"; timings="$2"; shift 2
mkdir -p "$(dirname "$timings")"; touch "$timings"
start=$(date +%s)
status=0
"$@" || status=$?
end=$(date +%s)
secs=$((end - start))
node -e '
const [file, tier, secs] = process.argv.slice(1);
const fs = require("fs");
let t = {};
try { t = JSON.parse(fs.readFileSync(file, "utf8")); } catch {}
t[tier] = secs;
fs.writeFileSync(file, JSON.stringify(t, null, 2) + "\n");
' "$timings" "$tier" "$secs"
exit $status
