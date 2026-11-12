// Landing/404/document template. Pure function: state -> full HTML page.
import { hero, benefits, components, guides, repo } from "../config.mjs";

export const ICON = `<svg class="logo" viewBox="0 0 32 32" fill="none" aria-hidden="true">
<path d="M2,22 H8 V28 H2 Z" fill="var(--mark-leg)"/><path d="M24,22 H30 V28 H24 Z" fill="var(--mark-leg)"/>
<path d="M2.00,22.00 A14,14 0 0 1 4.67,13.77 L9.53,17.30 A8,8 0 0 0 8.00,22.00 Z" fill="var(--mark-leg)"/>
<path d="M4.67,13.77 A14,14 0 0 1 11.67,8.69 L13.53,14.39 A8,8 0 0 0 9.53,17.30 Z" fill="var(--mark-leg)"/>
<path d="M20.33,8.69 A14,14 0 0 1 27.33,13.77 L22.47,17.30 A8,8 0 0 0 18.47,14.39 Z" fill="var(--mark-leg)"/>
<path d="M27.33,13.77 A14,14 0 0 1 30.00,22.00 L24.00,22.00 A8,8 0 0 0 22.47,17.30 Z" fill="var(--mark-leg)"/>
<path d="M10.59,5.36 A17.5,17.5 0 0 1 21.41,5.36 L18.47,14.39 A8,8 0 0 0 13.53,14.39 Z" fill="var(--mark-keystone)"/>
<circle cx="16" cy="9.4" r="1.9" fill="var(--mark-dot)"/></svg>`;

const ICONS = {
  shield: `<path d="M12 2 4 5.5V11c0 5.2 3.4 9.6 8 11 4.6-1.4 8-5.8 8-11V5.5Z" fill="none" stroke="currentColor" stroke-width="2"/><path d="m8.5 11.5 2.5 2.5 5-5.5" fill="none" stroke="currentColor" stroke-width="2"/>`,
  recover: `<path d="M12 4a8 8 0 1 1-7.6 5.5" fill="none" stroke="currentColor" stroke-width="2"/><path d="M4 4v6h6" fill="none" stroke="currentColor" stroke-width="2"/><circle cx="12" cy="12" r="2.4" fill="currentColor"/>`,
  gavel: `<path d="m3 21 7-7M8 6l6 6M11 3l8 8M13 5 9 9M16 2l6 6-2 2-6-6ZM12 9l6 6-2 2-6-6Z" fill="none" stroke="currentColor" stroke-width="2"/>`,
  wire: `<circle cx="5" cy="12" r="2.2" fill="currentColor"/><circle cx="19" cy="5" r="2.2" fill="currentColor"/><circle cx="19" cy="19" r="2.2" fill="currentColor"/><path d="M7 11.2 17 5.8M7 12.8 17 18.2" stroke="currentColor" stroke-width="2"/>`,
  layers: `<path d="m12 3 9 5-9 5-9-5Z" fill="none" stroke="currentColor" stroke-width="2"/><path d="m3 13 9 5 9-5M3 17l9 5 9-5" fill="none" stroke="currentColor" stroke-width="2"/>`,
  feather: `<path d="M20 4c-6 0-13 5-14 14l-2 2M20 4c1 6-2 12-9 13H7M20 4l-9 9" fill="none" stroke="currentColor" stroke-width="2"/>`,
};

