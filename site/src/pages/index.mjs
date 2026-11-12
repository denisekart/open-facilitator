// The landing page, as a template function. All copy lives in src/config.mjs;
// this file only arranges it.

import {
  repo,
  nav,
  hero,
  relay,
  story,
  components,
  benefits,
  guides,
  pages,
  docs,
} from "../config.mjs";

const ICON = `<svg viewBox="0 0 32 32" aria-hidden="true"><g stroke="currentColor" stroke-width="2.6" stroke-linecap="round"><path d="M11.8 11.8 L6.4 6.4"/><path d="M20.2 11.8 L25.6 6.4"/><path d="M11.8 20.2 L6.4 25.6"/><path d="M20.2 20.2 L25.6 25.6"/></g><rect x="9.6" y="9.6" width="12.8" height="12.8" rx="3.4" fill="currentColor"/><circle cx="4.6" cy="4.6" r="2.7" fill="currentColor" opacity="0.55"/><circle cx="27.4" cy="4.6" r="2.7" fill="currentColor" opacity="0.55"/><circle cx="4.6" cy="27.4" r="2.7" fill="currentColor" opacity="0.55"/><circle cx="27.4" cy="27.4" r="2.7" fill="#D97706"/></svg>`;

const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

function head(base, notFound) {
  const P = (s) => (notFound ? base + s : s);
  return `<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${notFound ? "Not found" : "Open Facilitator — your agents do the work, it remembers it"}${notFound ? "" : ""}</title>
<meta name="description" content="Open-source orchestration for teams of humans and AI agents: a durable coordination kernel your teams connect to over outbound links. Approvals, evidence, artifacts — survive every crash.">
<link rel="icon" href="${P("favicon.svg")}" type="image/svg+xml">
<link rel="stylesheet" href="${P("styles.css")}">
</head>`;
}

function header(base, notFound) {
  // On the 404 page there is no landing content: every hash target lives on
  // index.html, addressed through the site base.
  const P = (s) => (notFound ? (s.startsWith("#") ? base + "index.html" + s : base + s) : s);
  const links = nav
    .map((n) => {
      const href = n.href.startsWith("/#/") ? P(n.href.slice(1)) : P(n.href);
      return `<a href="${href}">${esc(n.label)}</a>`;
    })
    .join("");
  return `<header class="topbar">
  <a class="brand" href="${P("index.html")}">${ICON}<span>Open&nbsp;Facilitator</span></a>
  <nav class="topnav" aria-label="Main">${links}
    <form class="searchbox" action="${P("#/search")}" role="search">
      <input id="q" type="search" name="q" placeholder="Search…" aria-label="Search the documentation" autocomplete="off">
      <div class="search-pop" id="search-pop"></div>
    </form>
    <a class="ghost" rel="noopener" href="${repo.url}">GitHub</a>
  </nav>
</header>`;
}

