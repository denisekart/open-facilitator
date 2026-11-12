// Site content manifest. `site/` is fully self-contained: it reads the repo's
// docs tree at build time but shares no code with the facilitator runtime.
// Every file must live under site/src or docs/.

export const repo = {
  owner: "denisekart",
  ownerSlug: "denisekart",
  url: "https://github.com/denisekart/open-facilitator",
  npmScope: "@ofac",
  ghcr: "ghcr.io/denisekart/open-facilitator",
};

export const nav = [
  { label: "Overview", href: "#top" },
  { label: "Components", href: "#components" },
  { label: "Why", href: "#why" },
  { label: "Get started", href: "#start" },
  { label: "Docs", href: "#/docs" },
];

// The four answers the landing hero must give, verbatim.
export const hero = {
  tagline:
    "Portable, pluggable, multi-workspace orchestration of autonomous, assisted, and deterministic workloads — across independently operated execution environments.",
  oneLiner:
    "One persistent Facilitator coordinates the work; your teams keep execution. The kernel holds durable state, routing, approvals, and evidence — never your credentials, never your runtime.",
  demo: {
    caption: "The whole product on one laptop:",
    lines: [
      "./referencectl up slim",
      "./referencectl verify   # 11 acceptance hops, unattended",
    ],
  },
};

export const benefits = [
  {
    icon: "shield",
    title: "You keep execution authority",
    body: "The kernel holds no SCM/issue/deploy credential. Harnesses, models, secrets, and every external write stay team-side — the Facilitator coordinates, it never takes over. No platform lock-in on how work gets done.",
  },
  {
    icon: "recover",
    title: "Recoverable by construction",
    body: "Transactional inbox/outbox, leases, fencing generations, idempotent admission. Crash a kernel mid-approval and the run resumes exactly once — nothing lives only in a socket or an ephemeral container.",
  },
  {
    icon: "gavel",
    title: "Deterministic authority, human gates",
    body: "Models propose; only deterministic policy and verified identity evidence authorize. Approvals are durable, digest-pinned, single-use records. Every gate decision — allow and deny — is audited.",
  },
  {
    icon: "wire",
    title: "Protocol, not product",
    body: "CloudEvents 1.0 + versioned JSON Schema + OpenAPI 3.1, with a cross-language conformance suite. SignalR reference transport, plain HTTP fallback — implement any component in any language from the contract alone.",
  },
  {
    icon: "layers",
    title: "Work is not code-shaped",
    body: "Research reports, datasets, documents, PRs, and test evidence are first-class outputs: content-addressed artifacts, canonical result manifests, governed memory with team-scoped ACLs.",
  },
  {
    icon: "feather",
    title: "Small honest footprint",
    body: "Required infrastructure: PostgreSQL + OIDC + an object store. Kafka, Temporal, vector DBs, and LLMs are optional adapters, never load-bearing. The slim reference stack runs on a laptop.",
  },
];

export const components = [
  {
    name: "Facilitator kernel",
    pkg: "OCI · ghcr.io",
    body: "The .NET 10 service: work lifecycle, identity and policy gates, Link hub, artifact + memory brokers, embedded admin SPA, operator API and CLI.",
    path: "src/facilitator/",
    docs: "/docs/overview",
  },
  {
    name: "Wire contract",
    pkg: "JSON Schema · OpenAPI",
    body: "CloudEvents 1.0 envelopes, the full v1 type registry, frozen OpenAPI surfaces, and conformance fixtures two independent language stacks must pass.",
    path: "protocol/",
    docs: "/docs/protocol",
  },
  {
    name: "Team Gateway",
    pkg: "npm · @ofac/gateway",
    body: "The team-owned Node runtime that multiplexes Workspaces over one outbound Link endpoint, evaluates team policy, and keeps the execution ledger.",
    path: "src/gateway/",
    docs: "/docs/advanced",
  },
  {
    name: "Workspace Link SDK",
    pkg: "npm · @ofac/workspace-link",
    body: "Link client SDK + ofac-linkd supervisor: enroll, advertise capabilities, connect, buffer, and replay after a crash from a durable state file.",
    path: "src/workspace-link/",
    docs: "/docs/protocol",
  },
  {
    name: "Harness adapters",
    pkg: "npm · @ofac/adapter",
    body: "Thin translators from the Gateway child wire to one execution harness. Reference workers: deterministic, oh-my-pi, and Codex — plus a conformance kit for your own.",
    path: "src/adapters/harnesses/",
    docs: "/docs/advanced",
  },
  {
    name: "Communication adapters",
    pkg: "npm · chat + forge legs",
    body: "Mattermost, Telegram, Forgejo/GitHub/GitLab pairs, Kafka: they normalize provider events into the central domain types and execute outbound commands team-side.",
    path: "src/adapters/communication/",
    docs: "/docs/advanced",
  },
  {
    name: "Dev Container Features",
    pkg: "OCI features · ghcr.io",
    body: "One-Feature onboarding: add a single feature to any team container and it becomes a linked, headless workspace. Six features ship today.",
    path: "features/",
    docs: "/docs/getting-started",
  },
  {
    name: "Reference environment",
    pkg: "Docker Compose",
    body: "Slim and full stacks plus referencectl: boot, status, tokens, and the acceptance run that proves every hop — the same run every change must pass.",
    path: "reference/",
    docs: "/docs/getting-started",
  },
];

// Guides authored for the site (site/src/guides/*.md).
export const guides = [
  {
    id: "human",
    route: "/guide/human",
    title: "Getting started — Humans",
    kicker: "You drive a browser and a terminal",
    body: "Boot the whole stack, run your first Work through approval to artifact, and tour the admin SPA.",
    minutes: 10,
    src: "site/src/guides/human.md",
  },
  {
    id: "agent",
    route: "/guide/agent",
    title: "Getting started — AI agents",
    kicker: "You are an agent reading this programmatically",
    body: "The dense orientation: auth, the HTTP surface, link enrollment, where the machine-readable contract lives.",
    minutes: 6,
    src: "site/src/guides/agent.md",
  },
];

// docs/*.md published to the site (path = source of truth, route = site route).
export const docs = [
  {
    group: "Start here",
    order: 1,
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
    order: 2,
    entries: [
      {
        route: "/docs/getting-started",
        src: "docs/getting-started.md",
        title: "Reference environment walkthrough",
        body: "The full human walkthrough of the reference environment, end to end.",
      },
      {
        route: "/docs/getting-started-agents",
        src: "docs/getting-started-agents.md",
        title: "Agent orientation (in-repo)",
        body: "Agent-facing orientation: auth, HTTP surface, enrollment, repo law.",
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
    order: 3,
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
    order: 4,
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
