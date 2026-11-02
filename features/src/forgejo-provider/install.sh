#!/usr/bin/env bash
# ofac-forgejo-provider Feature install: verify + extract the baked runtime
# tarball (the connector + its @ofac/forgejo-adapter client dep, all
# workspace deps copied in by pnpm deploy), put ofac-forgejo-connector on
# PATH, drop the supervisor + this Feature's unit (numeric prefix 40, after
# the gateway + worker units). Idempotent: a second run with the same VERSION
# stamp is a no-op. Options arrive as uppercase env: CONFIGPATH.
set -euo pipefail

FEATURE_DIR=$(cd -- "$(dirname -- "$0")" && pwd)
NAME=forgejo-provider
VERSION=1.0.0
PREFIX=/usr/local/lib/openfacilitator/$NAME
SHARE=/usr/local/share/openfacilitator

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
  ( cd "$FEATURE_DIR/pkg" && sha256sum -c runtime.tar.gz.sha256 )
  tar -xzf "$FEATURE_DIR/pkg/runtime.tar.gz" -C "$PREFIX"
  printf '%s' "$VERSION" > "$STAMP"
fi

cat > /usr/local/bin/ofac-forgejo-connector <<EOF
#!/bin/sh
exec node "$PREFIX/dist/bin/connector.js" "\$@"
EOF
chmod 0755 /usr/local/bin/ofac-forgejo-connector

install -m 0755 "$FEATURE_DIR/pkg/ofac-supervisor.sh" "$SHARE/ofac-supervisor.sh"

# Opt-in by existence: without FORGEJO_URL the unit
# exits 0 immediately — installing the Feature in a chat-only workspace
# changes nothing. Mirrors the compose unit (reference/container/units/40-
# forgejo-connector.sh): same opt-in + exit-code law, image-path invocation.
cat > "$SHARE/units/40-forgejo-connector.sh" <<EOF
#!/bin/sh
# ofac-forgejo-connector unit (Feature-baked defaults; env still wins:
# FORGEJO_CONNECTOR_CONFIG overrides the config path, OFAC_CHILD_SECRET the
# file token, FORGEJO_ENABLE_* the advertised op domains).
export FORGEJO_CONNECTOR_CONFIG=\${FORGEJO_CONNECTOR_CONFIG:-${CONFIGPATH:-.facilitator/forgejo-connector.yaml}}
[ -n "\${FORGEJO_URL:-}" ] || exit 0
[ -f "\$FORGEJO_CONNECTOR_CONFIG" ] || { echo "unit: connector config \$FORGEJO_CONNECTOR_CONFIG not found"; exit 2; }
restarts=0
max=\${OFAC_SUPERVISOR_MAX_RESTARTS:-10}
while true; do
  ofac-forgejo-connector run --config "\$FORGEJO_CONNECTOR_CONFIG" &
  child=\$!
  trap 'kill -TERM "\$child" 2>/dev/null; exit 0' TERM INT
  wait "\$child"; rc=\$?
  trap - TERM INT
  # Exit-code law (connector): 0 clean · 1 runtime (restart: the Gateway may
  # have been down) · >=2 terminal (config error / register denied).
  [ "\$rc" -ge 2 ] && exit \$rc
  restarts=\$((restarts + 1))
  [ "\$restarts" -gt "\$max" ] && { echo "unit: restart budget exhausted"; exit 1; }
  sleep 2
done
EOF
chmod 0755 "$SHARE/units/40-forgejo-connector.sh"

echo "ofac-$NAME $VERSION installed at $PREFIX (unit: $SHARE/units/40-forgejo-connector.sh)"