function heroHtml() {
  return `<section class="hero" id="top"><div class="wrap"><div class="hero-grid">
  <div>
    <p class="kicker">${esc(hero.kicker)}</p>
    <h1>${esc(hero.tagline)}</h1>
    <p class="lede">${esc(hero.oneLiner)}</p>
    <div class="cta">
      <a class="btn" href="#start">Get running</a>
      <a class="btn inverse" href="#/what">What is this, exactly?</a>
    </div>
  </div>
  <div class="scene" id="scene">
    <p class="scene-title">What one piece of work looks like</p>
    <div class="relay">
      <svg viewBox="0 0 600 128" role="img" aria-label="Diagram: a work item passes from you, through the facilitator, into a team's environment, and becomes a permanent record">
        <line x1="75" y1="62" x2="525" y2="62" stroke="#dfe5ec" stroke-width="2.5"/>
        ${relay.steps.map((s, i) => stationSvg(s, i)).join("")}
        <g class="workchip">
          <rect x="-44" y="-13" width="88" height="26" rx="6" fill="#D97706"/>
          <rect x="-37" y="-6.5" width="42" height="3.5" rx="1.75" fill="#FFF" fill-opacity="0.85"/>
          <rect x="-37" y="0.5" width="26" height="3.5" rx="1.75" fill="#FFF" fill-opacity="0.55"/>
          <circle cx="29" cy="-2.5" r="3.4" fill="#FFF" fill-opacity="0.9"/>
          <text x="29" y="0.4" text-anchor="middle" font-size="5.5" font-weight="700" fill="#8a4d02" font-family="system-ui,sans-serif">✓</text>
        </g>
      </svg>
    </div>
    <div class="relay-steps" role="tablist" aria-label="Work lifecycle">
      ${relay.steps
        .map(
          (s, i) =>
            `<button role="tab" class="relay-step${i === 0 ? " on" : ""}" data-step="${i}" aria-selected="${i === 0}"><b>${esc(s.title)}</b>${esc(s.who)}</button>`,
        )
        .join("")}
    </div>
    <div class="relay-detail" id="relay-detail" aria-live="polite"></div>
    <div class="relay-log" aria-label="The record building up as this happens">
      <p class="relay-log-title">The record, while it happens</p>
      ${relay.steps
        .map(
          (s, i) =>
            `<p class="relay-log-line" data-line="${i}"><span>${String(i + 1).padStart(2, "0")}</span>${esc(s.log)}</p>`,
        )
        .join("")}
    </div>
  </div>
</div></div></section>`;
}

// Station glyphs: recognisable actors, not numbered dots.
function stationSvg(s, i) {
  const glyphs = [
    // you / your agent: a chat bubble + terminal
    `<rect x="-15" y="-13" width="30" height="20" rx="5" fill="#fff" stroke="currentColor" stroke-width="1.6"/><path d="M-6 7 l0 6 l7 -6" fill="#fff" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/><path d="M-8 -5 l5 4 l-5 4" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/><line x1="1" y1="3" x2="8" y2="3" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>`,
    // the kernel: the brand chip
    `<g stroke="currentColor" stroke-width="1.5" stroke-linecap="round"><path d="M-9 -9 L-14 -14"/><path d="M9 -9 L14 -14"/><path d="M-9 9 L-14 14"/><path d="M9 9 L14 14"/></g><rect x="-7.5" y="-7.5" width="15" height="15" rx="4" fill="currentColor"/>`,
    // a team environment: container with agent + human gate
    `<rect x="-16" y="-12" width="32" height="24" rx="5" fill="#fff" stroke="currentColor" stroke-width="1.6" stroke-dasharray="3.5 3"/><circle cx="-6" cy="-3" r="3.2" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M-11.5 6 a5.5 5.5 0 0 1 11 0" fill="none" stroke="currentColor" stroke-width="1.5"/><rect x="3" y="-6" width="10" height="12" rx="2" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M5.5 0 l2 2 l3.5 -4" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>`,
    // the record: vault/cylinder with a check
    `<path d="M-12 -9 a12 4 0 0 1 24 0 v14 a12 4 0 0 1 -24 0 z" fill="#fff" stroke="currentColor" stroke-width="1.6"/><path d="M-12 -9 a12 4 0 0 0 24 0" fill="none" stroke="currentColor" stroke-width="1.2" stroke-opacity="0.6"/><path d="M-4 -1 l3 3 l6 -6.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>`,
  ];
  return `<g class="relay-station" data-station="${i}" transform="translate(${s.x} 34)">
    <g class="relay-glyph" color="#9aa6b4">${glyphs[i]}</g>
    <text y="52" text-anchor="middle" fill="#7c8794" font-size="9.5" font-family="system-ui,sans-serif">${esc(s.who)}</text>
  </g>`;
}

