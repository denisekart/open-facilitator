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
  { label: "Architecture", href: "/#/architecture" },
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
    "A coordination kernel for teams of humans and AI agents. Hand it a piece of work — it tracks who has it, waits for human approval, files verified evidence, and survives every restart because state lives in your database.",
};

// The interactive hero scene: a work item crossing four stations, with the
// audit record accumulating underneath — the product promise in one image.
export const relay = {
  path: "M 75 62 L 525 62",
  steps: [
    {
      x: 75,
      who: "You or an agent",
      title: "A Work is created",
      body: "One POST to the API or an inbound chat message creates a Work with an objective. The record lands as a row in your database and outlives every worker process.",
      log: "work opened: refresh billing docs — via chat",
    },
    {
      x: 225,
      who: "The kernel",
      title: "Routing decides who acts",
      body: "Deterministic policy evaluates team capabilities, mints an assignment, and dispatches it over an outbound link. If that team is offline, the work waits in the durable queue.",
      log: "routed to team-alpha by policy (audited)",
    },
    {
      x: 375,
      who: "A team's environment",
      title: "Agents execute, humans approve",
      body: "Execution runs in the team's environment on team-owned credentials. The agent proposes changes; a designated human gate approves before privileged writes execute.",
      log: "agent proposed merge → alice approved (identity kept)",
    },
    {
      x: 525,
      who: "The record",
      title: "Evidence is filed",
      body: "Outputs become digest-verified artifacts, decisions become audit entries, and reusable lessons become memory candidates. The full provenance remains queryable.",
      log: "evidence filed · status: done",
    },
  ],
};

// The landing "how it works" story: one concrete week, in order.
export const story = [
  {
    who: "Monday, 09:02 — a person",
    title: "The ask goes in",
    body: "A team lead opens an issue or an agent creates a Work from a chat thread. The Work is durable, versioned, and searchable from the moment of creation.",
    cmd: 'curl -X POST https://facilitator.example/api/works \\\n  -H "authorization: Bearer $TOKEN" \\\n  -d \'{"objective": "draft the billing migration plan"}\'',
  },
  {
    who: "Monday, 09:02 — the kernel",
    title: "Policy routes without guessing",
    body: "The kernel matches required capabilities against advertised team capacity, verifies online state, and records the assignment. Routing decisions are deterministic and audited.",
  },
  {
    who: "Monday, 09:04 — a team's environment",
    title: "An agent accepts the assignment over an outbound link",
    body: "The team's Gateway dials out to the kernel, accepts the assignment, and starts an execution harness. The kernel holds no team credentials, source keys, or model endpoints.",
  },
  {
    who: "Monday, 11:37 — a human gate",
    title: "The agent proposes; a human approves",
    body: "The draft is ready, but opening a pull request is a privileged write. The kernel mints a durable approval request and delivers it into team chat, surviving mid-flight restarts.",
  },
  {
    who: "Monday, 13:10 — the record",
    title: "Evidence lands in the durable store",
    body: "The result and its manifest become digest-verified artifacts. The timeline records every event and decision, and proven lessons promote to scoped shared memory.",
  },
];

// What ships, in consumer terms: artifact coordinates, not source paths.
export const components = [
  {
    name: "The kernel",
    tag: "container image",
    body: "Stateless service running as a container. Coordinates work lifecycles, routing policy, approvals, links, artifacts, memory, and the admin console on your PostgreSQL and OIDC.",
    docs: "/artifacts",
  },
  {
    name: "The Gateway",
    tag: "npm · @ofac/gateway",
    body: "Team-side Node runtime. Maintains one outbound connection to the kernel, multiplexes child workspaces, and stores state in a local ledger.",
    docs: "/artifacts",
  },
  {
    name: "Harness adapters",
    tag: "npm · @ofac/adapter",
    body: "Translators between the Gateway and execution harnesses. Shipped packages support deterministic workers, oh-my-pi agents, and Codex, with an SDK for custom harnesses.",
    docs: "/extend",
  },
  {
    name: "Provider adapters",
    tag: "npm · chat + forge",
    body: "Chat and forge adapters for Mattermost, Telegram, GitHub, Forgejo, and GitLab. Normalizes inbound webhooks and gates outbound privileged writes through team policy.",
    docs: "/integrations",
  },
  {
    name: "The wire contract",
    tag: "open protocol",
    body: "CloudEvents 1.0 envelopes with versioned JSON Schemas and OpenAPI 3.1 specifications. A cross-language conformance suite verifies interoperability.",
    docs: "/docs/protocol",
  },
  {
    name: "Dev Container Features",
    tag: "OCI images · ghcr.io",
    body: "Turns any team container into a linked workspace through devcontainer.json. Automatically enrolls, connects, and advertises capabilities.",
    docs: "/artifacts",
  },
];

// Why it exists — one claim per card, each verifiable in the open protocol.
export const benefits = [
  {
    title: "Agents come and go; the work remains",
    body: "If a worker crashes mid-approval, the run resumes once. Nothing lives exclusively in a socket, container, or context window. The store is your PostgreSQL database.",
  },
  {
    title: "No one loses authority over their environment",
    body: "Teams connect outbound. The kernel never holds source credentials, deployment keys, or model access. Every external write passes team-side policy first.",
  },
  {
    title: "Humans stay in the loop where required",
    body: "Privileged actions require durable, digest-pinned, single-use approvals delivered to chat or console. A restart or retry cannot approve twice.",
  },
  {
    title: "AI without amnesia",
    body: "Outputs become content-addressed artifacts, decisions become audit entries, and verified lessons become scoped memories for future tasks.",
  },
  {
    title: "A protocol, not a platform",
    body: "Built on CloudEvents, JSON Schema, and OpenAPI with cross-language conformance tests. Swap harnesses, replace chat providers, or implement clients in any language.",
  },
  {
    title: "Replaceable infrastructure",
    body: "The kernel requires only three dependencies: PostgreSQL, an OIDC provider, and S3-compatible or local storage. Message buses, vector stores, and model gateways remain optional.",
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
        route: "/architecture",
        generated: "architecture",
        title: "Explore the architecture",
        body: "An interactive map of every part, who operates it, and guided scenario walkthroughs.",
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
        route: "/workflows",
        src: "site/src/pages/workflows.md",
        title: "Authoring flows",
        generated: "workflows",
        body: "The workflow plane: event, schedule, and callable triggers over a closed step palette, authored in the console and stored as immutable versions.",
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
        body: "The concepts and vocabulary from zero: Open Facilitator, Projects, Works, Gateways, Links, approvals, artifacts, memory.",
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
        body: "A self-contained laptop tour of the whole system, end to end, closed out by the same twelve-leg acceptance run that gates each release.",
      },
      {
        route: "/docs/getting-started-agents",
        src: "docs/getting-started-agents.md",
        title: "Getting started (AI agents)",
        body: "Orientation for an agent that will run, drive, or extend the system from source: the layout, the gates, the document that owns each rule.",
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
