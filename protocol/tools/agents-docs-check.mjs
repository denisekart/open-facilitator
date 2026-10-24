// AGENTS.md drift gate: agent docs are signposts, so every path they cite must
// exist and every area of the tree must have (or explicitly exempt) a signpost.
// Run from repo root: node protocol/tools/agents-docs-check.mjs
import { readdirSync, readFileSync, existsSync } from "node:fs";
import { join, dirname, resolve } from "node:path";

const ROOT = resolve(new URL("../..", import.meta.url).pathname);
const SKIP = new Set(["node_modules", "dist", "bin", "obj", "docs", ".git"]);
// Areas without their own signpost file; remove an entry once a source file lands.
const EXEMPT = new Set(["src/gateway"]);
const MAX_LINES = 80;
const failures = [];

function walk(dir) {
  const out = [];
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory() && !SKIP.has(e.name)) out.push(...walk(join(dir, e.name)));
    else if (e.isFile() && e.name === "AGENTS.md") out.push(join(dir, e.name));
  }
  return out;
}

// Backticked tokens that look like worktree paths, plus markdown link targets.
function referencedPaths(text) {
  const paths = [];
  const skip = (t) =>
    !t.includes("/") || // a bare word or a bare `bin/`-style token is layout noise
    !/[^/]+\/[^/]+/.test(t.replace(/\/+$/, "")) ||
    /^[a-zA-Z]+:\/\//.test(t) ||
    /^[#/~]/.test(t); // anchors, route literals, home paths
  for (const [, tok] of text.matchAll(/`([^`\s]+)`/g)) {
    if (!skip(tok)) paths.push(tok.replace(/\/+$/, ""));
  }
  for (const [, tok] of text.matchAll(/\]\(([^)\s]+)\)/g)) {
    if (/^[a-zA-Z]+:\/\//.test(tok) || /^[#/~]/.test(tok)) continue;
    const p = tok.split("#")[0];
    if (/[^/]+\/[^/]+/.test(p)) paths.push(p);
  }
  return paths;
}

const docs = walk(ROOT);
for (const doc of docs) {
  const rel = doc.slice(ROOT.length + 1);
  const text = readFileSync(doc, "utf8");
  const lines = text.split("\n");
  if (lines.length > MAX_LINES + 1)
    failures.push(`${rel}: ${lines.length} lines exceeds the ${MAX_LINES}-line cap`);
  if (/Plan [0-9]/.test(text))
    failures.push(`${rel}: cites implementation-plan status (banned; keep it in .omp/plans/)`);
  for (const p of referencedPaths(text)) {
    if (!existsSync(join(dirname(doc), p)) && !existsSync(join(ROOT, p)))
      failures.push(`${rel}: referenced path \`${p}\` does not exist`);
  }
}

const rootDoc = join(ROOT, "AGENTS.md");
if (existsSync(rootDoc) && readFileSync(rootDoc, "utf8").includes("Status: greenfield"))
  failures.push('AGENTS.md: contains "Status: greenfield" (stale status prose)');

for (const area of ["tests", "reference", "features", "protocol"]) {
  if (!EXEMPT.has(area) && !existsSync(join(ROOT, area, "AGENTS.md")))
    failures.push(`${area}/: no AGENTS.md and no EXEMPT entry`);
}
if (existsSync(join(ROOT, "src")))
  for (const e of readdirSync(join(ROOT, "src"), { withFileTypes: true })) {
    if (!e.isDirectory()) continue;
    const rel = `src/${e.name}`;
    if (!EXEMPT.has(rel) && !existsSync(join(ROOT, rel, "AGENTS.md")))
      failures.push(`${rel}/: no AGENTS.md and no EXEMPT entry`);
  }

if (failures.length) {
  for (const f of failures) console.error(`agents-docs-check: ${f}`);
  process.exit(1);
}
console.log(`agents-docs-check: ${docs.length} docs, all references resolve`);
