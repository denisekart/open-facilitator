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
tip("facilitator kernel", "The coordination service: work queue, routing policy, approvals, link hub, artifact ledger. Stateless — all durable state lives in your PostgreSQL.");
tip("kernel", "The coordination service. Coordinates and remembers; never executes.");
tip("your postgresql, your login provider, your object store", "The only infrastructure the kernel requires — three commodities, each replaceable, all yours to back up.");
tip("gateway", "The team-owned runtime. Dials out to the kernel, holds the team's execution ledger, spawns a harness per assignment.");
tip("workspaces: agents, scripts, people", "Whatever actually does the work on the team's machines, with the team's own credentials.");
tip("workspaces", "Agents, scripts, or human task lists — each runs as a child of the team's Gateway.");
tip("team a — your machines, your keys", "One authority boundary: its credentials, its policy, its ledger.");
tip("team b — independent, maybe another company", "Teams are independent authorities. Neither can reach into the other's machines.");
tip("you: curl / browser", "Every capability is public HTTP — no SDK required, no backdoor for the vendor either.");
tip("facilitator kernel (your postgresql, your login provider, your object store)", "The kernel process, holding none of your credentials.");
tip("postgresql", "The single durable store: works, decisions, ledgers, leases. Back it up; it is the product's memory.");
tip("s3 artifacts", "Content-addressed artifact storage — S3-compatible, or a local folder for experiments.");
tip("s3", "Content-addressed artifact store (S3-compatible).");
tip("keycloak oidc", "Login authority. Anything standards-compliant plugs in here — Keycloak, Azure AD, Authelia.");
tip("keycloak", "OpenID Connect provider (seeding the demo users).");
tip("demo team gateway", "A team runtime already connected over an outbound link, waiting for work.");
tip("workspace + deterministic worker", "A scripted worker: fixed inputs, fixed outputs — the conformance baseline for real harnesses.");
tip("demo workspace", "A linked team container: Gateway + worker, enrolling with a shared token.");
tip("outbound link", "Connections only ever point outward from teams to the kernel. Nothing inbound, ever.");
tip("kernel (one container)", "The published kernel image: coordination, approvals, artifact ledger. Stateless — pair it with a PostgreSQL you already run.");
tip("your browser / curl", "Everything is public HTTP: the console, your scripts, and your agents all speak the same API.");
tip("your team: gateway + agents/scripts", "The team side: npm-installed Gateway dialing out, harnesses doing the actual work with the team's own credentials.");

// ---- work lifecycle (state diagram) ----
tip("created (api, chat, webhook)", "One POST (or a chat message) creates a Work: objective, version 1, an entry in the audit timeline.");
tip("policy picks a team", "Deterministic routing: capability match + team online + policy allow. The decision is recorded with its inputs.");
tip("team's agent accepts", "The team's Gateway acknowledges the assignment and spawns a harness. A fencing token arbitrates any double-claim.");
tip("privileged step", "A step the team marked human-gated (e.g. a forge write). The kernel mints a durable, digest-pinned approval request.");
tip("human approves", "Approval consumed exactly once — a restart or a retry cannot approve twice.");
tip("human says not yet", "Denials and deferrals are first-class outcomes: recorded, reasoned, auditable.");
tip("result + evidence filed", "Result manifest + digest-verified artifacts + timeline. Optional memory candidate for lessons worth keeping.");
tip("you change your mind", "Cancellation is a normal, audited outcome at almost every state.");
tip("open", "A Work exists and is durable. It can be searched, steered, or cancelled.");
tip("routed", "Policy has chosen a team; delivery is in flight or waiting for the team to come online.");
tip("inprogress", "A harness is executing on the team's machines. Progress events land on the timeline as they arrive.");
tip("awaitingapproval", "A human decision is pending — durable, survives kernel restarts, fires exactly once.");
tip("done", "Result manifest available, artifacts verified. The timeline is complete and immutable.");
tip("cancelled", "Closed without a result; the reason is on the timeline.");

// ---- release pipeline ----
tip("tag release", "A pushed git tag vX.Y.Z triggers the whole pipeline.");
tip("build", "Multi-architecture container builds + cross-language conformance fixtures.");
tip("containers", "Kernel + node-runtime images: amd64 + arm64, SBOMs attached.");
tip("features", "Dev Container Features, OCI-published, semver + major aliases.");
tip("npm packages", "The @ofac scope. Trusted-publisher flow: no stored tokens.");
tip("ghcr", "The org's container registry namespace.");
tip("npm", "Public registry, provenance attached.");
tip("versions.env", "Third-party base images get digest-pinned back into the repo by the pipeline itself.");
tip("github release", "The release object: images by tag+digest, SBOMs attached.");

// ---- agent sequence ----
tip("your agent (endpoint)", "Any process that implements the link protocol: enroll, connect, ack, execute, report.");
tip("facilitator", "The kernel: routing, approvals, ledger. Your agent talks only to it.");
tip("gateway", "The team runtime your process attaches to (or embeds).");

export function tooltipFor(label) {
  return tooltips[norm(label)] || null;
}
export { norm };
