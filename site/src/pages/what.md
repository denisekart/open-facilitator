# What Open Facilitator is

You run work with a mix of AI agents, people, and scripts. Some of that
work matters enough to audit, to approve, to hand over a wall between
teams, or to still understand in six months. Open Facilitator is the
durable record and coordinator for exactly that work — and nothing else.

## The system at a glance

One picture of every moving part, grouped by **who operates it**. Click
the ⤢ to open it fullscreen — scroll to zoom, drag to pan, hover
anything for what it guarantees.

```mermaid title="The system landscape — grouped by owner"
flowchart TB
    subgraph operators["Operated by you: people & agents"]
        direction LR
        BR["Console<br/>(browser)"]
        API2["API clients<br/>curl · agents · CI"]
        HUM["Decisions in chat<br/>approve · deny · steer"]
    end
    subgraph kernelbox["The kernel — one stateless container, deployed by you"]
        direction LR
        ING["Ingress<br/>idempotent admission"]
        POL["Routing policy<br/>+ approval engine"]
        LNK["Link hub<br/>outbound-only transport"]
        ARTB["Artifact broker<br/>+ memory broker"]
        CON["Serves console<br/>+ public API"]
    end
    subgraph infra["Commodity infrastructure — yours, replaceable"]
        direction LR
        PG[("PostgreSQL<br/>the durable record")]
        IDP["OIDC provider<br/>login + claims"]
        OBJ[("S3-compatible store<br/>artifact bytes")]
    end
    subgraph envA["Team environment A — your keys, your policy"]
        direction LR
        GWA["Gateway<br/>team ledger + policy"]
        H1["Harness: LLM agent"]
        H2["Harness: deterministic script"]
        CN["Forge connector<br/>privileged writes"]
        MA["Chat adapter<br/>Mattermost / Telegram"]
    end
    subgraph envB["Team environment B — maybe a different organization"]
        direction LR
        GWB["Gateway"]
        H3["Harness: your own"]
    end
    subgraph prov["Optional: provisioning environment"]
        PL["OCI launcher<br/>spawns fresh workspaces"]
    end
    BR --> CON
    API2 --> CON
    HUM --> MA
    HUM --> BR
    CON --> ING
    ING --> POL
    POL --> LNK
    POL --> ARTB
    ING --> PG
    CON --> PG
    CON --> IDP
    ARTB --> OBJ
    LNK -."outbound link".-> GWA
    LNK -."outbound link".-> GWB
    LNK -."outbound link".-> PL
    GWA --> H1
    GWA --> H2
    GWA --> CN
    GWA --> MA
    GWB --> H3
    CN --> FORGE["Forge<br/>GitHub · Forgejo · GitLab"]
    MA --> CHAT["Chat platform"]
    PL -."fresh linked environment".-> GWB
```

Nothing crosses an ownership boundary except an **outbound link** or a
**normalized event**: the kernel cannot reach into a team environment,
and a team cannot forge another team's history. Everything inside a
subgraph is yours alone.

## The object at the center: a Work

A **Work** is one piece of work with a life of its own: an objective, a
state, a timeline of everything that happened to it, and the evidence it
produced. You create one with a single API call (or from a chat message),
and from that moment it survives every process, container, and model
involved in doing it.

```mermaid title="The life of a Work"
stateDiagram-v2
    direction LR
    [*] --> Open: created (API, chat, webhook)
    Open --> Routed: policy picks a team
    Routed --> InProgress: team's agent accepts
    InProgress --> AwaitingApproval: privileged step
    AwaitingApproval --> InProgress: human approves
    AwaitingApproval --> AwaitingApproval: human says not yet
    InProgress --> Done: result + evidence filed
    Open --> Cancelled: you change your mind
    Done --> [*]
    Cancelled --> [*]
```

Move the mouse over any node above to see what it guarantees.

## The cast, and who holds what

Three roles, each with exactly one job:

- **The kernel** coordinates and remembers. It holds the work queue, the
  routing decisions, the approvals, the artifact ledger. It never
  executes anything and holds none of your credentials.
- **Teams** execute. Each team runs a small **Gateway** on its own
  environment, holding its own source checkouts, API keys, and model
  access. It keeps its own ledger of what it was told and what it did.
- **You** decide. Wherever a gate is configured, the work pauses and a
  human — through chat or the console — makes the call. Gates are
  durable: they survive kernel restarts and fire exactly once.

Information always flows the same way: your team dials **out** to the
kernel. Nothing on a team's network is exposed to the kernel, to other
teams, or to the internet.

```mermaid title="One assignment, hop by hop — every arrow outbound from the team"
sequenceDiagram
    actor H as Human
    participant K as Kernel
    participant G as Team Gateway
    participant W as Harness (agent)
    H->>K: Create a Work (API / chat)
    K->>K: Policy: pick team, mint assignment
    K--)G: Push assignment (outbound link, at-least-once)
    G->>G: Team policy: allow? record in team ledger
    G->>W: Spawn harness for the assignment
    W--)K: Progress events (idempotent)
    W->>G: Propose privileged write (open PR)
    G--)K: Approval request (durable)
    K--)H: Approval prompt (chat / console)
    H->>K: Approve (consumed exactly once)
    K--)G: Approval granted
    G->>G: Connector executes the write, team-side
    W->>G: Complete with result manifest
    G--)K: Artifacts + completion (digest-verified)
    K->>K: File evidence, update timeline
```

Note what is _not_ here: the kernel never touches your repository, your
model provider, or your chat server. Those edges belong to the team
environment, and they only appear when a human or a policy let them.

## What makes it different from a task queue or a workflow engine

- **The workers are agents with opinions.** A Workflow engine assumes
  steps are known. Here, an agent decides _how_ to do the assignment,
  and the framework only insists on the contract: accept, progress,
  propose, deliver — every step recorded.
- **Federated, not centralized.** Every team is a separate authority
  with its own policy and its own ledger. The kernel cannot reach into
  a team's environment; a team cannot forge another team's history.
- **Evidence is the product.** Each Work accumulates digest-verified
  artifacts, a complete audit timeline, and optional shared memories.
  "Who did what, when, and who allowed it" is a query, not an
  investigation.
- **It is a protocol first.** The whole coordination is a published
  wire contract — event envelopes, schemas, HTTP surfaces — that two
  independent runtime implementations already satisfy. You are never
  trapped inside one implementation of the idea.

## What it is not

- **Not an agent framework inside your process.** It coordinates across
  processes and organizational boundaries, not within one agent loop.
- **Not a chat product, a forge, or a model gateway.** It plugs into
  the chat and forge you already use; it never proxies your model
  traffic.
- **Not a managed service.** You deploy the published image on
  infrastructure you already run; your database is your data.

## Where to go next

- See the pieces you install: [Artifacts & installation](/artifacts).
- See what plugs in today: [Supported integrations](/integrations).
- Look at the operator surface: [The admin console](/console).
- Or stop reading and [get running](/guide/human).
