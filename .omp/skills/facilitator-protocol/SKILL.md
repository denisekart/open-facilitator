---
name: facilitator-protocol
description: Workspace Link wire contract — CloudEvents envelope, command/event/receipt/control classes, handshake, HTTP fallback endpoints, idempotency keys, fencing generations, and conformance rules for the Open Facilitator Framework.
---

> **Status:** the contract layer is IMPLEMENTED (Plan 01, extended by Plans 03+07): 30 draft-2020-12 schemas in
> `protocol/json-schema/` + `protocol/cloudevents-schemas/`, 61 fixture cases (classes command/event/receipt/control/enroll/delivery + `adapter`) in
> `protocol/conformance-fixtures/` (index.json = arbiter), generated DTOs in
> `src/protocol/src/generated/` (TS) + `protocol/dotnet/Ofac.Protocol/Generated/` (C#),
> `@ofac/protocol` validators, and the `Validate.Fixtures` CLI. The kernel
> enforces the contract at `POST /api/ingress/events`: fail-closed `unknown-command`
> (7-type `KnownCommands` table, embedded schemas via `EnvelopeValidator`) and dedupe on
> the `inbox` table (`(event_source, event_id)`, `command_id`, provider delivery id).
> The Link server is IMPLEMENTED (Plan 03, `src/facilitator/Kernel/Link/`): SignalR hub
> `/hubs/link` (server methods `Hello`/`Envelope`/`Receipt`/`Heartbeat`/`Goodbye` ↔ client
> methods `Welcome`/`Deliver`/`FlowControl`/`Disconnect`), the full HTTP fallback of
> `protocol/openapi/link-v1.yaml` 1.0.1 incl. `POST /api/link/v1/capabilities`,
> enrollment with per-endpoint link tokens (`link_enrollments`), `link_sessions` leases,
> and the durable `link_deliveries` ledger with pump + FlowControl + redelivery.
> Config keys: `Link:EnrollmentToken`, `Link:TokenLifetimeHours` (720),
> `Link:MaxInFlight` (16), `Link:HeartbeatSeconds` (30, 5..600),
> `Link:InFlightLeaseSeconds` (120) — defaults in code (`LinkOptions`), no appsettings
> entry needed. Error URIs extend the kernel table: `unsupported` 422,
> `session-not-found` 404, `delivery-not-found` 404, `capabilities-not-found` 404,
> `team-mismatch` 403, `enrollment-secret-invalid` 401 (`session-superseded` 409 and
> `no-common-version` 409 are reserved HTTP shapes; as-built those arrive as SignalR
> `Disconnect` goodbyes). Interim auth: `ILinkTokenValidator` seam — Plan 07 swaps in
> OIDC + `principal_mappings`, call sites unchanged. The client half is IMPLEMENTED
> (Plan 04, `src/workspace-link` = `@ofac/workspace-link`): core `createLinkClient`
> (state machine idle→connecting→handshaking→ready→reconnecting, delivery pipeline
> law S:301–302 — validate → persist-before-answer → receipt, redelivery converges on
> `duplicate`), consumed hub methods `Hello`/`Envelope`/`Receipt`/`Heartbeat`/`Goodbye`
> + client handlers `Deliver`/`FlowControl`/`Disconnect`; `/node` exports
> `SignalrTransport` (negotiate + WS, bearer header), `HttpPollTransport` (long-poll
> `wait=25` + hidden SignalR session keeper: HTTP principals get no WelcomeFrame, the
> keeper holds the `link_sessions` lease row, a 404 `session-not-found` re-hellos
> before the next poll, and keeper-pushed `Deliver` frames are forwarded into the
> client pipeline — the ledger dedupes push-vs-poll arrival), `SqliteLedger` (replay
> ledger + client outbox buffer),
> `postCapabilities` (digest over exact POSTed bytes, `sha256:` prefix re-added for
> hello). `ofac-linkd` bin: enroll→capabilities→serve, exit codes 0/2/3/4. Client-side
> fixture gate: `test/fixtures.test.ts` runs the same `index.json`. E2E tier:
> `test/e2e/` (env-gated, CI job `link-e2e`, `make link-e2e`). The Gateway is
> implemented (Plans 05+06, `src/gateway`). The HarnessAdapter contract ships as
> `protocol/json-schema/adapter.v1.json` — the normative profile of the 06 child wire
> (NDJSON frames `v:1`+`type`, 256 KiB cap), fixture class `adapter` validated by BOTH
> conformance harnesses; its DTO generation is skipped (`CODEGEN_SKIP` in
> `protocol/tools/codegen.mjs`: quicktype flattens the frame `oneOf` past recovery).
> Implementation: `@ofac/adapter` (`src/adapters/harnesses/adapter-sdk`). Update this
> skill the moment a slice lands.

# Facilitator Protocol

Normative source: `docs/specs/open_facilitator_framework_architecture.md` §5, §6, §7 + `docs/specs/open-facilitator-appendix.md` §5, §7, §16. This skill encodes the rules an implementation must not violate; read the spec chapters before designing anything new.

## When to use

- Implementing or reviewing Link/Gateways/Facilitator messaging, delivery, receipts, or replay.
- Adding a new command/event type, schema, or transport adapter.
- Writing conformance tests or fixtures under `protocol/`.

## Hard rules (violations are bugs)

1. **Envelope = CloudEvents 1.0 structured JSON**; payload validated against an immutable advertised JSON Schema (`dataschema`). No runtime type execution from arbitrary schema versions. Event types are namespaced `org.facilitator.<class>.<name>.v<N>` — the major version is semantic.
2. **Four message classes**, never mix semantics:
   - `command` (work.assign, work.steer, work.cancel, work.status.request, work.nudge, context.fetch, approval.respond) — requests activity, implies no acceptance.
   - `event` (work.accepted, work.progress, assignment.completed, workspace.heartbeat, artifact.available, memory.candidate) — asserted facts to validate.
   - `receipt` (accepted | duplicate | rejected | unsupported) — inbox admission only, **never** work completion.
   - `control` (hello, welcome, heartbeat, flow_control, goodbye) — session mechanics only.
3. **Dedupe identity**: CloudEvents `(source, id)` at the receiver; `commandId` additionally dedupes business-command admission; `UNIQUE (provider, tenantId, providerDeliveryId)` for provider webhooks. Never dedupe on textual content.
4. **Outbox before wire**: persist outbox row atomically with the state mutation; receiver persists inbox row before returning `accepted`. A dropped receipt means redelivery; on redelivery reply `duplicate` and do not re-issue the command.
5. **Unknown command → typed `unsupported` (fail closed)**. Unknown event → retain/quarantine per policy. Never interpret an unknown command as generic work.
6. **Fencing**: every result reports `executionGeneration`; only the current generation may finalize its assignment. Work mutations require matching `workVersion` (optimistic concurrency). Heartbeat refreshes the *link lease*, never assignment progress.
7. **Exactly-once is NOT promised** across external APIs. The contract: transactional local state + at-least-once messaging + idempotent admissions + reconciled side effects. Provider writes without native idempotency keys use a durable connector ledger + read-before-retry; `uncertain` is a legal state.
8. **Approvals**: `actionDigest` = SHA-256 over RFC 8785 canonical JSON (NFC, sorted keys, shortest numerics, no whitespace); approvals carry `nonce` + `expiresAt`, are single-use (`consumed`), revocable, and replay of a stale approval is denied even for byte-identical command JSON.
9. **`actorEvidenceRef` is validated, never opaque**: signature/issuer/expiry + required claims (`sub`, `iss`, `aud`, groups/scopes) before honoring a capability request. LLM text and memory content can never authorize.
10. **Nudge is diagnostic**: `work.nudge` may not re-run costly tools, merge, or deploy; one nudge per `(assignmentId, generation, stallEpisode)`; adapters may advertise `supportsNudge: false`.

## Handshake (as-built, Plan 03)

`hello({protocolVersions, supportedTransports, endpointId, instanceId, capabilityManifestDigest, lastKnownAck})` → `welcome({sessionId, leaseEpoch, maxInFlight, heartbeatSeconds, protocolVersion})`. Client resume cursor is a *hint*; pending deliveries come from the DB receipt ledger (`link_deliveries`) and are pushed immediately after the welcome is minted. Each reconnect = new session generation: the epoch bump supersedes the old session (`Disconnect` goodbye `session-superseded`; stale-epoch heartbeats are fenced). Lease = `heartbeatSeconds*3` clamped ≥ 15 s; the pump sweeper ends sessions on lease expiry, not TCP. **On version mismatch the hub returns `null` from `Hello`** (the invocation has no result payload) after pushing `Disconnect` with the frozen goodbye frame `{reason:"no-compatible-protocol-version"}`; no `link_sessions` row is written — the goodbye's `sessionId` is minted for the frame only.

- SignalR (default reference): hub `/hubs/link`; server methods `Hello`, `Envelope`, `Receipt`, `Heartbeat`, `Goodbye`; server→client `Welcome` (Hello's return value), `Deliver`, `FlowControl`, `Disconnect`. Hub is thin transport — no work execution, no model calls. Bearer auth via the `facilitator-link` scheme (the SAME `ILinkTokenValidator` as HTTP); header-only for our TS clients (S:624), `?access_token=` is the documented WS-only fallback for clients that cannot set upgrade headers. `Deliver` payload = the OpenAPI `Delivery` object verbatim (`link-delivery.v1.json`). FlowControl `{maxInFlight:0}` = pause, restored value = resume; advisory hints — the claim window (`maxInFlight − inFlight`) enforces the cap.
- HTTP fallback (every language must be able to implement this): `POST /api/link/v1/enroll` (enrollment secret in the body; re-enroll rotates the token), `POST /api/link/v1/events`, `GET /api/link/v1/deliveries?wait=25&limit=16`, `POST /api/link/v1/deliveries/{deliveryId}/receipt` (idempotent 204), `GET|POST /api/link/v1/capabilities`. Same semantics as SignalR — one admission/ack code path (`LinkIngress.AdmitAsync` / `LinkLedger.AckAsync`) serves both; both transports pass the same conformance suite. Delivery reads require a live session (404 `session-not-found` before any handshake); capabilities do not.
- Trust mode is declared at enrollment and immutable for the session: `shared-runtime` | `separate-process-same-host` | `isolated-executor`. MUST be logged at enrollment and at every hello (appendix §4). Admin surfaces and audit records MUST show it truthfully.

## Compatibility discipline

- New optional fields are additive; breaking schema/semantics ⇒ new major event version.
- Feature/packaging version is independent from protocol major version.
- Cross-language surface is JSON Schema + OpenAPI 3.1 only — never .NET-serialized types on the wire.
- Conformance fixtures (`protocol/conformance-fixtures/`) are the arbiter for canonical serialization and rejection behavior; new transports/features gate on them.
