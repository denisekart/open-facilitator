#!/usr/bin/env bash
# ofac-gateway Feature install: verify + extract the baked runtime tarball,
# put ofac-gateway on PATH, drop the supervisor + this Feature's unit.
# Idempotent: a second run with the same VERSION stamp is a no-op.
# Options arrive as uppercase env (CONFIGPATH, LOG) per the Features spec.
set -euo pipefail

FEATURE_DIR=$(cd -- "$(dirname -- "$0")" && pwd)
NAME=gateway
VERSION=1.0.0
PREFIX=/usr/local/lib/openfacilitator/$NAME
SHARE=/usr/local/share/openfacilitator

# Node >= 22 prerequisite (hard fail, actionable).
if ! command -v node >/dev/null 2>&1; then
  echo "ofac-$NAME: node not found on PATH. Use a base image shipping Node >= 22 (e.g. mcr.microsoft.com/devcontainers/base)." >&2
  exit 1
fi
NODE_MAJOR=$(node -p 'process.versions.node.split(".")[0]')
if [ "$NODE_MAJOR" -lt 22 ]; then
  echo "ofac-$NAME: Node >= 22 required, found $(node --version). Choose a newer base image." >&2
  exit 1
fi

mkdir -p "$PREFIX" "$SHARE/units"

STAMP="$PREFIX/.installed-version"
if [ ! -f "$STAMP" ] || [ "$(cat "$STAMP")" != "$VERSION" ]; then
  # Checksum-verify, then extract the self-contained runtime tree.
  ( cd "$FEATURE_DIR/pkg" && sha256sum -c runtime.tar.gz.sha256 )
  tar -xzf "$FEATURE_DIR/pkg/runtime.tar.gz" -C "$PREFIX"
  printf '%s' "$VERSION" > "$STAMP"
fi

# PATH contract: `ofac-gateway` resolves to the extracted bundle entrypoint.
cat > /usr/local/bin/ofac-gateway <<EOF
#!/bin/sh
exec node "$PREFIX/dist/bin/gateway.js" "\$@"
EOF
chmod 0755 /usr/local/bin/ofac-gateway

# Supervisor (identical bytes across Features) + this Feature's unit.
install -m 0755 "$FEATURE_DIR/pkg/ofac-supervisor.sh" "$SHARE/ofac-supervisor.sh"

LOG_EXPORT=""
if [ "${LOG:-info}" = "debug" ]; then
  LOG_EXPORT='export OFAC_GATEWAY_LOG="${OFAC_GATEWAY_LOG:-debug}"'
fi
cat > "$SHARE/units/10-gateway.sh" <<EOF
#!/bin/sh
# ofac-gateway unit (Feature-baked defaults; env still wins at runtime).
export GATEWAY_CONFIG=\${GATEWAY_CONFIG:-${CONFIGPATH:-.facilitator/gateway.yaml}}
$LOG_EXPORT
restarts=0
max=\${OFAC_SUPERVISOR_MAX_RESTARTS:-10}
while true; do
  ofac-gateway run --config "\$GATEWAY_CONFIG" &
  child=\$!
  # Forward supervisor TERM/INT to the runtime (it exits clean 0), then stop.
  trap 'kill -TERM "\$child" 2>/dev/null; exit 0' TERM INT
  wait "\$child"; rc=\$?
  trap - TERM INT
  # Config/protocol/enrollment errors (>=2) are terminal: don't restart-loop.
  [ "\$rc" -ge 2 ] && exit \$rc
  restarts=\$((restarts + 1))
  [ "\$restarts" -gt "\$max" ] && { echo "unit: restart budget exhausted"; exit 1; }
  sleep 2
done
EOF
chmod 0755 "$SHARE/units/10-gateway.sh"
