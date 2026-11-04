# examples/local/

## Purpose
Self-hosted "at home" deployment reference (fork-me example, not a
product): kernel box + N node boxes, everything outbound from the nodes.
Roles file (`envs/<env>.json`) in, deterministic per-box docker-compose
bundles out (`localctl render`). Operator guide: `README.md`.

## Invariants (invisible in code)
- **Sim is the gate.** `make example-local-smoke` (topology `sim`, one
  host, plain loopback ports) must pass from a clean tree, AI-free. The
  multi-box postures (`lan`/`tailscale`) share the renderer and are
  verified manually — no hosted CI runs them.
- **Node boxes expose no inbound API surface.** Gateway diagnostics bind
  container loopback; the launcher has no health port. Only sshd (for the
  `localctl` push) and outbound HTTPS/WebSocket.
- **Rendered, never handwritten.** Every bundle file comes from
  `templates/` + the roles file + `versions.env`; `.out/` is a product
  (gitignored). Registry images are digest-or-tag via
  `envs/<env>.lock.json` (committed audit data).
- **Enrollment hand-off:** node gateways ride a MINTED token
  (`POST /api/admin/enroll-tokens`, first use binds the endpoint, revoke
  kills the issued link enrollment); children + the launcher ride the
  STATIC `Link__EnrollmentToken` (break-glass / workload-identity law).

## Anchors
- Roles schema + fail-closed validation: `tools/localctl/src/env.ts`.
- Deterministic renderer (issuer alias law, image law):
  `tools/localctl/src/render.ts`.
- Driver split (sim projects vs ssh push): `tools/localctl/src/drivers.ts`,
  `tools/localctl/src/compose.ts`, `tools/localctl/src/ssh.ts`; smoke legs:
  `tools/localctl/src/smoke.ts`.
- Kernel token endpoints:
  `../../src/facilitator/Kernel/Link/EnrollmentTokenEndpoints.cs`; gate
  binding `enrollment-tokens.manage` in the rendered `facilitator.json`.
