#!/bin/bash
set -eu
command -v ofac-omp-worker
ofac-omp-worker --help >/dev/null
test -x /usr/local/share/openfacilitator/ofac-supervisor.sh
test -f /usr/local/share/openfacilitator/units/31-omp-adapter.sh
test -f /usr/local/lib/openfacilitator/omp-adapter/.installed-version
# Fake-CLI law: the scenario installs with requireCli:false (omp absent at
# install time); a native omp stub satisfies the runtime surface.
printf '#!/bin/sh\nexit 0\n' > /usr/local/bin/omp
chmod 0755 /usr/local/bin/omp
# Config law: a missing worker config is terminal exit 2.
rc=0
env -u WORKER_CONFIG sh /usr/local/share/openfacilitator/units/31-omp-adapter.sh || rc=$?
[ "$rc" -eq 2 ] || { echo "unit: expected terminal 2 for missing config, got $rc" >&2; exit 1; }
# Unit starts with a fake worker: a stub emitting a `worker ready` line and
# parking (like the real child link) stands in for the shim; the unit must
# reach its run loop and shut down cleanly on TERM.
cat > /usr/local/bin/ofac-omp-worker <<'STUB'
#!/bin/sh
[ "$1" = "run" ] || exit 0
echo "worker ready childId=gw-stub/omp socket=stub"
trap 'exit 0' TERM INT
while :; do sleep 5; done
STUB
chmod 0755 /usr/local/bin/ofac-omp-worker
touch /tmp/omp-harness.yaml
WORKER_CONFIG=/tmp/omp-harness.yaml sh -c \
  'sh /usr/local/share/openfacilitator/units/31-omp-adapter.sh > /tmp/omp-unit.log 2>&1 & p=$!; sleep 1; kill -TERM "$p"; wait "$p"' \
  || { cat /tmp/omp-unit.log; echo "unit: unexpected exit" >&2; exit 1; }
grep -q "worker ready" /tmp/omp-unit.log
echo OMP_FEATURE_OK
