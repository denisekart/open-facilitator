#!/bin/bash
set -eu
ofac-forgejo-connector --help 2>&1 | grep -q "forgejo-connector run"
test -x /usr/local/share/openfacilitator/ofac-supervisor.sh
test -f /usr/local/share/openfacilitator/units/40-forgejo-connector.sh
test -f /usr/local/lib/openfacilitator/forgejo-provider/.installed-version
# Opt-in law: without FORGEJO_URL the unit exits 0 silently.
env -u FORGEJO_URL sh /usr/local/share/openfacilitator/units/40-forgejo-connector.sh
# With the env set but no config file, the config law says terminal exit 2.
if FORGEJO_URL=http://forgejo.example env -u FORGEJO_CONNECTOR_CONFIG \
    sh /usr/local/share/openfacilitator/units/40-forgejo-connector.sh; then
  echo "unit: expected terminal 2 for missing config" >&2; exit 1
fi
echo FORGEJO_FEATURE_OK
