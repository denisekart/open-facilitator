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
  "The published kernel image: coordination, approvals, artifact ledger. Stateless — pair it with a PostgreSQL you already run.",
);
tip(
  "postgresql",
  "The single durable store: works, decisions, ledgers, leases. Back it up; it is the product's memory.",
);
tip(
  "artifacts s3 or a folder",
  "Content-addressed artifact storage: S3-compatible in production, a plain folder for experiments.",
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
  "agents, scripts, humans",
  "Whatever actually does the work, inside the team's own environment, on the team's credentials.",
);
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

export function tooltipFor(label) {
  return tooltips[norm(label)] || null;
}
export { norm };
