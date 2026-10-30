#!/bin/sh
# ofac-coding-worker unit for the compose coding-workspace container.
# Delta-marked copy of units/30-harness-adapter.sh (unit-copy law: identical
# except the runtime path, the config default, and the enroll identity/team):
# the self-enroll block is the same byte-for-byte copy, only the endpointId
# default and OFAC_WORKER_TEAM default differ.
export WORKER_CONFIG=${WORKER_CONFIG:-/opt/ofac/config/worker.yaml}
# Self-enroll: when the kernel origin + enrollment secret are set and no
# kernel token exists, enroll a child endpoint so the worker gets a kernel
# link credential for the artifact byte path. Retries until the kernel
# answers (bounded).
if [ -n "${OFAC_KERNEL_URL:-}" ] && [ -n "${OFAC_ENROLL_SECRET:-}" ] && [ -z "${OFAC_KERNEL_TOKEN:-}" ]; then
  i=0
  while [ "$i" -lt 60 ]; do
    tok=$(OFAC_ENROLL_URL="$OFAC_KERNEL_URL/api/link/v1/enroll" \
      OFAC_ENROLL_BODY="{\"token\":\"$OFAC_ENROLL_SECRET\",\"endpointId\":\"${OFAC_WORKER_ENDPOINT:-coding-worker-child}\",\"instanceId\":\"unit-$$\",\"trustMode\":\"isolated-executor\",\"role\":\"child\",\"team\":\"${OFAC_WORKER_TEAM:-team-coding}\"}" \
      node -e 'fetch(process.env.OFAC_ENROLL_URL,{method:"POST",headers:{"content-type":"application/json"},body:process.env.OFAC_ENROLL_BODY}).then(r=>r.ok?r.json():{}).then(j=>process.stdout.write(j.linkToken??"")).catch(()=>{})') || true
    if [ -n "$tok" ]; then
      export OFAC_KERNEL_TOKEN="$tok"
      break
    fi
    i=$((i + 1))
    sleep 2
  done
  [ -n "${OFAC_KERNEL_TOKEN:-}" ] || { echo "unit: kernel self-enrollment failed at $OFAC_KERNEL_URL"; exit 1; }
fi
# Exit-code law (worker): 0 clean · 1 runtime (restart: the Gateway may have
# been down) · 2 config (terminal) · 3 register denied w/ backoff off (terminal).
[ -f "$WORKER_CONFIG" ] || { echo "unit: worker config $WORKER_CONFIG not found"; exit 2; }
restarts=0
max=${OFAC_SUPERVISOR_MAX_RESTARTS:-10}
while true; do
  node /app/coding-worker/dist/bin/worker.js run --config "$WORKER_CONFIG" &
  child=$!
  trap 'kill -TERM "$child" 2>/dev/null; exit 0' TERM INT
  wait "$child"; rc=$?
  trap - TERM INT
  [ "$rc" -ge 2 ] && exit $rc
  restarts=$((restarts + 1))
  [ "$restarts" -gt "$max" ] && { echo "unit: restart budget exhausted"; exit 1; }
  sleep 2
done
