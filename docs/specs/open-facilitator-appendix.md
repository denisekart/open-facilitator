# Appendix: Clarifications, Steering, and Implementation Guidance  
**For:** Open Facilitator Framework — Architecture and Reference Implementation Specification  
**Purpose:** Supplement the main specification with concrete clarifications, operational steering, missing artifacts, user stories, and implementation guidance. This appendix is intended to be distributed alongside the original spec as a living addendum for implementers, integrators, and early adopters.

> Two-sentence excerpt from the original specification:
> “Deploy one persistent Facilitator; let any team register a Project, connect its existing tools and optionally attach one or more Gateways or Workspaces. The Facilitator handles external communication, durable context, work breakdown, routing, human interaction, optional reasoning, governed memory and artifact references.”

---

## Table of contents

1. Goals of this appendix  
2. Executive clarifications (normative)  
3. Identity, authorization and provenance (detailed)  
4. Trust modes, co-location and runtime guarantees  
5. Delivery, outbox/inbox, idempotency and connector patterns  
6. Memory model, ACLs and evidence semantics  
7. Approval hashing, canonicalization and replay protection  
8. Artifact handling and large payload patterns  
9. Observability, metrics and runbooks  
10. Conformance, test harness and SDK guidance  
11. Developer experience and onboarding (CLI, seed data, local loop)  
12. Personas and role-specific guidance (several voices)  
13. User stories and prioritized use cases  
14. Suggested text edits and additions to the spec (diff-style)  
15. Acceptance criteria and minimal viable conformance checklist  
16. Appendix: canonical JSON examples and deterministic rules  
17. References and further reading

---

## 1. Goals of this appendix

- **Clarify** ambiguous or underspecified behaviors that are critical for secure, interoperable implementations (identity mapping, trust modes, approvals, memory usage).
- **Steer** implementers toward robust, battle-tested patterns (outbox/inbox, idempotent connectors, artifact transfer).
- **Provide** concrete developer ergonomics: local dev loop, SDKs, conformance tests, and runbooks.
- **Enrich** the spec with user stories and persona-driven guidance so product and engineering teams can prioritize features.
- **Ship** a downloadable, copy-pasteable markdown that can be appended to the original spec.

---

## 2. Executive clarifications (normative)

- **TrustMode is required.** Every Gateway and Workspace enrollment record **MUST** include a `trustMode` field with one of: `shared-runtime`, `separate-process-same-host`, `isolated-executor`. The Facilitator **MUST** expose this value in the admin UI and in enrollment responses.
- **Actor evidence validation is normative.** `actorEvidenceRef` is not an opaque string: the Facilitator **MUST** validate it to a set of claims (IdP groups, certificate SAN, token scopes) before honoring capability requests.
- **Approval hashing canonicalization is required.** Approvals **MUST** be computed over canonical JSON (RFC 8785 or a deterministic canonicalization) and hashed with SHA-256. The spec must include the canonicalization algorithm and an example.
- **Memory entries require provenance and ACLs.** Every `MemoryEntry` **MUST** include `provenance` and `accessControl` metadata. Memory **MUST NOT** be used as an authorization source.

---

## 3. Identity, authorization and provenance (detailed)

### 3.1 Problem statement
The spec separates identity provenance from actor evidence but leaves the mapping underspecified. Implementations need a deterministic algorithm to avoid privilege escalation and inconsistent decisions across replicas.

### 3.2 Normative mapping algorithm (recommended)
1. **Authentication layer:** Link authenticates using one of: short-lived bearer token (OIDC), mTLS client cert, or platform token. The Facilitator resolves the principal to an **enrollment identity** (e.g., `gateway:team-alpha:instance-1`).
2. **Evidence resolution:** If `actorEvidenceRef` is present, the Facilitator fetches the referenced evidence (signed assertion, IdP token, or certificate chain) and validates:
   - Signature validity and issuer trust chain.
   - Expiry and `nbf`/`iat` claims.
   - Required claims: `sub`, `iss`, `aud`, `groups` or `scopes`.
3. **Effective principal derivation:** Combine the Link-authenticated principal and validated evidence claims to derive the **effective principal**. Rules:
   - If evidence is signed by a trusted IdP and includes `delegation: true`, the evidence can extend the Link principal's capabilities only if the Link principal is authorized to accept delegated claims.
   - Otherwise, the Link principal's identity is authoritative.
