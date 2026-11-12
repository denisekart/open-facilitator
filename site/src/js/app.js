// Open Facilitator site runtime: hash-routed content viewer + live search.
// No dependencies; works from file:// static hosting and project pages.
(function () {
  "use strict";

  const landing = document.getElementById("landing");
  const app = document.getElementById("app");
  const searchInput = document.getElementById("q");
  const searchPop = document.getElementById("search-pop");

  let manifest = null; // { repo, nav, guides }
  let searchIdx = null; // { pages, tok }

  const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

  // ---------------------------------------------------------------- routing

  function parseHash() {
    const h = location.hash || "";
    if (!h.startsWith("#/")) return { route: null, anchor: null, query: null };
    const raw = h.slice(1); // "/docs/overview?section"
    const [path, frag] = raw.split("?");
    const [routePart, queryPart] = path.split("?");
    void queryPart;
    if (raw.startsWith("/search")) {
      const q = (frag || "").replace(/^q=/, "");
      return { route: "/search", anchor: null, query: decodeURIComponent(q) };
    }
    return { route: routePart || "/", anchor: frag || null, query: null };
  }

  function isKnownRoute(route) {
    if (!manifest) return false;
    for (const g of manifest.nav) for (const it of g.items) if (it.r === route) return true;
    for (const g of manifest.guides) if (g.r === route) return true;
    return false;
  }

  // ---------------------------------------------------------------- chrome

  function sidebarHtml(activeRoute) {
    const groups = [];
    groups.push({
      group: "Guides",
      items: manifest.guides.map((g) => ({ r: g.r, t: g.t })),
    });
    for (const g of manifest.nav) groups.push(g);
    return groups
      .map(
        (g) =>
          `<div class="grp"><div>${esc(g.group)}</div>` +
          g.items
            .map(
              (it) =>
                `<a href="#${it.r}" class="${it.r === activeRoute ? "active" : ""}">${esc(it.t)}</a>`,
            )
            .join("") +
          `</div>`,
      )
      .join("");
  }

  function buildToc(root) {
    const hs = root.querySelectorAll("h2[id], h3[id]");
    if (!hs.length) return "";
    return (
      `<div class="grp">On this page</div>` +
      [...hs]
        .map(
          (h) =>
            `<a href="#${location.hash.split(/[?]/)[0]}?${h.id}" data-anchor="${h.id}">${esc(h.textContent)}</a>`,
        )
        .join("")
    );
  }

  function activateAnchor(root, anchor) {
    if (!anchor) return;
    const el = root.querySelector(`[id="${CSS.escape(anchor)}"]`);
    if (el) el.scrollIntoView({ behavior: "smooth", block: "start" });
  }

  // ------------------------------------------------ fullscreen diagram view

  let modal = null;
  function ensureModal() {
    if (modal) return modal;
    modal = document.createElement("div");
    modal.className = "dia-modal";
    modal.innerHTML =
      `<div class="dia-stage"><div class="dia-canvas"></div>` +
      `<div class="dia-bar"><button data-a="in" aria-label="Zoom in">+</button>` +
      `<button data-a="out" aria-label="Zoom out">−</button>` +
      `<button data-a="fit" aria-label="Fit to screen">fit</button>` +
      `<button data-a="close" aria-label="Close">close ✕</button></div>` +
      `<div class="dia-hint">scroll = zoom · drag = pan · esc = close</div></div>`;
    document.body.appendChild(modal);
    const stage = modal.querySelector(".dia-stage");
    const canvas = modal.querySelector(".dia-canvas");
    const view = { k: 1, x: 0, y: 0 };
    const apply = () => {
      canvas.style.transform = `translate(${view.x}px,${view.y}px) scale(${view.k})`;
    };
    const fit = () => {
      const svg = canvas.querySelector("svg");
      if (!svg) return;
      view.k = 1;
      view.x = 0;
      view.y = 0;
      apply();
      const r = svg.getBoundingClientRect();
      const w = r.width || 1;
      const h = r.height || 1;
      view.k = Math.min((stage.clientWidth * 0.94) / w, (stage.clientHeight * 0.9) / h, 3);
      view.x = (stage.clientWidth - w * view.k) / 2;
      view.y = (stage.clientHeight - h * view.k) / 2;
      apply();
    };
    modal.querySelector(".dia-bar").addEventListener("click", (e) => {
      const a = e.target.dataset && e.target.dataset.a;
      if (a === "in" || a === "out") {
        view.k = Math.min(8, Math.max(0.1, view.k * (a === "in" ? 1.3 : 1 / 1.3)));
        apply();
      } else if (a === "fit") fit();
      else close();
    });
    stage.addEventListener(
      "wheel",
      (e) => {
        e.preventDefault();
        const f = e.deltaY < 0 ? 1.18 : 1 / 1.18;
        const k2 = Math.min(8, Math.max(0.1, view.k * f));
        const r = stage.getBoundingClientRect();
        const px = e.clientX - r.left;
        const py = e.clientY - r.top;
        view.x = px - ((px - view.x) * k2) / view.k;
        view.y = py - ((py - view.y) * k2) / view.k;
        view.k = k2;
        apply();
      },
      { passive: false },
    );
    let drag = null;
    stage.addEventListener("pointerdown", (e) => {
      drag = { x: e.clientX, y: e.clientY, vx: view.x, vy: view.y };
      stage.setPointerCapture(e.pointerId);
    });
    stage.addEventListener("pointermove", (e) => {
      if (!drag) return;
      view.x = drag.vx + (e.clientX - drag.x);
      view.y = drag.vy + (e.clientY - drag.y);
      apply();
    });
    stage.addEventListener("pointerup", () => {
      drag = null;
    });
    modal._fit = fit;
    stage.addEventListener("mousemove", tipMoveHandler);
    stage.addEventListener("mouseleave", tipLeaveHandler);
    return modal;
  }
  function openDiagram(svg) {
    const m = ensureModal();
    const canvas = m.querySelector(".dia-canvas");
    canvas.innerHTML = "";
    canvas.appendChild(svg.cloneNode(true));
    m.classList.add("open");
    document.body.style.overflow = "hidden";
    m._fit();
  }
  function close() {
    if (!modal) return;
    modal.classList.remove("open");
    document.body.style.overflow = "";
  }
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") {
      close();
      if (searchPop) searchPop.classList.remove("open");
    }
  });

  // --------------------------------------------------------- hover tooltips

  let tipEl = null;
  function ensureTip() {
    if (!tipEl) {
      tipEl = document.createElement("div");
      tipEl.className = "diagram-tip";
      document.body.appendChild(tipEl);
    }
    return tipEl;
  }
  function tipMoveHandler(e) {
    const fig = e.currentTarget;
    let g = null;
    for (let el = e.target; el && el !== fig; el = el.parentElement) {
      if (el.querySelector && el.querySelector(":scope > title")) {
        g = el;
        break;
      }
    }
    const t = g && g.querySelector(":scope > title");
    const tip = ensureTip();
    if (!t || !t.textContent.trim()) {
      tip.style.display = "none";
      return;
    }
    const label =
      [...g.childNodes]
        .filter((n) => !(n instanceof SVGTitleElement) && n.nodeName !== "title")
        .map((n) => n.textContent || "")
        .join(" ")
        .replace(/\s+/g, " ")
        .trim() || "element";
    tip.innerHTML = `<b>${esc(label)}</b>${esc(t.textContent)}`;
    tip.style.display = "block";
    const w = tip.offsetWidth || 280;
    const x = Math.min(Math.max(12, e.clientX + 14), innerWidth - w - 12);
    tip.style.left = x + "px";
    tip.style.top = Math.min(e.clientY + 18, innerHeight - 90) + "px";
  }
  function tipLeaveHandler() {
    if (tipEl) tipEl.style.display = "none";
  }
  function wireDiagramTips(scope) {
    for (const fig of scope.querySelectorAll(".diagram")) {
      if (!fig._tipsWired) {
        fig._tipsWired = true;
        fig.addEventListener("mousemove", tipMoveHandler);
        fig.addEventListener("mouseleave", tipLeaveHandler);
      }
      const btn = fig.querySelector(".diagram-zoom");
      const svg = fig.querySelector(".svg-frame svg");
      if (btn && svg && !btn._zoomWired) {
        btn._zoomWired = true;
        btn.addEventListener("click", () => openDiagram(svg));
        if (svg.querySelector("title")) {
          svg.style.cursor = "zoom-in";
          svg.addEventListener("click", () => openDiagram(svg));
        }
      }
    }
  }

  // ------------------------------------------- client-side diagram fallback
  //
  // When the build host had no chromium, content ships `<pre class="mermaid-
  // source">` inside a pending figure. The vendored mermaid renders it here,
  // with the same theme and the same hover tooltips the build injects.

  const MERMAID_THEME = {
    darkMode: false,
    fontFamily: "system-ui, sans-serif",
    primaryColor: "#EAF1F9",
    primaryBorderColor: "#0B5FBF",
    primaryTextColor: "#1F2328",
    lineColor: "#5B6B7C",
    secondaryColor: "#FDF0DC",
    tertiaryColor: "#F2F5F8",
    edgeLabelBackground: "#FFFFFF",
    clusterBkg: "#F7F9FC",
    clusterBorder: "#C6CFDA",
    actorBkg: "#EAF1F9",
    actorBorder: "#0B5FBF",
    activationBkgColor: "#E7F0FA",
    sequenceNumberColor: "#FFFFFF",
    labelBoxBkgColor: "#EAF1F9",
    labelBoxBorderColor: "#0B5FBF",
  };

  let mermaidP = null;
  function loadMermaid() {
    if (!mermaidP) {
      mermaidP = new Promise((resolve, reject) => {
        const s = document.createElement("script");
        s.src = "mermaid.min.js";
        s.onload = () => {
          window.mermaid.initialize({
            startOnLoad: false,
            theme: "base",
            security: "strict",
            themeVariables: MERMAID_THEME,
          });
          resolve(window.mermaid);
        };
        s.onerror = () => {
          mermaidP = null;
          reject(new Error("mermaid unavailable"));
        };
        document.head.appendChild(s);
      });
    }
    return mermaidP;
  }

  let tipsP = null;
  function loadTips() {
    if (!tipsP)
      tipsP = fetch("tooltips.json")
        .then((r) => (r.ok ? r.json() : {}))
        .catch(() => ({}));
    return tipsP;
  }

  const normLabel = (s) => s.toLowerCase().replace(/\s+/g, " ").trim();

  async function hydrateDiagrams(scope) {
    const figs = [...scope.querySelectorAll(".diagram-pending")];
    if (!figs.length) return;
    let mermaid;
    let tips;
    try {
      [mermaid, tips] = await Promise.all([loadMermaid(), loadTips()]);
    } catch {
      for (const f of figs) {
        const cap = f.querySelector("figcaption");
        if (cap)
          cap.textContent = cap.textContent.replace(
            "rendering diagram…",
            "diagram source shown below",
          );
      }
      return;
    }
    let n = 0;
    for (const fig of figs) {
      const pre = fig.querySelector("pre.mermaid-source");
      if (!pre) continue;
      try {
        const { svg } = await mermaid.render("c" + Date.now().toString(36) + n++, pre.textContent);
        const titled = svg.replace(
          /(<g[^>]*class="[^"]*\b(?:node|actor)\b[^>]*>)([\s\S]{0,800}?)(<(?:\/g>|<g\b))/g,
          (whole, open, inner, next) => {
            const label = inner
              .replace(/<[^>]+>/g, " ")
              .replace(/\s+/g, " ")
              .trim();
            const tip = tips[normLabel(label)];
            return tip ? `${open}<title>${esc(tip)}</title>${inner}${next}` : whole;
          },
        );
        const frame = document.createElement("div");
        frame.className = "svg-frame";
        frame.innerHTML = titled;
        const btn = document.createElement("button");
        btn.className = "diagram-zoom";
        btn.setAttribute("aria-label", "Expand diagram");
        btn.textContent = "⤢ expand";
        fig.replaceChild(frame, pre);
        fig.insertBefore(btn, fig.firstChild);
        const cap = fig.querySelector("figcaption");
        if (cap)
          cap.textContent = cap.textContent.replace(
            "rendering diagram…",
            "hover any box for what it guarantees",
          );
        fig.classList.remove("diagram-pending");
      } catch {
        const cap = fig.querySelector("figcaption");
        if (cap)
          cap.textContent = cap.textContent.replace(
            "rendering diagram…",
            "diagram source shown below",
          );
      }
    }
    wireDiagramTips(scope);
  }

  // ---------------------------------------------------------------- search

  const STOP = new Set(
    "a an and are as at be by for from has have he if in is it its of on or that the to was were will with you your not but can this".split(
      " ",
    ),
  );

  function search(q) {
    if (!searchIdx) return [];
    const terms = q
      .toLowerCase()
      .split(/[^a-z0-9]+/)
      .filter((t) => t.length >= 2 && !STOP.has(t));
    if (!terms.length) return [];
    const scores = new Map(); // pageIdx -> score
    const perTerm = [];
    for (const t of terms) {
      const postings = searchIdx.tok[t] || [];
      const hit = new Map();
      for (const [pi, n] of postings) hit.set(pi, (hit.get(pi) || 0) + n);
      perTerm.push(hit);
    }
    for (const hit of perTerm) for (const [pi, n] of hit) scores.set(pi, (scores.get(pi) || 0) + n);
    // require at least one term; prefer pages matching most terms
    const results = [];
    for (const [pi, score] of scores) {
      const termsHit = perTerm.filter((h) => h.has(pi)).length;
      const p = searchIdx.pages[pi];
      let bestSec = null;
      for (const s of p.s) {
        const st = (s[0] + " " + s[2]).toLowerCase();
        if (terms.some((t) => st.includes(t))) {
          if (
            !bestSec ||
            terms.filter((t) => st.includes(t)).length >
              terms.filter((t) => (bestSec[0] + " " + bestSec[2]).toLowerCase().includes(t)).length
          )
            bestSec = s;
        }
      }
      results.push({ p, score: score * (1 + termsHit), bestSec });
    }
    results.sort((a, b) => b.score - a.score);
    return results.slice(0, 24);
  }

  function mark(text, q) {
    const terms = q
      .toLowerCase()
      .split(/[^a-z0-9]+/)
      .filter((t) => t.length >= 2);
    let out = esc(text);
    for (const t of terms) {
      out = out.replace(
        new RegExp(`(${t.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")})`, "gi"),
        "<mark>$1</mark>",
      );
    }
    return out;
  }

  function popHtml(q) {
    const rs = search(q).slice(0, 8);
    if (!rs.length)
      return `<div class="sp-none">No matches. Try “artifact”, “approval”, “gateway”.</div>`;
    return rs
      .map((r) => {
        const sec = r.bestSec;
        const href = sec && sec[1] ? `#${r.p.r}?${sec[1]}` : `#${r.p.r}`;
        return `<a href="${href}"><div class="sp-sec">${esc(r.p.g)}</div><div class="sp-page">${mark(sec ? sec[0] : r.p.t, q)}</div><div class="sp-x">${mark(sec ? sec[2] : "", q)}</div></a>`;
      })
      .join("");
  }

  function renderSearchPage(q) {
    app.innerHTML =
      `<div class="doc-layout"><div class="doc-nav">${sidebarHtml("/search")}</div>` +
      `<div class="doc-main"><h1 class="doc-title">Search</h1>` +
      `<p class="search-head">${q ? `Results for “${esc(q)}”` : "Type in the search box above."}</p>` +
      `<div id="sr-list"></div></div><div class="doc-toc"></div></div>`;
    const list = app.querySelector("#sr-list");
    const rs = search(q);
    list.innerHTML = rs
      .map((r) => {
        const sec = r.bestSec;
        const href = sec && sec[1] ? `#${r.p.r}?${sec[1]}` : `#${r.p.r}`;
        const crumb = sec ? `${esc(r.p.t)} › ${esc(sec[0])}` : esc(r.p.t);
        return `<div class="sr"><a href="${href}">${crumb}</a><div class="sr-sec">${esc(r.p.g)}</div><p>${mark(sec ? sec[2] : "", q)}</p></div>`;
      })
      .join("");
    if (q && !rs.length) list.innerHTML = `<p class="search-head">No matches for “${esc(q)}”.</p>`;
  }

  let popTimer;
  function onSearchInput() {
    clearTimeout(popTimer);
    popTimer = setTimeout(() => {
      const q = searchInput.value.trim();
      if (q.length < 2) {
        searchPop.classList.remove("open");
        return;
      }
      searchPop.innerHTML = popHtml(q);
      searchPop.classList.add("open");
    }, 120);
  }

  // integrations page: live category/row filter
  function wireIntegrationsFilter(body) {
    if (!body) return;
    const bar = document.createElement("div");
    bar.className = "intg-filter";
    bar.innerHTML =
      `<input type="search" placeholder="Filter integrations: telegram, forge, harness, oidc…" aria-label="Filter integrations">` +
      `<span class="count"></span>`;
    body.parentNode.insertBefore(bar, body);
    const input = bar.querySelector("input");
    const count = bar.querySelector(".count");
    // group content into h2 sections
    const sections = [];
    let cur = null;
    for (const el of [...body.children]) {
      if (el.tagName === "H2") {
        cur = { h: el, els: [el], text: "" };
        sections.push(cur);
      }
      if (cur) cur.els.push(el);
      if (cur && el.tagName !== "H1") cur.text += " " + (el.textContent || "");
    }
    const rows = [];
    for (const tbl of body.querySelectorAll("table"))
      for (const tr of tbl.querySelectorAll("tbody tr")) {
        rows.push(tr);
        tr.classList.add("intg-row");
      }
    for (const s of sections) if (s.h.closest("h2")) s.h.classList.add("intg-sec");
    input.addEventListener("input", () => {
      const q = input.value.trim().toLowerCase();
      let shown = 0;
      for (const r of rows) {
        const hit = !q || r.textContent.toLowerCase().includes(q);
        r.classList.toggle("hidden", !hit);
        if (hit) shown++;
      }
      let visibleSections = 0;
      for (const s of sections) {
        const secHit = !q || s.text.toLowerCase().includes(q);
        for (const el of s.els) el.style.display = secHit ? "" : "none";
        if (secHit) visibleSections++;
      }
      count.textContent = q ? `${visibleSections} sections · ${shown} rows match` : "";
    });
  }

  const cache = new Map();

  async function showRoute(route, anchor) {
    if (landing) landing.hidden = false;
    let html = cache.get(route);
    if (!html) {
      const file = route === "/docs" ? "content/docs.html" : "content" + route + ".html";
      const r = await fetch(file);
      if (!r.ok) return showError(route);
      html = await r.text();
      cache.set(route, html);
    }
    const entry = findEntry(route);
    if (landing) landing.hidden = true;
    const titleMatch = html.match(/<h1 class="doc-title"[^>]*>[\s\S]*?<\/h1>/);
    const titleHtml = titleMatch ? titleMatch[0] : "";
    const bodyHtml = titleHtml ? html.replace(titleHtml, "") : html;
    app.innerHTML =
      `<div class="doc-layout"><nav class="doc-nav">${sidebarHtml(route)}</nav>` +
      `<div class="doc-main">${titleHtml}<div class="doc-body">${bodyHtml}</div></div>` +
      `<aside class="doc-toc" id="toc"></aside></div>`;
    document.getElementById("toc").innerHTML = buildToc(app.querySelector(".doc-main"));
    app.querySelectorAll(".doc-nav a, #toc a").forEach((a) =>
      a.addEventListener("click", (e) => {
        e.preventDefault();
        location.hash = a.getAttribute("href");
      }),
    );
    wireDiagramTips(app);
    hydrateDiagrams(app);
    if (route === "/integrations") wireIntegrationsFilter(app.querySelector(".doc-body"));
    setActiveInSidebar(route);
    requestAnimationFrame(() => activateAnchor(app, anchor));
    document.title = (entry ? entry.t + " — " : "") + "Open Facilitator";
    void document.getElementById; // keep lint calm in odd hosts
  }

  function setActiveInSidebar(route) {
    app.querySelectorAll(".doc-nav a").forEach((a) => {
      a.classList.toggle("active", a.getAttribute("href") === "#" + route);
    });
  }

  function findEntry(route) {
    for (const g of manifest.nav) for (const it of g.items) if (it.r === route) return it;
    for (const g of manifest.guides) if (g.r === route) return g;
    return null;
  }

  function showError(route) {
    if (landing) landing.hidden = true;
    app.innerHTML =
      `<div class="doc-layout"><nav class="doc-nav">${sidebarHtml("")}</nav>` +
      `<div class="doc-main"><h1 class="doc-title">Not found</h1>` +
      `<p class="doc-kicker">Nothing is published at <code>${esc(route)}</code>. Try the <a href="#/docs">docs home</a> or search above.</p>` +
      `</div><aside class="doc-toc"></aside></div>`;
  }

  // ---------------------------------------------------------------- boot

  function render() {
    const { route, anchor, query } = parseHash();
    if (route === null) {
      // landing anchor (#how etc.) or bare load
      if (landing) {
        landing.hidden = false;
        app.innerHTML = "";
        app.style.display = "none";
        const el = location.hash ? document.getElementById(location.hash.slice(1)) : null;
        if (el) el.scrollIntoView({ behavior: "smooth", block: "start" });
      }
      return;
    }
    app.style.display = "block";
    if (route === "/search") return renderSearchPage(query || searchInput.value.trim());
    if (!isKnownRoute(route)) return showError(route);
    showRoute(route, anchor).catch(() => showError(route));
  }

  async function boot() {
    try {
      manifest = await (await fetch("site-manifest.json")).json();
    } catch {
      return; // static landing only
    }
    try {
      searchIdx = await (await fetch("search-index.json")).json();
    } catch {
      searchIdx = { pages: [], tok: {} };
    }
    searchInput.addEventListener("input", onSearchInput);
    searchInput.addEventListener("focus", onSearchInput);
    searchInput.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        location.hash = "#/search?q=" + encodeURIComponent(searchInput.value.trim());
        searchPop.classList.remove("open");
      }
      if (e.key === "Escape") searchPop.classList.remove("open");
    });
    document.addEventListener("click", (e) => {
      if (!e.target.closest(".searchbox")) searchPop.classList.remove("open");
    });
    searchPop.addEventListener("click", (e) => {
      const a = e.target.closest("a");
      if (a) {
        e.preventDefault();
        location.hash = a.getAttribute("href");
        searchPop.classList.remove("open");
        searchInput.value = "";
      }
    });
    window.addEventListener("hashchange", render);
    render();
    if (!location.hash && location.pathname.replace(/\/$/, "").endsWith("index.html")) {
      history.replaceState(
        null,
        "",
        location.pathname.replace(/index\.html$/, "") + location.search,
      );
    }
  }

  boot();
})();
