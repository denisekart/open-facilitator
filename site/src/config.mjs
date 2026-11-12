// Site content manifest. `site/` is fully self-contained: it renders the
// repo's docs tree at build time but shares no code with the facilitator.
// Landing copy lives here; deep content lives in the markdown it references.

export const repo = {
  owner: "denisekart",
  ownerSlug: "denisekart",
  url: "https://github.com/denisekart/open-facilitator",
  npmScope: "@ofac",
  npmHtml: "https://www.npmjs.com/search?q=scope:ofac",
  ghcr: "ghcr.io/denisekart/open-facilitator",
};

export const nav = [
  { label: "How it works", href: "#how" },
  { label: "Components", href: "#components" },
  { label: "Integrations", href: "/#/integrations" },
  { label: "Console", href: "/#/console" },
  { label: "Get started", href: "#start" },
  { label: "Docs", href: "#/docs" },
];

// The hero answers one question: what is this product?
export const hero = {
  kicker: "Open-source orchestration for human + agent teams",
  tagline: "Your agents do the work. Open Facilitator remembers it.",
  oneLiner:
    "A coordination kernel for teams of humans and AI agents. Hand it a piece of work — it tracks who is doing what, waits for the humans who must approve, files the evidence, and survives every crash, restart, and reorg along the way.",
};

// The interactive hero scene: one loop, four stations.
export const relay = {
  path: "M 44 128 A 88 88 0 1 1 236 128 A 88 88 0 1 1 44 128",
  steps: [
    {
      at: "0%",
      x: 44,
      y: 128,
      who: "You or an agent",
      title: "A Work is created",
      body: "One POST to the API — or a message in chat. An objective, nothing else required. The kernel records it durably: it now outlives every process involved.",
    },
    {
      at: "25%",
      x: 140,
      y: 40,
      who: "The kernel",
      title: "Routing decides who acts",
      body: "Deterministic policy — never a model — picks a team by capability, mints an assignment, and pushes it out over an always-outbound link. If that team is offline, the work simply waits.",
    },
    {
      at: "50%",
      x: 236,
      y: 128,
      who: "A team's workspace",
      title: "Agents execute, humans approve",
      body: "Execution happens in the team's own environment, with the team's own credentials. The agent proposes; a human gate decides. Neither the kernel nor any other team can reach in.",
    },
    {
      at: "75%",
      x: 140,
      y: 216,
      who: "The record",
      title: "Evidence is filed",
      body: "Every output becomes a content-addressed artifact, every decision an audit entry, every lesson a memory candidate. Six months later you can still answer: who did what, when, and why.",
    },
  ],
};

// The landing "how it works" story: one concrete week, in order.
export const story = [
  {
    who: "Monday, 09:02 — a person",
    title: "The ask goes in",
    body: "A team lead opens a ticket that reads “draft the billing migration plan” — or an agent files it from a chat message. It lands as a Work: durable, versioned, searchable.",
    cmd: 'curl -X POST https://facilitator.example/api/works \\\n  -H "authorization: Bearer $TOKEN" \\\n  -d \'{"objective": "draft the billing migration plan"}\'',
  },
  {
    who: "Monday, 09:02 — the kernel",
    title: "Policy routes, never guesses",
    body: "The kernel matches the work's capability need against what teams advertise, checks who is online, and assigns it. The decision is deterministic and written to the audit trail — a model can propose a route, only policy can take one.",
  },
  {
    who: "Monday, 09:04 — a team's environment",
    title: "An agent picks it up over an outbound link",
    body: "The team's Gateway dials out to the kernel (nothing is exposed inbound), accepts the assignment, and starts a harness — an LLM agent, a deterministic script, or a human task list. The kernel never holds the team's source credentials, models, or API keys.",
  },
  {
    who: "Monday, 11:37 — a human gate",
    title: "The agent asks; a human decides",
    body: "The draft is ready, but opening the pull request is a privileged write. The kernel mints a durable approval request and delivers it into the team's chat. The kernel can restart, upgrade, or fall over at this exact moment — the approval survives and fires exactly once.",
  },
  {
    who: "Monday, 13:10 — the record",
    title: "Everything is filed where it can be found",
    body: "The report and its manifest are stored as digest-verified artifacts. The timeline holds every event, actor, and decision. Useful lessons can be promoted to shared memory with team-scoped access — so the next agent starts smarter.",
  },
];