4. **Authorization decision:** Evaluate the effective principal against:
   - Project-level allow lists (explicit capability grants).
   - Team-level policies (deny-by-default).
   - Gateway-local policy (if Gateway is owner of the action).
5. **Audit event:** Emit `authorization.evaluated` with `effectivePrincipal`, `evidenceRef`, `decision`, `policyVersion`, and `timestamp`.

### 3.3 Claims and authoritative fields
- **Authoritative claims:** `sub`, `groups`, `scope`, `exp`, `iss`, `aud`, `cn` (from cert SAN).
- **Non-authoritative fields:** free-form JSON fields in evidence payloads; they may be recorded for provenance but not trusted for authorization.

### 3.4 Example: capability resolution
- A Link authenticates as `gateway:team-alpha`. It receives `work.assign` with `actorEvidenceRef` pointing to an IdP token claiming `group:researchers`. The Facilitator:
  - Validates token signature and issuer.
  - Confirms `gateway:team-alpha` is allowed to accept `group:researchers` claims for the target `capabilityId`.
  - Derives effective principal `team-alpha:researchers` and evaluates policy.

---

## 4. Trust modes, co-location and runtime guarantees

### 4.1 TrustMode semantics (machine-readable)
- **`shared-runtime`**
  - **Guarantee:** Gateway and workspace share OS-level resources; no isolation guarantees.
  - **Facilitator assertion:** "This runtime may access team secrets and local filesystem; treat as high-trust."
  - **Operator guidance:** Use only for trusted, single-team devcontainers.
- **`separate-process-same-host`**
  - **Guarantee:** Processes are separate; OS-level isolation may be limited (namespaces, user separation).
  - **Facilitator assertion:** "Process-level separation exists; secrets may be scoped but not fully isolated."
  - **Operator guidance:** Use for semi-trusted workloads.
- **`isolated-executor`**
  - **Guarantee:** Strong isolation (VM, container with strict seccomp, separate user namespaces).
  - **Facilitator assertion:** "Executor is isolated; do not assume access to host secrets."
  - **Operator guidance:** Required for untrusted third-party code or multi-tenant workloads.

### 4.2 Enrollment record example
```json
{
  "gatewayId": "gateway-team-alpha",
  "instanceId": "gw-01",
  "trustMode": "separate-process-same-host",
  "capabilityManifestDigest": "sha256:abcd...",
  "enrolledAt": "2026-10-22T18:00:00Z"
}
```

### 4.3 Admin UI and audit
- The admin UI **MUST** display `trustMode` and a human-readable explanation.
- The Facilitator **MUST** log `trustMode` at enrollment and on every reconnect.

---

## 5. Delivery, outbox/inbox, idempotency and connector patterns

### 5.1 Core principles (summary)
- Persist state and outbox in the same transaction before emitting messages.
- Persist inbox before returning `accepted` receipt.
- Use idempotency keys for business commands and provider writes.
- Do not promise exactly-once external writes; provide reconciliation patterns.

### 5.2 Outbox pattern (concrete)
- **Schema (simplified):**
  - `outbox(id, aggregate_type, aggregate_id, event_type, payload, created_at, processed_at, attempts, last_error)`
- **Flow:**
  1. Transactionally write domain state and outbox row.
  2. Background dispatcher reads unprocessed outbox rows, sends to Link or external connector.
  3. On success, mark `processed_at`.
  4. On failure, increment `attempts` and set `last_error`. After N attempts, move to dead-letter.

### 5.3 Inbox pattern (concrete)
- **Schema (simplified):**
  - `inbox(source, event_id, payload, received_at, processed_at, status)`
- **Flow:**
  1. On delivery, persist inbox row.
  2. Return `accepted` receipt.
  3. Process inbox row idempotently (use `event_id` dedupe).
  4. Mark `processed_at`.

### 5.4 Idempotency for external connectors
- **Connector pattern:** For each external write (e.g., GitHub issue creation), include a connector-level idempotency key derived from:
  - `connectorName + targetResource + canonicalPayloadHash`
- **Recommended algorithm:** SHA-256 over canonical JSON of the write request.
- **Connector ledger:** Maintain `connector_actions(idempotency_key, external_id, status, created_at)`.

### 5.5 Example: GitHub issue connector
- Use GitHub's `client_mutation_id` or `idempotency_key` (if available) for GraphQL/REST calls.
- If the provider lacks idempotency, implement read-before-write reconciliation:
  1. Search for an existing issue with a unique marker (title prefix or body token).
  2. If found, update instead of creating.
  3. Otherwise, create and record `external_id`.

