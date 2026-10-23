---
name: facilitator-protocol
description: Workspace Link wire contract — CloudEvents envelope, command/event/receipt/control classes, handshake, HTTP fallback endpoints, idempotency keys, fencing generations, and conformance rules for the Open Facilitator Framework.
---

> **Status:** the framework is unimplemented. Every schema, endpoint, and path named below is a spec-proposed target, not existing code. Update this skill the moment a slice lands so implemented reality replaces spec proposals.

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

## Handshake (must match)

`hello({protocolVersions, supportedTransports, endpointId, instanceId, capabilityManifestDigest, lastKnownAck})` → `welcome({sessionId, leaseEpoch, maxInFlight, heartbeatSeconds, protocolVersion})`. Client resume cursor is a *hint*; pending deliveries come from the DB receipt ledger. Each reconnect = new session generation.

- SignalR (default reference): hub methods `Hello`, `Envelope`, `Receipt`, `Heartbeat`; server→client `Welcome`, `Deliver`, `FlowControl`, `Disconnect`. Hub is thin transport — no work execution, no model calls.
- HTTP fallback (every language must be able to implement this): `POST /api/link/v1/enroll`, `POST /api/link/v1/events`, `GET /api/link/v1/deliveries?wait=25&limit=16`, `POST /api/link/v1/deliveries/{deliveryId}/receipt`, `GET /api/link/v1/capabilities`. Same semantics as SignalR; both transports pass the same conformance suite.
- Trust mode is declared at enrollment and immutable for the session: `shared-runtime` | `separate-process-same-host` | `isolated-executor`. Admin surfaces and audit records MUST show it truthfully.

## Compatibility discipline

- New optional fields are additive; breaking schema/semantics ⇒ new major event version.
- Feature/packaging version is independent from protocol major version.
- Cross-language surface is JSON Schema + OpenAPI 3.1 only — never .NET-serialized types on the wire.
- Conformance fixtures (`protocol/conformance-fixtures/`) are the arbiter for canonical serialization and rejection behavior; new transports/features gate on them.
