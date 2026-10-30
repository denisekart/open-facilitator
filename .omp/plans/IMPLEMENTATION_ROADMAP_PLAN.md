# Open Facilitator Framework — Master Implementation Roadmap

## Context

The repo is spec-only: `docs/specs/open_facilitator_framework_architecture.md` (normative architecture, §26 defines ten delivery slices) plus `docs/specs/open-facilitator-appendix.md` (normative clarifications: trustMode, actorEvidenceRef, RFC 8785 approval hashing, outbox/inbox, memory ACLs, conformance must-haves). Nothing is implemented. This roadmap is the top-level collection of numbered, autonomous implementation plans in execution order. Early plans establish architecture infrastructure (repo skeleton, protocol contracts, transactional kernel, transport); later plans deliver single vertical feature slices.

**Deliverable of this plan:** create `docs/roadmap.md` containing exactly the plan catalogue below (sections "How to use" + plans 00–33), and nothing else. No code, no scaffold — implementation starts when Plan 00 is executed after its own replanning pass.

## How to use this roadmap

- **Status legend:** ⬜ `not-started` → 🔵 `replanning` (mandatory gate) → 🟡 `in-progress` → 🟢 `done`.
- **Every plan starts with a replanning pass** (status flips to 🔵): re-verify assumptions against the specs and the state left by prior plans, run the listed research, produce a per-plan detail design (`.omp/plans/<slug>.md`), and only then flip to 🟡. Decisions made during earlier plan executions are expected to force local re-optimization here; the roadmap scope/objective stays, the internal detail is re-derived.
- Plans execute **in numeric order** except where a `Depends on` line names a strict subset. Phase B runs 05→06→07→08 in sequence; Phase C runs 09→10→11→12 in sequence; Phase D runs 13→14→15 in sequence, 16 independent of the storage leg (after 09/10); Phase E runs 17→18→19 then 20→21 in sequence; Phase F runs 22→23→24→25→26→27→28→29→30 in sequence; Phase G is independent of 27–30.
- Each plan owns its tests and docs in the same change (AGENTS.md skill-maintenance rule applies: update `.omp/skills/` projections in the slice's change).
- Cross-cutting conformance: shared fixtures live in `protocol/conformance-fixtures/` from Plan 01 onward; every later plan adds fixtures for its behavior. Spec §26.1's 15-item suite is the acceptance backlog; each plan below names which items it lands.

---

## Phase A — Architecture foundations

### Plan 00 — Repository skeleton, build system, and CI spine

**Status:** 🟡 in-progress (detail design: `00-repository-skeleton-build-ci.md`; 🟢 when CI is green on `main`)
**Objective:** Turn the spec-only repo into a buildable monorepo matching spec §22.1 (`framework/` tree: `src/facilitator`, `src/gateway`, `src/workspace-link`, `src/adapters`, `protocol/`, `features/`, `examples/`, `reference/`, `docs/`, `Makefile`) with green CI on an empty-but-compiling codebase.
**Replanning gate:** run `/scaffold` (stack `dotnet-aspire-svelte`) and reconcile its output against §22.1; decide sln/csproj granularity (one `Facilitator.sln`, projects `Facilitator`, `Facilitator.Tests`; pnpm workspace for TS packages `@ofac/gateway`, `@ofac/workspace-link`), TS toolchain (Node 22 LTS, vitest, tsup), .NET 10 SDK pin in `global.json`.
**Scope:** directory tree with stub projects/packages that compile; `versions.env` pinning Postgres 17, Keycloak 26.x, SeaweedFS digest, Aspire dashboard release, Kafka 4.2.x, Forgejo 16.x, Mattermost TE, Ollama (§22.4); `Makefile` targets `build|test|lint|compose-slim|compose-full` placeholders; GitHub Actions per §22.6 (build .NET + TS, validate configs, unit tests; compose jobs gated until the slim reference env, now Plans 17–19); `.editorconfig`, NuGet/npm config, `.gitignore` additions (`reference/secrets/`, `bin/`, `obj/`, `dist/`, `node_modules/`).
**State after:** `make build` and `make test` pass locally and in CI on empty projects; every §22.1 path exists; CI is the merge gate from here on.
**Testing:** CI runs dotnet build/test + `pnpm -r build/test` on stubs; no behavior tests yet.
**Documentation:** `README.md` build instructions section; `docs/contributing.md` stub (repo layout, make targets, PR gate).
**Architecture fit:** §22.1 (paths), §22.4 (pinning), §22.6 (CI/release contract). The static skeleton all later plans write into.
**Depends on:** —
**Additional resources:** spec §22.1/§22.4/§22.6; Dev Container repo `.devcontainer/`; `dotnet-aspire-svelte` scaffold; GitHub Actions `actions/setup-dotnet@v4`, `pnpm/action-setup`.

### Plan 01 — Protocol contract package: schemas, envelope, conformance fixtures

**Status:** 🟡 in-progress (detail design + Implementation Status: `01-protocol-contract-package.md`; 🟢 when CI is green on `main`)
**Objective:** Publish the normative wire contract as machine-readable artifacts before any runtime code: CloudEvents 1.0 envelope JSON Schemas for the four message classes (command/event/receipt/control, §5.2), JSON Schemas per message type (`org.facilitator.*.v1` namespace), canonical-serialization fixtures, and a schema-validation conformance harness consumed by later plans in .NET and TypeScript.
**Replanning gate:** fix the exact type-name list (commands: `work.assign|steer|cancel|status.request|nudge`, `context.fetch`, `approval.respond`; events: `work.accepted|progress|input_required`, `assignment.completed|failed`, `workspace.heartbeat`, `artifact.available`, `memory.candidate`; receipts `accepted|duplicate|rejected|unsupported`; control `hello|welcome|heartbeat|flow_control|goodbye`); fix required fields per §5.2 (commandId, workId, assignmentId, targetId, deadline, expectedContextVersion, actorEvidenceRef, capabilityId) and appendix §16 canonicalization rules.
**Scope:** `protocol/json-schema/*.json` (one schema per type, `$id` under `https://schemas.openfacilitator.org/...`), `protocol/cloudevents-schemas/envelope.json`, `protocol/conformance-fixtures/*.json` (valid/invalid pairs, incl. unknown-command-rejection and payload-limit cases), `protocol/openapi/link-v1.yaml` stub for the HTTP fallback surface (§5.3), generated C# DTOs project + TS types package (`@ofac/protocol`) from the schemas (choose generation tool at gate: `quicktype` or hand-rolled with `JsonSchema.Net`/`ajv`), validation CLI (`dotnet run --project protocol/tools/Validate.Fixtures`) + vitest fixture suite.
**State after:** any runtime component can reference versioned schemas + typed DTOs; CI validates every fixture against every schema; §26.1 item 1 (schema validation, payload limits, major-version negotiation, unknown command rejection) has its fixture basis.
**Testing:** fixture harness: all valid fixtures validate, all invalid fixtures fail with the expected error class; DTO/codegen drift check in CI.
**Documentation:** `docs/protocol.md` — envelope, message classes, namespacing/versioning rules (§5.5), how to add a v2 type.
**Architecture fit:** §5 entirely; appendix §5 (outbox/inbox payload shapes), §16 (canonical JSON). The contract every other plan implements against — this is why it precedes all runtime code.
**Depends on:** 00
**Additional resources:** CloudEvents 1.0 spec + JSON schema binding; JSON Schema 2020-12; RFC 8785; spec §5.2/§5.5, appendix §16.

### Plan 02 — Transactional kernel: Postgres schema, state machines, inbox/outbox, ingress

**Status:** 🟡 in-progress (detail design + Implementation Status: `02-transactional-kernel.md`; 🟢 when CI is green on `main`)
**Objective:** Implement the durable coordination core (spec slice 1): EF Core + PostgreSQL 17 schema (§19.1 tables: projects, works, assignments, inbox, outbox, provider_events, link_sessions, approvals, communication_bindings), Work/Assignment state machines (§4.1), transactional inbox/outbox with leases (§7.1–7.2, appendix §5), optimistic `workVersion` fencing (§7.4), the database-backed event loop (§20 `ICoordinationStore`/`IEventDispatcher`), and HTTP + CLI ingress for create-Work/steer/status.
**Replanning gate:** reconcile §19.1 table sketch against Plan 01 schemas; decide migration tooling (EF Core migrations, one baseline), dispatcher background-service architecture (`IHostedService` + `FOR UPDATE SKIP LOCKED`), and how CLI (`dotnet run --project src/facilitator -- work create ...` vs separate `referencectl` seed) authenticates locally pre-IdP (bootstrap shared-token mode, replaced at identity federation, now Plan 09).
**Scope:** `framework/src/facilitator/` ASP.NET Core app: minimal-API endpoints `POST /api/works`, `GET /api/works/{id}`, `POST /api/works/{id}/steer`, `GET /api/works/{id}/timeline`; state-machine guard table as explicit transitions (no implicit jumps); inbox dedupe on `(source,id)` + `commandId`; outbox same-transaction writes + leased dispatcher with attempt/nextAttemptAt/dead-letter; per-Work advisory-lock ordering; local testcontainers fixture.
**State after:** accepted event survives process restart; duplicate business commands are idempotent; a Work can be driven `open→planning→active→review→completed` purely via HTTP with the outbox draining to a stub sink. Spec slice-1 exit test passes.
**Testing:** xUnit + Testcontainers(Postgres 17): restart-after-accepted-event test (kill dispatcher mid-drain, restart, no loss/no duplicate), duplicate webhook → single business command, concurrent steer on same Work → version conflict retry, invalid state transition rejected with typed error. §26.1 item 3 (duplicated command) basis.
**Documentation:** `docs/security.md` stub for trust/data-plane split (§3.3); ADR note in-code for lock strategy; `.omp/skills/facilitator-architecture` + `facilitator-protocol` updated to implemented reality.
**Architecture fit:** §4, §7.1–7.4, §19, §20 — "the database is the coordination authority." Kernel is the hub; Link/Gateway/adapters later attach at its edges.
**Depends on:** 01
**Additional resources:** spec §4/§7/§19/§20, appendix §5.2–5.3; `Npgsql` docs on `FOR UPDATE SKIP LOCKED`; EF Core 10 + `Testcontainers.PostgreSql`; OTLP wiring baseline (`OpenTelemetry.AspNetCore`) so the slim reference env's Aspire step (now Plans 17/19) is config-only.

### Plan 03 — Link server: SignalR hub and HTTP fallback

**Status:** 🟡 in-progress (detail design + Implementation Status: `03-link-server.md`; 🟢 when CI is green on `main`)
**Objective:** Expose the Facilitator side of the Workspace Link: SignalR hub (`Hello`, `Envelope`, `Receipt`, `Heartbeat` server methods; `Welcome`, `Deliver`, `FlowControl`, `Disconnect` client methods) plus the provider-neutral HTTP fallback (`POST /api/link/v1/events`, `GET /api/link/v1/deliveries?wait&limit`, `POST /api/link/v1/deliveries/{id}/receipt`, `GET /api/link/v1/capabilities`, `POST /api/link/v1/enroll`), handshake/version negotiation, `link_sessions` lease records, and outbox→delivery wiring with `maxInFlight` backpressure.
**Replanning gate:** decide auth interim (identity federation, now Plan 09, lands OIDC; interim: static enrollment tokens bound to a team record) vs building 03 after it — recommended: 03 first with pluggable authenticator seam; token-validation swap later. Decide delivery-cursor semantics (appendix: resume cursor is a hint, receipt ledger is truth).
**Scope:** hub per §5.3 with authenticated principal → endpoint/team binding; session epoch/`leaseUntil` in `link_sessions`; delivery rows generated from outbox for online sessions only (owner-replica routing via LISTEN/NOTIFY hint, §7.3 — single-replica path now, lease fields ready); HTTP fallback identical semantics; dead-letter visibility endpoint.
**State after:** a scripted fake link client can enroll, hello/welcome, receive a queued `work.assign` delivery, post a receipt, reconnect with a new epoch, and replay only un-acked deliveries. §26.1 item 2 basics + receipt-loss (item 3) pass for the server side.
**Testing:** xUnit + SignalR client integration tests: happy handshake, stale resume cursor handled, dropped receipt → redelivery, duplicate redelivery → `duplicate` receipt not re-executed, unknown command → typed `unsupported`. Fixtures consumed from `protocol/conformance-fixtures/`.
**Documentation:** `docs/protocol.md` handshake + HTTP fallback sections with exact JSON (appendix §14 wants normative hello/welcome examples — author them here); `.omp/skills/facilitator-protocol` update.
**Architecture fit:** §5.3–5.4, §6, §7.3. The transport edge of the kernel.
**Depends on:** 02
**Additional resources:** spec §5.3/§5.4/§6, §7.3, appendix §5.6, §10.1 (must-have handshake tests), §16.3 envelope example; SignalR .NET docs (connection affinity, streaming).

### Plan 04 — Workspace Link SDK and runtime (TypeScript)

**Status:** 🟡 in progress (all 7 steps implemented + e2e 6/6 green locally; Implementation Status: `04-workspace-link-sdk.md`; 🟢 when the new `link-e2e` CI job is green on `main`)
**Objective:** Ship `@ofac/workspace-link`: the official TypeScript Link client — SignalR primary with HTTP long-poll fallback, token-factory auth, receipt ledger, envelope helpers — plus a small runtime binary (`linkd`) that connects a workspace outbound, registers a capability manifest, executes injected command handlers, and survives disconnect/reconnect with replay.
**Replanning gate:** pin `@microsoft/signalr` version + fallback polling loop against Plan 03's actual endpoints; decide SDK vs runtime package split (recommend: one package, two entry points); confirm `@ofac/protocol` generated types are consumable by the client without re-mapping.
**Scope:** client state machine (connecting→hello→welcome→ready, heartbeat lease refresh, epoch bump per reconnect), durable local delivery ledger (SQLite via `better-sqlite3`) so a crashed process reconciles receipts on restart, handler interface `onCommand(cmd) => Promise<Accept|Reject>`, `flow_control` honoring maxInFlight, test mode simulating dropped receipts/partitions (appendix §12.3).
**State after:** `linkd` pointed at a dev Facilitator receives a real `work.assign`, invokes a JS handler, and after a kill/restart replays nothing already receipted and redelivers nothing un-acked wrongly.
**Testing:** vitest against a real Facilitator (dockerized via make target): §26.1 items 2–3 client side, plus partition simulation (dropped receipt, duplicated delivery, stale epoch ignored). Shared conformance fixtures run in TS via `ajv`.
**Documentation:** `docs/protocol.md` client section; package README with quickstart.
**Architecture fit:** §5, §6.3 (reconnect/reconcile), appendix §10.2 (SDK scope). First half of the team-owned side of the wire.
**Depends on:** 03
**Additional resources:** spec §5.3/§6.3, appendix §10.2/§12.3; `@microsoft/signalr` docs (accessTokenFactory, reconnect policies); SQLite ledger prior art: outbox pattern in worker processes.

---

## Phase B — Team-owned execution side

**Reorganized 2026-10-24 against Phase A reality** (plans 00–04 as-built). The two original entries were too coarse for single-autonomous-unit execution, so Phase B is four plans, each with its own detail-design file. **Downstream roadmap numbers shift by +2:** roadmap 07 Identity → Plan 09, 08 Policy → Plan 10, 09 Artifacts → Plan 11, 10 Memory → Plan 12, 11 Slim ref-env → Plan 13, 12 Admin SPA → Plan 14, 13–18 → Plans 15–20. Their content is unchanged; later detail-design passes re-derive internals as always. **Renumbered again 2026-10-25 (Phase C split):** the two 2026-10-24 entries (09 Identity, 10 Policy+approvals) were themselves too coarse, so Phase C is four plans (09–12, detail files in the Phase C section); everything from old 11 onward shifts +2: Artifacts → 13, Memory → 14, Slim ref-env → 15, Admin SPA → 16, Providers/Multi-work/Scale/Hardening → 17–22, Ecosystem → 23–25. **Superseded 2026-10-27 (Phase D split +2):** see the Phase D section — 13/14 split into 13–16, everything from 15 onward shifts +2.

### Plan 05 — Gateway runtime core: single embedded child

**Status:** 🟡 in-progress (detail design: `05-gateway-runtime-core.md`)
**Objective:** `@ofac/gateway` composing `@ofac/workspace-link`: one Gateway endpoint (`gw.<slug>`), one embedded child through a serializable executor interface, execution ledger + binding tuple (§6.1) + reconcile-on-restart (§6.3), deny-by-default policy seam. First leg of the original 05.

### Plan 06 — Gateway multiplexing: child transport, shared + standalone modes

**Status:** 🟡 implemented locally — unit + e2e tiers green locally, flips green on CI (detail design: `06-gateway-multiplexing-child-transport.md`)
**Objective:** Unix-socket/localhost child transport, N children, union capability manifest, generation fencing, all three §9.1 modes from one codebase; completes the original 05's "one Gateway serves two children" exit state (§26.1 item 7).

### Plan 07 — HarnessAdapter contract + deterministic worker harness

**Status:** 🟡 implemented locally — all six steps landed: contract + SDK + harness + kit + worker e2e + docs, unit/e2e/docs-check/lint green locally, flips green on CI (detail design + status table: `07-harness-adapter-deterministic-worker.md`)
**Objective:** Public adapter lifecycle contract (§8.3) as schemas + `@ofac/adapter` package + deterministic research-worker reference harness producing a Markdown artifact with result manifest, steer-aware; lifecycle test kit. First half of the original 06.

### Plan 08 — Dev Container Feature family + slim example workspace

**Status:** 🟡 implemented locally — Feature family + payload builder + supervisor + slim composed devcontainer E2E-proven; full Feature matrix is the CI first-run (status table: `08-devcontainer-features-slim-example.md`)
**Objective:** `features/gateway|workspace-link|harness-adapter` (OCI/GHCR, idempotent install, headless supervisor) + `examples/slim/deterministic-research-worker` composed devcontainer; Feature matrix CI. Second half of the original 06; closes Phase B.

**Normalized against Phase A as-built:** Gateway is a composition layer over the Plan 04 SDK (no forked Link client); children ride the Plan 03 server semantics (enrollment rotation, lease/epoch law, `link.*` routing) unchanged; interim trust seams (bootstrap token 02, `ILinkTokenValidator` 03, policy stub 05) are single-extension-point clean-cutover targets for Plans 09/10; `@ofac/*` GHCR/npm publish flip is a Plan 08 gate decision (packages still `private: true`).

---

## Phase C — Trust: identity, policy, approvals

**Split 2026-10-25 against Phase B reality** (plans 00–08 as-built). The single 2026-10-24 entry pair was too coarse — identity alone spans two distinct auth planes (human ingress vs workload link edge), and approvals span two runtimes (kernel ledger vs Gateway flow) — so Phase C is four plans, each with its own detail-design file (the mapping stated there superseded 2026-10-27 by the Phase D note's +2 for everything from 15 onward):

### Plan 09 — Identity foundation: Keycloak realm, claim-pointer mapper, kernel-edge human identity

**Status:** 🟢 implemented (detail design + status table: `09-identity-foundation-oidc.md`)
**Objective:** OIDC cutover of the kernel-token surface (`Auth:BootstrapToken` → Keycloak 26 realm `reference/keycloak/realm.json` + `identity:` claim-pointer config per §11.2 + `(iss, sub)` human identity + `facilitator.audit` spine). Human plane of §11/appendix §3.
**Depends on:** 03

### Plan 10 — Workload identity: enrollment binding, client-credentials option, actorEvidenceRef → effective principal

**Status:** 🟢 implemented (detail design + status table: `10-workload-identity-evidence.md`)
**Objective:** Link-edge trust: real team rosters (no `default` seeding), Gateway-vs-child enrollment roles (§11.3), appendix §3.2 evidence validation producing the effective principal admission audits.
**Depends on:** 09

### Plan 11 — Approval lifecycle: ledger, action digest, atomic single use, revocation

**Status:** 🟢 implemented (detail design + status table: `11-approval-lifecycle.md`)
**Objective:** Kernel half of §12.1/appendix §7: `facilitator.approvals` schema-only → lifecycle authority (record/respond/consume/revoke, kernel-recomputed RFC 8785 digest, lazy expiry, minimal human REST pair, `kernel:` evidence mint).
**Depends on:** 09, 10

### Plan 12 — Gateway team policy engine and confirm→approval round-trip

**Status:** 🟢 implemented (detail design + status table: `12-gateway-policy-approval-loop.md`)
**Objective:** Gateway half of §12: typed allow/deny/confirm YAML policy replacing `DefaultPolicy`'s guts at untouched call-sites, confirm→`approval.required`→consume→re-evaluate→execute loop, cross-resource denial. Closes Phase C (slice-4 exit, §26.1 items 5+8 complete).
**Depends on:** 11

**Phase C closed:** the full spec §12 loop runs across kernel + Gateway —
policy → confirm → `approval.required` (kernel-recomputed digest) → human
respond → decided-push → consume → re-evaluate → execute once — proven
end-to-end by `src/gateway/test/e2e/approval-loop.e2e.test.ts` over real
process + DB boundaries (§26.1 items 5+8 complete).

**Normalized against Phase B as-built:** approval/command/event schemas and the RFC 8785 golden fixtures froze in Plan 01 — `approval.respond` is already a known command and the `approvals` table already exists (schema-only), so Phase C adds behavior, not contract (one additive amendment allowed per plan if a frozen schema proves short, both-harness fixtures same commit); the interim seams are single-extension-point (`IIngressTokenValidator` 09, `ILinkTokenValidator` 10, `policy.ts` call-site-stable seam 12) and are deleted, not shimmed, at cutover; the `default`-team seed, `dev-token`, and `enroll-dev` literals die with 09/10's test cutover (Keycloak testcontainer, realm file as single source of truth).

---

## Phase D — Data services

**Split 2026-10-27 against Phase C reality** (plans 00–12 as-built, Phase C closed with the approval loop proven e2e). The two 2026-10-25 entries (13 Artifacts, 14 Memory) were too coarse — the artifact entry bundled storage backends, the upload-flow trust shift, manifest authority, and the producer cutover, three runtimes (kernel, SDK, harness) and two clean-cutover moments (FS→S3, `file://` inline→kernel-stored) — so Phase D is four plans, each with its own detail-design file:

### Plan 13 — Artifact store core: IArtifactStore, filesystem backend, upload flow, artifact.available wiring

**Status:** 🟢 implemented (detail design + status table: `13-artifact-store-kernel-core.md`)
**Objective:** Kernel artifact authority: `artifacts` ledger + `IArtifactStore` port with FS adapter, `command.artifact.request-upload` + kernel-side content PUT with kernel-recomputed SHA-256 verification, real `event.artifact.available` handling, team-scoped read pair. §26.1 item 12 basis.
**Depends on:** 03

### Plan 14 — S3 artifact backend: SeaweedFS adapter, signed URLs, verified external refs, retention GC

**Status:** 🟢 implemented (detail design + status table: `14-s3-artifact-backend-gc.md`; execution record + divergences: `14_S3_ARTIFACT_BACKEND_GC_PLAN.md`)
**Objective:** S3-wire `IArtifactStore` adapter (SeaweedFS test/dev only), presigned upload/download cutover of the 13 interim, operator-bound external-ref SSRF screen, retention GC pass. Provider behavior is a test matrix, not a fork.
**Depends on:** 13

### Plan 15 — Result manifests, typed outputs, worker artifact cutover, `@ofac/adapter` artifact client

**Status:** 🟢 done (detail design: `15-result-manifests-worker-cutover.md`)
**Objective:** Immutable Result Manifests with revision chains + RFC 8785 digests as kernel records, completed-event amendment (spec §15.3 ref+digest shape), deterministic-worker cutover off the inline/`file://` interim, artifact client in `@ofac/adapter`. Slice-5 exit (artifact half).
**Depends on:** 13, 14

### Plan 16 — Memory broker: scopes, ACLs, retain/recall/reflect over PostgreSQL FTS, adapter memory client

**Status:** 🟢 done (executed against `16_MEMORY_BROKER_PLAN.md`; detail design: `16-memory-broker.md`)
**Objective:** Memory contract layer (§14.3 core ops) as a broker over Postgres FTS: scope ownership + ACL-before-search, frozen candidate→promoted lifecycle, `memory.used` law, per-Project retention, cross-team private block, injection audit; memory client in `@ofac/adapter`. Slice-5 exit (memory half), §26.1 item 11.
**Depends on:** 09, 10

**Phase D closed:** both slice-5 halves run end-to-end — completion truth is
kernel-verified manifest documents (Plan 15), and memory is a brokered service:
retain/recall/reflect over Postgres FTS with ACL-before-search, owner-team law,
candidate→promoted lifecycle, usage attestation, per-Project retention and the
cross-team private block, proven by `src/gateway/test/e2e/memory-broker.e2e.test.ts`
over real Keycloak + Postgres + kernel boundaries (§26.1 items 11+12 complete).

**Normalized against Phase C as-built:** the Phase D wire surface is already half-frozen — `event.artifact.available`, `event.memory.candidate`, `event.memory.used`, `artifactRef`, and an extensible (`additionalProperties:true`) `result-manifest.v1` exist with fixtures, so 13–16 add commands/behavior, not contract (additive amendments follow the Plan 11 precedent, both harnesses same commit); every new inbound command rides 10's admission/evidence plane and 09's role gate with new binding rows, no new auth mechanism; digests split by law — artifacts SHA-256-of-bytes (schema-locked), result manifests RFC 8785 reusing 11's dual-language machinery; artifact/memory ledgers copy the 11 ApprovalLedger template (same-tx audit + outbox), the 13/16 arms copy 11/12's `ApprovalIngress` shape; the kernel never names a vendor (09's standing directive) — SeaweedFS lives in containers only.

**Updated against Plan 15 as-built:** the manifest half landed — 1.1 completed-event ref+digest with the kernel-chained revision ledger (`facilitator.result_manifests`, canonical-bytes law enforced at record time), and the GC reference check strengthened: a recorded manifest pins its document AND every declared output blob regardless of Work state (supersession releases).

**Downstream renumber +2 (2026-10-27):** old 15 Slim ref-env → 17, 16 Admin SPA → 18, 17–22 Phase F → 19–24, 23–25 Phase G → 25–27. `Depends on:` lines and cross-references updated throughout this file. **Superseded 2026-10-28 (+3):** Phase E was split into 17–21 and the +2 targets from 19 onward shifted again; see the Phase E section's note for the current mapping.

---

## Phase E — Reference environment and operator surface

**Split 2026-10-28 against Phase D reality** (plans 00–16 as-built, Phase D closed with manifest authority + memory broker proven e2e). The two 2026-10-27 entries were too coarse — the reference-env entry bundled image builds, compose topology, seeds, a new CLI, an acceptance harness, a CI tier, and a docs rewrite across two runtimes; the admin entry bundled a whole kernel API plane plus an SPA plus a new E2E tier — so Phase E is five plans, each with its own detail-design file, chain 17→18→19 (reference env) then 20→21 (operator surface):

### Plan 17 — Slim Compose, kernel-side: framework images + kernel/infra stack

**Status:** 🟢 implemented (detail design + status table: `17-slim-compose-images.md`)
**Objective:** Source-built node images for gateway/linkd/worker + `reference/compose.slim.yaml` booting Postgres, Keycloak, SeaweedFS, Aspire dashboard, and the Facilitator container, wired verbatim from `versions.env`/`.env.example`, healthchecked; real `make compose-slim`.
**Depends on:** 08, 09, 13, 14, 15, 16

### Plan 18 — Gateway + worker in Compose, bootstrap seeds, referencectl

**Status:** 🟢 implemented (detail design + status table: `18-slim-workspace-seeds-referencectl.md`)
**Objective:** Gateway/worker container (supervisor story, localhost child leg), API-driven demo seeds, `referencectl up slim|down|status`; kernel-URL dev interim dies.
**Depends on:** 17

### Plan 19 — Acceptance run: `referencectl verify`, CI tier, getting-started

**Status:** 🟡 acceptance run + trace propagation + docs landed, verified locally (`verify --json` 11/11 on a clean slim stack); the hosted CI tier is written but commented out (free-tier compute) — the acceptance run is a documented local gate (execution record: `19_SLIM_ACCEPTANCE_VERIFY_CI_PLAN.md`)
**Objective:** §21.1 acceptance script as `referencectl verify` (incl. mid-task restarts + OTLP→Aspire correlation), PR-bring-up/main-nightly-verify dual-arch CI tier, `docs/getting-started.md` to §28. Slice-6 exit, §26.1 item 15 (slim half).
**Depends on:** 17, 18

### Plan 20 — Admin API: §17.4 read models, dead-letter replay, versioned config

**Status:** 🟡 admin surface landed, verified locally (slim `verify` 11/11 + live replay/config proofs; execution record: `20_ADMIN_READ_MODELS_CONFIG_API_PLAN.md`)
**Objective:** `/api/admin/*` server surface — summary/search/detail/timeline/links(trustMode)/deliveries/metrics, dead-letter replay, manifest re-mark, `PATCH config` + `If-Match`; dev-endpoint cutover.
**Depends on:** 19

### Plan 21 — Embedded Svelte inspector + Playwright E2E

**Status:** 🟡 SPA + browser tier landed, E2E green (execution record: `21_ADMIN_SPA_INSPECTOR_E2E_PLAN.md` Implementation Status table)
**Objective:** Svelte 5 SPA embedded in the kernel's `wwwroot` (single deployable): search → inspector → timeline, inline metrics, replay button, minimal §17.3 edit affordances; Playwright .NET against the slim stack. Closes Phase E.
**Depends on:** 20

**Normalized against Phase D as-built:** no compose exists anywhere and only the kernel has a Dockerfile (17 builds the rest, source-first — no npm-publish dependency, 04's deferred image-shape question dies here); the Feature env-overlay names (`OFAC_ENROLL_SECRET`/`OFAC_LINK_TOKEN`/`OFAC_CHILD_SECRET`, `facilitatorUrl`) are the compose env contract, not a new one; 06's open cross-container child-leg question resolves to one container (sidecar decision owned by 18); 15's `kernelUrl`-unset dev path is deleted by 18 (its own kill rule); SeaweedFS config relocates from the .NET-test fixture to compose per 14's replanning caveat; 11's realm-mapper fix + 09's realm are the single identity source, 18 seeds via API only; admin surface starts from zero routes (only `/api/dead-letters` + dev endpoints exist — 20 cuts them over); §26.1 item 15's inspector claim splits: API assertions in 19, browser proof in 21; every live "Lands with Plan 11" doc stub is stale old numbering and is rewritten to fact-based prose by 17/18 (cross-plan no-plan-prose rule).

**Downstream renumber +3 (2026-10-28):** old 19–24 Phase F → 22–27, old 25–27 Phase G → 28–30. `Depends on:` lines and cross-references updated throughout this file. **Superseded 2026-10-29 (+3):** Phase F was expanded into nine plans 22–30; Phase G shifted 28–30 → 31–33. See the Phase F section's note for the current mapping.

---

## Phase F — Providers, multi-work, scale, hardening

**Expanded 2026-10-29 against Phase E reality** (plans 00–21 as-built through the Admin SPA + acceptance run). The six 2026-10-28 entries were too coarse for single-autonomous-unit execution — the Forgejo entry bundled adapter + connector + coding worker + steering across three runtimes, the multi-work entry bundled DAG + generation + nudge + provisioning, and the decision-provider entry presumed a routing engine that never existed (Production-mode assign is seeded outbox SQL against a hardcoded endpoint) — so Phase F is nine plans, each with its own detail-design file, chain 22→23→24→25→26→27→28→29→30:

### Plan 22 — Provider ingest plane + Mattermost adapter: threads, reactions, echo suppression

**Status:** ⬜ not-started (detail design: `22-mattermost-provider-ingress.md`)
**Objective:** Kernel provider-ingress plane (`/api/integrations/{provider}/events`, provider_events+inbox same-tx dedupe, `communication_bindings` woken) + first full adapter (Mattermost TE): mention→Work, thread replies, reaction-ack-never-consent, echo suppression, single-use account linking. §26.1 item 9 basis.
**Depends on:** 19

### Plan 23 — Forgejo adapter + team-owned connector

**Status:** 🟡 implemented locally — 13/13 `make provider-e2e` + slim verify from clean green locally, flips green on CI (detail design + status table: `23-forgejo-adapter-connector.md`)
**Objective:** Forgejo webhooks → normalized events on 22's plane (issue→Work, review→steer intent) + Gateway-side privileged connector (issue read, branch push, PR create — 12's policy/approval flow, read-before-reconcile idempotency) + seed-repository init story.
**Depends on:** 22

### Plan 24 — Coding worker vertical slice (§23.1 complete)

**Status:** ⬜ not-started (detail design: `24-coding-worker-vertical-slice.md`)
**Objective:** `examples/full/coding-worker` on `@ofac/adapter`: chat → Work → assignment → branch + PR via 23's connector → review comment steers the same Assignment. Closes §21.2 steps 2–7. Slice-7 coding half.
**Depends on:** 23

### Plan 25 — Kernel routing: capability matching + decision-provider seam

**Status:** ⬜ not-started (detail design: `25-kernel-routing-decision-seam.md`)
**Objective:** The routing the roadmap assumed since 02: deterministic capability-match assignment creation + addressed dispatch in the kernel, advisory-only `IDecisionProvider` seam (deterministic impl), `routing-defaults` becomes behavior, the hardcoded seed-SQL assign interim dies. §10.1.
**Depends on:** 24

### Plan 26 — Kafka input adapter + Ollama decision provider

**Status:** ⬜ not-started (detail design: `26-kafka-adapter-ollama-decisions.md`)
**Objective:** Kafka KRaft at-least-once input adapter on 22's plane (offsets ≠ business completion, §26.1 item 14 basis) + Ollama `IDecisionProvider` implementation (advisory-only, allow-listed, fallback-tested) + research-worker example. §21.2 steps 8–9.
**Depends on:** 25

### Plan 27 — Dependencies, generation fencing, nudge

**Status:** ⬜ not-started (detail design: `27-dependencies-generation-nudge.md`)
**Objective:** Slice 8's coordination core: assignment `dependsOn` (completed|result_validated|approved) gated deterministically at the dispatch funnel, steer bumps execution generation with old-generation fencing, nudge consumed with rate caps + stalled state. §23.2 two-repo run. §26.1 items 10+13 basis.
**Depends on:** 25, 24

### Plan 28 — Provisioning port + local OCI launcher

**Status:** ⬜ not-started (detail design: `28-provisioning-port-oci-launcher.md`)
**Objective:** §18 provisioning as idempotent outbox commands + team-side docker launcher adapter: workers zero→N→zero, killed worker relaunches on the same assignment with continuity (verify's honest-failure law rewritten), `workload-scale` becomes behavior.
**Depends on:** 27

### Plan 29 — Horizontal replicas: two Facilitators, one Postgres

**Status:** ⬜ not-started (detail design: `29-horizontal-replicas.md`)
**Objective:** §7's claim tested at production shape: loop audit + lease-guarding, replica overlay + sticky-LB topology, duplicate-webhook race, lease handover under SIGKILL, session resume on the survivor. Slice-9 exit, §26.1 item 4.
**Depends on:** 28

### Plan 30 — Operational hardening: conformance package, runbooks, production entry

**Status:** ⬜ not-started (detail design: `30-conformance-runbooks-production-entry.md`)
**Objective:** Close §26.1–26.2: 15-item gap matrix → closed, executable implementation-agnostic conformance CLI with machine-readable report, `referencectl verify --full`, executed incident drills + runbook set (§27), SBOM + digest-pin law (§22.6), v2 negotiation test, plan-prose scrub. Closes Phase F.
**Depends on:** 21, 26, 29

**Normalized against Phase E as-built:** the full-profile services (forgejo/mattermost/kafka/ollama) already boot and are pinned in `versions.env` — 22/23/26 wake them, no new infra; `provider_events`/`communication_bindings` exist schema-only (22 owns them); the assign path is seeded SQL against `link.gw.team-alpha` (25 cuts it over); the free-tier law holds (ref-env CI jobs stay commented — provider/replica/provisioning legs are the documented **local** gate, `make provider-e2e` + `verify`); 19's honest-failure verify step is 28's in-place rewrite target; 20/21's admin surface is 30's re-verification input, not new construction; the stale "Plan 13 behaviour" comment at `Entities.cs:365` and the docs' live plan cites die in 30's scrub.

**Downstream renumber +3 (2026-10-29):** old 28–30 Phase G → 31–33. `Depends on:` lines and cross-references updated throughout this file; this section owns the current mapping.

---

## Phase G — Ecosystem extensibility

The framework's adoption thesis: first-party adapters are ordinary _implementations_ of published contracts, not privileged code — anyone can build their own harness adapter, communication integration, or onboard an existing repo with their own LLM setup. This phase turns those seams into shippable, independently consumable products. Independent of 27–30; 31–32 depend on 15/16 (adapter client surface) and on 22–26 (worked provider/harness examples), 33 on 19.

### Plan 31 — Communication adapter SDK: open protocol for third-party integrations

**Status:** ⬜ not-started
**Objective:** Publish the CommunicationAdapter contract (§13.5) the same way the harness side published `@ofac/adapter`: JSON Schemas for provider-inbound events and adapter capability manifests under `protocol/json-schema/`, a TS SDK package (ingest/reply/react/ack ops, echo suppression, dedupe helpers), and a lifecycle conformance kit. The Mattermost/Forgejo/Kafka adapters (Phase F) are then demonstrably just first implementations — a user's Slack/Discord/GitHub/Jira integration ships as an external package, developed against the published contract, not against repo internals.
**Replanning gate:** extract the de-facto interface from 22/23's shipped adapters (the 26 Kafka leg is the third consumer); fix which ops are core vs optional capability flags; fix the adapter↔kernel ingress path (the provider-events posting plane) and its auth (workload identity from 10).
**Testing:** 22/23's adapters cut over onto the SDK and pass its conformance kit unmodified-except-imports; a synthetic reference "toy provider" adapter written only against published docs+schemas passes the same suite.
**Documentation:** `docs/advanced.md` "write your own integration" walkthrough mirroring the adapter-sdk one; SDK README with minimal example.
**Depends on:** 22, 23

### Plan 32 — Independently pullable harness adapters (Feature + registry story)

**Status:** ⬜ not-started
**Objective:** Each harness adapter is its own folder, its own package, and its own pullable artifact: the `ofac-harness-adapter` Feature's `adapter` option accepts an arbitrary adapter bundle (npm spec or OCI tarball digest) instead of only the baked-in `deterministic`, so any devcontainer composes any adapter — first-party or team-authored — without building this repo. First-party sibling adapters land as worked third-party-shaped implementations of `@ofac/adapter` (one folder each under `src/adapters/harnesses/`): the coding harness family (Pi, OpenCode, Codex-class CLIs) and one local-LLM harness (Ollama-backed) proving the "hook in your own model" story.
**Replanning gate:** fix the bundle contract (entry binary, config env, supervisor unit expectations); decide the trust model for third-party bundles (digest pinning, no postinstall); pick the first two sibling adapters from 24's as-built example and the deterministic harness surface.
**Testing:** Feature scenario test installs a non-baked adapter from a local tarball end-to-end; each sibling adapter passes `@ofac/adapter/testing` and a composed-devcontainer smoke.
**Documentation:** `features/src/harness-adapter/README.md` rewrite for arbitrary bundles; each adapter folder's README as the copy-me template.
**Depends on:** 15, 24

### Plan 33 — Self-service onboarding: adopt-an-existing-repo skill + local-LLM hookup

**Status:** ⬜ not-started
**Objective:** A user (or their agent) onboard a repository by pointing any LLM at a shipped entry document: a canonical `ONBOARDING.md` + agent skill (repo root and published as a standalone pullable doc/skill) that walks enrollment, Dev Container Feature or standalone `linkd` wiring, capability manifest authoring, and hooking the team's own harness + local LLM (Ollama/vLLM/etc.) via the adapter contract. Runbook-driven: the skill drives the _user's_ agent, the framework only ships instructions + CLI.
**Replanning gate:** fix the exact onboarding sequence against the shipped `referencectl`/enrollment UX (reference-env chain); decide skill packaging (in-repo file vs published doc vs both) so it's consumable without cloning.
**Testing:** a fresh agent with only the skill document onboards a scratch repo against slim ref-env and completes one Work end-to-end.
**Documentation:** the skill/`ONBOARDING.md` itself; `docs/getting-started.md` pointer; harness-adapter hookup section cross-linking Plan 32.
**Depends on:** 19

### Plan 34 — Provider-neutral event plane (pre-24 interlude)

**Status:** 🟡 implemented locally — full-repo gates + 13/13 provider-e2e + slim verify from clean green, flips green on CI (status table: `34-provider-neutral-event-plane.md`)
**Objective:** Core protocol + kernel speak only integration DOMAINS (`message|mention|reaction|issue|change_request|pipeline`), closed action enums, provider as `data.provider`; both shipped adapters become pure mapping layers; zero vendor names in schemas/generated types/kernel. A new forge = normalizer + policy + config section, zero core change.
**Sequencing:** lands BEFORE 24 starts (24 must not pin the vendor-shaped wire); this is a clean cutover of 22/23's per-provider event surface, no shims.
**Depends on:** 22, 23

---

## Cross-plan rules

- **Replanning before implementation is mandatory** — status ⬜ never jumps to 🟡; the 🔵 gate produces `.omp/plans/<slug>.md` detail design checked against this roadmap's objective + exit state.
- **Skill/doc co-update:** every plan updates the `.omp/skills/` projections it invalidates in the same change.
- **Fixtures-first testing:** anything asserted about wire behavior must exist as a `protocol/conformance-fixtures/` entry, run by both .NET and TS harnesses.
- **No forward shims:** a later plan may replace an earlier interim (auth tokens in 02/03→09/10, policy stub in 05→12, local artifact FS→S3 in 13→14, FTS→optional vector seam) by clean cutover, not by keeping both paths.
- **Integration placement law (established 22+23, provider-neutral form per 34, binding on every plan that adds an integration):** a provider integration is a **team-domain process dialing out to the link plane**, never a kernel module. Inbound: an adapter that completes transport trust before normalize and publishes **purpose-bearing domain events** (closed central type set + `data.provider`; the adapter owns the provider→purpose mapping); kernel arms stay thin readers of those flags (registry ceiling: options section + domain switch case — no per-provider hosted services or workflow in the kernel). Privileged outbound: a connector as a Gateway fleet child — capability-routed `work.assign`, the Gateway's policy/approval umbrella, a durable action ledger with read-before-reconcile; the external-system credential never leaves that process (the purpose→provider instruction mapping lives in the connector). Reply addressing resolves from the binding's provider section, never a singleton. Facilitator-owned chat is a placement, not a kernel privilege — a team-owned comms adapter must cost only deployment. A new forge is therefore **normalizer + policy + config section with zero core change**; a new domain/action purpose is a deliberate protocol enum widening, never a silent string — anything else is a design smell to raise at the 🔵 gate.
- **No plan prose outside the plan layer:** code, code comments, schemas, and shipped documentation state facts, never delivery sequencing — no plan numbers, phases, stages, or "lands with / until Plan NN" (`.omp/` and `docs/specs/` excepted). Forward-looking notes name the capability ("until admin views exist"), not the schedule.