### 5.6 Redelivery and duplicate handling
- On duplicate delivery, the receiver **MUST** return `duplicate` receipt and not re-apply the command.
- The Facilitator **MUST** expose a dead-letter queue with human-readable diagnostics and the original CloudEvent.

**Supporting reference:** Outbox/inbox patterns and idempotency best practices are widely documented in reliable messaging literature and are recommended for distributed systems. 

---

## 6. Memory model, ACLs and evidence semantics

### 6.1 Memory entry schema (required fields)
```json
{
  "memoryId": "mem-123",
  "scope": "project|team|workspace|private",
  "accessControl": {
    "visibility": "private|team-only|facilitator-visible|public",
    "allowedPrincipals": ["team:alpha", "user:alice"]
  },
  "provenance": {
    "source": "workspace:ws-01",
    "evidenceRef": "artifact://sources/brief-1",
    "createdBy": "user:alice",
    "createdAt": "2026-10-22T18:00:00Z"
  },
  "contentSummary": "Short text summary for search",
  "contentHash": "sha256:abcd...",
  "fullTextRef": "artifact://memory/full/mem-123"
}
```

### 6.2 Policy labels and usage events
- **Policy labels:** `private`, `team-only`, `facilitator-visible`, `evidence-only`.
- **Usage event:** Any automated process that reads memory for decision-making **MUST** emit `memory.used` with `memoryId`, `consumer`, and `justification`.

### 6.3 Search and retention
- Memory search results **MUST** include `provenance` and `accessControl` metadata.
- Retention policies **MUST** be configurable per Project and enforced by the Facilitator.

---

## 7. Approval hashing, canonicalization and replay protection

### 7.1 Canonicalization
- Use **deterministic JSON canonicalization** (RFC 8785 or equivalent) for approval payloads.
- Normalize:
  - Unicode normalization (NFC).
  - Numeric canonical forms.
  - Sorted object keys.
  - No insignificant whitespace.

### 7.2 Hash algorithm
- Use SHA-256 over canonicalized bytes.
- Approval record:
```json
{
  "approvalId": "appr-123",
  "actionHash": "sha256:...",
  "eligibleActors": ["user:alice","group:team-leads"],
  "expiresAt": "2026-10-23T18:00:00Z",
  "createdAt": "2026-10-22T18:00:00Z"
}
```

### 7.3 Replay protection and revocation
- Approvals **MUST** include `nonce` and `expiresAt`.
- The Facilitator **MUST** maintain an approvals ledger and mark approvals as `consumed` when used.
- Revocation: support `approval.revoked` events and require consumers to check revocation status before applying an approval.

---

## 8. Artifact handling and large payload patterns

### 8.1 Principles
- Links **MUST NOT** carry large artifacts inline.
- Use content-addressed artifact manifests and signed upload URLs.
- Artifacts are referenced by `ArtifactManifest` with `mediaType`, `size`, `digest`, and `storageUrl`.

### 8.2 Artifact lifecycle
- **Upload flow:**
  1. Workspace requests an upload URL from Facilitator: `POST /api/artifacts/request-upload`.
  2. Facilitator returns signed URL and `artifactId`.
  3. Workspace uploads directly to object store.
  4. Workspace notifies Facilitator with `artifact.available` event referencing `artifactId`.
- **Retention and GC:**
  - Artifacts have `retentionPolicy` (e.g., `30d`, `archive`, `permanent`).
  - Background GC removes unreferenced artifacts after retention expiry.

### 8.3 Example manifest
```json
{
  "artifactId": "artifact-123",
  "mediaType": "text/markdown",
  "size": 10240,
  "digest": "sha256:abcd...",
  "storageUrl": "s3://example-bucket/artifact-123",
  "createdBy": "workspace:ws-01",
  "createdAt": "2026-10-22T18:00:00Z"
}
```

---

## 9. Observability, metrics and runbooks

### 9.1 Recommended metrics (OTLP)
- **Delivery metrics**
  - `facilitator.delivery.latency` (histogram)
  - `facilitator.delivery.attempts` (counter)
  - `facilitator.delivery.dead_letter_count` (gauge)
- **Queue metrics**
  - `facilitator.outbox.queue_depth`
  - `facilitator.inbox.queue_depth`
- **Authorization metrics**
  - `facilitator.authz.denied` (counter)
  - `facilitator.authz.latency` (histogram)
- **Memory metrics**
  - `facilitator.memory.search_latency`
  - `facilitator.memory.entries_count`

