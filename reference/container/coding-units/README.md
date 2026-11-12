# Reference Container Units

Supervisor unit scripts for the compose `demo-workspace` service, mounted at
`/opt/ofac/units` (the supervisor honors `UNITS_DIR`). The
`30-harness-adapter.sh` self-enroll block is a deliberate byte-for-byte copy
of the block the harness-adapter Feature generates
(`features/src/harness-adapter/install.sh`) — the two consumers (devcontainer
Feature install, compose bind mount) ship the same procedure twice rather
than share a file; a rewrite in one lands in the other.

The `40-forgejo-connector.sh` unit is opt-in by existence: without
`FORGEJO_URL` it exits 0 immediately, so the slim stack (which mounts this
same directory) stays functionally provider-less — the supervisor simply runs
one unit fewer. It mirrors the forgejo-provider Feature unit
(`features/src/forgejo-provider/install.sh`): same opt-in + exit-code law,
image-path invocation.
