# Plan 18 — Slim reference environment, leg 2: Gateway + worker in Compose, seeds, referencectl
**Status:** ⬜ not-started (flips 🔵 at the replanning gate, then 🟡)
**Depends on:** 17
**Phase E leg 2 of 5.** Chain: 17 → 18 → 19 (reference env), 20 → 21 (operator surface).

## Objective
Complete §21.1's slim profile as a one-command demo: Gateway + deterministic
worker containers join `compose.slim.yaml` (real Link dispatch, not just a
healthy kernel), `reference/bootstrap/` ships the demo seed set (demo team,
capability manifest, deterministic worker fixture), and `referencectl` becomes
the framework's entry CLI: `up slim`, `down`, `status`. After this plan a fresh
clone reaches a linked, admitting, policy-gated workspace path with
`./referencectl up slim`.

## Known facts (as-built anchors; code wins, divergence goes in the status table)
- Plan 04 deferred the linkd/gateway image shape to this environment ("Plan 11's reference env decides image shape", `04-workspace-link-sdk.md:124`); Plan 17 builds the node images.
- Feature options already define the env overlay the compose must reuse: `OFAC_ENROLL_SECRET`, `OFAC_LINK_TOKEN`, `OFAC_CHILD_SECRET` (`08-devcontainer-features-slim-example.md:22-24`); `facilitatorUrl` default `http://localhost:8088`, container variant `http://host.docker.internal:8088` + absolute socket paths + `OFAC_LINKD_STATE` pin (`:39,:63-64`).
- Plan 06's open contingency (`06-gateway-multiplexing-child-transport.md:195`): the child leg is localhost TCP with NDJSON; cross-container needs either `childTransport.tcp.port` bound `0.0.0.0` or a sidecar-per-socket-volume. **This plan decides it** (locked default below).
- Plan 15 left a `kernelUrl`-unset dev path in the harness with an explicit kill rule: permitted only while no consumer sets it; once a compose/Feature consumer sets `OFAC_KERNEL_URL`+`OFAC_KERNEL_TOKEN`, the inline path is deleted (`15-result-manifests-worker-cutover.md:150,:196`).
- Enrollment modes exist today: (a) shared secret, (b) client-credentials (`ofac-gateway-alpha` client in `reference/keycloak/realm.json`); enrollment rate-limited 30/min/IP. Gateway config surface: per-file generated `gateway.yaml` (e2e harness `gatewayConfig`), deny-by-default YAML policy (Plan 12), `/status` diagnostics JSON.
- Team roster today is dev-config `Link:Teams`; the `default`-team auto-seed was deleted in Plan 10 — real seeds are this plan's job. `reference/bootstrap/` holds only a stub README ("Lands with Plan 11").
- The kernel CLI (`Kernel/Shared/Cli/CliRunner.cs`) speaks only `work create|get|timeline|steer` with `--url`/`--token`; tokens minted today by Keycloak password/client-credentials form POSTs (e2e harness pattern).
- `reference/AGENTS.md:5-7,:21` states compose/`referencectl` do not exist yet — updated by this plan.

## Locked decisions (from planning; replanning may only amend with recorded reason)
- **Child leg in compose: gateway + linkd child in one container** (sidecar per child too heavy; TCP `0.0.0.0` across containers leaks the unauthenticated child leg). The slim image runs the Plan 08 supervisor story: gateway process + linkd child + deterministic harness over the existing localhost TCP leg. One codebase, §9.1 mode-(b) embedded.
- **`referencectl` is a thin shell/TS orchestrator** over `docker compose -f reference/compose.slim.yaml` + `docker compose exec` + Keycloak token minting + the kernel CLI — not a new runtime. Implemented as a repo-root executable so `./referencectl up slim` works verbatim in §28 docs.
- **Seeds via bootstrap pass, not image bake-in:** `reference/bootstrap/` runs post-up (enroll team via `/api/link/v1/enroll`, register manifest via the link plane, create demo project + policy file), so seeds ride the real API surface.
- Demo identities come from the shipped realm only (`alice` + service accounts); no new realm users.
- The Plan 15 `kernelUrl` dev path **dies in this plan** (clean cutover, per its own kill rule).