### 9.2 Traces and logs
- Trace key spans:
  - `link.handshake`
  - `delivery.dispatch`
  - `connector.write`
  - `approval.evaluate`
- Logs must include correlation IDs: `workId`, `assignmentId`, `deliveryId`, `traceparent`.

### 9.3 Runbook snippets
- **Stuck deliveries**
  - Check `outbox` rows with `attempts > N`.
  - Inspect connector logs and provider rate limits.
  - If transient, increase backoff; if permanent, move to dead-letter and notify owners.
- **Orphaned executions**
  - Query `execution` rows with `status=starting` and `last_heartbeat > threshold`.
  - If lost, mark `execution.lost` and create a new assignment or notify owner.
- **Identity mismatch**
  - When `actorEvidenceRef` validation fails, emit `authorization.failed` and notify the enrollment owner.

---

## 10. Conformance, test harness and SDK guidance

### 10.1 Conformance suite (must-have tests)
- **Handshake tests**
  - `hello`/`welcome` happy path.
  - Resume with stale cursor.
  - Token refresh during reconnect.
- **Delivery tests**
  - Delivery accepted and processed.
  - Duplicate delivery returns `duplicate`.
  - Rejected command returns `unsupported`.
- **Outbox/inbox tests**
  - Transactional outbox write with state mutation.
  - Dispatcher marks processed_at on success.
- **Authorization tests**
  - Evidence validation success/failure.
  - Deny-by-default policy enforcement.
- **Memory tests**
  - ACL enforcement on search results.
  - `memory.used` event emission.

### 10.2 SDKs and local dev loop
- Provide a **TypeScript** SDK implementing:
  - Link client (SignalR + HTTP fallback).
  - Envelope/receipt helpers.
  - Local test harness that can run a Facilitator and Gateway in-process.
- Provide a **Go** or **Python** minimal SDK for non-Node teams.
- **One-command local loop**:
  - `./dev up` spins up Facilitator (slim Compose), a sample Gateway, and a sample Workspace.
  - Seed data: one Project, one Team, one capability manifest, and a sample Work/Assignment.
  - Example CLI commands:
    - `./dev assign --work work-1 --capability research.analyze`
    - `./dev show deliveries --pending`

---

## 11. Developer experience and onboarding

### 11.1 One-command local dev loop (detailed)
- `./dev up` does:
  - Start PostgreSQL, Facilitator, and object store (minio).
  - Start sample Gateway and sample Workspace.
  - Seed DB with `project:demo`, `team:demo`, `capability:analysis`.
  - Output a short walkthrough: "Run `./dev assign` to create a Work and watch the Gateway accept it."

### 11.2 Debugging helpers
- CLI `./dev inspect delivery del-123` prints CloudEvent, schema URL, and last error.
- Admin UI: filter by `trustMode`, `pending deliveries`, `dead-letter`.
- Provide a "replay" button to requeue a dead-letter after fix.

---

## 12. Personas and role-specific guidance

### 12.1 Senior Solution Architect (voice)
- **Focus:** system invariants, security, scale.
- **Advice:** Make trust boundaries explicit; require `trustMode` and canonical approval hashing. Provide conformance tests for identity and delivery semantics.

### 12.2 Gateway implementer / DevOps engineer (voice)
- **Focus:** deployability, observability, connectors.
- **Advice:** Ship a slim Gateway image that can run in a devcontainer and a hardened image for production. Use environment-driven token factories for token refresh.

### 12.3 Workspace developer / Software engineer (voice)
- **Focus:** fast feedback, local testing, deterministic runs.
- **Advice:** Provide a local harness adapter and a sample assignment flow. Ensure the Link client has a test mode that simulates dropped receipts and network partitions.

### 12.4 Security engineer (voice)
- **Focus:** least privilege, auditability.
- **Advice:** Deny-by-default policies, signed evidence validation, and mandatory audit events for authorization decisions and memory usage.

### 12.5 Product manager / Team owner (voice)
- **Focus:** adoption, UX, governance.
- **Advice:** Prioritize the one-command dev loop and clear admin UI for trustMode and capability grants. Provide migration guidance for teams with existing CI/CD.

---

## 13. User stories and prioritized use cases

