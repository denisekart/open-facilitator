#!/bin/sh
# ofac-supervisor — headless entrypoint for Feature-installed OFAC runtimes
# (never rely on devcontainer lifecycle hooks; the entrypoint works
# in IDE and headless contexts alike). Installed identically by every Feature;
# the last Feature to install simply rewrites the same bytes.
#
# Contract: run every executable in ${UNITS_DIR:-/usr/local/share/openfacilitator/units}/
# (sorted), each logging to ${OFAC_LOG_DIR:-/tmp/openfacilitator/logs}/<unit>.log.
# A unit script is a plain loop: it restarts its runtime with backoff and
# exits non-zero when it gives up. The supervisor forwards TERM/INT to all
# units, waits, and exits with the first non-zero unit status.
set -eu

UNITS_DIR=${UNITS_DIR:-/usr/local/share/openfacilitator/units}
LOG_DIR=${OFAC_LOG_DIR:-/tmp/openfacilitator/logs}
mkdir -p "$LOG_DIR"

pids=""
first_rc=0
forward() {
  # Forward the signal once; children handle their own graceful shutdown.
  for p in $pids; do kill -TERM "$p" 2>/dev/null || true; done
}
trap forward TERM INT

for unit in $(find "$UNITS_DIR" -maxdepth 1 -name '[0-9]*-*.sh' -perm -u+x 2>/dev/null | sort); do
  name=$(basename "$unit")
  sh "$unit" >>"$LOG_DIR/$name.log" 2>&1 &
  pids="$pids $!"
done

if [ -z "$pids" ]; then
  echo "ofac-supervisor: no units in $UNITS_DIR" >&2
  exit 0
fi

# Aggregate: wait for every unit; remember the first non-zero exit. The trap
# may interrupt wait (EINTR), so loop until all are reaped.
running="1"
while [ -n "$running" ]; do
  remaining=""
  for p in $pids; do
    if kill -0 "$p" 2>/dev/null; then
      remaining="$remaining $p"
    else
      # Reap to collect the exit status; sh cannot read a dead child's rc
      # after a prior wait, so best-effort per-pid wait.
      wait "$p" 2>/dev/null || { [ "$first_rc" -ne 0 ] || first_rc=1; }
    fi
  done
  pids="$remaining"
  [ -n "$pids" ] || running=""
  [ -n "$pids" ] && sleep 1
done

exit "$first_rc"
