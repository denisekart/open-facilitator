#!/bin/bash
set -eu
command -v ofac-linkd
ofac-linkd --help >/dev/null
test -x /usr/local/share/openfacilitator/ofac-supervisor.sh
test -f /usr/local/share/openfacilitator/units/20-linkd.sh
test -f /usr/local/lib/openfacilitator/workspace-link/.installed-version
echo LINKD_FEATURE_OK
