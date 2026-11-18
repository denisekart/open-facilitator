// Coverage ladder merger + ratchet (`make coverage` final step).
//
// Reads raw tier-tagged output under artifacts/coverage/ (Cobertura from the
// .NET data collector, lcov json-summary from vitest v8), rolls it up per
// shipped area, and compares against the committed baseline in
// .omp/coverage/baseline.json. Any area whose line coverage regressed by
// more than RATCHET_SLOP fails the run; COVERAGE_UPDATE=1 instead rewrites
// the baseline (how the ratchet is moved upward). The human README.md is
// regenerated from the current run on every invocation.
//
// Semantics:
// - .NET (Cobertura): union across tiers at LINE level — a line is covered
//   iff any tier saw hits>0; branch conditions per line take the max covered
//   count across tiers (condition identity across runs is only the ordinal;
//   documented approximation, never averaged). Only classes some run loaded
//   appear — Cobertura has no all-files view.
// - TS (json-summary, `all: true`): every shipped file of the package is
//   present, untested at 0%. Each file appears in exactly one package, so
//   no cross-source union is needed.
// - Ecosystem numbers are kept per area AND per ecosystem in the baseline:
//   merged numbers sum counters, and the README can show dotnet vs ts side
//   by side if the merge is ever questioned — no silent apple-orange means.
//
// Usage: node tools/coverage-report.mjs   (from anywhere; paths are script-relative)
import fs from "node:fs";
import path from "node:path";

const REPO = path.resolve(import.meta.dirname, "..");
const RAW = path.join(REPO, "artifacts", "coverage");
const OUT = path.join(REPO, ".omp", "coverage");
const RATCHET_SLOP = 0.5; // percentage-point slop for merge-order noise

// ---------------------------------------------------------------- areas ---
// Area = shipped-code directory bucket (idea: per-directory rollup).
// null = deliberately unmeasured.
function areaOf(rel) {
  const p = rel.split("\\").join("/");
  if (p.startsWith("src/facilitator/ui/")) return "src/facilitator/ui";
  if (p.startsWith("src/facilitator/")) return "src/facilitator";
  if (p.startsWith("src/gateway/")) return "src/gateway";
  if (p.startsWith("src/workspace-link/")) return "src/workspace-link";
  if (p.startsWith("src/otel/")) return "src/otel";
  if (p.startsWith("src/protocol/")) {
    if (p.startsWith("src/protocol/src/generated/")) return null; // generated
    return "src/protocol";
  }
  if (p.startsWith("protocol/dotnet/")) {
    if (/(^|\/)Generated\//.test(p)) return null; // generated
    return "protocol/dotnet";
  }
  if (p.startsWith("src/adapters/")) {
    const seg = p.split("/");
    const deep = seg[2] === "communication" || seg[2] === "harnesses";
    return seg.slice(0, deep ? 4 : 3).join("/"); // per-adapter area
  }
  if (p.startsWith("tools/scenario-plane/")) return "tools/scenario-plane";
  return null;
}

// ------------------------------------------------------------- helpers ----
function newCount(covered = 0, total = 0) {
  return { covered, total };
}
function mergeCount(dst, src) {
  dst.covered += src.covered;
  dst.total += src.total;
}
function round1(n) {
  return Math.round(n * 10) / 10;
}
function pct(c) {
  return c.total === 0 ? null : round1((c.covered / c.total) * 100);
}
function walk(dir, out = []) {
  if (!fs.existsSync(dir)) return out;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, out);
    else out.push(p);
  }
  return out;
}

// ------------------------------------------------- dotnet line-level ------
// rel -> { lines: Map<lineno, Set<tier>>, branch: Map<lineno, {total, covered: Map<tier, n>}>, tiers: Set }
const dn = new Map();
function dnFile(rel) {
  let f = dn.get(rel);
  if (!f) dn.set(rel, (f = { lines: new Map(), branch: new Map() }));
  return f;
}

