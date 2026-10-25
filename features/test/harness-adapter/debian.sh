#!/bin/bash
set -eu
command -v ofac-deterministic-worker
ofac-deterministic-worker --help >/dev/null
test -x /usr/local/share/openfacilitator/ofac-supervisor.sh
test -f /usr/local/share/openfacilitator/units/30-harness-adapter.sh
test -f /usr/local/lib/openfacilitator/harness-adapter/.installed-version
echo ADAPTER_FEATURE_OK
