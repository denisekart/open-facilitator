// The landing page, as a template function. All copy lives in src/config.mjs;
// this file only arranges it.

import { repo, nav, hero, relay, story, components, benefits, guides, pages, docs } from "../config.mjs";

const ICON = `<svg viewBox="0 0 32 32" aria-hidden="true"><g fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round"><path d="M6.49 10.5 A 11 11 0 0 1 25.51 10.5"/><path d="M25.51 21.5 A 11 11 0 0 1 6.49 21.5"/></g><path d="M25.51 10.5 l4.6 0.9 M25.51 10.5 l0.9 4.6 M6.49 21.5 l-4.6 -0.9 M6.49 21.5 l-0.9 -4.6" stroke="currentColor" stroke-width="3" stroke-linecap="round" fill="none"/><circle cx="16" cy="16" r="4.4" fill="currentColor"/><circle cx="20.8" cy="5.6" r="2.8" fill="#D97A22"/></svg>`;

const esc = (s) =>
  String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

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
    <p class="scene-title">The loop every Work travels</p>
    <div class="relay">
      <svg viewBox="0 0 280 264" role="img" aria-label="Diagram: work travels a loop between teams and the central kernel">
        <path d="${relay.path}" fill="none" stroke="#e6e3da" stroke-width="2"/>
        <circle class="relay-hub" cx="140" cy="128" r="26" fill="#0B6B57"/>
        <circle cx="140" cy="128" r="26" fill="none" stroke="#0B6B57" stroke-opacity="0.25" stroke-width="10"/>
        <text x="140" y="124" text-anchor="middle" fill="#ffffff" font-size="11" font-weight="600" font-family="system-ui,sans-serif">kernel</text>
        <text x="140" y="138" text-anchor="middle" fill="#bfe0d6" font-size="9" font-family="system-ui,sans-serif">remembers</text>
        ${relay.steps.map((s, i) => stationSvg(s, i)).join("")}
        <circle class="workdot" r="7" fill="#D97A22" style="offset-path: path('${relay.path}'); offset-distance: 0%"/>
      </svg>
    </div>
    <div class="relay-steps" role="tablist" aria-label="Work lifecycle">
      ${relay.steps.map((s, i) => `<button role="tab" class="relay-step${i === 0 ? " on" : ""}" data-step="${i}" aria-selected="${i === 0}"><b>${esc(s.title)}</b>${esc(s.who)}</button>`).join("")}
    </div>
    <div class="relay-detail" id="relay-detail" aria-live="polite"></div>
  </div>
</div></div></section>`;
}

function stationSvg(s, i) {
  const words = ["create", "route", "execute", "record"];
  const dy = i === 0 ? 34 : i === 1 ? -24 : i === 2 ? 34 : 40;
  return `<g class="relay-station" data-station="${i}" transform="translate(${s.x} ${s.y})">
    <circle r="15" fill="#ffffff" stroke="#0B6B57" stroke-width="1.5"/>
    <text y="4" text-anchor="middle" fill="#084A3C" font-size="8.5" font-family="system-ui,sans-serif">${i + 1}</text>
    <text y="${dy}" text-anchor="middle" fill="#7d8883" font-size="9.5" font-family="system-ui,sans-serif">${esc(words[i % 4])}</text>
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
      ${s.cmd ? `<pre class="cmd">${esc(s.cmd).replace(/^\$/, "<span class=\"c\">$</span>")}</pre>` : ""}
    </div>`
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
        (c) => `<div class="cell"><span class="tag">${esc(c.tag)}</span><h3>${esc(c.name)}</h3><p>${esc(c.body)}</p><p style="margin-top:12px"><a href="${c.docs.startsWith("/") ? "#" + c.docs : c.docs}">Details →</a></p></div>`
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
    <img src="console-explorer.png" alt="The Open Facilitator console showing Work search results, a fleet summary, and throughput counters" loading="lazy" width="1140" height="760">
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
      <p style="color:var(--ink-2);font-size:15px">Dense orientation for LLM workers: auth planes, the endpoints you'll actually call, idempotency rules, how to fail well, and the machine-readable contract.</p>
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
  var dot = document.querySelector(".relay .workdot");
  function show(i){
    btns.forEach(function(b,j){ b.classList.toggle("on", i===j); b.setAttribute("aria-selected", i===j); });
    detail.innerHTML = "<b>"+steps[i].title+"</b> — "+steps[i].body;
    if (dot) { dot.style.animation = "none"; dot.style.offsetDistance = steps[i].at; }
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
