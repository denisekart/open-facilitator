// open-facilitator.org build: markdown -> static SPA content + search index.
// Self-contained: reads the repo docs tree, shares no code with the runtime.
//
//   node build.mjs   -> dist/
import { readFile, writeFile, mkdir, rm, cp, readdir } from "node:fs/promises";
import { existsSync } from "node:fs";
import { join, dirname, posix } from "node:path";
import { fileURLToPath } from "node:url";
import { marked } from "marked";
import { docs, guides, pages, repo } from "./src/config.mjs";
import { renderMermaid, mermaidAvailable } from "./mermaid-ssr.mjs";
import { tooltipFor, tooltips } from "./src/tooltips.mjs";
import { renderArchitecture, renderArchitectureMini } from "./src/arch-render.mjs";
import { architectureBody } from "./src/pages/architecture.mjs";
import { paletteReference, galleryDoc } from "./src/schema-ref.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, ".."); // repo root
const DIST = join(HERE, "dist");

// Add native <title> tooltips to SSR'd diagram nodes, keyed by visible
// label; the runtime styles them into hover cards.
function injectTooltips(svg) {
  return svg.replace(
    /(<g[^>]*class="[^"]*\b(?:node|actor)\b[^"]*"[^>]*>)([\s\S]{0,800}?)(<(?:\/g>|<g\b))/g,
    (whole, open, inner, next) => {
      const label = inner
        .replace(/<[^>]+>/g, " ")
        .replace(/\s+/g, " ")
        .trim();
      const tip = tooltipFor(label);
      return tip
        ? `${open}<title>${tip.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")}</title>${inner}${next}`
        : whole;
    },
  );
}

// collect all mermaid blocks repo-wide, render once
const norm = (s) => s.replace(/\r\n/g, "\n").replace(/\n+$/, "");
const MERMAID = new Map(); // normalized code -> svg
{
  const allMd = [];
  for (const g of docs)
    for (const e of g.entries) allMd.push(await readFile(join(ROOT, e.src), "utf8"));
  for (const g of pages)
    for (const e of g.entries) if (e.src) allMd.push(await readFile(join(ROOT, e.src), "utf8"));
  for (const g of guides) allMd.push(await readFile(join(ROOT, g.src), "utf8"));
  const codes = [];
  for (const md of allMd) {
    for (const m of md.matchAll(/```mermaid[^\n]*\r?\n([\s\S]*?)```/g)) codes.push(m[1]);
  }
  const uniq = [...new Set(codes)];
  if (uniq.length) {
    if (!mermaidAvailable())
      console.warn("no chromium for mermaid SSR; diagrams render client-side");
    else {
      const map = await renderMermaid(uniq);
      if (map) uniq.forEach((c, i) => MERMAID.set(norm(c), injectTooltips(map.get(i))));
      else console.warn("mermaid SSR incomplete; diagrams render client-side");
    }
  }
}

// Ship the mermaid library so the runtime can render any source blocks
// (hosts without chromium at build time, e.g. the GitHub Pages runner).

// ---------------------------------------------------------------- markdown

marked.setOptions({ gfm: true });

const renderer = new marked.Renderer();
renderer.code = (token) => {
  const t = typeof token === "object" ? token : { text: token, lang: "" };
  const info = String(t.lang || "");
  if (info.split(" ")[0] === "archmap") {
    return (
      `<a class="arch-teaser" href="#/architecture" aria-label="Open the interactive architecture explorer">` +
      renderArchitectureMini() +
      `<span class="arch-teaser-cta">Open the interactive map — click any part, walk the scenarios →</span></a>`
    );
  }
  if (info.split(" ")[0] === "mermaid") {
    const cap = info.match(/title="([^"]+)"/);
    const caption = cap ? cap[1] : "system diagram";
    const svg = MERMAID.get(norm(t.text));
    if (svg)
      return (
        `<figure class="diagram"><button class="diagram-zoom" aria-label="Expand diagram">⤢ expand</button>` +
        `<div class="svg-frame">${svg}</div>` +
        `<figcaption>${escape(caption)} · hover any box for what it guarantees</figcaption></figure>`
      );
    return (
      `<figure class="diagram diagram-pending">` +
      `<pre class="mermaid-source">${escape(t.text)}</pre>` +
      `<figcaption>${escape(caption)} · rendering diagram…</figcaption></figure>`
    );
  }
  const cls = info.split(" ")[0] ? ` class="language-${info.split(" ")[0]}"` : "";
  return `<pre><code${cls}>${escape(t.text)}</code></pre>`;
};
const escape = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
marked.use({ renderer });

