#!/usr/bin/env bash
# ofac-codex-adapter Feature install: verify + extract the baked runtime
# tarball, put ofac-codex-worker on PATH, drop the supervisor + this
# Feature's unit. Idempotent: a second run with the same VERSION stamp is a
# no-op. Options arrive as uppercase env: CONFIGPATH, REQUIRECLI.
# The native codex CLI is assumed present in the base image unless
# REQUIRECLI is false.
set -euo pipefail

FEATURE_DIR=$(cd -- "$(dirname -- "$0")" && pwd)
NAME=codex-adapter
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

if [ "${REQUIRECLI:-true}" != "false" ] && ! command -v codex >/dev/null 2>&1; then
  echo "ERROR: 'codex' not found on PATH — install it in the base image (npm i -g @openai/codex) or set requireCli:false" >&2
  exit 1
fi

mkdir -p "$PREFIX" "$SHARE/units"

STAMP="$PREFIX/.installed-version"
if [ ! -f "$STAMP" ] || [ "$(cat "$STAMP")" != "$VERSION" ]; then
  ( cd "$FEATURE_DIR/pkg" && sha256sum -c runtime.tar.gz.sha256 )
  tar -xzf "$FEATURE_DIR/pkg/runtime.tar.gz" -C "$PREFIX"
  printf '%s' "$VERSION" > "$STAMP"
fi

cat > /usr/local/bin/ofac-codex-worker <<EOF
#!/bin/sh
exec node "$PREFIX/dist/bin/worker.js" "\$@"
EOF
chmod 0755 /usr/local/bin/ofac-codex-worker

install -m 0755 "$FEATURE_DIR/pkg/ofac-supervisor.sh" "$SHARE/ofac-supervisor.sh"

cat > "$SHARE/units/32-codex-adapter.sh" <<EOF
#!/bin/sh
# ofac-codex-worker unit (Feature-baked defaults; env still wins:
# WORKER_CONFIG overrides the config path, OFAC_CHILD_SECRET the file token,
# OFAC_ADAPTER_LOG the level).
export WORKER_CONFIG=\${WORKER_CONFIG:-${CONFIGPATH:-.facilitator/codex-harness.yaml}}
# Self-enroll (mirrored byte-for-byte in reference/container/units): when the
# kernel origin + enrollment secret are set and no kernel token exists, enroll
# a child endpoint so the worker gets a kernel link credential for the
# artifact byte path. Retries until the kernel answers (bounded).
if [ -n "\${OFAC_KERNEL_URL:-}" ] && [ -n "\${OFAC_ENROLL_SECRET:-}" ] && [ -z "\${OFAC_KERNEL_TOKEN:-}" ]; then
  i=0
  while [ "\$i" -lt 60 ]; do
    tok=\$(OFAC_ENROLL_URL="\$OFAC_KERNEL_URL/api/link/v1/enroll" \\
      OFAC_ENROLL_BODY="{\\"token\\":\\"\$OFAC_ENROLL_SECRET\\",\\"endpointId\\":\\"\${OFAC_WORKER_ENDPOINT:-demo-worker-child}\\",\\"instanceId\\":\\"unit-\$\$\\",\\"trustMode\\":\\"isolated-executor\\",\\"role\\":\\"child\\",\\"team\\":\\"\${OFAC_WORKER_TEAM:-team-alpha}\\"}" \\
      node -e 'fetch(process.env.OFAC_ENROLL_URL,{method:"POST",headers:{"content-type":"application/json"},body:process.env.OFAC_ENROLL_BODY}).then(r=>r.ok?r.json():{}).then(j=>process.stdout.write(j.linkToken??"")).catch(()=>{})') || true
    if [ -n "\$tok" ]; then
      export OFAC_KERNEL_TOKEN="\$tok"
      break
    fi
    i=\$((i + 1))
    sleep 2
  done
  [ -n "\${OFAC_KERNEL_TOKEN:-}" ] || { echo "unit: kernel self-enrollment failed at \$OFAC_KERNEL_URL"; exit 1; }
fi
# Exit-code law (worker): 0 clean · 1 runtime (restart: the Gateway may have
# been down) · 2 config (terminal) · 3 register denied w/ backoff off (terminal).
[ -f "\$WORKER_CONFIG" ] || { echo "unit: worker config \$WORKER_CONFIG not found"; exit 2; }
restarts=0
max=\${OFAC_SUPERVISOR_MAX_RESTARTS:-10}
while true; do
  ofac-codex-worker run --config "\$WORKER_CONFIG" &
  child=\$!
  trap 'kill -TERM "\$child" 2>/dev/null; exit 0' TERM INT
  wait "\$child"; rc=\$?
  trap - TERM INT
  [ "\$rc" -ge 2 ] && exit \$rc
  restarts=\$((restarts + 1))
  [ "\$restarts" -gt "\$max" ] && { echo "unit: restart budget exhausted"; exit 1; }
  sleep 2
done
EOF
chmod 0755 "$SHARE/units/32-codex-adapter.sh"
