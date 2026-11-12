// The /architecture page: intro chrome around the generated explorer
// graph. The interactive behaviour lives in app.js; this file only shapes
// the static shell (headings carry search-index weight).

export function architectureBody({ svg, payload }) {
  return `<p class="lede">One map, drawn to scale with the system: a box is a thing that runs, an
arrow is data actually moving. Hover a box to see what touches it, click it
for what it guarantees. Then walk a scenario to watch the pieces light up in
sequence.</p>

<div class="arch-controls" id="arch-controls" role="tablist" aria-label="Ways to explore"></div>

<div class="arch-fig">
  <div class="arch-scroll">${svg}</div>
  <aside class="arch-panel" id="arch-panel" aria-live="polite"></aside>
</div>
<div class="arch-player" id="arch-player" hidden></div>

<h2 id="legend">Reading the map</h2>
<div class="arch-legend">
  <p><span class="al-key" style="border-color:#0B5FBF;background:#F2F7FD"></span><b>Solid outline</b><span>parts you deploy yourself. The kernel is one stateless container; it keeps no secrets of its own, which is why it can restart at any moment.</span></p>
  <p><span class="al-key" style="border-color:#B25E09;background:#FDFAF3"></span><b>Warm outline</b><span>a team's own environment. It holds the team's keys, makes its own allow/deny calls, and dials <i>out</i> to the kernel — nothing can dial in.</span></p>
  <p><span class="al-key" style="border-color:#9AA6B2;background:#F7F8F9"></span><b>Dashed outline</b><span>replaceable or external: your Postgres, your login provider, your object store, and the platforms teams already use.</span></p>
  <p><span class="al-line" style="border-top:2.6px dotted #0B5FBF"></span><b>The link</b><span>every team-side connection, always outbound, resumable after any disconnect.</span></p>
</div>

<h2 id="three-planes">Three planes of identity</h2>
<p>Who may talk to what, and which secret lives where. <b>Human plane:</b>
console and API calls carry an OIDC bearer from your login provider — every
action keeps an identity. <b>Link plane:</b> each gateway authenticates with
its own link token; revoke one team and every other team is untouched.
<b>Adapter plane:</b> chat platforms and forges reach team-side adapters
first, signature-verified, before anything becomes a kernel event. Platform
tokens and model keys never enter the kernel — that containment is why a
kernel breach is survivable. The <em>identity</em> scenario walks it.</p>

<h2 id="where-things-run">Where things run</h2>
<p>The kernel is the only always-on piece, and it executes nothing: work
happens in team environments, evidence lands in your object store, state in
your Postgres. Scaling is horizontal and boring — add kernels behind a
load balancer, add teams by pointing another gateway at the hub. There is no
second control plane to adopt. Deep dives:
<a href="/docs/overview">concepts</a> ·
<a href="/docs/protocol">the wire contract</a> ·
<a href="/docs/security">security posture</a> ·
<a href="/docs/advanced">operations</a>.</p>

<script type="application/json" id="arch-data">${payload}</script>`;
}