const slug = (s) =>
  s
    .toLowerCase()
    .replace(/`/g, "")
    .replace(/[^a-z0-9 -]/g, "")
    .trim()
    .replace(/ /g, "-");

// route lookups for link rewriting
const routeBySrc = new Map();
for (const g of docs)
  for (const e of g.entries) {
    routeBySrc.set(e.src, e.route);
    routeBySrc.set("route:" + e.route, e.route);
  }
for (const g of pages)
  for (const e of g.entries) {
    routeBySrc.set(e.src, e.route);
    routeBySrc.set("route:" + e.route, e.route);
  }
for (const g of guides) {
  routeBySrc.set(g.src, g.route);
  routeBySrc.set("route:" + g.route, g.route);
}

function rewriteHref(href, ctxDir) {
  if (!href) return href;
  if (/^(https?:|mailto:|#)/.test(href)) return href;
  const [pathPart, anchor] = href.split("#");
  if (!pathPart) return href;
  // site-internal absolute links: /artifacts -> #/artifacts
  if (pathPart.startsWith("/")) {
    const target = routeBySrc.has("route:" + pathPart) ? pathPart : null;
    if (target) return "#" + target + (anchor ? `?${anchor}` : "");
    return href;
  }
  const abs = posix.normalize(posix.join(ctxDir, pathPart));
  // repo-root-relative for docs/* sources
  const repoRel = abs.startsWith("../") ? abs.slice(3) : abs;
  const route = routeBySrc.get(repoRel);
  if (route) return "#" + route + (anchor ? `?${anchor}` : "");
  // anything else in the repo points at GitHub
  const branch = "main";
  return `https://github.com/${repo.ownerSlug}/open-facilitator/blob/${branch}/${repoRel}${anchor ? `#${anchor}` : ""}`;
}

function renderMarkdown(md, ctxDir) {
  const tokens = marked.lexer(md);
  let html = marked.parser(tokens);
  // heading ids + inline anchors
  const seen = new Map();
  html = html.replace(/<h([1-4])>([\s\S]*?)<\/h\1>/g, (_, lvl, inner) => {
    const text = inner.replace(/<[^>]+>/g, "");
    let id = slug(text) || "section";
    const n = seen.get(id) || 0;
    seen.set(id, n + 1);
    if (n) id = `${id}-${n}`;
    return `<h${lvl} id="${id}">${inner}</h${lvl}>`;
  });
  // relative links
  html = html.replace(/<a href="([^"]+)"/g, (m, href) => `<a href="${rewriteHref(href, ctxDir)}"`);
  // images authored against site/src/assets ship at the dist root
  html = html.replace(/<img src="([^"]+)"/g, (m, src) => {
    if (/^(https?:|\/|#)/.test(src)) return m;
    const abs = posix.normalize(posix.join(ctxDir, src));
    return abs.startsWith("site/src/assets/") ? `<img src="${posix.basename(src)}"` : m;
  });
  // tables: frame them so wide content scrolls instead of overflowing
  html = html.replace(/<table>[\s\S]*?<\/table>/g, (m) => `<div class="tbl-wrap">${m}</div>`);
  // bare repo paths like `src/facilitator/` become github links inside <code>
  // - but not globs or placeholders (`src/<name>/`, `examples/**`): those
  // would 404 on GitHub, so they stay plain code.
  html = html.replace(/<code>([a-z0-9_./-]+\/(?:[a-z0-9_./-]+)?(?:\/)?[^<]*)<\/code>/gi, (m, p) =>
    /^(src|docs|protocol|features|examples|reference|tests|tools)\//.test(p) &&
      !/[ ()<>*]/.test(p) &&
      existsSync(join(ROOT, p.replace(/\/$/, "")))
      ? `<a href="https://github.com/${repo.ownerSlug}/open-facilitator/tree/main/${p.replace(/\/$/, "")}"><code>${p}</code></a>`
      : m,
  );
  return html;
}

