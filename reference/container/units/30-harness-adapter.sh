#!/bin/sh
# ofac-cli-worker unit for the compose demo-workspace container.
# Mirrors the Feature-baked unit (features/src/harness-adapter/install.sh);
# the kernel-leg block below is a deliberate byte-for-byte copy (see
# README.md). Config paths are compose mounts; tokens come from env only.
export WORKER_CONFIG=${WORKER_CONFIG:-/opt/ofac/config/worker.yaml}
# Kernel leg: OFAC_KERNEL_ENDPOINT lets the worker run the credential law
# against the kernel itself (identity.key in its state dir, the enrollment
# code spent on first boot, 5-minute sessions after — the bin owns that now,
# a shell cannot keep a session JWT fresh). A kernel still booting just fails
# the first worker start; the restart loop below is the retry.
if [ -n "${OFAC_KERNEL_URL:-}" ] && [ -n "${OFAC_ENROLL_SECRET:-}" ]; then
  export OFAC_KERNEL_ENDPOINT=${OFAC_WORKER_ENDPOINT:-demo-worker-child}
fi
# Exit-code law (worker): 0 clean · 1 runtime (restart: the Gateway may have
# been down) · 2 config (terminal) · 3 register denied w/ backoff off (terminal).
[ -f "$WORKER_CONFIG" ] || { echo "unit: worker config $WORKER_CONFIG not found"; exit 2; }
restarts=0
max=${OFAC_SUPERVISOR_MAX_RESTARTS:-10}
while true; do
  node /app/worker/dist/bin/worker.js run --config "$WORKER_CONFIG" &
  child=$!
  trap 'kill -TERM "$child" 2>/dev/null; exit 0' TERM INT
  wait "$child"; rc=$?
  trap - TERM INT
  [ "$rc" -ge 2 ] && exit $rc
  restarts=$((restarts + 1))
  [ "$restarts" -gt "$max" ] && { echo "unit: restart budget exhausted"; exit 1; }
  sleep 2
done
