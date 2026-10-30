#!/bin/sh
# ofac-forgejo-connector unit for the compose coding-workspace container:
# coding-fleet copy of units/40-forgejo-connector.sh, byte-for-byte (the
# second connector instance is told apart by its mounted config: own
# childWorkspaceId + own sqlite ledger under the workspace's state volume.)
# Thin wrapper per the 30-harness-adapter law — the process logic lives in
# TS, this only supervises. Opt-in by existence: the
# slim stack ships zero forgejo env, so without FORGEJO_URL the unit exits 0
# quietly and the supervisor just runs one unit fewer (slim stays
# provider-less). Same exit-code law as the worker unit: 0 clean · 1 runtime
# (restart, bounded) · >=2 terminal (config/register).
export FORGEJO_CONNECTOR_CONFIG=${FORGEJO_CONNECTOR_CONFIG:-/opt/ofac/config/forgejo-connector.yaml}
[ -n "${FORGEJO_URL:-}" ] || exit 0
[ -f "$FORGEJO_CONNECTOR_CONFIG" ] || { echo "unit: connector config $FORGEJO_CONNECTOR_CONFIG not found"; exit 2; }
restarts=0
max=${OFAC_SUPERVISOR_MAX_RESTARTS:-10}
while true; do
  node /app/forgejo-connector/dist/bin/connector.js run --config "$FORGEJO_CONNECTOR_CONFIG" &
  child=$!
  trap 'kill -TERM "$child" 2>/dev/null; exit 0' TERM INT
  wait "$child"; rc=$?
  trap - TERM INT
  [ "$rc" -ge 2 ] && exit $rc
  restarts=$((restarts + 1))
  [ "$restarts" -gt "$max" ] && { echo "unit: restart budget exhausted"; exit 1; }
  sleep 2
done
