#!/usr/bin/env bash
# ofac-workspace-link Feature install: verify + extract the baked runtime
# tarball, put ofac-linkd on PATH, drop the supervisor + this Feature's unit.
# Idempotent: a second run with the same VERSION stamp is a no-op.
# Options arrive as uppercase env per the Features spec: FACILITATORURL,
# ENDPOINTID, TEAM, MANIFEST, MODE.
set -euo pipefail

FEATURE_DIR=$(cd -- "$(dirname -- "$0")" && pwd)
NAME=workspace-link
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

cat > /usr/local/bin/ofac-linkd <<EOF
#!/bin/sh
exec node "$PREFIX/dist/bin/linkd.js" "\$@"
EOF
chmod 0755 /usr/local/bin/ofac-linkd
install -m 0755 "$FEATURE_DIR/pkg/ofac-supervisor.sh" "$SHARE/ofac-supervisor.sh"

cat > "$SHARE/units/20-linkd.sh" <<EOF
#!/bin/sh
# ofac-linkd unit (Feature-baked defaults; OFAC_* env still wins at runtime —
# OFAC_ENROLL_SECRET / OFAC_LINK_TOKEN are the credential surface, OFAC_LINKD_LOG=debug the log level).
URL=\${OFAC_LINKD_URL:-${FACILITATORURL:-http://localhost:8088}}
ENDPOINT=\${OFAC_LINKD_ENDPOINT:-${ENDPOINTID:-ws.dev}}
TEAM=\${OFAC_LINKD_TEAM:-${TEAM:-default}}
MANIFEST=\${OFAC_LINKD_MANIFEST:-${MANIFEST:-.facilitator/capabilities.json}}
MODE=\${OFAC_LINKD_MODE:-${MODE:-echo}}
STATE=\${OFAC_LINKD_STATE:-.ofac-linkd.sqlite3}
# linkd requires a manifest it can read; absent file => config exit 2 (terminal).
[ -f "\$MANIFEST" ] || { echo "unit: manifest \$MANIFEST not found"; exit 2; }
# Exit-code law (linkd): 0 clean · 2 config (terminal) · 3 protocol · 4 enrollment.
# Restart everything but config errors; a kernel that was down at enroll is up later.
restarts=0
max=\${OFAC_SUPERVISOR_MAX_RESTARTS:-10}
while true; do
  ofac-linkd run --url "\$URL" --endpoint "\$ENDPOINT" --team "\$TEAM" \\
    --manifest "\$MANIFEST" --mode "\$MODE" --state "\$STATE" &
  child=\$!
  trap 'kill -TERM "\$child" 2>/dev/null; exit 0' TERM INT
  wait "\$child"; rc=\$?
  trap - TERM INT
  [ "\$rc" -eq 0 ] && exit 0
  [ "\$rc" -eq 2 ] && exit \$rc
  restarts=\$((restarts + 1))
  [ "\$restarts" -gt "\$max" ] && { echo "unit: restart budget exhausted"; exit 1; }
  sleep 2
done
EOF
chmod 0755 "$SHARE/units/20-linkd.sh"
