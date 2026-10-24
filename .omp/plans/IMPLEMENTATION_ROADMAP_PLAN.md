# Open Facilitator Framework — Master Implementation Roadmap

## Context

The repo is spec-only: `docs/specs/open_facilitator_framework_architecture.md` (normative architecture, §26 defines ten delivery slices) plus `docs/specs/open-facilitator-appendix.md` (normative clarifications: trustMode, actorEvidenceRef, RFC 8785 approval hashing, outbox/inbox, memory ACLs, conformance must-haves). Nothing is implemented. This roadmap is the top-level collection of numbered, autonomous implementation plans in execution order. Early plans establish architecture infrastructure (repo skeleton, protocol contracts, transactional kernel, transport); later plans deliver single vertical feature slices.

**Deliverable of this plan:** create `docs/roadmap.md` containing exactly the plan catalogue below (sections "How to use" + plans 00–20), and nothing else. No code, no scaffold — implementation starts when Plan 00 is executed after its own replanning pass.

## How to use this roadmap

- **Status legend:** ⬜ `not-started` → 🔵 `replanning` (mandatory gate) → 🟡 `in-progress` → 🟢 `done`.
- **Every plan starts with a replanning pass** (status flips to 🔵): re-verify assumptions against the specs and the state left by prior plans, run the listed research, produce a per-plan detail design (`.omp/plans/<slug>.md`), and only then flip to 🟡. Decisions made during earlier plan executions are expected to force local re-optimization here; the roadmap scope/objective stays, the internal detail is re-derived.
- Plans execute **in numeric order** except where a `Depends on` line names a strict subset. Phase B runs 05→06→07→08 in sequence; Plans 09 (identity) and 11 (artifacts) depend only on Phase A and may start any time after 03 is 🟢; 10/12 follow 09; 15∥16∥17 chain within Phase F independently of 18/19.
- Each plan owns its tests and docs in the same change (AGENTS.md skill-maintenance rule applies: update `.omp/skills/` projections in the slice's change).
- Cross-cutting conformance: shared fixtures live in `protocol/conformance-fixtures/` from Plan 01 onward; every later plan adds fixtures for its behavior. Spec §26.1's 15-item suite is the acceptance backlog; each plan below names which items it lands.

---

## Phase A — Architecture foundations

### Plan 00 — Repository skeleton, build system, and CI spine
**Status:** 🟡 in-progress (detail design: `00-repository-skeleton-build-ci.md`; 🟢 when CI is green on `main`)
**Objective:** Turn the spec-only repo into a buildable monorepo matching spec §22.1 (`framework/` tree: `src/facilitator`, `src/gateway`, `src/workspace-link`, `src/adapters`, `protocol/`, `features/`, `examples/`, `reference/`, `docs/`, `Makefile`) with green CI on an empty-but-compiling codebase.
**Replanning gate:** run `/scaffold` (stack `dotnet-aspire-svelte`) and reconcile its output against §22.1; decide sln/csproj granularity (one `Facilitator.sln`, projects `Facilitator`, `Facilitator.Tests`; pnpm workspace for TS packages `@ofac/gateway`, `@ofac/workspace-link`), TS toolchain (Node 22 LTS, vitest, tsup), .NET 10 SDK pin in `global.json`.
**Scope:** directory tree with stub projects/packages that compile; `versions.env` pinning Postgres 17, Keycloak 26.x, SeaweedFS digest, Aspire dashboard release, Kafka 4.2.x, Forgejo 16.x, Mattermost TE, Ollama (§22.4); `Makefile` targets `build|test|lint|compose-slim|compose-full` placeholders; GitHub Actions per §22.6 (build .NET + TS, validate configs, unit tests; compose jobs gated until the slim reference env, now Plan 13); `.editorconfig`, NuGet/npm config, `.gitignore` additions (`reference/secrets/`, `bin/`, `obj/`, `dist/`, `node_modules/`).
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
**Replanning gate:** reconcile §19.1 table sketch against Plan 01 schemas; decide migration tooling (EF Core migrations, one baseline), dispatcher background-service architecture (`IHostedService` + `FOR UPDATE SKIP LOCKED`), and how CLI (`dotnet run --project src/facilitator -- work create ...` vs separate `referencectl` seed) authenticates locally pre-Keycloak (bootstrap shared-token mode, replaced at identity federation, now Plan 09).
**Scope:** `framework/src/facilitator/` ASP.NET Core app: minimal-API endpoints `POST /api/works`, `GET /api/works/{id}`, `POST /api/works/{id}/steer`, `GET /api/works/{id}/timeline`; state-machine guard table as explicit transitions (no implicit jumps); inbox dedupe on `(source,id)` + `commandId`; outbox same-transaction writes + leased dispatcher with attempt/nextAttemptAt/dead-letter; per-Work advisory-lock ordering; local testcontainers fixture.
**State after:** accepted event survives process restart; duplicate business commands are idempotent; a Work can be driven `open→planning→active→review→completed` purely via HTTP with the outbox draining to a stub sink. Spec slice-1 exit test passes.
**Testing:** xUnit + Testcontainers(Postgres 17): restart-after-accepted-event test (kill dispatcher mid-drain, restart, no loss/no duplicate), duplicate webhook → single business command, concurrent steer on same Work → version conflict retry, invalid state transition rejected with typed error. §26.1 item 3 (duplicated command) basis.
**Documentation:** `docs/security.md` stub for trust/data-plane split (§3.3); ADR note in-code for lock strategy; `.omp/skills/facilitator-architecture` + `facilitator-protocol` updated to implemented reality.
**Architecture fit:** §4, §7.1–7.4, §19, §20 — "the database is the coordination authority." Kernel is the hub; Link/Gateway/adapters later attach at its edges.
**Depends on:** 01
**Additional resources:** spec §4/§7/§19/§20, appendix §5.2–5.3; `Npgsql` docs on `FOR UPDATE SKIP LOCKED`; EF Core 10 + `Testcontainers.PostgreSql`; OTLP wiring baseline (`OpenTelemetry.AspNetCore`) so the slim reference env's Aspire step (now Plan 13) is config-only.

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

**Reorganized 2026-10-24 against Phase A reality** (plans 00–04 as-built). The two original entries were too coarse for single-autonomous-unit execution, so Phase B is four plans, each with its own detail-design file. **Downstream roadmap numbers shift by +2:** roadmap 07 Identity → Plan 09, 08 Policy → Plan 10, 09 Artifacts → Plan 11, 10 Memory → Plan 12, 11 Slim ref-env → Plan 13, 12 Admin SPA → Plan 14, 13–18 → Plans 15–20. Their content is unchanged; later detail-design passes re-derive internals as always.

### Plan 05 — Gateway runtime core: single embedded child
**Status:** 🟡 in-progress (detail design: `05-gateway-runtime-core.md`)
**Objective:** `@ofac/gateway` composing `@ofac/workspace-link`: one Gateway endpoint (`gw.<slug>`), one embedded child through a serializable executor interface, execution ledger + binding tuple (§6.1) + reconcile-on-restart (§6.3), deny-by-default policy seam. First leg of the original 05.

### Plan 06 — Gateway multiplexing: child transport, shared + standalone modes
**Status:** ⬜ not-started (detail design: `06-gateway-multiplexing-child-transport.md`)
**Objective:** Unix-socket/localhost child transport, N children, union capability manifest, generation fencing, all three §9.1 modes from one codebase; completes the original 05's "one Gateway serves two children" exit state (§26.1 item 7).

### Plan 07 — HarnessAdapter contract + deterministic worker harness
**Status:** ⬜ not-started (detail design: `07-harness-adapter-deterministic-worker.md`)
**Objective:** Public adapter lifecycle contract (§8.3) as schemas + `@ofac/adapter` package + deterministic research-worker reference harness producing a Markdown artifact with result manifest, steer-aware; lifecycle test kit. First half of the original 06.

### Plan 08 — Dev Container Feature family + slim example workspace
**Status:** ⬜ not-started (detail design: `08-devcontainer-features-slim-example.md`)
**Objective:** `features/gateway|workspace-link|harness-adapter` (OCI/GHCR, idempotent install, headless supervisor) + `examples/slim/deterministic-research-worker` composed devcontainer; Feature matrix CI. Second half of the original 06; closes Phase B.

**Normalized against Phase A as-built:** Gateway is a composition layer over the Plan 04 SDK (no forked Link client); children ride the Plan 03 server semantics (enrollment rotation, lease/epoch law, `link.*` routing) unchanged; interim trust seams (bootstrap token 02, `ILinkTokenValidator` 03, policy stub 05) are single-extension-point clean-cutover targets for Plans 09/10; `@ofac/*` GHCR/npm publish flip is a Plan 08 gate decision (packages still `private: true`).

---

## Phase C — Trust: identity, policy, approvals

### Plan 09 — Identity federation: Keycloak realm, claim mapper, workload identity
**Status:** ⬜ not-started
**Objective:** Replace Plan 02/03 interim auth with real OIDC: Keycloak 26 demo realm (`reference/keycloak/realm.json`), startup-configurable claim-pointer mapping (§11.2 — operator declares where identities/groups/scopes live in tokens), `actorEvidenceRef` validation to claim sets (appendix §3.2 algorithm), separate human/workload/approval identities (§11.1), and workload client-credentials enrollment (§11.3) — an enrolled child cannot claim to be a Gateway.
**Replanning gate:** map the interim token interfaces (`ILinkAuthenticator`, bootstrap shared token) onto OIDC without breaking 03/04 tests; fix claim-pointer config schema exactly; fix evidence-validation steps incl. issuer/audience/time checks and non-authoritative-field handling (appendix §3.3).
**Scope:** `identity:` config block per §11.2 with staged validation; realm import with two teams, groups, client roles; token-factory wiring in TS clients; team-scope checks on capability requests; audit log of claims used per decision.
**State after:** invalid group path → no write permission; co-located `trustMode` visible in enrollment responses; unverified JSON identity fields cannot privilege-escalate. Slice-4 exit test.
**Testing:** integration: Keycloak container + realm import; §26.1 item 5 (incorrect claim path, issuer/audience validation, unmapped users, replayed approval deferred to 10); TS clients refresh rotated short-lived tokens.
**Documentation:** `docs/security.md` identity model + claim-pointer guide; admin notes on claim-mapping staged validation.
**Architecture fit:** §11 + appendix §3. Third identity plane (human/workload/approval) enforced at the kernel edge built in 02/03.
**Depends on:** 03
**Additional resources:** spec §11 + appendix §3; Keycloak 26 admin docs (claim mapping, audiences, token exchange RFC 8693 for later); OIDC + RFC 6901.

### Plan 10 — Team policy engine and approval lifecycle
**Status:** ⬜ not-started
**Objective:** Implement the typed allow/deny/confirm Gateway-side evaluator (§12) and Facilitator-side approval lifecycle: `approval.required` with RFC 8785-canonicalized `actionDigest`, eligible approver set, expiry, nonce, atomic single use, revocation, and the audit record (§12.2) — closing appendix §7 replay protection.
**Replanning gate:** freeze the approval payload canonical form against Plan 01's canonicalization fixtures; decide where the approvals ledger lives (§19.1 approvals table exists from 02 — confirm columns suffice incl. consumed/revoked state); decide default policy DSL (typed JSON predicates, not a language).
**Scope:** `IPolicyEvaluator` port with default allowlist engine consuming team manifest; confirm→pending action→`approval.respond` flow end to end; `approval.revoked` handling; consumers MUST check revocation before applying; cross-resource write denial even when Facilitator proposes wrong capability.
**State after:** replayed/stale approval is denied even with byte-identical command JSON; policy blocks cross-team capability claims. Slice-4 exit + §26.1 items 5 (replay) and 8.
**Testing:** digest golden fixtures (canonical JSON, §16.2 example), single-use race test (two concurrent approvals, one consumes), replay-after-consume denial, revocation-before-apply, deny-by-default policy matrix.
**Documentation:** `docs/security.md` approval lifecycle + audit-record schema; `.omp/skills/facilitator-protocol` receipt/approval update.
**Architecture fit:** §12 + appendix §7. Completion of the trust plane; prerequisite for any privileged provider write (Plans 15/16).
**Depends on:** 09
**Additional resources:** spec §12 + appendix §7; RFC 8785; canonical-json test vectors.

---

## Phase D — Data services

### Plan 11 — Artifacts, result manifests, and artifact store
**Status:** ⬜ not-started
**Objective:** Implement `IArtifactStore` (`Put/Get/List/Delete/VerifyDigest`, §15.4) with local-FS and S3 (SeaweedFS) backends, signed upload-URL flow (appendix §8.2 `POST /api/artifacts/request-upload`), immutable Result Manifests with revision chains (§15.3), and typed outputs referenced from `assignment.completed`.
**Replanning gate:** fix manifest schema fields against Plan 01 schemas (mediaType, size, digest, storageUrl, revisionOf); fix digest algorithm (SHA-256) and external-reference trust rules (verified external ref vs unverified URL, SSRF guard).
**Scope:** artifact endpoints + Link command adapter for workers; outbox-linked `artifact.available` event; access-policy rows; large-payload rule: Links never carry artifacts inline.
**State after:** a research assignment's Markdown report survives worker destruction, is retrievable by digest, and the Work timeline shows the manifest revision. Slice-5 exit (artifact half).
**Testing:** digest verification, revision chain immutability, unauthorized access 403, invalid external URL + SSRF, upload-then-complete integration with 04/05 clients. §26.1 item 12.
**Documentation:** `docs/protocol.md` artifacts section; appendix §8.3 manifest example promoted to fixture.
**Architecture fit:** §15 + appendix §8 — durable typed outputs as the completion truth, decoupled from execution containers.
**Depends on:** 03
**Additional resources:** spec §15, appendix §8; S3 API docs (SeaweedFS `weed mini`); IETF CID content-addressing prior art (optional).

### Plan 12 — Memory broker: scopes, ACLs, retain/recall/reflect contract
**Status:** ⬜ not-started
**Objective:** Implement the memory contract layer (§14.3 minimal ops `retain/recall/reflect/curate`) over PostgreSQL FTS with scope ownership + ACL intersections, policy labels (`private|team-only|facilitator-visible|evidence-only`), `memory.used` usage events (appendix §6.2), retention-per-Project, and memory-scope sharing grants — a broker, not a product; Hindsight stays an optional seam.
**Replanning gate:** map §14.2 scope model + appendix §6.1 required entry fields onto 02's schema (new `memory_entries`/`memory_scopes` tables via migration); fix FTS strategy (tsvector + trigger, pgvector seam deferred); fix candidate→promoted lifecycle (§14.6) against the kernel's event loop.
**Scope:** HTTP/OpenAPI + Link command adapter; `scopeSelectors` treated as hints intersected with verified grants; injection audit (§14.9: entries actually injected into a decision); cross-team private-memory block.
**State after:** `memory.candidate` from Plan 07's worker becomes a promotable entry; cross-team recall cannot see private entries; deletion purges the FTS index. Slice-5 exit (memory half).
**Testing:** scope-ACL matrix (home scope vs read grants), correction/revocation/delete lifecycle, `memory.used` emitted on every automated read, retention enforcement. §26.1 item 11.
**Documentation:** `docs/advanced.md` memory model + Hindsight seam; `.omp/skills/facilitator-architecture` memory section update.
**Architecture fit:** §14 + appendix §6 — lower-trust searchable knowledge, never an authorization source (§3.3).
**Depends on:** 09
**Additional resources:** spec §14 + appendix §6; Postgres FTS docs; Hindsight repo (mapping sketch only).

---

## Phase E — Reference environment and operator surface

### Plan 13 — Slim reference environment: Compose, referencectl, Aspire
**Status:** ⬜ not-started
**Objective:** Make §21.1's slim acceptance run reproducible: `reference/compose.slim.yaml` (Facilitator, Postgres, Keycloak, SeaweedFS, Aspire dashboard, embedded Gateway+worker container), `reference/config/*.yaml`, bootstrap seeds, and the `referencectl` CLI (`up slim|down|verify`) — the framework's single-command demo.
**Replanning gate:** validate §22.2's template against the actually-built images/config surface from 02–12; pin `.localhost` hostname behavior per §22.5 preflight; decide seed data set (demo team, capability manifest, deterministic worker fixture).
**Scope:** image Dockerfiles (slim runtime), env wiring from `versions.env`, healthchecks, `referencectl verify` running the acceptance script (login → create Work → link dispatch → deterministic harness → artifact → memory candidate → trace correlation → restart mid-task).
**State after:** fresh clone → `referencectl up slim` → §21.1 acceptance run passes unattended. Slice-6 exit test.
**Testing:** CI job brings up slim Compose on linux/arm64+amd64 runners and executes `referencectl verify`; OTLP spans visible in Aspire checked via dashboard API. §26.1 item 15 (slim half).
**Documentation:** `docs/getting-started.md` completed to spec §28 (exact CLI/curl + expected output, port map, budget, cleanup).
**Architecture fit:** §21.1/§22 — the reference proves the architecture; every prior plan gains its first end-to-end integration harness.
**Depends on:** 08, 09, 10, 11, 12
**Additional resources:** spec §21.1/§22.2/§22.5/§28, appendix §11 (one-command dev loop); compose `healthcheck`/`depends_on: condition` docs; Aspire dashboard OTLP ingestion docs.

### Plan 14 — Admin SPA: embedded single-page inspector
**Status:** ⬜ not-started
**Objective:** Build the embedded Svelte 5 + Tailwind + shad/svelte admin app (§17): universal search, health strip, contextual inspector for Work/Gateway/workspace/artifact/memory objects, activity timeline, inline metrics (§17.2), `PATCH /api/admin/config/{resource}` with `If-Match` optimistic revision (§17.4), trustMode displayed per enrollment (appendix §4.3), dead-letter replay control (appendix §11.2).
**Replanning gate:** confirm the read-model endpoints needed beyond §17.4's list (timeline, deliveries, metrics) against 02–12's actual tables and add them to the Facilitator in the same slice; fix build/embed pipeline (SPA built into the Facilitator's `wwwroot`, single deployable).
**State after:** operator can trace a slim acceptance-run Work from search to artifact to trace link without psql.
**Testing:** Playwright .NET E2E against slim Compose: search a Work, open timeline, replay a forced dead-letter, verify trustMode badge; API tests for If-Match conflicts.
**Documentation:** `docs/getting-started.md` admin tour section; SPA README.
**Architecture fit:** §17 — operational inspector over the kernel, never a second source of truth.
**Depends on:** 13
**Additional resources:** spec §17 + appendix §4.3/§11.2; `svelte` profile skills; shadcn/svelte docs; playwright-dotnet + Aspire integration test patterns.

---

## Phase F — Providers, multi-work, scale, hardening

### Plan 15 — Mattermost adapter: threads, reactions, echo suppression
**Status:** ⬜ not-started
**Objective:** First full provider adapter (spec slice 7, first leg): Mattermost Team Edition service in full Compose, inbound event verification → normalized inbox (`provider_events` dedupe per §7.1), `communication_bindings` thread tracking, verified account linking via single-use challenge (§11/§21.2 — never email-equality), threaded replies, low-noise reaction ack with idempotency ledger, bot echo suppression (§13.4), reaction-never-consent rule (§13.3).
**Replanning gate:** pin adapter interface to 02's inbox port + 10's approval flow (reaction ≠ approval path explicit); confirm Mattermost TE bot-account API version tested; fix echo-suppression matching on exact provider actor IDs.
**Scope:** `src/adapters/mattermost/` implementing the §13.5 CommunicationAdapter ops it supports (receive/reply/react/updateMessage/acknowledge + capability flags); outbound notificationId metadata; `docs`-driven reply fallbacks.
**State after:** `@facilitator` mention in a thread creates/links a Work and the completion reply lands in the same thread; Facilitator's own posts never re-trigger processing.
**Testing:** adapter contract tests + recorded-fixture tests; live-container E2E in full Compose; §26.1 item 9 (echo suppression, thread replies).
**Documentation:** `docs/advanced.md` adapter authoring walkthrough (first worked example).
**Architecture fit:** §13 — thin provider adapter at the kernel's edge; team-owned writes stay behind Gateway policy (10).
**Depends on:** 13 (14 optional)
**Additional resources:** spec §13 + appendix §10; Mattermost REST API v4 docs; Forgejo webhook docs as contrast for Plan 16.

### Plan 16 — Forgejo adapter + coding worker vertical slice
**Status:** ⬜ not-started
**Objective:** Full-profile coding leg: Forgejo (VCS + issues, one deployment) webhooks → normalized events; Gateway-side team-owned Forgejo connector (issue read, branch push, PR create — all through Gateway policy + approval when privileged, §9.2); coding-worker harness example (`examples/full/coding-worker`) completing §23.1's sequence: chat → Work → assignment → feature branch + PR → review comment steers the same Assignment (or relaunches a scaled-to-zero worker).
**Replanning gate:** decide issue-ID→Work binding ownership (provider_events + communication_bindings reuse); fix connector idempotency strategy per appendix §5.4–5.5 (canonical payload hash + read-before-reconcile for PR creation); fix worker lifecycle vs 18's provisioning seam (worker may stay long-lived here, scale-to-zero deferred — state it).
**Scope:** `src/adapters/forgejo/`, `examples/full/seed-repositories/`, webhook wiring to `/api/integrations/forgejo/webhook`, review-comment steering to running assignment, PR-URL bound to Work as artifact reference (11).
**State after:** §21.2 demo steps 2–7 run in full Compose. Slice-7 coding half.
**Testing:** webhook dedupe, PR-create idempotent retry (ambiguous timeout → reconcile), steering routing to live vs relaunched worker; live full-Compose E2E script.
**Documentation:** `docs/advanced.md` provider-connector privilege ceilings; seed-repo README.
**Depends on:** 15
**Additional resources:** spec §9.2/§13.6/§23.1, appendix §5.4–5.5 (connector idempotency, GitHub-style prior art applies); Forgejo webhook/API docs.

### Plan 17 — Kafka adapter + Ollama decision provider
**Status:** ⬜ not-started
**Objective:** Complete full-profile parity (§21.2 steps 8–9, 4): a Kafka KRaft input adapter (at-least-once consume → normalized inbox events, offsets never treated as business completion, §7.5) and the `IDecisionProvider` (§10.2) with a deterministic stub plus Ollama-backed implementation for routing disambiguation among allowed advertised candidates — model output is advisory only.
**Replanning gate:** fix decision-provider call sites against the routing evaluation steps (§10.1) actually implemented in 02/09; bound model latency/limits; fix Kafka consumer group/offset governance vs inbox dedupe overlap.
**Scope:** `src/adapters/kafka/`, `examples/full/research-worker` on the non-code path, Ollama container + permissive small model, decision audit recording the allowed-candidate set + chosen candidate + model reference.
**Testing:** at-least-once redelivery test (no double business command, §26.1 item 14); decision provider contract tests incl. unauthorized-candidate rejection and model-down fallback to deterministic-first behavior.
**Documentation:** `docs/advanced.md` model-advisory boundary + Kafka seam rationale (§7.5 "introduce when measurements show need").
**Architecture fit:** §7.5/§10.2/§13 — proving the seam points: broker and model are optional adapters around the Postgres authority.
**Depends on:** 16 (15 sufficient if 16 slips)
**Additional resources:** spec §7.5/§10/§13.6/§21.2; Kafka KRaft single-node compose examples; Ollama API docs.

### Plan 18 — Multi-work, dependencies, and zero-to-N provisioning
**Status:** ⬜ not-started
**Objective:** Deliver spec slice 8: Assignment `dependsOn` with conditions (`completed|result_validated|approved`, §4.2), one Work spanning two repositories/two independent executions (§23.2), steering that restarts a transient worker with context continuity, and the provisioning port (§18) with a local OCI launcher so workers scale zero→N and back.
**Replanning gate:** fix DAG evaluation as pure deterministic kernel step (no AI), placement in 02's event loop; fix provisioning contract (`provisioningOperationId` idempotency per §7.4) vs Gateway's child lifecycle from 05; fix nudge semantics + rate caps (§6.4, §23.5) with stalled-vs-offline reconciliation.
**Scope:** dependency gating in dispatcher, `work.steer` → execution generation bump with old-generation fencing (§7.4), provisioner adapter starting/stopping example worker containers, context re-hydration via artifacts + memory.
**State after:** §23.2's two-repo sequence runs; killing a mid-flight worker container relaunches it on the same Assignment without lost context or duplicate side effects.
**Testing:** two-assignment DAG (one blocked on `result_validated`), steer-during-run generation fencing test, launcher idempotency, nudge cap test (§26.1 items 10, 13).
**Documentation:** `docs/advanced.md` dependencies + provisioning contract.
**Architecture fit:** §4.2/§18/§23.2 — the scaling story on the execution side, still kernel-first.
**Depends on:** 16
**Additional resources:** spec §4.2/§6.4/§18/§23.2/§23.5; OCI runtime CLI docs for the local launcher.

### Plan 19 — Horizontal replicas: two Facilitators, one Postgres
**Status:** ⬜ not-started
**Objective:** Prove §7 at production shape: two Facilitator replicas behind the shared Postgres — inbox dedupe under concurrent duplicate webhooks, outbox lease handover on replica crash, `link_sessions` owner routing (LISTEN/NOTIFY hints, polling fallback), SignalR sticky-session guidance, and `workVersion` optimistic-conflict retries under a two-replica race.
**Replanning gate:** audit 02/03 for hidden singleton assumptions (in-process caches, timers, hub state); decide compose topology (`compose.full.yaml` replica overlay vs separate file per §22's "one tested method" rule); fix crash-injection method for CI.
**Scope:** replicaId/endpointId wiring, `referencectl` full-profile multi-replica mode, load-shed/backpressure defaults (§7.5).
**State after:** concurrent duplicate webhook hits both replicas → one business command; SIGKILL a replica owning a live link → session lease expires, other replica's reconnected session resumes deliveries. Slice-9 exit test.
**Testing:** dual-replica integration: duplicate-webhook race, outbox lease recovery, reconnect-epoch fencing, §26.1 item 4.
**Documentation:** `docs/advanced.md` replication + SignalR affinity; runbook updates (begin Plan 20 doc set).
**Architecture fit:** §7 — "connections are transient delivery optimizations, the database is the authority" becomes a tested claim.
**Depends on:** 18
**Additional resources:** spec §7; SignalR scale-out docs (affinity, `IUserIdProvider`); Postgres advisory-lock + LISTEN/NOTIFY docs.

### Plan 20 — Operational hardening: dead letters, runbooks, conformance suite, production entry
**Status:** ⬜ not-started
**Objective:** Close §26.1–26.2: complete the shared conformance suite as an executable package (all 15 items green across Facilitator/Gateway/Link), administration audit trail + versioned preference changes (§17.1), dead-letter UX (replay button wired in 14 verified here), full runbook set (§27), backup/restore + upgrade/rollback drills, and the incident exercise: DB restart, provider outage, invalid token replay.
**Replanning gate:** diff §26.1's 15 items against fixtures/tests existing from 01–19; enumerate the gaps and assign each to a harness (conformance package vs E2E); fix what "production-eligible" checks become automatable (`referencectl verify --full`).
**Scope:** conformance-suite CLI (run against any conformant implementation, not just this one), bad-provider fault-injection fixtures, SBOM + digest-pinned release job (§22.6), version-negotiation test (protocol v1 vs a simulated v2 link).
**State after:** `referencectl verify` full profile passes from empty volumes; conformance package publishes a machine-readable pass/fail report; runbooks validated by executing the incident drill.
**Testing:** the suite itself is the deliverable; drill logged in runbooks.
**Documentation:** `docs/security.md` + runbook chapter (§27) finalized; `docs/getting-started.md` re-verified word-for-word by a clean-clone run; all `.omp/skills/` updated to implemented reality (AGENTS.md maintenance rule).
**Architecture fit:** §26 — the conformance gate that turns "reference implementation" into "conformant framework," and §29.5's final statement made testable.
**Depends on:** 14, 17, 19
**Additional resources:** spec §26.1/§26.2/§27/§28/§22.6, appendix §9 (metrics/runbook snippets), §10.1 (must-have conformance list), §15 (MVP checklist).

---

## Cross-plan rules
- **Replanning before implementation is mandatory** — status ⬜ never jumps to 🟡; the 🔵 gate produces `.omp/plans/<slug>.md` detail design checked against this roadmap's objective + exit state.
- **Skill/doc co-update:** every plan updates the `.omp/skills/` projections it invalidates in the same change.
- **Fixtures-first testing:** anything asserted about wire behavior must exist as a `protocol/conformance-fixtures/` entry, run by both .NET and TS harnesses.
- **No forward shims:** a later plan may replace an earlier interim (auth tokens in 02/03→09, policy stub in 05→10, local artifact FS→S3 in 11, FTS→optional vector seam) by clean cutover, not by keeping both paths.
