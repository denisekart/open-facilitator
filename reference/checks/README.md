# Reference smoke checks

Planned post-up verification scripts (health endpoints, link handshake) for a
`referencectl` workflow — not built yet. The live smoke gate for this tree is
the `ref-env-bringup` job in `.github/workflows/ci.yml`, which asserts health,
realm, auth and artifact-bucket liveness against a booted slim stack.