function splitSections(html, pageTitle) {
  const parts = html.split(/(?=<h[23] )/);
  const out = [];
  let current = { h: pageTitle, id: "", x: "" };
  for (const part of parts) {
    const m = part.match(/^<h([23]) id="([^"]+)">([\s\S]*?)<\/h\1>/);
    if (!m) {
      current.x += " " + strip(part);
      continue;
    }
    if (current.x.trim()) out.push(current);
    current = { h: m[3].replace(/<[^>]+>/g, ""), id: m[2], x: "" };
    current.x += " " + strip(part.slice(m[0].length));
  }
  if (current.x.trim()) out.push(current);
  return out.map((s) => ({
    h: s.h.trim(),
    id: s.id,
    x: s.x.replace(/\s+/g, " ").trim().slice(0, 280),
    f: s.x.replace(/\s+/g, " ").trim(),
  }));
}

const strip = (h) => h.replace(/<pre[\s\S]*?<\/pre>/g, " ").replace(/<[^>]+>/g, " ");
// ---------------------------------------------------------------- build

await rm(DIST, { recursive: true, force: true });
await mkdir(join(DIST, "content"), { recursive: true });

const searchPages = []; // for search index + sitemap
const contentEntries = [
  ...pages.flatMap((g) => g.entries.map((e) => ({ ...e, group: g.group }))),
  ...docs.flatMap((g) => g.entries.map((e) => ({ ...e, group: g.group }))),
];

// The workflows manual's generated tail: the palette described by the
// schema that validates it, a lifecycle diagram (mermaid: the existing
// SSR pipeline renders it), and the cookbook's gallery YAML — the actual
// seeder bytes, with a jump into the live console rather than a second
// graph renderer (a second layout engine would be a second truth).
async function workflowsAppendix(root) {
  const palette = await paletteReference(root);
  const nudge = await galleryDoc(root, "nudge-escalation");
  const fork = await galleryDoc(root, "review-fork");
  const code = (t, label) =>
    `<pre class="cook-yaml" aria-label="${label}"><code>${escape(t)}</code></pre>`;
  const lifecycle = `stateDiagram-v2
    [*] --> running: triggered (event, schedule, invoke)
    [*] --> shadowed: triggered by a shadow definition
    shadowed --> shadowed: every leg only recorded
    running --> waiting: park (approval, wait, timer)
    waiting --> running: woken (decision, match, deadline)
    running --> completed: end reached
    running --> failed: leg failed unhandled
    running --> cancelled: operator cancel
    running --> paused: operator pause
    paused --> running: operator resume
    completed --> [*]
    failed --> [*]
    cancelled --> [*]`;
  return (
    `<h2 id="lifecycle">How a run moves</h2>
<p>An instance is a row, not a process. These are the only states it
holds; every arrow is an act the kernel itself records. A shadowed
definition never enters <code>running</code> — its instances park as
<code>shadowed</code> and the shadow report shows what they would have
done.</p>
<figure class="diagram diagram-pending"><button class="diagram-zoom" aria-label="Expand diagram">⤢ expand</button><pre class="mermaid-source">${escape(lifecycle)}</pre><figcaption>workflow instance lifecycle · every arrow is a kernel act</figcaption></figure>
<h2 id="palette-reference">The palette, field by field</h2>
<p>Generated from <code>protocol/json-schema/workflow-definition.v1.json</code>
— the schema the kernel validates against, so this table and the
validator can never disagree. <code>*</code> marks a required field.</p>
${palette}
<h2 id="cookbook">Cookbook: the gallery, byte for byte</h2>
<p>Two of the six shipped definitions, embedded as the seeder stores
them. Clone either in the console (<code>#/flows</code>) and the diff
block will show exactly what you bent.</p>
<h3 id="cookbook-nudge">Nudge ladder</h3>
<p>The patience curve: park, nudge, escalate, end — every park a durable
row, every nudge an audit line.</p>
${code(nudge.yaml, "nudge-escalation gallery definition")}
<p>Clone it under <b>Flows</b> in your console (see <a class="arch-more" href="#/console">the console tour</a>) and the diff block will show your bend against this stored version.</p>
<h3 id="cookbook-fork">Review fork</h3>
<p>The review law as data: a terminal guard, then a fork that inherits
the subject's identity.</p>
${code(fork.yaml, "review-fork gallery definition")}
<p>Same drill: clone <code>review-fork</code> under <b>Flows</b> and rename the pieces to your team — the audit line will say exactly who bent what.</p>`
  );
}

