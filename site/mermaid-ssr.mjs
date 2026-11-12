// Build-time mermaid rendering via headless chromium --dump-dom.
// Returns a map diagramCode -> standalone <svg>. If no chromium binary is
// available, returns null and the caller keeps ```mermaid blocks as
// <pre class="mermaid-source"> (visible, greppable, honest).
import { spawnSync } from "node:child_process";
import { writeFileSync, mkdirSync, rmSync, copyFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const CHROMIUM = ["chromium", "chromium-browser", "google-chrome"].find(
  (b) => spawnSync(b, ["--version"]).status === 0,
);

const MERMAID_MIN = join(import.meta.dirname, "node_modules/mermaid/dist/mermaid.min.js");

export function mermaidAvailable() {
  return Boolean(CHROMIUM) && existsSync(MERMAID_MIN);
}

export async function renderMermaid(codes) {
  if (!CHROMIUM || codes.length === 0) return null;
  const dir = join(tmpdir(), `ofac-site-mermaid-${process.pid}`);
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  copyFileSync(MERMAID_MIN, join(dir, "mermaid.min.js"));
  const diags = codes
    .map(
      (c, i) =>
        `<pre class="mermaid" data-i="${i}">${escapeHtml(c)}</pre>`,
    )
    .join("\n");
  writeFileSync(
    join(dir, "page.html"),
    `<!DOCTYPE html><html><body style="margin:0;background:#fff">
${diags}
<script src="./mermaid.min.js"></script>
<script>
(async () => {
  mermaid.initialize({ startOnLoad: false, theme: "base", security: "strict", themeVariables: {
    darkMode: false, fontFamily: "system-ui, sans-serif",
    primaryColor: "#EAF6F2", primaryBorderColor: "#0B8A70", primaryTextColor: "#082A25",
    lineColor: "#2C6E63", secondaryColor: "#FDF1DE", tertiaryColor: "#F4F6F5",
    edgeLabelBackground: "#FFFFFF"
  } });
  const out = [];
  for (const el of document.querySelectorAll("pre.mermaid")) {
    try {
      const { svg } = await mermaid.render("d" + el.dataset.i, el.textContent);
      out.push([Number(el.dataset.i), svg]);
    } catch (e) {
      out.push([-1 - Number(el.dataset.i), "ERROR " + e.message]);
    }
  }
  const w = document.createElement("div");
  w.id = "svgs";
  w.textContent = encodeURIComponent(JSON.stringify(out));
  document.body.appendChild(w);
})();
</script></body></html>`,
  );
  const r = spawnSync(
    CHROMIUM,
    [
      "--headless",
      "--no-sandbox",
      "--disable-gpu",
      "--allow-file-access-from-files",
      "--virtual-time-budget=15000",
      "--dump-dom",
      `file://${join(dir, "page.html")}`,
    ],
    { encoding: "utf8", maxBuffer: 256 * 1024 * 1024 },
  );
  const dom = r.stdout || "";
  const m = dom.match(/<div id="svgs">([\s\S]*?)<\/div>/);
  rmSync(dir, { recursive: true, force: true });
  if (!m) return null;
  const map = new Map();
  for (const [i, svg] of JSON.parse(decodeURIComponent(m[1]))) {
    if (i >= 0) map.set(i, svg);
  }
  return map.size === codes.length ? map : null;
}

const escapeHtml = (s) =>
  s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

void existsSync;