## Steps
0. **Research (mandatory):** re-verify the supervisor/startup story in `features/` payload + `examples/slim/` devcontainer against container constraints (no devcontainer CLI in compose — plain entrypoint script reproducing supervisor behavior); verify enrollment + manifest-registration request shapes against `LinkEndpoints.Enroll.cs` / capabilities endpoints; verify gateway config file schema against `src/gateway` config loader; check §22.5 hostname rules for `facilitator.localhost`.
1. Slim workspace image: entrypoint composing gateway + linkd + deterministic worker from the Plan 17 node image; config from mounted `reference/config/gateway.yaml` + worker config; env overlays `OFAC_*` names as-built.
2. Compose services `gateway` (+ healthcheck on `/status`) wired to facilitator + keycloak; `GATEWAY_URL`-style env per 08's overlay names.
3. `reference/bootstrap/`: seed script (project, team binding, capability manifest, policy YAML for the demo action set) + `generate-secrets.sh` filling `.env` when absent.
4. `referencectl`: `up slim` (compose up + wait-healthy + bootstrap + print port map/login URL), `down` (compose down, volumes flag), `status` (health + link-session summary via existing endpoints). `verify` lands in Plan 19 — reserve the verb.
5. Cutover: delete the harness `kernelUrl`-unset dev path; `make example-slim-up` reconciled or superseded by `referencectl up slim` (record which).
6. Update `reference/AGENTS.md` + README stubs to as-built facts; docs-check clean.

## State after
Fresh clone → copy `.env.example` → `./referencectl up slim` → kernel healthy, team enrolled via API, gateway online in `link_sessions`, deterministic worker capability advertised; `./referencectl`-minted token + a `work create` reaches the Gateway and is admitted (full acceptance assertions are Plan 19).

## Testing
- Extend the Plan 17 CI bring-up job: gateway healthy + a link session appears for the demo team after bootstrap (PR tier: structural, no business flow).
- `referencectl up slim && ./referencectl down` idempotent round-trip proven locally on WSL arm64 (and repeated — no volume state leaks between runs).
- Existing e2e tiers stay compose-free (they pin spawn-from-binary semantics); no test migration in this plan.

## Documentation
- `docs/getting-started.md`: replace the manual docker-run walkthrough's kernel section with the `referencectl` path (full §28 rewrite is Plan 19; here: the up/down/status loop).
- `reference/README.md` port map extended with gateway ports; `reference/AGENTS.md` de-staled.
- `.omp/skills/facilitator-reference-env` and `facilitator-workspace-packaging` updated to as-built.

## Architecture fit
§21.1 (full slim service set), §22.2 (compose topology), §8.1/§9.1 (the same Feature/supervisor story, containerized), appendix §11 (one-command dev loop). The wire behavior is unchanged — this plan proves it across container boundaries.

## Assumptions & contingencies (replanning caveat)
- If one-container gateway+child proves wrong (e.g. child crash drags gateway), fall back to two containers on a shared network with the TCP leg bound to the docker bridge — record as a deviation, it is a §9.1 mode question.
- If bootstrap-via-API proves too chatty for a cold realm (Keycloak import timing), a bounded retry/wait loop is acceptable; baking SQL seeds is NOT (keeps ledger invariants kernel-owned).
- If `example-slim-up`'s devcontainer smoke and `referencectl up slim` converge, delete the devcontainer compose variant rather than maintaining both (one tested method, §22).
- Replanning or optimization may be required before implementation begins; implementation begins with additional research (Step 0).
- **Questions may be asked during planning, but not during implementation.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research | Not started | |
| 1 Workspace image | Not started | |
| 2 Compose gateway service | Not started | |
| 3 Bootstrap seeds | Not started | |
| 4 referencectl | Not started | |
| 5 kernelUrl cutover | Not started | |
| 6 Docs/AGENTS refresh | Not started | |

## Verification
`./referencectl up slim` from a fresh clone shows an online link session + advertised capability; `down`/`up` round-trip clean; CI bring-up job green including the gateway-health/link-session assertion.