// Hand-rolled Cobertura parse: <class filename> blocks, <line> elements
// (hits; branch condition coverage when branch="true").
function parseCobertura(xml, tier) {
  const classRe = /<class\b[^>]*filename="([^"]+)"[^>]*>([\s\S]*?)<\/class>/g;
  let m;
  while ((m = classRe.exec(xml))) {
    const rel = m[1];
    const body = m[2];
    const f = dnFile(rel);
    const lineRe = /<line\b([^>]*?)\/?>/g;
    let l;
    while ((l = lineRe.exec(body))) {
      const attrs = l[1];
      const num = /\bnumber="(\d+)"/.exec(attrs);
      const hits = /\bhits="(\d+)"/.exec(attrs);
      if (!num || !hits) continue;
      const lineno = Number(num[1]);
      let tset = f.lines.get(lineno);
      if (!tset) f.lines.set(lineno, (tset = new Set()));
      tset.add(tier);
      const covered = Number(hits[1]) > 0;
      if (covered) tset.add("covered:" + tier);
      if (/branch="[Tt]rue"/.test(attrs)) {
        const cc = /condition-coverage="(\d+)% \((\d+)\/(\d+)\)"/.exec(attrs);
        if (cc) {
          let b = f.branch.get(lineno);
          if (!b) f.branch.set(lineno, (b = { total: Number(cc[3]), covered: new Map() }));
          b.total = Math.max(b.total, Number(cc[3]));
          b.covered.set(tier, Math.max(b.covered.get(tier) ?? 0, Number(cc[2])));
        }
      }
    }
  }
}

// ------------------------------------------------------ ts file-level -----
// rel -> { lines, branches, tier } (json-summary per-file totals).
const tsFiles = new Map();
function parseSummary(summaryPath) {
  const data = JSON.parse(fs.readFileSync(summaryPath, "utf8"));
  for (const [abs, f] of Object.entries(data)) {
    if (abs === "total") continue;
    const rel = path.relative(REPO, path.resolve(abs)).split(path.sep).join("/");
    let rec = tsFiles.get(rel);
    if (!rec)
      tsFiles.set(
        rel,
        (rec = {
          lines: newCount(),
          branches: newCount(),
          tier: "unit",
        }),
      );
    // Multiple packages cannot own the same file; multiple tiers in future
    // phases would sum — v8 line-level union of identical paths is additive
    // only when disjoint, so keep the MAX per tier-merge rule instead:
    // take the richer record. (Phase 1: ts is unit-only, this never fires.)
    if (f.lines.covered >= rec.lines.covered) {
      rec.lines = newCount(f.lines.covered, f.lines.total);
      rec.branches = newCount(f.branches.covered, f.branches.total);
    }
  }
}

// --------------------------------------------------------- collect raw ----
const dotnetRoot = path.join(RAW, "dotnet");
const dotnetTiers = fs.existsSync(dotnetRoot)
  ? fs
      .readdirSync(dotnetRoot, { withFileTypes: true })
      .filter((e) => e.isDirectory())
      .map((e) => e.name)
  : [];
for (const tier of dotnetTiers) {
  for (const f of walk(path.join(dotnetRoot, tier))) {
    if (f.endsWith(".cobertura.xml")) parseCobertura(fs.readFileSync(f, "utf8"), tier);
  }
}
for (const f of walk(path.join(RAW, "ts"))) {
  if (path.basename(f) === "coverage-summary.json") parseSummary(f);
}
// Optional leg timing written by the Makefile (tier -> wall seconds).
let tierSeconds = {};
const timingPath = path.join(RAW, "timings.json");
if (fs.existsSync(timingPath)) tierSeconds = JSON.parse(fs.readFileSync(timingPath, "utf8"));
// Optional reference-tier record (FULL=1): flows proved; kernel runs in a
// container, so no .NET instrumentation — non-numeric attribution only.
let reference = null;
const refPath = path.join(RAW, "reference.json");
if (fs.existsSync(refPath)) reference = JSON.parse(fs.readFileSync(refPath, "utf8"));

// ------------------------------------------------------------- rollup -----
// area -> { lines, branches, eco: {dotnet|ts: {lines, branches}},
//           tierLines: {tier: {covered,total}}, files: [...] }
const areas = new Map();
function area(a) {
  let v = areas.get(a);
  if (!v)
    areas.set(
      a,
      (v = {
        lines: newCount(),
        branches: newCount(),
        eco: {},
        tierLines: {},
        files: [],
      }),
    );
  return v;
}
function ensure(v, eco) {
  return (v.eco[eco] ??= { lines: newCount(), branches: newCount() });
}
function ensureTier(v, tier) {
  return (v.tierLines[tier] ??= newCount());
}