function storyHtml() {
  return `<section id="how"><div class="wrap">
  <p class="kicker">How it works</p>
  <h2>One piece of work, start to finish</h2>
  <p class="section-lede">Not an abstraction — a Monday. The same sequence runs for a research report, a migration plan, or a dependency update.</p>
  <div class="story">
    ${story
      .map(
        (s, i) => `<div class="story-num">${i + 1}</div>
    <div class="story-item">
      <span class="who">${esc(s.who)}</span>
      <h3>${esc(s.title)}</h3>
      <p>${esc(s.body)}</p>
      ${s.cmd ? `<pre class="cmd">${esc(s.cmd).replace(/^\$/, '<span class="c">$</span>')}</pre>` : ""}
    </div>`,
      )
      .join("")}
  </div>
</div></section>`;
}

function whyHtml() {
  return `<section id="why"><div class="wrap">
  <p class="kicker">Why teams adopt it</p>
  <h2>Coordination you can audit, authority you keep</h2>
  <p class="section-lede">Six claims, each one enforced by the published protocol — checkable in the open, not promised by a vendor.</p>
  <div class="grid cols-3">
    ${benefits.map((b) => `<div class="cell"><h3>${esc(b.title)}</h3><p>${esc(b.body)}</p></div>`).join("")}
  </div>
</div></section>`;
}

function componentsHtml() {
  return `<section id="components"><div class="wrap">
  <p class="kicker">Components</p>
  <h2>Small pieces, published artifacts, clear seams</h2>
  <p class="section-lede">Take the whole kernel, or consume one package and speak the wire directly. Every component below ships versioned and stands on the same contract.</p>
  <div class="grid cols-3">
    ${components
      .map(
        (c) =>
          `<div class="cell"><span class="tag">${esc(c.tag)}</span><h3>${esc(c.name)}</h3><p>${esc(c.body)}</p><p style="margin-top:12px"><a href="${c.docs.startsWith("/") ? "#" + c.docs : c.docs}">Details →</a></p></div>`,
      )
      .join("")}
  </div>
</div></section>`;
}

function consoleHtml() {
  return `<section id="console"><div class="wrap">
  <p class="kicker">The operator surface</p>
  <h2>The console is the product, photographed</h2>
  <p class="section-lede">Every kernel serves an operator console at its own URL: search across every Work, the live fleet, the delivery ledger, the audit trail. It is a client of the same public API your agents use — <a href="#/console">here is what each view shows</a>.</p>
  <figure class="shot" style="margin:0">
    <div class="shot-bar"><i></i><i></i><i></i></div>
    <img src="console-explorer.png" alt="The Open Facilitator console showing Work search results, a fleet summary, and throughput counters" width="1140" height="760">
  </figure>
</div></section>`;
}

function startHtml() {
  return `<section id="start"><div class="wrap">
  <p class="kicker">Get running</p>
  <h2>Two doors in</h2>
  <div class="roles">
    <div class="role">
      <h3>You are a person</h3>
      <p style="color:var(--ink-2);font-size:15px">Run the kernel image next to your PostgreSQL, point it at your login provider, connect a team, and drive your first Work from curl and a browser.</p>
      <a class="btn" style="margin-top:6px" href="#/guide/human">Human guide →</a>
    </div>
    <div class="role">
      <h3>You are an agent</h3>
      <p style="color:var(--ink-2);font-size:15px">Dense orientation for LLM workers: auth planes, the endpoints you'll actually call, idempotency rules, how to fail well, and the computer-readable contract.</p>
      <a class="btn inverse" style="margin-top:6px" href="#/guide/agent">Agent guide →</a>
    </div>
  </div>
  <p style="margin-top:26px;color:var(--ink-2)">Want the artifact coordinates first? <a href="#/artifacts">Artifacts &amp; installation</a> · <a href="#/integrations">Supported integrations</a> · <a href="#/extend">Write your own adapter</a></p>
