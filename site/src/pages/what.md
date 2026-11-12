# What Open Facilitator is

You run work with a mix of AI agents, humans, and scripts. Some of that
work matters enough to audit, to approve, to hand over a wall between
teams, or to still understand in six months. Open Facilitator is the
durable record and coordinator for exactly that work — and nothing else.

## The system at a glance

One map of every moving part, grouped by **who operates it** — and it is
interactive: click any box for what it guarantees, or walk a scenario to
watch the pieces light up in sequence.

```archmap

```

Nothing crosses an ownership boundary except an **outbound link** or a
**normalized event**: the kernel cannot reach into a team environment,
and a team cannot forge another team's history. Everything inside your
border is yours alone.

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