// What ships, in consumer terms: artifact coordinates, not source paths.
export const components = [
  {
    name: "The kernel",
    tag: "container image",
    body: "One stateless service you run anywhere containers run. Work lifecycle, routing policy, approvals, links, artifacts, memory, admin console. Your PostgreSQL, your OIDC provider, your object store — no proprietary infrastructure.",
    docs: "/artifacts",
  },
  {
    name: "The Gateway",
    tag: "npm · @ofac/gateway",
    body: "The team-side runtime. One outbound connection to the kernel, N workspaces behind it, a durable local ledger. Runs as a CLI process, a container, or inside your existing CI fleet.",
    docs: "/artifacts",
  },
  {
    name: "Harness adapters",
    tag: "npm · @ofac/adapter",
    body: "The plug between the Gateway and whatever actually executes: shipped reference harnesses for deterministic runs, oh-my-pi agents, and Codex — plus the SDK and conformance kit for your own.",
    docs: "/extend",
  },
  {
    name: "Provider adapters",
    tag: "npm · chat + forge",
    body: "Chat and forge legs for Mattermost, Telegram, GitHub, Forgejo, and GitLab. Inbound events arrive normalized; privileged outbound writes ride approval policies and execute team-side.",
    docs: "/integrations",
  },
  {
    name: "The wire contract",
    tag: "open protocol",
    body: "Every message is a CloudEvents 1.0 envelope with a versioned JSON Schema; every endpoint is specified in OpenAPI 3.1. Two independent runtime stacks pass the same conformance fixtures — you can implement any component in any language.",
    docs: "/docs/protocol",
  },
  {
    name: "Dev Container Features",
    tag: "OCI images · ghcr.io",
    body: "Turn any team container into a linked workspace by adding one feature to devcontainer.json. Enroll, connect, advertise capabilities, stream logs — no bespoke daemon to write.",
    docs: "/artifacts",
  },
];

// Why it exists — one claim per card, each verifiable in the open protocol.
export const benefits = [
  {
    title: "Agents come and go; the work remains",
    body: "Crash a worker mid-approval and the run resumes exactly once. Nothing important lives only in a socket, a container, or a model's context window. The kernel's store is your PostgreSQL — back it up like anything else you care about.",
  },
  {
    title: "No one loses authority over their environment",
    body: "Teams dial out, never in. The kernel holds no source credentials, no deploy keys, no model access. Every external write a team makes passes the team's own policy first — enforced by code you can read, not by a hosting agreement.",
  },
  {
    title: "Humans stay in the loop where they chose to be",
    body: "Privileged actions ride durable, digest-pinned, single-use approvals delivered into your chat. A restart, a failover, or a duplicate delivery cannot approve twice — the ledger refuses.",
  },
  {
    title: "AI without amnesia",
    body: "Outputs are content-addressed artifacts, decisions are audit entries, and proven lessons become shared memories with team-scoped access. Next quarter's agents can be held to what this quarter's work proved.",
  },
  {
    title: "A protocol, not a platform",
    body: "CloudEvents + JSON Schema + OpenAPI, with a cross-language conformance suite. Swap harnesses, swap chat providers, write your own adapter in any language — or implement the whole side from the contract alone.",
  },
  {
    title: "Boring, replaceable infrastructure",
    body: "Required: PostgreSQL, an OIDC provider, an object store. Everything else — message buses, schedulers, vector databases, even the model providers — is optional. Small enough to audit, standard enough to keep.",
  },
];

// Guides authored for the site (site/src/guides/*.md).
export const guides = [
  {
    id: "human",
    route: "/guide/human",
    title: "Get running — Humans",
    kicker: "You drive a browser and a terminal",
    body: "Run the kernel from its published image, connect a team, and take your Work from create to approval to artifact.",
    minutes: 12,
    src: "site/src/guides/human.md",
  },
  {
    id: "agent",
    route: "/guide/agent",
    title: "Get running — AI agents",
    kicker: "You are an agent reading this programmatically",
    body: "Dense orientation: auth, the HTTP surface, link enrollment, the computer-readable contract, where artifacts live.",
    minutes: 6,
    src: "site/src/guides/agent.md",
  },
];

// Site-authored consumer pages (site/src/pages/*.md).
export const pages = [
  {
    group: "The product",
    order: 1,
    entries: [
      {
        route: "/what",
        src: "site/src/pages/what.md",
        title: "What Open Facilitator is",
        body: "The whole model from zero: Works, teams, links, approvals, artifacts, memory — in plain language.",
      },
      {
        route: "/integrations",
        src: "site/src/pages/integrations.md",
        title: "Supported integrations",
        body: "Chat, forges, harnesses, provisioning, transports: what works today and what each one does.",
      },
      {
        route: "/console",
        src: "site/src/pages/console.md",
        title: "The admin console",
        body: "The operator surface: search, links, deliveries, config, AiDeck — with screenshots of the real thing.",
      },
      {
        route: "/artifacts",
        src: "site/src/pages/artifacts.md",
        title: "Artifacts & installation",
        body: "Published images, packages, and features — coordinates, versions, and how to consume each.",
      },
      {
        route: "/extend",
        src: "site/src/pages/extend.md",
        title: "Extend it yourself",
        body: "Write your own harness, provider, or whole component: the contract, the SDK, the conformance kit.",
      },
    ],
  },
];

