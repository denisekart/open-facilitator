#!/bin/bash
set -eu
command -v ofac-gateway ofac-linkd ofac-deterministic-worker
for u in 10-gateway.sh 20-linkd.sh 30-harness-adapter.sh; do
  test -f "/usr/local/share/openfacilitator/units/$u"
done
# One supervisor for all three Features; identical install is idempotent.
test "$(find /usr/local/share/openfacilitator -maxdepth 1 -name ofac-supervisor.sh | wc -l)" = 1
echo COMBO_FEATURE_OK