for (const [rel, f] of dn) {
  const a = areaOf(rel);
  if (a === null) continue;
  const v = area(a);
  const eco = ensure(v, "dotnet");
  const fileLines = newCount();
  const fileBranches = newCount();
  const tierSeen = new Set();
  for (const tset of f.lines.values()) {
    const covered = [...tset].some((t) => t.startsWith("covered:"));
    fileLines.total += 1;
    fileLines.covered += covered ? 1 : 0;
    for (const t of tset) if (!t.startsWith("covered:")) tierSeen.add(t);
  }
  for (const [, b] of f.branch) {
    const covered = Math.max(0, ...b.covered.values());
    fileBranches.total += b.total;
    fileBranches.covered += covered;
  }
  mergeCount(v.lines, fileLines);
  mergeCount(v.branches, fileBranches);
  mergeCount(eco.lines, fileLines);
  mergeCount(eco.branches, fileBranches);
  // per-tier share: recompute this file's lines for each tier that saw it
  for (const tier of tierSeen) {
    const t = ensureTier(v, tier);
    let cov = 0;
    let tot = 0;
    for (const [, tset] of f.lines) {
      if (!tset.has(tier)) continue;
      tot += 1;
      if (tset.has("covered:" + tier)) cov += 1;
    }
    mergeCount(t, newCount(cov, tot));
  }
  v.files.push({ eco: "dotnet", rel, lines: fileLines, branches: fileBranches });
}

for (const [rel, rec] of tsFiles) {
  const a = areaOf(rel);
  if (a === null) continue;
  const v = area(a);
  const eco = ensure(v, "ts");
  mergeCount(v.lines, rec.lines);
  mergeCount(v.branches, rec.branches);
  mergeCount(eco.lines, rec.lines);
  mergeCount(eco.branches, rec.branches);
  mergeCount(ensureTier(v, rec.tier), rec.lines);
  v.files.push({ eco: "ts", rel, lines: rec.lines, branches: rec.branches });
}

const tierPercents = {}; // area -> {tier: pct|"runs-only"}
for (const [a, v] of areas) {
  const t = {};
  for (const [tier, c] of Object.entries(v.tierLines)) t[tier] = pct(c);
  tierPercents[a] = t;
}
if (reference) {
  for (const a of ["src/facilitator"]) (tierPercents[a] ??= {}).reference = "runs-only";
  if (reference.seconds != null) tierSeconds.reference = reference.seconds;
}

// --------------------------------------------------------- dead zones ----
// Files WITH tests (some line covered) whose branch % trails line % by
// >= DEAD_GAP: untested decision paths inside tested code (Phase 2 input).
const DEAD_GAP = 20;
const deadZones = {};
for (const [a, v] of [...areas.entries()].sort()) {
  const rows = [];
  for (const f of v.files) {
    if (f.lines.covered === 0 || f.lines.total === 0 || f.branches.total === 0) continue;
    const lp = (f.lines.covered / f.lines.total) * 100;
    const bp = (f.branches.covered / f.branches.total) * 100;
    if (lp - bp >= DEAD_GAP)
      rows.push({
        file: f.rel,
        eco: f.eco,
        lines: round1(lp),
        branches: round1(bp),
        untestedBranches: f.branches.total - f.branches.covered,
      });
  }
  rows.sort((x, y) => y.untestedBranches - x.untestedBranches || x.file.localeCompare(y.file));
  if (rows.length) deadZones[a] = rows;
}

// ------------------------------------------------------------ baseline ----
const baselinePath = path.join(OUT, "baseline.json");
const readmePath = path.join(OUT, "README.md");
const next = { generated: new Date().toISOString(), areas: {}, tierSeconds };
for (const [a, v] of [...areas.entries()].sort()) {
  const entry = {
    lines: { pct: pct(v.lines), covered: v.lines.covered, total: v.lines.total },
    branches: {
      pct: pct(v.branches),
      covered: v.branches.covered,
      total: v.branches.total,
    },
    tiers: tierPercents[a],
    // Shipped files with a measurable size that no test in the ladder
    // imported (the `all: true` honesty proof; the ratchet's rise when you
    // light one up is the point).
    zeroFiles: v.files
      .filter((f) => f.lines.total > 0 && f.lines.covered === 0)
      .map((f) => ({ path: f.rel, lines: f.lines.total }))
      .sort((x, y) => y.lines - x.lines || x.path.localeCompare(y.path)),
  };
  for (const [eco, c] of Object.entries(v.eco)) {
    (entry.ecosystems ??= {})[eco] = {
      lines: { pct: pct(c.lines), covered: c.lines.covered, total: c.lines.total },
      branches: {
        pct: pct(c.branches),
        covered: c.branches.covered,
        total: c.branches.total,
      },
    };
  }
  next.areas[a] = entry;
}