</div></section>`;
}

function footerHtml(base, notFound) {
  const P = (s) => (notFound ? (s.startsWith("#") ? base + "index.html" + s : base + s) : s);
  return `<footer class="site"><div class="wrap">
  <span style="display:inline-flex;align-items:center;gap:8px">${ICON}<b>Open Facilitator</b></span>
  ${pages
    .flatMap((g) => g.entries)
    .map((e) => `<a href="${P("#" + e.route)}">${esc(e.title)}</a>`)
    .join("")}
  ${docs
    .flatMap((g) => g.entries)
    .filter((e) => ["/docs", "/docs/protocol", "/docs/security"].includes(e.route))
    .map((e) => `<a href="${P("#" + e.route)}">${esc(e.title)}</a>`)
    .join("")}
  <a rel="noopener" href="${repo.url}">GitHub</a>
</div></footer>`;
}

const RELAY_JS = `
(function(){
  var steps = ${JSON.stringify(relay.steps)};
  var btns = document.querySelectorAll(".relay-step");
  var detail = document.getElementById("relay-detail");
  var chip = document.querySelector(".workchip");
  var glyphs = document.querySelectorAll(".relay-glyph");
  var lines = document.querySelectorAll(".relay-log-line");
  function show(i){
    btns.forEach(function(b,j){ b.classList.toggle("on", i===j); b.setAttribute("aria-selected", i===j); });
    detail.innerHTML = "<b>"+steps[i].title+"</b> — "+steps[i].body;
    if (chip) chip.style.transform = "translate(" + steps[i].x + "px, 62px)";
    glyphs.forEach(function(g,j){ g.setAttribute("color", j<=i ? (j===i ? "#0550AE" : "#7c93b5") : "#9aa6b4"); });
    lines.forEach(function(l,j){ l.classList.toggle("lit", j<=i); });
  }
  btns.forEach(function(b){
    b.addEventListener("click", function(){ show(+b.dataset.step); });
    b.addEventListener("mouseenter", function(){ show(+b.dataset.step); });
  });
  show(0);
  var t, i = 0;
  function cycle(){ if (!document.hidden) { show(i % steps.length); i++; } t = setTimeout(cycle, 5200); }
  t = setTimeout(cycle, 5200);
  document.getElementById("scene").addEventListener("mouseenter", function(){ clearTimeout(t); });
})();
`;

const SITE_ROUTES = [
  "/docs",
  ...pages.flatMap((g) => g.entries.map((e) => e.route)),
  ...docs.flatMap((g) => g.entries.map((e) => e.route)),
  ...guides.map((g) => g.route),
];

function notFoundBody(base) {
  const P = (s) => (s.startsWith("#") ? base + "index.html" + s : base + s);
  return `<section class="hero"><div class="wrap"><div>
  <p class="kicker">404</p>
  <h1>Nothing stands here.</h1>
  <p class="lede">That path is not part of this site. The documentation index is one click away:</p>
  <div class="cta"><a class="btn" href="${P("#/docs")}">Documentation home</a><a class="btn inverse" href="${P("index.html")}">Landing page</a></div>
</div></div></section>
<script>
(function () {
  var base = ${JSON.stringify(base)};
  var routes = ${JSON.stringify(SITE_ROUTES)};
  var p = decodeURIComponent(location.pathname);
  if (p.indexOf(base) !== 0) return;
  var tail = p.slice(base.length).replace(/\\/+$/, "");
  var cand = "/" + tail;
  if (routes.indexOf(cand) !== -1) location.replace(base + "index.html#" + cand);
})();
</script>`;
}

export function landingHtml(opts = {}) {
  const { notFound = false, base = "/" } = opts;
  return `<!DOCTYPE html>
<html lang="en">
${head(base, notFound)}
<body>
<a class="skip" href="#main">Skip to content</a>
${header(base, notFound)}
<main id="main">
${
  notFound
    ? notFoundBody(base)
    : `<div id="landing">${heroHtml() + storyHtml() + whyHtml() + componentsHtml() + consoleHtml() + startHtml()}</div><div id="app"></div>`
}
</main>
${footerHtml(base, notFound)}
${notFound ? "" : `<script>${RELAY_JS}</script>`}
<script src="${notFound ? base + "app.js" : "app.js"}"></script>
</body></html>`;
}
