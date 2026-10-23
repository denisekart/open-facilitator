---
name: facilitator-architecture
description: Domain model, ownership boundaries, state machines, durable coordination (Postgres outbox/inbox, SKIP LOCKED leases, session placement), and design-gate questions for the Open Facilitator Framework.
---

> **Status:** the framework is unimplemented. Every table, interface, and state named below is a spec-proposed target, not existing code. Update this skill the moment a slice lands so implemented reality replaces spec proposals.

# Facilitator Architecture

Normative source: `docs/specs/open_facilitator_framework_architecture.md` §2–§4, §7, §9–§12, §15, §19–§20; appendix §2–§4. Read the relevant chapter before changing a boundary — component ownership is prescriptive, not stylistic.

## When to use

- Placing new code: deciding Facilitator vs Gateway vs Workspace vs adapter responsibility.
- Designing schema/state changes, routing, memory, artifact, or approval features.
- Reviewing PRs that touch coordination or privilege placement.

## Ownership law (spec §2.1)

| Concern | Owner |
|---|---|
| Normalize events, resolve Work, communicate externally | Facilitator |
| Execute implementation/research/testing; pick models & skills | Workspace/harness |
| Authorize privileged writes (SCM/issues/deploy) | Gateway (or workspace in direct mode) — Facilitator does admission only |
| Authoritative work state | Facilitator PostgreSQL |
| Team-private memory | Team; Facilitator only when explicitly delegated |
| Start/stop workers | Gateway provisioner / external scheduler; Facilitator records status only |

The Facilitator holds **no general SCM/issue/deploy credential** (D-10). It may hold only a narrow chat-bot identity for thread replies/reactions. Anything that writes outside the framework runs under team-owned credentials behind team policy.

## State machines (spec §4.1) — do not invent shortcuts

- Work: `open → planning → active → review → completed` (+ `blocked|cancelled|failed`); closure independent of execution completion.
- Assignment: `proposed → routed → accepted → running → input_required|completed|failed|cancelled|expired`.
- Execution: `starting → ready → working → stopping → stopped|failed|lost`.
- Result: `missing → submitted → validated|rejected → superseded`.
- Approval: `pending → approved|denied|expired|revoked|consumed`.
- Delivery: `pending → leased → sent → durably_acknowledged|rejected|dead_letter`.

Invariants: one *current* execution generation per assignment; assignment-scoped events carry `workId`+`assignmentId`; completion event never publishes its artifact; parent Work closes only per its completion policy. Four output classes are distinct states: execution status, result state, review status, delivery/publication state — never conflate "agent finished" with "business accepted".

## Coordination pattern (PostgreSQL-first)

- Ingress: verify signature/origin → insert `provider_events`/inbox transactionally (dedupe = success, no processing).
- Claim due rows `FOR UPDATE SKIP LOCKED` with `leaseOwner/leaseUntil/attempt/nextAttemptAt`; serialize per-Work via row lock or advisory lock keyed by Work ID in a **short** transaction; persist new state + outbox rows **atomically**; call providers only after commit, outside the transaction.
- Session placement: `link_sessions(endpointId, connectionInstanceId, replicaId, sessionEpoch, leaseUntil)`; only the session owner (epoch match) may deliver; LISTEN/NOTIFY is a wake hint, never the event store. No Redis backplane, no Kafka, no Temporal in the slim reference — those are adapter seams (D-16).
- Memory default = `postgres-fts` (GIN/tsvector). Memory is a context supplement: never consulted for authn/authz/approval; recall budget-bounded; injection framed as untrusted reference material; missing authorization excludes the whole scope (no post-filter-by-LLM).

## Extension seams (implement one of these, or don't exist)

`LinkTransport`, `HarnessAdapter`, `CapabilityProvider`, `ExecutionProvisioner`, `MemoryProvider`/`IMemoryBroker`, `ArtifactStore`, `CommunicationAdapter` (receive/read/reply/react/discover — provider ops are OPTIONAL per adapter, discovered not assumed), `IDecisionProvider` (only the **authorization** evaluator may allow/deny, and it must be deterministic), `IWorkCoordinator` (Temporal/Orleans behind it, optional).

## Design gate for any new component (spec §25.3)

Answer all: which contract does it implement; does it change team onboarding; does it add an always-on container; what identity/data can it touch; does it degrade offline/local; version compatibility path; at-least-once/idempotency behavior; recovery with a transient workspace; would a thin provider adapter have sufficed.

## Capability routing (spec §10)

Deterministic first: verified resource bindings → authorized candidate set (capability id/version, schemas, resources, availability, team admission) → decision table. Model sees **only** the candidate list + bounded `llmContext` excerpts and may return only candidate IDs; the selection is re-validated independently. Descriptive text never expands permissions; ambiguity ⇒ clarification, never speculative execution.
