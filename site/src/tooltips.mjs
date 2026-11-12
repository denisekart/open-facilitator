// Hover tooltips for diagram nodes, keyed by the node's visible label
// (normalized: lowercase, collapsed whitespace). Injected into SSR'd SVGs
// as native <title> at build time; the site runtime upgrades them to
// styled tooltips. Facts here must stay true to the implementation.

const norm = (s) => s.toLowerCase().replace(/\s+/g, " ").trim();

export const tooltips = {};
function tip(label, text) {
  tooltips[norm(label)] = text;
}

// ---- deployment topology ----
tip(
  "facilitator kernel",
  "The coordination service: work queue, routing policy, approvals, link hub, artifact ledger. Stateless — all durable state lives in your PostgreSQL.",
);
tip("kernel", "The coordination service. Coordinates and remembers; never executes.");
tip(
  "your postgresql, your login provider, your object store",
  "The only infrastructure the kernel requires — three commodities, each replaceable, all yours to back up.",
);
tip(
  "gateway",
  "The team-owned runtime. Dials out to the kernel, holds the team's execution ledger, spawns a harness per assignment. Lives entirely inside one team environment.",
);
tip(
  "workspaces: agents, scripts, people",
  "Whatever actually does the work: runs inside the team's own environment, on the team's own credentials.",
);
tip(
  "workspaces",
  "Agents, scripts, or human task lists — each runs as a child of the team's Gateway.",
);
tip(
  "team environment a — your environment, your keys",
  "One authority boundary: its credentials, its policy, its ledger.",
);
tip(
  "team b — independent, maybe another company",
  "Teams are independent authorities. Neither can reach into the other's environment.",
);
tip(
  "you: curl / browser",
  "Every capability is public HTTP — no SDK required, no backdoor for the vendor either.",
);
tip(
  "facilitator kernel (your postgresql, your login provider, your object store)",
  "The kernel process, holding none of your credentials.",
);
tip(
  "postgresql",
  "The single durable store: works, decisions, ledgers, leases. Back it up; it is the product's memory.",
);
tip(
  "s3 artifacts",
  "Content-addressed artifact storage — S3-compatible, or a local folder for experiments.",
);
tip("s3", "Content-addressed artifact store (S3-compatible).");
tip(
  "keycloak oidc",
  "Login authority. Anything standards-compliant plugs in here — Keycloak, Azure AD, Authelia.",
);
tip("keycloak", "OpenID Connect provider (seeding the demo users).");
tip(
  "demo team gateway",
  "A team runtime already connected over an outbound link, waiting for work.",
);
tip(
  "workspace + deterministic worker",
  "A scripted worker: fixed inputs, fixed outputs — the conformance baseline for real harnesses.",
);
tip("demo workspace", "A linked team container: Gateway + worker, enrolling with a shared token.");
tip(
  "outbound link",
  "Connections only ever point outward from teams to the kernel. Nothing inbound, ever.",
);
tip(
  "the kernel (one container, your server)",
  "The published kernel image: coordination, approvals, artifact ledger. Stateless — pair it with a PostgreSQL you already run.",
);
tip(
  "your login provider",
  "Any OIDC-compliant provider: Azure AD, Keycloak, Authelia — the kernel verifies tokens, it does not store passwords.",
);
tip(
  "your team's gateway",
  "The team-side runtime: npm-installed, dialing out, spawning a harness per assignment.",
);
tip(
  "agents, scripts, people",
  "Whatever actually does the work, inside the team's own environment, on the team's credentials.",
);
tip(
  "artifacts s3 or a folder",
  "Content-addressed artifact storage: S3-compatible in production, a plain folder for experiments.",
);

// ---- work lifecycle (state diagram) ----
tip(
  "created (api, chat, webhook)",
  "One POST (or a chat message) creates a Work: objective, version 1, an entry in the audit timeline.",
);
tip(
  "policy picks a team",
  "Deterministic routing: capability match + team online + policy allow. The decision is recorded with its inputs.",
);
tip(
  "team's agent accepts",
  "The team's Gateway acknowledges the assignment and spawns a harness. A fencing token arbitrates any double-claim.",
);
tip(
  "privileged step",
  "A step the team marked human-gated (e.g. a forge write). The kernel mints a durable, digest-pinned approval request.",
);
tip(
  "human approves",
  "Approval consumed exactly once — a restart or a retry cannot approve twice.",
);
tip(
  "human says not yet",
  "Denials and deferrals are first-class outcomes: recorded, reasoned, auditable.",
);
tip(
  "result + evidence filed",
  "Result manifest + digest-verified artifacts + timeline. Optional memory candidate for lessons worth keeping.",
);
tip("you change your mind", "Cancellation is a normal, audited outcome at almost every state.");
tip("open", "A Work exists and is durable. It can be searched, steered, or cancelled.");
tip(
  "routed",
  "Policy has chosen a team; delivery is in flight or waiting for the team to come online.",
);
tip(
  "inprogress",
  "A harness is executing inside the team's own environment. Progress events land on the timeline as they arrive.",
);
tip(
  "awaitingapproval",
  "A human decision is pending — durable, survives kernel restarts, fires exactly once.",
);
tip(
  "done",
  "Result manifest available, artifacts verified. The timeline is complete and immutable.",
);
tip("cancelled", "Closed without a result; the reason is on the timeline.");