### 13.1 High priority (MVP)
1. **As a developer**, I want to run a local Facilitator and Gateway with one command so I can test assign→accept→complete flows locally.
2. **As a security engineer**, I want the Facilitator to validate `actorEvidenceRef` and log the claims used so I can audit approvals.
3. **As an operator**, I want to see `trustMode` for each enrollment in the admin UI so I can assess risk.
4. **As a connector author**, I want a recommended idempotency pattern for GitHub/Jira so my writes are safe to retry.

### 13.2 Medium priority
1. **As a team lead**, I want memory entries to be `team-only` by default so private knowledge is not leaked.
2. **As a developer**, I want a CLI to inspect pending deliveries and dead-letter items so I can debug failures.
3. **As an integrator**, I want a conformance test suite so I can validate my Gateway implementation.

### 13.3 Low priority / future
2. **As a researcher**, I want optional model reasoning to be auditable and evidence-backed.

---

## 14. Suggested text edits and additions to the spec (diff-style)

- **Add to §3.2 Runtime roles:** include `trustMode` in enrollment examples and clarify that `trustMode` is immutable for a session.
- **Add to §5.3 Link handshake:** include normative `hello`/`welcome` JSON examples and required fields for capability negotiation and resume cursors.
- **Add new §5.6:** "SignalR replication and affinity" describing sticky sessions, DB-directed delivery, and backplane trade-offs.
- **Add to §14 Persistent memory:** require `provenance` and `accessControl` fields and `memory.used` events.
- **Add new Appendix:** "Conformance test matrix" listing handshake, delivery, authorization, memory, and artifact tests.

---

## 15. Acceptance criteria and minimal viable conformance checklist

**MUST have for first release**
- Facilitator accepts Link `hello` and returns `welcome`.
- Outbox/inbox transactional semantics implemented and tested.
- `trustMode` present and surfaced in admin UI.
- Approval hashing canonicalization documented and implemented.
- Artifact upload flow with signed URLs and manifest references.
- Conformance tests for handshake and delivery pass.

**SHOULD have for near-term**
- TypeScript Link SDK and local dev loop.
- Connector examples for GitHub and Jira with idempotency patterns.
- Observability dashboards and runbook snippets.

---

## 16. Appendix: canonical JSON examples and deterministic rules

### 16.1 Canonical JSON rules (for approvals and idempotency)
- Use RFC 8785 deterministic JSON canonicalization.
- Sort object keys lexicographically.
- Represent numbers in shortest decimal form without trailing zeros.
- Normalize strings to Unicode NFC.
- No insignificant whitespace.

### 16.2 Example canonical approval payload (pre-hash)
```json
{"action":"merge","target":"repo:acme/service","assignmentId":"analysis-1","nonce":"n-123","expiresAt":"2026-10-23T18:00:00Z"}
```
Hash: `sha256` over canonical bytes → `sha256:...`

### 16.3 Example CloudEvent envelope (delivery)
```json
{
  "specversion":"1.0",
  "id":"01JCOMMAND01",
  "source":"urn:facilitator:instance:main",
  "type":"org.facilitator.command.work.assign.v1",
  "subject":"work/work-123/assignment/analysis-1",
  "time":"2026-10-22T18:00:00Z",
  "datacontenttype":"application/json",
  "dataschema":"https://schemas.example.invalid/work-assign/v1.json",
  "data":{
    "commandId":"cmd-123",
    "workId":"work-123",
    "assignmentId":"analysis-1",
    "targetId":"gateway-team-alpha",
    "capabilityId":"research.analyze",
    "expectedContextVersion":4,
    "inputs":{"objective":"Compare data retention alternatives"},
    "expectedDeliverables":[{"kind":"analysis.report","mediaType":"text/markdown"}],
    "actorEvidenceRef":"identity-evidence:ref-123",
    "deadline":"2026-10-23T18:00:00Z"
  }
}
```

---

## 17. References and further reading

- Outbox pattern and idempotency best practices (reliable messaging literature). 

### Closing notes (multi-persona sign-off)

- **Senior Architect:** "Make trust explicit, canonicalize approvals, and require conformance tests."  
- **Gateway implementer:** "Ship a slim devcontainer image and a TypeScript SDK."  
- **Security engineer:** "Validate evidence, deny-by-default, and log every authorization decision."  
- **Developer:** "Give me a one-command local loop, a CLI to inspect deliveries, and deterministic tests for retries."

If you want, I can now:
- Produce the `open-facilitator-appendix.md` file content packaged for download (ready to save), or
- Expand any section into a full RFC-style proposal with diagrams, or
- Generate the conformance test list as runnable test cases (e.g., Postman collection or JSON fixtures).

Which would you like next?