const prev = fs.existsSync(baselinePath) ? JSON.parse(fs.readFileSync(baselinePath, "utf8")) : null;
const update = process.env.COVERAGE_UPDATE === "1";
const regressions = [];
if (prev && !update) {
  for (const [a, e] of Object.entries(next.areas)) {
    const old = prev.areas[a]?.lines?.pct;
    if (old == null || e.lines.pct == null) continue; // new area: no failure
    if (e.lines.pct < old - RATCHET_SLOP) regressions.push({ area: a, old, now: e.lines.pct });
  }
}

function fmtPct(p) {
  return p == null || p === undefined ? "—" : `${Number(p).toFixed(1)}%`;
}

function renderReadme(baseline) {
  const L = [];
  L.push("# Coverage baseline (measurement ladder)");
  L.push("");
  L.push(
    `Generated by \`make coverage\` + \`tools/coverage-report.mjs\` at ${baseline.generated}.`,
  );
  L.push("");
  L.push(
    "Merged numbers sum line/branch counters across ecosystems and tiers " +
      "(.NET union at line level; TS from v8 `all: true`, so untested shipped " +
      "files count at 0%; Cobertura contributes only classes a run loaded). " +
      "Per-ecosystem numbers live in `baseline.json` under `ecosystems`. " +
      "Tier columns show each tier's own line %; `reference` = acceptance " +
      "flows proved only (kernel in container: no instrumentation there).",
  );
  L.push("");
  L.push("| area | lines | branches | tiers (line %) |");
  L.push("| --- | ---: | ---: | --- |");
  for (const [a, e] of Object.entries(baseline.areas)) {
    const tiers = Object.entries(e.tiers ?? {})
      .map(([t, p]) => `${t} ${p === "runs-only" ? "runs-only" : fmtPct(p)}`)
      .join(", ");
    L.push(`| \`${a}\` | ${fmtPct(e.lines.pct)} | ${fmtPct(e.branches.pct)} | ${tiers} |`);
  }
  L.push("");
  if (Object.keys(baseline.tierSeconds ?? {}).length) {
    L.push("## Leg wall time (seconds)");
    L.push("");
    for (const [k, v] of Object.entries(baseline.tierSeconds)) L.push(`- ${k}: ${v}`);
    L.push("");
  }
  if (Object.keys(deadZones).length) {
    L.push(`## Dead zones (tested files whose branch % trails line % by >= ${DEAD_GAP})`);
    L.push("");
    L.push("Phase 2 input: untested decision paths in code that has tests.");
    L.push("");
    for (const [a, rows] of Object.entries(deadZones)) {
      L.push(`### \`${a}\``);
      L.push("");
      for (const r of rows)
        L.push(
          `- \`${r.file}\` — lines ${r.lines}%, branches ${r.branches}% (${r.untestedBranches} untested branches)`,
        );
      L.push("");
    }
  }
  L.push(
    `Ratchet: any area regressing > ${RATCHET_SLOP} pt line coverage fails the report. ` +
      "Move the ratchet with `COVERAGE_UPDATE=1`.",
  );
  L.push("");
  return L.join("\n");
}

// README always reflects the just-measured run; baseline.json moves only on
// first write or explicit COVERAGE_UPDATE=1 (the ratchet).
fs.mkdirSync(OUT, { recursive: true });
if (update || !prev) fs.writeFileSync(baselinePath, JSON.stringify(next, null, 2) + "\n");
fs.writeFileSync(readmePath, renderReadme(next));

if (regressions.length) {
  console.error("coverage ratchet FAILED — regressed areas (line %):");
  for (const r of regressions)
    console.error(
      `  ${r.area}: ${r.old.toFixed(1)} -> ${r.now.toFixed(1)} (-${(r.old - r.now).toFixed(1)})`,
    );
  console.error("If intended: COVERAGE_UPDATE=1 node tools/coverage-report.mjs");
  process.exit(1);
}
console.log(
  `coverage: ${Object.keys(next.areas).length} areas; ${
    prev ? (update ? "baseline updated" : "ratchet ok") : "baseline written"
  }.`,
);