function heroSvg() {
  // the arch with linked workspaces: big mark + nodes
  return `<svg viewBox="0 0 560 300" class="arch" role="img" aria-label="A keystone arch holding up connected workspaces over a foundation">
  <defs>
    <linearGradient id="archfade" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#12B48F"/><stop offset="1" stop-color="#0B8A70"/>
    </linearGradient>
  </defs>
  <!-- foundation: durable state -->
  <rect x="40" y="252" width="480" height="14" rx="7" fill="#123B33"/>
  <text x="280" y="292" text-anchor="middle" class="arch-label" fill="#7FB8AC">PostgreSQL · durable state</text>
  <!-- arch: 5 voussoirs, keystone lifted -->
  <g transform="translate(280 250) scale(9.2) translate(-16 -22)">
    <path d="M2,22 H8 V28 H2 Z" fill="url(#archfade)"/>
    <path d="M24,22 H30 V28 H24 Z" fill="url(#archfade)"/>
    <path d="M2.00,22.00 A14,14 0 0 1 4.67,13.77 L9.53,17.30 A8,8 0 0 0 8.00,22.00 Z" fill="url(#archfade)"/>
    <path d="M4.67,13.77 A14,14 0 0 1 11.67,8.69 L13.53,14.39 A8,8 0 0 0 9.53,17.30 Z" fill="url(#archfade)"/>
    <path d="M20.33,8.69 A14,14 0 0 1 27.33,13.77 L22.47,17.30 A8,8 0 0 0 18.47,14.39 Z" fill="url(#archfade)"/>
    <path d="M27.33,13.77 A14,14 0 0 1 30.00,22.00 L24.00,22.00 A8,8 0 0 0 22.47,17.30 Z" fill="url(#archfade)"/>
    <g class="ks">
      <path d="M10.59,5.36 A17.5,17.5 0 0 1 21.41,5.36 L18.47,14.39 A8,8 0 0 0 13.53,14.39 Z" fill="#F2A33C"/>
      <circle cx="16" cy="9.4" r="1.9" fill="#082A25"/>
    </g>
  </g>
  <!-- team nodes hanging on the arch legs -->
  <g class="node n1"><rect x="6" y="150" width="86" height="34" rx="9"/><text x="49" y="171" text-anchor="middle">Team A</text></g>
  <g class="node n2"><rect x="468" y="150" width="86" height="34" rx="9"/><text x="511" y="171" text-anchor="middle">Team B</text></g>
  <g class="node n3"><rect x="237" y="26" width="86" height="30" rx="9"/><text x="280" y="46" text-anchor="middle">Facilitator</text></g>
  <path class="wire w1" d="M49 150 V120 Q49 104 65 104 H150"/><text x="154" y="108" class="arch-label" fill="#5FA897">outbound link</text>
  <path class="wire w2" d="M511 150 V120 Q511 104 495 104 H410"/><text x="406" y="108" class="arch-label" fill="#5FA897" text-anchor="end">outbound link</text>
</svg>`;
}

