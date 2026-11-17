// Hover tooltips for diagram nodes, keyed by the node's visible label
// (normalized: lowercase, collapsed whitespace). Injected into SSR'd SVGs
// as native <title> at build time; the runtime upgrades them to styled
// tooltips. Facts here must stay true to the implementation. Keys that no
// rendered diagram carries are pruned — the interactive explorer keeps its
// own per-node copy in architecture.mjs.

const norm = (s) => s.toLowerCase().replace(/\s+/g, " ").trim();

export const tooltips = {};
function tip(label, text) {
  tooltips[norm(label)] = text;
}

tip(
  "the kernel (one container, your server)",
  "The published kernel image: work lifecycle, approvals, and artifact ledger. Stateless container running against PostgreSQL.",
);
tip(
  "postgresql",
  "The primary durable store: works, decisions, outbox, and session leases. Back it up as the system of record.",
);
tip(
  "artifacts s3 or a folder",
  "Content-addressed storage: S3-compatible in production, local filesystem directory for testing.",
);
tip(
  "your login provider",
  "Any OIDC-compliant provider: Keycloak, Entra ID, Okta. The kernel validates tokens without storing user credentials.",
);
tip(
  "your team's gateway",
  "The team-side Node runtime: connects outbound, manages child workspaces, and executes local policy.",
);
tip(
  "agents, scripts, humans",
  "Workload executors running inside the team environment using team-owned credentials.",
);
tip("open", "A Work exists and is durable. It can be searched, steered, or cancelled.");
tip(
  "routed",
  "Policy has assigned a team; delivery is in flight or queued waiting for the Gateway.",
);
tip(
  "inprogress",
  "A harness executes inside the team workspace, emitting progress events to the timeline.",
);
tip(
  "awaitingapproval",
  "Execution is paused on a durable approval request, surviving restarts until resolved.",
);
tip(
  "done",
  "Execution completed with verified result manifests and artifacts filed in storage.",
);
tip("cancelled", "Closed without a result; the cancellation reason records on the timeline.");
tip(
  "agent endpoint",
  "Any runtime connecting to the link plane: enrolls an Ed25519 key and proves possession per session.",
);
tip(
  "facilitator kernel",
  "The coordination service: admission, durable delivery ledger, session leases, and event routing.",
);
tip(
  "adapter verify webhook, normalize",
  "Translates external webhooks into standard CloudEvents; holds no outbound write credentials.",
);
tip(
  "connector privileged write",
  "Executes approved outbound writes team-side using local platform credentials.",
);
tip(
  "kernel image - stateless, any container host",
  "Stateless multi-arch container. All relational state persists to PostgreSQL.",
);
tip(
  "postgresql - durable relational state",
  "Durable storage for works, timeline events, routing configurations, and audit records.",
);
tip(
  "s3-compatible store - artifact storage",
  "Content-addressed binary storage for reports, diffs, datasets, and execution evidence.",
);
tip(
  "oidc provider - user authentication",
  "Validates operator and human identities for API and console access.",
);
tip(
  "@ofac/adapter - child process communicating via line-oriented stdio",
  "Harness protocol interface: accept, report progress, propose writes, and complete execution.",
);
tip(
  "webhook ingress and approval-gated outbound writes",
  "Pairing inbound event normalization with gated outbound write execution.",
);
tip(
  "custom gateway or service implementing wire schemas",
  "Custom implementation conforming to published CloudEvents, OpenAPI, and JSON Schemas.",
);

export function tooltipFor(label) {
  return tooltips[norm(label)] || null;
}
export { norm };
