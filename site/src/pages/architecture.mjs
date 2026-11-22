// The /architecture page: interactive explorer graph + deep architectural guide
// for system architects, engineering leads, and technical evaluators.

import { scenarios } from "../architecture.mjs";

export const architectureMarkdown = `
## Reading the map

<div class="arch-legend">
  <p><span class="al-key al-solid"></span><b>Solid outline</b><span>Self-hosted core services. The kernel is a stateless container that persists all state to PostgreSQL and delegates storage to S3-compatible backends.</span></p>
  <p><span class="al-key al-team"></span><b>Copper outline</b><span>Team-operated environments holding team credentials, evaluating local security policy, and connecting outbound to the kernel.</span></p>
  <p><span class="al-key al-external"></span><b>Dashed outline</b><span>External infrastructure: PostgreSQL, OIDC identity providers, object storage, and forge/chat platforms.</span></p>
  <p><span class="al-line"></span><b>The link</b><span>Outbound, resumable SignalR WebSocket or HTTP long-poll connections maintained by team Gateways.</span></p>
</div>

## Architectural Overview & Problem Context

Modern software engineering and operations increasingly rely on a heterogeneous mix of **autonomous AI agents** (running locally or in containers), **human operators** (reviewing drafts, approving privileged actions), and **deterministic automation** (CI/CD pipelines, scheduled jobs, verification scripts).

Traditional architectures struggle to coordinate these mixed workloads because:
1. **Coordination state is fragmented:** The chat platform holds conversations, the forge holds code, and agent runtimes hold execution state in ephemeral memory or local processes. There is no single, queryable source of truth for "what are we trying to achieve, who is assigned, what evidence exists, and who authorized what."
2. **The "God-Agent" security hazard:** Centralized orchestrators often require administrative credentials to every team's private networks, code repositories, and cloud environments. A compromise of the orchestrator compromises the entire enterprise.
3. **Fragile execution and amnesia:** If a worker process, container, or network socket crashes mid-execution, state is lost, retries risk executing duplicate side-effects, and successful lessons are never systematically retained for future runs.

**Open Facilitator solves this by establishing a strict separation between Coordination and Execution.** The Facilitator Kernel is a lightweight, stateless coordination service that maintains durable state, routing policy, approval gates, and content-addressed evidence. Execution authority remains 100% decentralized inside team-owned environments.

\`\`\`mermaid title="System tier decomposition and network communication boundaries"
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#2b313b", "primaryBorderColor": "#c96a2d", "primaryTextColor": "#f3f4f6", "lineColor": "#9ca3af", "secondaryColor": "#3d332d"}}}%%
flowchart TB
    subgraph INGRESS["1. Ingress & Integration Tier"]
        PROV["External Providers<br/>(GitHub, GitLab, Forgejo, Telegram, Mattermost)"]
        WEBHOOK["Webhook Ingress Controller<br/>POST /api/ingress/webhooks/{provider}"]
        ADMIN_API["Admin REST & Chat API<br/>/api/admin/* · /api/chat/*"]
    end

    subgraph KERNEL["2. Facilitator Kernel (.NET 10 / ASP.NET Core)"]
        INGRESS_PROC["Ingress Processor & Role Gate"]
        COORD_STORE["EF Core Coordination Store"]
        ROUTING["Capability-Based Router"]
        LINK_HUB["Link Hub & Delivery Pump<br/>(SignalR / SSE / HTTP Duplex)"]
        WORKFLOW_ENG["Workflow DAG Interpreter"]
        OUTBOX["Transactional Outbox Dispatcher"]
    end

    subgraph PERSISTENCE["3. State & Evidence Persistence Tier"]
        PG[("PostgreSQL 17+<br/>(Works, Links, Flows, Outbox, Memories)")]
        BLOB[("Object Storage (S3 / Local)<br/>(Content-Addressed Artifacts: sha256)")]
    end

    subgraph EXECUTION["4. Team Execution Tier (TypeScript / Node.js)"]
        GW["Team Gateway (@ofac/gateway)<br/>(Local Policy + SQLite Ledger)"]
        WS1["Workspace Sandboxes<br/>(Dev Containers / VMs)"]
        HARNESS["Harness Adapters (@ofac/adapter)<br/>(Deterministic, Agent CLI, LLMs)"]
    end

    PROV -->|"Inbound Webhooks (HMAC Signed)"| WEBHOOK
    WEBHOOK --> INGRESS_PROC
    ADMIN_API --> INGRESS_PROC
    INGRESS_PROC --> COORD_STORE
    COORD_STORE <--> PG
    COORD_STORE --> ROUTING
    ROUTING --> OUTBOX
    OUTBOX <--> PG
    OUTBOX --> LINK_HUB
    GW -->|"Outbound WebSocket / HTTP Fallback"| LINK_HUB
    GW --> WS1
    WS1 --> HARNESS
    HARNESS -->|"Upload Artifacts (Digest-Pinned)"| BLOB
    HARNESS -->|"Progress & Evidence"| GW
    WORKFLOW_ENG <--> COORD_STORE
\`\`\`

---

## Core Architectural Decisions & Design Rationale

When evaluating Open Facilitator, architects should understand the explicit design decisions that shape its architecture and operational guarantees.

### 1. Inverted Control Plane (Outbound-Only Link Connectivity)

**The Problem:** Traditional distributed architectures require the central orchestrator to reach inbound into worker nodes (via SSH, gRPC, or reverse tunnels). In enterprise multi-cloud setups, worker nodes run behind NATs, corporate firewalls, private VPCs, or developer laptops where opening inbound ports is prohibited or operationally prohibitive.

**The Solution:** The Facilitator utilizes an *inverted control plane*. Team Gateways initiate and maintain persistent *outbound* connections to the Kernel's Link Hub (\`/hubs/link\`) over WebSocket (SignalR) with an automatic HTTP streaming fallback. The Kernel pushes assignments only over channels the team established.

\`\`\`mermaid title="Inverted control plane: outbound connection and work dispatch sequence"
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#2b313b", "primaryBorderColor": "#c96a2d", "primaryTextColor": "#f3f4f6", "lineColor": "#9ca3af", "secondaryColor": "#3d332d"}}}%%
sequenceDiagram
    participant GW as Team Gateway (@ofac/gateway)
    participant HUB as Kernel Link Hub (/hubs/link)
    participant DB as PostgreSQL (Outbox & Ledger)
    participant HARN as Workspace Harness Child

    Note over GW,HUB: Phase 1: Outbound Mutual Cryptographic Handshake
    GW->>HUB: Outbound Connect (SignalR / WebSocket)
    HUB-->>GW: Server Challenge Nonce
    GW->>HUB: Signed Handshake (Ed25519 Endpoint Key + Capability Manifest)
    HUB->>DB: Record Link Session & Lease (Optimistic Concurrency)
    HUB-->>GW: Handshake Acknowledged & Lease Granted

    Note over HUB,DB: Phase 2: Asynchronous Dispatch via Transactional Outbox
    DB-->>HUB: Outbox Pump: Pending work.assign Event
    HUB->>GW: Dispatch work.assign (Fenced Generation)

    Note over GW,HARN: Phase 3: Local Policy Evaluation & Execution
    GW->>GW: Evaluate Local Policy (allow | deny | confirm)
    GW->>HARN: Spawn Sandboxed Child Execution
    HARN-->>GW: Streaming Progress & Artifact Metadata
    GW-->>HUB: Progress Envelope (Echoes traceparent)
    HARN->>GW: Execution Complete (Result Manifest + SHA-256 Digest)
    GW->>HUB: assignment.completed (Single Terminal Transition)
    HUB->>DB: Atomic Settlement: Update Work Timeline & Read Models
\`\`\`

**Architectural Benefits:**
- **Zero Inbound Attack Surface:** Team networks expose no public listeners or open firewall ports.
- **NAT & Firewall Agnostic:** Gateways operate seamlessly from developer machines, air-gapped VPCs, or ephemeral cloud containers.
- **Resilient Reconnection:** Disconnections trigger automatic reconnection with session leases, preventing duplicate executions during network blips.

---

### 2. Transactional Outbox Pattern for Resilient Event Dispatch

**The Problem:** Distributed architectures that write state to a database and directly publish events to an external message broker (Kafka, RabbitMQ) suffer from the *dual-write hazard*. If the database commit succeeds but the message broker publish fails (or vice versa), the system enters an inconsistent state, leading to ghost tasks or missed dispatches.

**The Solution:** All state mutations and outgoing event notifications are written atomically into a single PostgreSQL transaction using the **Transactional Outbox Pattern** (\`facilitator.outbox\`). An asynchronous background delivery engine (\`DbEventDispatcher\` and \`LinkDeliveryPump\`) reads outbox rows, obtains leases, and delivers events with at-least-once guarantees.

\`\`\`mermaid title="Transactional Outbox Pattern guaranteeing crash-resilient event dispatch"
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#2b313b", "primaryBorderColor": "#c96a2d", "primaryTextColor": "#f3f4f6", "lineColor": "#9ca3af", "secondaryColor": "#3d332d"}}}%%
flowchart LR
    CMD["State Change Command<br/>(Create Work, Transition, Approve)"] --> TX

    subgraph TX["Single Atomic PostgreSQL Transaction (ACID)"]
        DOMAIN["Write Domain Entity<br/>(facilitator.works, approvals)"]
        OUT_ROW["Insert Outbox Row<br/>(facilitator.outbox)"]
    end

    TX --> COMMIT[("PostgreSQL Commit")]
    COMMIT -.->|"NOTIFY / Polling Loop"| PUMP["DbEventDispatcher / LinkDeliveryPump"]

    subgraph DISPATCH["At-Least-Once Delivery Engine"]
        PUMP --> LEASE["Acquire Outbox Row Lease"]
        LEASE --> DISPATCH_ACT{"Target Active?"}
        DISPATCH_ACT -->|"Yes"| LINK_PUSH["Push to Connected Gateway Link"]
        DISPATCH_ACT -->|"No"| RETRY_QUEUE["Hold in Durable Outbox Queue<br/>(Exponential Backoff)"]
        LINK_PUSH --> RECEIPT["Receive Transport Receipt"]
        RECEIPT --> MARK_DONE["Mark Outbox Row Dispatched"]
    end
\`\`\`

**Architectural Benefits:**
- **Strict ACID Consistency:** State changes and outgoing events can never drift apart.
- **Minimal Infrastructure Footprint:** Deployments do not require an external message queue cluster to achieve guaranteed delivery.
- **Pluggable Event Sinks:** Sinks (e.g., Kafka, webhook dispatchers) read directly from the verified outbox stream.

---

### 3. Three Disjoint Identity & Security Planes

**The Problem:** Combining administrative operator access, machine-to-machine worker authentication, and external webhook integrations into a single authentication mechanism creates privilege escalation risks.

**The Solution:** The system partitions security into three disjoint identity planes, each with distinct cryptographic protocols, lifecycle rules, and credential residency:

\`\`\`mermaid title="Three independent identity planes and strict credential boundaries"
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#2b313b", "primaryBorderColor": "#c96a2d", "primaryTextColor": "#f3f4f6", "lineColor": "#9ca3af", "secondaryColor": "#3d332d"}}}%%
flowchart TB
    subgraph HUMAN_PLANE["1. Human / Operator Plane"]
        USER["Browser / Admin Operator"] -->|"OIDC Bearer Token (JWT with PKCE)"| ROLE_GATE["Kernel Role Gate<br/>(admin.read · admin.mutate · chat.use)"]
    end

    subgraph MACHINE_PLANE["2. Machine / Link Plane"]
        GATEWAY["Team Gateway"] -->|"Ed25519 Cryptographic Signatures"| LINK_GATE["Kernel Link Admission Gate<br/>(Endpoint Key Authentication)"]
    end

    subgraph PROVIDER_PLANE["3. Integration / Provider Plane"]
        WEBHOOKS["External VCS / Chat Webhooks"] -->|"HMAC-SHA256 Signatures"| PROV_GATE["Webhook Receiver Gate<br/>(Secret Tokens)"]
    end

    subgraph BOUNDARY["Zero-Trust Credential Residency Rule"]
        KERNEL_STORE["Facilitator Kernel Store<br/>Holds: Work state, Audit rows, Hashes<br/>NEVER HOLDS: Git SSH keys, Deploy credentials, Model keys"]
        TEAM_STORE["Team Local Environment<br/>Holds: LLM API Keys, Git Write Tokens, Deployment Keys, Tool Secrets"]
    end

    ROLE_GATE --> KERNEL_STORE
    LINK_GATE --> KERNEL_STORE
    PROV_GATE --> KERNEL_STORE
    GATEWAY <--> TEAM_STORE
\`\`\`

1. **Human / Operator Plane:** OpenID Connect (OIDC) JWT bearer tokens with PKCE. Roles (\`admin.read\`, \`admin.mutate\`, \`admin.works.read\`, \`chat.use\`) are mapped dynamically from token claims.
2. **Machine / Link Plane:** Asymmetric Ed25519 cryptographic keypairs. Each Gateway endpoint enrolls using a one-time enrollment token and establishes authenticated sessions using cryptographic signatures.
3. **Integration / Provider Plane:** HMAC-SHA256 signature verification for inbound webhooks (GitHub, GitLab, Forgejo, Telegram, Mattermost).
4. **Credential Residency Law:** The Facilitator Kernel **never** stores Git SSH keys, CI/CD deploy tokens, or team LLM API keys. All privileged credentials remain team-side.

---

### 4. Deterministic Work Lifecycle with Optimistic Concurrency & Fencing

**The Problem:** In asynchronous multi-agent systems, worker crashes, network partitions, and concurrent updates can cause split-brain executions, duplicate work attempts, and conflicting state writes.

**The Solution:** Every Work follows an explicit, audited finite state machine with optimistic concurrency control (\`RowVersion\`) and execution generation fencing.

\`\`\`mermaid title="The Work lifecycle state machine"
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#2b313b", "primaryBorderColor": "#c96a2d", "primaryTextColor": "#f3f4f6", "lineColor": "#9ca3af", "secondaryColor": "#3d332d"}}}%%
stateDiagram-v2
    direction LR
    [*] --> Open: Created (API / Chat / Webhook)
    Open --> Planning: Ingress Routed & Admitted
    Planning --> Active: Assignment Accepted by Team Gateway
    Active --> Review: Result Manifest & Evidence Filed
    Review --> Completed: Verified & Accepted

    Open --> Blocked: Approval Gate or Dependency Parked
    Planning --> Blocked: Awaiting Capability or Resource
    Active --> Blocked: Human Approval Required
    Blocked --> Planning: Resumed from Ingress Park
    Blocked --> Active: Approval Granted by Operator

    Open --> Cancelled: Operator Cancel
    Planning --> Failed: Execution Exhausted
    Active --> Failed: Terminal Error
    Completed --> [*]
    Cancelled --> [*]
    Failed --> [*]
\`\`\`

**Concurrency & Fencing Guarantees:**
- **Optimistic Concurrency:** State updates require matching \`rowVersion\` (ETag) values; concurrent modifications are rejected with HTTP 412 conflicts.
- **Fencing Generations:** Each assignment carries an incrementing generation integer. A delayed worker resuming after a timeout cannot commit results for an assignment that has already been reassigned or settled.
- **Append-Only Timeline:** State transitions append immutable event records to the work timeline for auditing and forensics.

---

### 5. Cryptographically Pinned, Single-Use Approval Gates (HITL)

**The Problem:** Autonomous agents should not perform destructive or privileged operations (e.g. production deployments, merging PRs, financial transactions) without human authorization. However, traditional approval systems are vulnerable to replay attacks, payload tampering, or multiple approvals being consumed concurrently.

**The Solution:** The Facilitator implements **digest-pinned, single-use approvals**. When an agent proposes a privileged action, it submits a cryptographic SHA-256 digest of the exact action payload.

\`\`\`mermaid title="Single-use, digest-pinned approval lifecycle"
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#2b313b", "primaryBorderColor": "#c96a2d", "primaryTextColor": "#f3f4f6", "lineColor": "#9ca3af", "secondaryColor": "#3d332d"}}}%%
stateDiagram-v2
    direction LR
    [*] --> Pending: approval.required<br/>(Bound to SHA-256 Digest of proposed payload)
    Pending --> Approved: Eligible Human Decides (Audited)
    Pending --> Denied: Operator Rejects Proposal
    Pending --> Expired: Deadline Exceeded (Lazy Sweep)
    Pending --> Revoked: Cancelled by Requesting Work
    Approved --> Consumed: Action Executed Team-Side (Single-Use Token)
    Denied --> [*]
    Expired --> [*]
    Revoked --> [*]
    Consumed --> [*]
\`\`\`

**Approval Guarantees:**
- **Digest Invariance:** If the proposed action changes by even one byte, the digest verification fails and the approval is rejected.
- **Single-Use Consumption:** Once approved, the decision transitions to \`consumed\` upon execution. It cannot be re-applied or reused on retries.
- **Auditable Accountability:** Every approval decision records the authenticated actor, timestamp, decision rationale, and correlation ID.

---

### 6. Content-Addressed Artifacts & Scoped Memory Broker

**The Problem:** Storing large outputs (diffs, test logs, datasets, container images) directly in relational databases causes performance degradation. Furthermore, uncurated LLM memories risk hallucinated data polluting shared knowledge bases.

**The Solution:**
- **Content-Addressed Storage:** Artifacts are stored in S3-compatible or local object storage, indexed by SHA-256 hash (\`sha256:...\`). The Kernel validates digests on ingest; large payloads are passed by reference, never inline in messages.
- **Two-Phase Memory Governance:** Workloads write \`candidate\` memory proposals. These proposals become active and searchable only after owner-team curation and promotion. Recall queries execute PostgreSQL Full-Text Search with strict team-level access control lists (ACLs).

---

## Technical Stack & Runtime Justifications

| Component | Technology | Architectural Justification |
|---|---|---|
| **Facilitator Kernel** | .NET 10 / ASP.NET Core | High-throughput asynchronous I/O, minimal memory footprint, enterprise-grade SignalR WebSocket multiplexing, built-in OpenTelemetry diagnostics (\`ActivitySource\`, \`Meter\`), and strong type safety matching the protocol schemas. |
| **Team Gateway** | TypeScript / Node.js | Native integration with AI agent ecosystems (LangChain, LlamaIndex, OpenAI/Anthropic SDKs), lightweight daemon footprint, and simple container packaging via Dev Container Features. |
| **Relational Store** | PostgreSQL 17+ | ACID transaction guarantees for the outbox pattern, native JSONB support for CloudEvents payloads, full-text search indexing for memory recall, and advisory locks for distributed sweeper coordination. |
| **Evidence Store** | S3-Compatible Storage | Scalable, cost-effective content-addressed BLOB storage for artifacts, execution logs, and result manifests. |
| **Identity Provider** | OpenID Connect (OIDC) | Standardized, enterprise-ready identity integration (Keycloak, Auth0, Okta, Azure AD) supporting OAuth2 Authorization Code Flow with PKCE. |

---

## Architect Discussion Guide: Evaluating Architecture & Operational Trade-offs

When discussing Open Facilitator in architecture reviews, consider these key structural comparisons:

### 1. How does Open Facilitator compare to Workflow Orchestrators (Temporal, Airflow, Camunda)?
- **Workflow Orchestrators** assume pre-defined, deterministic DAGs where the orchestrator executes code or drives task workers directly.
- **Open Facilitator** is a *coordination and governance plane* for autonomous agents and humans. Agents determine their own execution paths within sandboxed workspaces, while the Kernel enforces policy, coordinates multi-team handoffs, records immutable timelines, and gates privileged actions behind cryptographic human approvals.

### 2. How does the system handle high availability and failure recovery?
- **Stateless Kernel Fleet:** Kernel instances are completely stateless containers. Any replica can handle API requests, process webhooks, or admit Gateway link connections.
- **Distributed Leaderless Sweep Coordination:** Periodic maintenance tasks (timed-out parks, session loss detection, retention sweeps) use PostgreSQL advisory locks, ensuring only one replica executes a given sweep tick without requiring a dedicated leader election cluster.
- **Resilient Work Resumption:** Because all state resides in PostgreSQL, restarting a Kernel container or failing over to another replica causes zero data loss. Connected Gateways automatically reconnect and resume pending assignments.

### 3. How is multi-tenancy enforced?
- **Non-Disclosure by Default:** Cross-team queries return standard \`404 Not Found\` rather than \`403 Forbidden\`, preventing unauthorized actors from discovering the existence of projects, teams, or works.
- **Delivery-Scoped Artifact Grants:** When artifacts are shared across team boundaries, the router team mints temporary, delivery-scoped access grants. No team ever shares bucket credentials or long-lived cloud keys.

---

## Domain Vocabulary & Architectural Glossary

| Term | Industry Analogy | Definition in Open Facilitator |
|---|---|---|
| **Facilitator (Kernel)** | Coordination Control Plane | The central stateless service managing work lifecycles, capability routing, approval gates, artifact metadata, and the outbox event pump. |
| **Team** | Tenant / Security Realm | The sovereign administrative boundary owning workspaces, Link endpoints, credentials, local policies, and memory entries. |
| **Gateway** | Edge Agent / Worker Supervisor | The team-side daemon (\`@ofac/gateway\`) that maintains the outbound Link to the Kernel, evaluates local policy, and manages workspace processes. |
| **Workspace** | Execution Sandbox | The isolated container, VM, or environment where agent harnesses or deterministic programs execute. |
| **Work** | Durable Objective / Epic | A stateful, versioned coordination objective tracking progress, timelines, approvals, and produced artifacts. |
| **Assignment** | Task Attempt / Lease | A single, generation-fenced attempt by a specific workspace to execute a Work. |
| **Link** | Bidirectional Transport Channel | The outbound, resumable WebSocket/HTTP connection established from a Team Gateway to the Kernel. |
| **Harness Adapter** | Runtime Wrapper | An adapter translating between the Gateway's execution commands and specific agent CLIs, LLM toolchains, or deterministic scripts. |
| **Provider Adapter** | Ingress/Egress Connector | A translator converting external chat (Telegram, Mattermost) or SCM (GitHub, GitLab, Forgejo) webhooks into protocol-compliant CloudEvents. |
| **Approval Gate** | Human-in-the-Loop (HITL) Barrier | A durable, content-digest-pinned decision record gating privileged external actions behind human confirmation. |
| **Result Manifest** | Execution Output Attestation | A structured document describing all artifacts, status results, and external action requests produced by an assignment. |
| **Memory Candidate** | Knowledge Proposal | A workload-proposed lesson or context note awaiting team curation before being promoted into active searchable memory. |
`;

export function architectureBody({ svg, payload }, renderMarkdown) {
  const walks = scenarios
    .map(
      (sc) =>
        `<span class="arch-walk"><b>${sc.title}</b> — ${sc.intro}</span>`,
    )
    .join("");

  const renderedGuide = renderMarkdown
    ? renderMarkdown(architectureMarkdown, "site/src/pages")
    : "";

  return `<p class="lede">Explore the system topology: boxes represent running services, and arrows indicate active data flows. Hover a box to inspect connections, click to view guarantees, or select a scenario to follow execution steps.</p>

<div class="arch-controls" id="arch-controls" role="tablist" aria-label="Ways to explore"></div>
<p class="arch-walks" hidden>${walks}</p>

<div class="arch-fig">
  <div class="arch-scroll">${svg}</div>
  <aside class="arch-panel" id="arch-panel" aria-live="polite"></aside>
</div>
<div class="arch-player" id="arch-player" hidden></div>

${renderedGuide}

<script type="application/json" id="arch-data">${payload}</script>`;
}