// A console screenshot and its dark twin, offered as one <picture>: the
// dark source rides the OS preference (the site's default theme law).
// Only rewritten when the e2e run actually produced the twin, so the
// markup never references a file that was not photographed.
const SHOT_NAMES = [
  "console-explorer",
  "console-work",
  "console-editor",
  "console-instance",
];
function withDarkShots(html) {
  for (const name of SHOT_NAMES) {
    if (!existsSync(join(HERE, "src/assets", `${name}-dark.png`))) continue;
    html = html.replaceAll(
      `<img src="${name}.png"`,
      `<picture><source media="(prefers-color-scheme: dark)" srcset="${name}-dark.png"><img src="${name}.png"`,
    );
  }
  // <picture> needs a closing tag: mark the wrapped imgs and close them.
  for (const name of SHOT_NAMES) {
    const wrapped = `<picture><source media="(prefers-color-scheme: dark)" srcset="${name}-dark.png">`;
    let at = html.indexOf(wrapped);
    while (at !== -1) {
      const close = html.indexOf(">", html.indexOf("<img", at));
      const end = close + 1;
      html = html.slice(0, end) + "</picture>" + html.slice(end);
      at = html.indexOf(wrapped, end + 10);
    }
  }
  return html;
}

async function emit(entry, srcPath, group) {
  const md = await readFile(join(ROOT, srcPath), "utf8");
  const ctxDir = dirname(srcPath);
  let html = renderMarkdown(md, ctxDir);
  if (entry.generated === "workflows") html += await workflowsAppendix(ROOT);
  html = withDarkShots(html);
  const titleMatch = md.match(/^#\s+(.+)$/m);
  const title = entry.title || (titleMatch ? titleMatch[1] : srcPath);
  const route = entry.route;
  const file = join(
    DIST,
    "content",
    route === "/docs" ? "docs.html" : route.replace(/^\//, "") + ".html",
  );
  await mkdir(dirname(file), { recursive: true });
  const body = html.replace(/<h1[^>]*>[\s\S]*?<\/h1>/, (m) =>
    m.replace("<h1", '<h1 class="doc-title"'),
  );
  await writeFile(file, body);
  const secs = splitSections(body, title);
  searchPages.push({
    r: route,
    t: title,
    g: group || "Guide",
    s: secs.map((s) => ({ h: s.h, i: s.id, x: s.x, f: s.f })),
  });
  return { route, title };
}

for (const e of contentEntries)
  if (e.generated !== "architecture") await emit(e, e.src, e.group);
for (const g of guides) await emit({ route: g.route, title: g.title }, g.src, "Guide");

// Generated pages: the interactive architecture explorer.
{
  const entry = contentEntries.find((e) => e.generated === "architecture");
  const arch = renderArchitecture();
  const html = `<h1 class="doc-title">${entry.title}</h1>` + architectureBody(arch);
  await writeFile(join(DIST, "content", "architecture.html"), html);
  const secs = splitSections(html, entry.title);
  searchPages.push({
    r: entry.route,
    t: entry.title,
    g: entry.group,
    s: secs.map((s) => ({ h: s.h, i: s.id, x: s.x, f: s.f })),
  });
}

// search index
await writeFile(
  join(DIST, "site-manifest.json"),
  JSON.stringify({
    repo: repo.url,
    nav: [...pages, ...docs].map((g) => ({
      group: g.group,
      items: g.entries.map((e) => ({ r: e.route, t: e.title })),
    })),
    guides: guides.map((g) => ({ r: g.route, t: g.title })),
  }),
);

const STOP = new Set(
  "a an and are as at be by for from has have he if in is it its of on or that the to was were will with you your not but can this".split(
    " ",
  ),
);
const tok = {};
searchPages.forEach((p, pi) => {
  const t = new Map();
  const add = (text, weight) => {
    for (const w of text.toLowerCase().split(/[^a-z0-9]+/)) {
      if (w.length < 3 || STOP.has(w) || /^\d+$/.test(w)) continue;
      t.set(w, (t.get(w) || 0) + weight);
    }
  };
  add(p.t, 6);
  add(p.g, 2);
  for (const s of p.s) {
    add(s.h, 3);
    add(s.f, 1);
  }
  for (const [w, n] of t) {
    (tok[w] ||= []).push([pi, n]);
  }
});
const sections = searchPages.map((p) => ({
  r: p.r,
  t: p.t,
  g: p.g,
  s: p.s.map((s) => [s.h, s.i, s.x]),
}));
await writeFile(join(DIST, "search-index.json"), JSON.stringify({ pages: sections, tok }));
await writeFile(join(DIST, "tooltips.json"), JSON.stringify(tooltips));
// ---------------------------------------------------------------- assets

const copy = async (src, dest) => {
  await mkdir(dirname(join(DIST, dest)), { recursive: true });
  await cp(join(HERE, src), join(DIST, dest));
};
await copy("src/styles.css", "styles.css");
await copy("src/js/app.js", "app.js");
await copy("src/assets/icon.svg", "icon.svg");
await copy("src/assets/icon.svg", "favicon.svg");
await copy("src/assets/console-explorer.png", "console-explorer.png");
await copy("src/assets/console-work.png", "console-work.png");
for (const shot of [
  "console-explorer-dark.png",
  "console-work-dark.png",
  "console-editor.png",
  "console-instance.png",
  "console-editor-dark.png",
  "console-instance-dark.png",
]) {
  if (existsSync(join(HERE, "src/assets", shot))) await copy(`src/assets/${shot}`, shot);
}

// The interactive explorer bundle (Svelte 5 + Svelte Flow). Programmatic
// vite build so the site stays a single `npm run build`; the page stays
// dependency-free outside this one lazily-injected pair of files.
{
  const { build } = await import("vite");
  await build({
    configFile: join(HERE, "arch-explorer.vite.config.mjs"),
    root: HERE,
    logLevel: "warn",
  });
  await cp(join(HERE, ".explorer-dist/arch-explorer.js"), join(DIST, "arch-explorer.js"));
  const bundleCss = join(HERE, ".explorer-dist/arch-explorer.css");
  if (existsSync(bundleCss)) await cp(bundleCss, join(DIST, "arch-explorer.css"));
}

const mermaidMin = join(HERE, "node_modules/mermaid/dist/mermaid.min.js");
if (existsSync(mermaidMin)) await cp(mermaidMin, join(DIST, "mermaid.min.js"));

// 404 for GitHub Pages (deep path links land here; it forwards to the SPA).
const BASE = process.env.OFAC_SITE_BASE ?? "/";
const { landingHtml } = await import("./src/pages/index.mjs");
await writeFile(join(DIST, "index.html"), withDarkShots(landingHtml({ base: BASE })));
await writeFile(join(DIST, "404.html"), landingHtml({ notFound: true, base: BASE }));
await writeFile(join(DIST, ".nojekyll"), "");

// png icons (favicon fallback + og)
if (existsSync(join(HERE, "src/assets/icon.png"))) await copy("src/assets/icon.png", "icon.png");

const routeList = ["/", ...contentEntries.map((e) => e.route), ...guides.map((g) => g.route)];
console.log(`built ${DIST}`);
console.log(`pages: ${routeList.length}, search tokens: ${Object.keys(tok).length}`);

// sanity: every manifest source exists
for (const e of contentEntries) {
  if (e.src && !existsSync(join(ROOT, e.src))) throw new Error(`missing doc source: ${e.src}`);
}
void readdir;
