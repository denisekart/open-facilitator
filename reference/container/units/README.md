# Reference Container Units

Supervisor unit scripts for the compose `demo-workspace` service, mounted at
`/opt/ofac/units` (the supervisor honors `UNITS_DIR`). The
`30-harness-adapter.sh` self-enroll block is a deliberate byte-for-byte copy
of the block the harness-adapter Feature generates
(`features/src/harness-adapter/install.sh`) — the two consumers (devcontainer
Feature install, compose bind mount) ship the same procedure twice rather
than share a file; a rewrite in one lands in the other.