// docs/*.md published to the site (path = source of truth, site route).
export const docs = [
  {
    group: "Start here",
    order: 2,
    entries: [
      {
        route: "/docs",
        src: "docs/README.md",
        title: "Documentation home",
        body: "Pick your path through the documentation.",
      },
      {
        route: "/docs/overview",
        src: "docs/overview.md",
        title: "Overview",
        body: "The concepts and vocabulary from zero: Facilitator, Projects, Works, Gateways, Links, approvals, artifacts, memory.",
      },
    ],
  },
  {
    group: "Walkthroughs",
    order: 3,
    entries: [
      {
        route: "/docs/getting-started",
        src: "docs/getting-started.md",
        title: "Guided tour (reference environment)",
        body: "A self-contained laptop tour of the whole system, end to end, with every hop proved.",
      },
      {
        route: "/docs/getting-started-agents",
        src: "docs/getting-started-agents.md",
        title: "Agent orientation (in-repo)",
        body: "Agent-facing orientation for people working inside the source repository.",
      },
      {
        route: "/docs/contributing",
        src: "docs/contributing.md",
        title: "Contributing",
        body: "Make targets, protocol changes, PR gates, version pinning.",
      },
      {
        route: "/docs/release-setup",
        src: "docs/release-setup.md",
        title: "Release artifactory setup",
        body: "Provisioning the registries and CI credentials the pipeline consumes.",
      },
    ],
  },
  {
    group: "Reference",
    order: 4,
    entries: [
      {
        route: "/docs/protocol",
        src: "docs/protocol.md",
        title: "Wire protocol (v1)",
        body: "Envelopes, type registry, versioning rules, kernel ingress, Link transport.",
      },
      {
        route: "/docs/security",
        src: "docs/security.md",
        title: "Security posture",
        body: "Identities, roles, gates, approval lifecycle, team policy, artifact and memory laws.",
      },
      {
        route: "/docs/advanced",
        src: "docs/advanced.md",
        title: "Advanced topics",
        body: "Kernel without Compose, Gateway modes, the HarnessAdapter contract, provider adapters, memory broker.",
      },
    ],
  },
  {
    group: "Operations",
    order: 5,
    entries: [
      {
        route: "/docs/runbooks",
        src: "docs/runbooks/index.md",
        title: "Runbooks",
        body: "Symptom → diagnosis → action for every operational scene.",
      },
      {
        route: "/docs/runbooks/gateway-offline",
        src: "docs/runbooks/gateway-offline.md",
        title: "Gateway offline",
      },
      {
        route: "/docs/runbooks/work-stuck",
        src: "docs/runbooks/work-stuck.md",
        title: "Work stuck",
      },
      {
        route: "/docs/runbooks/duplicate-event-suspicion",
        src: "docs/runbooks/duplicate-event-suspicion.md",
        title: "Duplicate event suspicion",
      },
      {
        route: "/docs/runbooks/database-outage",
        src: "docs/runbooks/database-outage.md",
        title: "Database outage",
      },
      {
        route: "/docs/runbooks/artifact-memory-backend-outage",
        src: "docs/runbooks/artifact-memory-backend-outage.md",
        title: "Artifact/memory backend outage",
      },
      {
        route: "/docs/runbooks/provider-api-rate-limit",
        src: "docs/runbooks/provider-api-rate-limit.md",
        title: "Provider API rate limit",
      },
      {
        route: "/docs/runbooks/identity-claim-mapper-error",
        src: "docs/runbooks/identity-claim-mapper-error.md",
        title: "Identity claim-mapper error",
      },
      {
        route: "/docs/runbooks/horizontal-scale-anomalies",
        src: "docs/runbooks/horizontal-scale-anomalies.md",
        title: "Horizontal scale anomalies",
      },
      {
        route: "/docs/runbooks/consent-expired",
        src: "docs/runbooks/consent-expired.md",
        title: "Consent expired",
      },
      {
        route: "/docs/runbooks/notification-undeliverable",
        src: "docs/runbooks/notification-undeliverable.md",
        title: "Notification undeliverable",
      },
      {
        route: "/docs/runbooks/contributor-retired",
        src: "docs/runbooks/contributor-retired.md",
        title: "Contributor retired",
      },
      {
        route: "/docs/runbooks/work-merged",
        src: "docs/runbooks/work-merged.md",
        title: "Work merged",
      },
      {
        route: "/docs/runbooks/manual-verification-tiers",
        src: "docs/runbooks/manual-verification-tiers.md",
        title: "Manual verification tiers",
      },
    ],
  },
];
