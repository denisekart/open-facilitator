#!/bin/bash
set -eu
command -v ofac-gateway
ofac-gateway --help >/dev/null
test -x /usr/local/share/openfacilitator/ofac-supervisor.sh
test -f /usr/local/share/openfacilitator/units/10-gateway.sh
test -f /usr/local/lib/openfacilitator/gateway/.installed-version
echo GATEWAY_FEATURE_OK