export function landingHtml(opts = {}) {
  const { notFound = false, base = "/" } = opts;
  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${notFound ? "Not found" : "Open Facilitator — durable coordination for human + agent work"}</title>
<meta name="description" content="Portable, pluggable, multi-workspace orchestration of autonomous, assisted, and deterministic workloads across independently operated execution environments.">
<link rel="icon" href="favicon.svg" type="image/svg+xml">
<link rel="stylesheet" href="styles.css">
</head>
<body>
<a class="skip" href="#main">Skip to content</a>
<header class="topbar">
  <a class="brand" href="index.html">${ICON}<span>Open&nbsp;Facilitator</span></a>
  <nav class="topnav" aria-label="Main">
    <a href="index.html#why">Why</a>
    <a href="index.html#how">How it works</a>
    <a href="index.html#components">Components</a>
    <a href="index.html#start">Get started</a>
    <a href="#/docs">Docs</a>
    <a class="ghost" rel="noopener" href="${repo.url}">GitHub</a>
    <form class="searchbox" action="#/search" role="search">
      <input id="q" type="search" name="q" placeholder="Search docs…" aria-label="Search documentation" autocomplete="off">
    </form>
  </nav>
</header>
<main id="main" class="site">
${notFound ? notFoundBody(base) : homeBody()}
</main>
<footer class="foot">
  <div>${ICON}&nbsp; Open Facilitator — built and maintained by teams that run mixed human/agent workloads.</div>
  <nav class="footnav" aria-label="Footer">
    <a rel="noopener" href="${repo.url}">GitHub</a>
    <a href="#/docs">Docs</a>
    <a href="#/docs/getting-started">Reference environment</a>
    <a href="#/docs/protocol">Protocol</a>
  </nav>
  <div class="fine">Dual-licensed AGPL-3.0-or-later / commercial. Every artifact ships its license.</div>
</footer>
<script src="app.js"></script>
</body></html>`;
}

function homeBody() {
  return `
<section class="hero" id="top">
  <div class="hero-copy">
    <p class="kicker">Open-source coordination kernel · .NET + TypeScript · AGPL</p>
    <h1>Coordination that outlives your agents.</h1>
    <p class="tagline">${hero.tagline}</p>
    <p class="lede">${hero.oneLiner}</p>
    <div class="cta">
      <a class="btn" href="#start">Get started</a>
      <a class="btn inverse" href="#/docs">Read the docs</a>
      <a class="btn ghostbtn" rel="noopener" href="${repo.url}">Star on GitHub</a>
    </div>
  </div>
  <div class="hero-art">${heroSvg()}</div>
</section>

<section class="console-wrap">
  <div class="console" aria-label="Quick start">
    <div class="console-bar"><i></i><i></i><i></i><span>${hero.demo.caption}</span></div>
    <pre><code>git clone ${repo.url}.git && cd open-facilitator
<span class="cmt"># needs only Docker: Postgres, Keycloak, S3, kernel, demo workspace</span>
${hero.demo.lines[0]}
${hero.demo.lines[1]}</code></pre>
  </div>
</section>

<section id="why" class="band">
  <h2>Why teams pick it</h2>
  <p class="band-sub">Six promises, each enforced by code you can read — not by a hosting agreement.</p>
  <div class="grid three">
    ${benefits
      .map(
        (b) => `<article class="card">
      <span class="icon"><svg viewBox="0 0 24 24" aria-hidden="true">${ICONS[b.icon]}</svg></span>
      <h3>${b.title}</h3><p>${b.body}</p></article>`,
      )
      .join("\n")}
  </div>
</section>

<section id="how" class="band alt">
  <h2>How a piece of work flows</h2>
  <ol class="flow">
    <li><b>Ingress.</b> A chat mention, issue label, or API call creates a <b>Work</b> — durable, versioned, with an append-only timeline.</li>
    <li><b>Decision.</b> Deterministic routing matches the work to a capable <b>Workspace</b>; models may propose, only code authorizes.</li>
    <li><b>Dispatch.</b> The assign command rides the outbox to the team's <b>Gateway</b> over an <em>outbound</em> Link — no inbound ports on your side.</li>
    <li><b>Execution.</b> Team policy gates it (<code>allow | deny | confirm</code>); <code>confirm</code> parks a durable <b>approval</b> for a human. Otherwise a harness adapter runs it.</li>
    <li><b>Proof.</b> Progress, content-addressed <b>artifacts</b>, and a canonical result manifest flow back, trace-linked end to end.</li>
    <li><b>Settle.</b> Exactly-once completion; side effects (PRs, replies) happen team-side under team credentials.</li>
  </ol>
</section>

<section id="components" class="band">
  <h2>The components</h2>
  <p class="band-sub">One repository, eight pieces, nothing hidden — the same artifacts you can build today.</p>
  <div class="grid four">
    ${components
      .map(
        (c) => `<article class="card comp">
      <h3>${c.name}</h3><p class="pkg">${c.pkg}</p><p>${c.body}</p>
      <p class="path"><a rel="noopener" href="${repo.url}/tree/main/${c.path}">${c.path}</a> · <a href="#${c.docs}">docs</a></p></article>`,
      )
      .join("\n")}
  </div>
</section>

<section id="start" class="band alt">
  <h2>Get started</h2>
  <p class="band-sub">Two doors, same stack. Pick the one that matches who is at the keyboard.</p>
  <div class="grid two">
    ${guides
      .map(
        (g) => `<article class="card guide">
      <h3>${g.title}</h3>
      <p class="pkg">${g.kicker} · ~${g.minutes} min</p>
      <p>${g.body}</p>
      <p><a class="btn" href="#${g.route}">Open guide →</a></p></article>`,
      )
      .join("\n")}
  </div>
  <p class="more">Prefer the terminal? The whole documentation set is in the
    <a rel="noopener" href="${repo.url}/tree/main/docs">repo's <code>docs/</code></a> and
    <a href="#/docs">rendered here</a>.</p>
</section>
`;
}

function notFoundBody(base) {
  return `<section class="hero"><div class="hero-copy">
  <h1>Nothing stands here.</h1>
  <p class="lede">That page is not in this site. The documentation may know better:</p>
  <div class="cta"><a class="btn" href="#/docs">Docs home</a><a class="btn inverse" href="index.html">Landing page</a></div>
</div></section>
<script>
// GitHub Pages serves 404.html for unknown paths (no SPA fallback). When it
// was loaded for a site route, forward that route to the real index.
(function () {
  var base = ${JSON.stringify(base)};
  var rest = location.pathname.slice(base.length); // "docs/protocol" style
  if (/^(docs|guide|search)(\\/|$)/.test(rest))
    location.replace(base + "index.html#/" + rest + location.hash);
})();
</script>`;
}
