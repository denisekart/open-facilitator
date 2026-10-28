#!/bin/sh
# ofac-gateway unit for the compose demo-workspace container. Mirrors the
# Feature-baked unit (features/src/gateway/install.sh): same exit-code law,
# same restart budget — the binaries come from the mounted image paths, the
# config from the mounted config dir.
export GATEWAY_CONFIG=${GATEWAY_CONFIG:-/opt/ofac/config/gateway.yaml}
restarts=0
max=${OFAC_SUPERVISOR_MAX_RESTARTS:-10}
while true; do
  node /app/gateway/dist/bin/gateway.js run --config "$GATEWAY_CONFIG" &
  child=$!
  # Forward supervisor TERM/INT to the runtime (it exits clean 0), then stop.
  trap 'kill -TERM "$child" 2>/dev/null; exit 0' TERM INT
  wait "$child"; rc=$?
  trap - TERM INT
  # Config/protocol/enrollment errors (>=2) are terminal: don't restart-loop.
  [ "$rc" -ge 2 ] && exit $rc
  restarts=$((restarts + 1))
  [ "$restarts" -gt "$max" ] && { echo "unit: restart budget exhausted"; exit 1; }
  sleep 2
done
