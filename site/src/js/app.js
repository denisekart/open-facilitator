// Open Facilitator site runtime: hash-routed doc viewer + browser search.
// No dependencies. Routes: #/docs..., #/guide/..., #/search?q=...
(() => {
  "use strict";

  const state = { manifest: null, index: null, content: new Map() };

  const $ = (s, r) => (r || document).querySelector(s);
  const esc = (s) =>
    String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

  function parseHash() {
    const h = location.hash || "";
    const m = h.match(/^#(\/[^?]*)(?:\?(.*))?$/);
    if (!m) return { route: null, anchor: "", query: new URLSearchParams("") };
    return {
      route: m[1].replace(/\/+$/, "") || "/",
      anchor: m[2] || "",
      query: new URLSearchParams(m[2] || ""),
    };
  }

  const contentFile = (route) =>
    "content/" + (route === "/docs" ? "docs.html" : route.slice(1) + ".html");

  async function loadContent(route) {
    if (state.content.has(route)) return state.content.get(route);
    const r = await fetch(contentFile(route));
    if (!r.ok) throw new Error(String(r.status));
    const html = await r.text();
    state.content.set(route, html);
    return html;
  }

  async function load() {
    const [mf, ix] = await Promise.all([
      fetch("site-manifest.json").then((r) => r.json()),
      fetch("search-index.json").then((r) => r.json()),
    ]);
    state.manifest = mf;
    state.index = ix;
  }

  // ------------------------------------------------------------ doc pages

  function docnavHtml(activeRoute) {
    const m = state.manifest;
    const groups = [];
    for (const gr of m.nav) {
      let items = gr.items.filter((i) => i.r !== "/docs");
      if (gr.group === "Start here")
        items = [{ r: "/docs", t: "Docs home" }, ...items, ...m.guides];
      if (!items.length) continue;
      groups.push(
        `<div class="group"><p>${esc(gr.group)}</p>${items
          .map((i) => link(i, activeRoute))
          .join("")}</div>`,
      );
    }
    return `<nav class="docnav" aria-label="Documentation">
      <div class="crumbs"><a href="index.html">← Home</a></div>
      ${groups.join("")}</nav>`;
  }

  const link = (i, active) =>
    `<a href="#${i.r}" class="${i.r === active ? "active" : ""}">${esc(i.t)}</a>`;

  function tocFor(html) {
    const host = document.createElement("div");
    host.innerHTML = html;
    const hs = [...host.querySelectorAll("h2[id], h3[id]")].filter(
      (h) => h.tagName === "H2",
    );
    if (hs.length < 2) return "";
    return `<div class="toc"><p>On this page</p>${hs
      .map((h) => `<a href="#${location.pathname}${location.search}${location.hash.split("?")[0]}?${h.id}" data-anchor="${h.id}">${esc(h.textContent)}</a>`)
      .join("")}</div>`;
  }

  async function showDoc(route, anchor) {
    let html;
    try {
      html = await loadContent(route);
    } catch {
      render("404");
      $("#main").innerHTML =
        `<section class="searchpage"><h1>Page not in site</h1><p class="searchhint">This document is not published here — it may live only in the <a href="https://github.com/denisekart/open-facilitator/tree/main/docs">repository docs</a>.</p><p><a class="btn" href="#/docs">Docs home</a></p></section>`;
      return;
    }
    const host = document.createElement("div");
    host.innerHTML = html;
    const toc = tocFor(html);
    // add anchor links to headings
    for (const h of host.querySelectorAll("h2[id], h3[id]")) {
      const a = document.createElement("a");
      a.className = "anchors";
      a.href = "javascript:void 0";
      a.textContent = "#";
      a.dataset.anchor = h.id;
      h.prepend(a);
    }
    const body = host.innerHTML;
    $("#main").innerHTML = `<div class="docspage">${docnavHtml(route)}
      <article class="article">${toc}${body}</article></div>`;
    wireArticle();
    if (anchor) {
      const el = document.getElementById(anchor);
      if (el) el.scrollIntoView();
    } else {
      window.scrollTo({ top: 0 });
    }
    document.title = ($(".article h1", document).textContent || "Docs") + " · Open Facilitator";
  }

  function wireArticle() {
    const main = $("#main");
    main.addEventListener("click", (e) => {
      const a = e.target.closest("a");
      if (!a) return;
      const anchor = a.dataset && a.dataset.anchor;
      if (anchor) {
        e.preventDefault();
        const el = document.getElementById(anchor);
        if (el) {
          el.scrollIntoView({ behavior: "smooth" });
          history.replaceState(null, "", `${location.pathname}${location.search}${currentHashRoute()}?${anchor}`);
        }
        return;
      }
      const href = a.getAttribute("href") || "";
      // relative #/... links: let the router handle; also strip ?anchor suffix links
      if (href.startsWith("#/") && href.includes("?")) {
        e.preventDefault();
        const [route, anc] = href.slice(2).split("?");
        navigate(`#${route}?${anc || ""}`);
      }
    });
  }

  const currentHashRoute = () => {
    const { route } = parseHash();
    return route ? `#${route}` : "#/docs";
  };

  // ------------------------------------------------------------ search

  function search(q) {
    const ix = state.index;
    const terms = q
      .toLowerCase()
      .split(/[^a-z0-9]+/)
      .filter((t) => t.length >= 2 && isNaN(Number(t)));
    if (!terms.length) return [];
    const scores = new Map(); // "pageIdx#secIdx" -> score
    let matchedAll = true;
    for (const t of terms) {
      let postings = ix.tok[t];
      if (!postings) {
        const pfx = Object.keys(ix.tok).filter((k) => k.startsWith(t)).slice(0, 12);
        postings = pfx.flatMap((k) => ix.tok[k].map(([p, n]) => [p, n * 0.6]));
      }
      if (!postings.length) matchedAll = false;
      for (const [p, n] of postings) scores.set(p, (scores.get(p) || 0) + n);
    }
    if (!matchedAll && scores.size === 0) return [];
    // score sections, keep best section per page
    const best = new Map();
    for (const [p, n] of scores) {
      const page = ix.pages[p];
      if (!page) continue;
      const cur = best.get(p);
      if (!cur || n > cur.n) best.set(p, { n, page });
    }
    return [...best.values()]
      .sort((a, b) => b.n - a.n)
      .slice(0, 20)
      .map(({ page }) => page);
  }

  function snippet(page, q) {
    const terms = q.toLowerCase().split(/[^a-z0-9]+/).filter((t) => t.length >= 2);
    for (const s of page.s) {
      const hay = (s[0] + " " + s[2]).toLowerCase();
      for (const t of terms) {
        const i = hay.indexOf(t);
        if (i >= 0) {
          const src = s[2];
          const at = s[0].toLowerCase().includes(t) ? 0 : Math.max(0, src.toLowerCase().indexOf(t) - 60);
          let snip = esc(src.slice(at, at + 200));
          for (const tt of terms)
            snip = snip.replace(new RegExp(`(${tt.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")})`, "ig"), "<mark>$1</mark>");
          return { text: (at > 0 ? "…" : "") + snip + "…", anchor: s[1] };
        }
      }
    }
    return { text: page.s.length ? esc(page.s[0][2].slice(0, 160)) + "…" : "", anchor: "" };
  }

  function showSearch(q) {
    const main = $("#main");
    main.innerHTML = `<section class="searchpage">
      <h1>Search the documentation</h1>
      <input class="field" id="sfield" type="search" placeholder="e.g. approval lifecycle, link token, gateway config…" value="${esc(q)}" autocomplete="off">
      <p class="searchhint" id="scount"></p>
      <div id="sresults"></div></section>`;
    const field = $("#sfield");
    const run = () => {
      const v = field.value.trim();
      const hist = history;
      hist.replaceState(null, "", v ? `#/search?q=${encodeURIComponent(v)}` : "#/search");
      const hits = v ? search(v) : [];
      $("#scount").textContent = v
        ? `${hits.length} page${hits.length === 1 ? "" : "s"} match${hits.length === 1 ? "es" : ""}`
        : "Type to search — the index is built into this site, nothing leaves your browser.";
      $("#sresults").innerHTML = hits
        .map((p) => {
          const sn = snippet(p, v);
          const where = p.g === "Guide" ? "Guide" : p.g;
          const routeHash = sn.anchor ? `#${p.r}?${sn.anchor}` : `#${p.r}`;
          return `<div class="result">
            <span class="where">${esc(where)}</span>
            <a class="title" href="${routeHash}">${esc(p.t)}${p.s.length && sn.anchor ? " › " + esc(p.s.find((s) => s[1] === sn.anchor)[0]) : ""}</a>
            <p class="snip">${sn.text}</p></div>`;
        })
        .join("");
    };
    field.addEventListener("input", run);
    run();
    field.focus();
    field.setSelectionRange(field.value.length, field.value.length);
  }

  // ------------------------------------------------------------ router

  async function render(what) {
    const { route, query } = parseHash();
    if (!state.manifest) await load();
    if (what === "search" || route === "/search") {
      showSearch(query.get("q") || "");
      document.title = "Search · Open Facilitator";
      return;
    }
    if (route && (route.startsWith("/docs") || route.startsWith("/guide"))) {
      await showDoc(route, parseHash().anchor);
      return;
    }
    showHome();
  }

  function showHome() {
    document.title = "Open Facilitator — durable coordination for human + agent work";
    // home body is server-rendered in index.html: nothing to do.
  }

  function navigate(hash) {
    if (location.hash === hash) render();
    else location.hash = hash;
  }

  window.addEventListener("hashchange", () => render());

  // topbar search: submit jumps to #/search
  document.addEventListener("submit", (e) => {
    const f = e.target.closest("form.searchbox");
    if (!f) return;
    e.preventDefault();
    const q = new FormData(f).get("q");
    navigate(`#/search?q=${encodeURIComponent(q || "")}`);
  });
  document.addEventListener("keydown", (e) => {
    if (e.key === "/" && !/input|textarea/i.test(document.activeElement.tagName)) {
      e.preventDefault();
      const q = document.getElementById("q");
      if (q) q.focus();
    }
  });

  // initial route (also serves 404.html: any deep link redirects home+route)
  const { route } = parseHash();
  if (route && route !== "/home") render();
  else if (location.pathname.endsWith("/404.html") && document.referrer === "") {
    // GitHub Pages deep-link fallback: send to the same route on the real index
  }
})();