// ---- release pipeline ----
tip("tag release", "A pushed git tag vX.Y.Z triggers the whole pipeline.");
tip("build", "Multi-architecture container builds + cross-language conformance fixtures.");
tip("containers", "Kernel + node-runtime images: amd64 + arm64, SBOMs attached.");
tip("features", "Dev Container Features, OCI-published, semver + major aliases.");
tip("npm packages", "The @ofac scope. Trusted-publisher flow: no stored tokens.");
tip("ghcr", "The org's container registry namespace.");
tip("npm", "Public registry, provenance attached.");
tip(
  "versions.env",
  "Third-party base images get digest-pinned back into the repo by the pipeline itself.",
);
tip("github release", "The release object: images by tag+digest, SBOMs attached.");

// ---- agent sequence ----
tip(
  "your agent (endpoint)",
  "Any process that implements the link protocol: enroll, connect, ack, execute, report.",
);
tip("facilitator", "The kernel: routing, approvals, ledger. Your agent talks only to it.");

// ---- system landscape ----
tip(
  "console (browser)",
  "Served by the kernel itself at its own URL. A client of the public API — anything it shows, your tooling can query too.",
);
tip(
  "api clients curl · agents · ci",
  "Every capability is plain HTTPS with an OIDC bearer: scripts, CI jobs, and agent runtimes are first-class operators.",
);
tip(
  "decisions in chat approve · deny · steer",
  "Where humans stay in the loop: approval prompts arrive in the chat you already use, and answers travel back as signed events.",
);
tip(
  "ingress idempotent admission",
  "The single front door for events. Deduplicated by (source, event id) — retries are safe, double-apply is impossible.",
);
tip(
  "routing policy + approval engine",
  "Deterministic decisions: capability match, team online, policy allow. Models propose; only this engine commits. Every decision recorded with its inputs.",
);
tip(
  "link hub outbound-only transport",
  "Teams dial in and hold the connection open. No inbound ports on team environments, ever. SignalR reference, plain HTTP fallback.",
);
tip(
  "artifact broker + memory broker",
  "Digest-verified artifact filing and governed shared memory. The kernel brokers, your object store holds the bytes.",
);
tip(
  "serves console + public api",
  "One origin, one surface: console, API, health, and OpenAPI descriptions from the same process.",
);
tip(
  "postgresql the durable record",
  "The only mandatory dependency. Works, ledgers, leases, approvals, timelines — if it is not in here, it did not happen.",
);
tip(
  "oidc provider login + claims",
  "Any standards-compliant login authority. The kernel maps claims you configure; it stores no passwords.",
);
(tip(
  "s3-compatible store artifact bytes",
  "Content-addressed artifact storage. S3, R2, MinIO, SeaweedFS — whatever you already operate.",
),
  tip(
    "gateway team ledger + policy",
    "The team's border control: one outbound connection, its own execution ledger, its own allow/deny policy before anything runs.",
  ));
tip(
  "harness: llm agent",
  "An autonomous agent runtime doing the assignment with the team's models and keys. Proposes; the gate disposes.",
);
tip(
  "harness: deterministic script",
  "Scripted worker: fixed inputs, fixed outputs. The conformance baseline every harness must match.",
);
tip(
  "harness: your own",
  "Anything you can spawn — a data pipeline, an RPA bot, a human checklist. The SDK + conformance kit make it first-class.",
);
tip(
  "forge connector privileged writes",
  "The only component holding the team's forge credentials. Outbound writes can ride human approvals.",
);
tip(
  "chat adapter mattermost / telegram",
  "Normalizes chat events into work events and delivers approval prompts inbound to the team's channels.",
);
tip(
  "oci launcher spawns fresh workspaces",
  "Stands up a fresh, linked team environment on request, over its own outbound link. Nothing inbound.",
);
tip(
  "forge github · forgejo · gitlab",
  "Source hosting: issues and PRs feed Works; privileged writes flow back through the connector under approval.",
);
tip(
  "chat platform",
  "Mattermost, Telegram, or your own — inbound normalization plus approval delivery.",
);
tip(
  "fresh linked environment",
  "A clean workspace, enrolled and connected in one step — the substrate pattern for one-shot agent runs.",
);
tip(
  "console",
  "The operator surface, served by the kernel: search, fleet, deliveries, audit trail.",
);
tip(
  "api clients",
  "Plain HTTPS with an OIDC bearer: scripts, CI, and agents are first-class operators.",
);
tip(
  "decisions in chat",
  "Approval prompts and answers as signed events, inside the chat you already use.",
);
tip(
  "ingress",
  "Single front door for events: dedupe by (source, event id). Retries safe, double-apply impossible.",
);
tip(
  "routing policy + approval engine",
  "Deterministic decisions with recorded inputs. Models propose; only this engine commits.",
);
tip("link hub", "Outbound-only transport: teams dial in, no inbound ports, ever.");
tip(
  "artifact broker + memory broker",
  "Digest-verified filing and governed memory. Bytes live in your object store.",
);
tip("serves console + public api", "One origin: console, API, health, OpenAPI — same process.");
tip(
  "oidc provider",
  "Any standards-compliant login authority; claims mapped by configuration you own.",
);

export function tooltipFor(label) {
  return tooltips[norm(label)] || null;
}
export { norm };
