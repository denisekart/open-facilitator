// The /architecture page: intro chrome around the generated explorer
// graph. The interactive behaviour lives in the explorer bundle (app.js
// hydrates it over the snapshot); this file shapes the static shell —
// headings and walk titles carry search-index weight and read honestly
// with JS off.

import { scenarios } from "../architecture.mjs";

export function architectureBody({ svg, payload }) {
  const walks = scenarios
    .map(
      (sc) =>
        `<span class="arch-walk"><b>${sc.title}</b> — ${sc.intro}</span>`,
    )
    .join("");
  return `<p class="lede">Explore the system topology: boxes represent running services, and arrows indicate active data flows. Hover a box to inspect connections, click to view guarantees, or select a scenario to follow execution steps.</p>

<div class="arch-controls" id="arch-controls" role="tablist" aria-label="Ways to explore"></div>
<p class="arch-walks" hidden>${walks}</p>

<div class="arch-fig">
  <div class="arch-scroll">${svg}</div>
  <aside class="arch-panel" id="arch-panel" aria-live="polite"></aside>
</div>
<div class="arch-player" id="arch-player" hidden></div>

<h2 id="legend">Reading the map</h2>
<div class="arch-legend">
  <p><span class="al-key" style="border-color:#ab8612;background:#f7f9fb"></span><b>Solid outline</b><span>Self-hosted services. The kernel is a stateless container that persists all state to PostgreSQL.</span></p>
  <p><span class="al-key" style="border-color:#ab8612;background:#faf2d6"></span><b>Gold outline</b><span>Team-operated environments holding team credentials, evaluating local policy, and connecting outbound to the kernel.</span></p>
  <p><span class="al-key" style="border-color:#767d85;background:#f7f9fb"></span><b>Dashed outline</b><span>External infrastructure: PostgreSQL, OIDC identity providers, object storage, and forge platforms.</span></p>
  <p><span class="al-line" style="border-top:2.6px dotted #ab8612"></span><b>The link</b><span>Outbound, resumable SignalR or HTTP connections maintained by team Gateways.</span></p>
</div>

<h2 id="three-planes">Three identity planes</h2>
<p>Authentication boundaries isolate credentials across planes. <b>Human plane:</b> Console and API requests carry OIDC bearer tokens. <b>Link plane:</b> Each Gateway authenticates using a pinned Ed25519 endpoint key. <b>Adapter plane:</b> Chat platforms and forges authenticate via signed webhooks. Platform tokens and model keys remain team-side.</p>

<h2 id="where-things-run">Where workloads run</h2>
<p>The kernel runs continuously as a coordination service without executing application workloads. Workloads execute inside team environments, storing evidence in object storage and state in PostgreSQL. Scale horizontally by running additional kernel replicas behind a load balancer and connecting additional team Gateways.</p>

<script type="application/json" id="arch-data">${payload}</script>`;
}
