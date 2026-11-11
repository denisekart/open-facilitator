#!/usr/bin/env node
// The publishable set, computed as a CLOSURE, not a flag scan: a package is
// publishable when it declares `"private": false` OR some publishable
// package depends on it (a workspace: dep of a published package MUST be
// published, or the released tarball resolves to nothing). Print one package
// directory per line, dependency-ordered (deps before dependents) — the
// publish loops in `.github/workflows/release.yml` and the Makefile's
// `release` target consume this, so CI and a local release see one law.
import { readFileSync, existsSync } from "node:fs";
import { join, relative, resolve } from "node:path";

const root = resolve(import.meta.dirname, "../..");
// `--only <package-or-dir>` narrows the set to one package's dependency
// subtree (deps before it) — the manual single-adapter publisher needs
// exactly this closure, nothing unrelated.
const only = (() => {
  const i = process.argv.indexOf("--only");
  return i === -1 ? null : process.argv[i + 1];
})();
const workspace = readFileSync(join(root, "pnpm-workspace.yaml"), "utf8");
const globs = [...workspace.matchAll(/^ {2}- "([^"]+)"$/gm)].map((m) => m[1]);

// Exact-directory globs only (every workspace entry here is one) — no glob
// engine needed while the list stays literal.
const dirs = globs
  .map((g) => (g.includes("*") ? null : g))
  .filter((g) => g && existsSync(join(root, g, "package.json")));

const byName = new Map();
for (const dir of dirs) {
  const pkg = JSON.parse(readFileSync(join(root, dir, "package.json"), "utf8"));
  const entry = {
    dir,
    name: pkg.name,
    publishable: pkg.private === false,
    deps: Object.keys(pkg.dependencies ?? {}).filter((d) => d.startsWith("@ofac/")),
  };
  if (byName.has(pkg.name)) {
    throw new Error(`duplicate package name ${pkg.name} (${byName.get(pkg.name).dir} and ${dir})`);
  }
  byName.set(pkg.name, entry);
}
for (const e of byName.values()) {
  for (const d of e.deps) {
    if (!byName.has(d)) throw new Error(`${e.name} depends on ${d}, not in the workspace`);
  }
}
// Closure: a package is publishable when flagged or depended on (transitively)
// by a flagged one. DFS post-order emits dependencies first.
const state = new Map(); // name -> "visiting" | "done"
const out = [];
function visit(e, chain) {
  if (state.get(e.name) === "done") return;
  if (state.get(e.name) === "visiting") {
    throw new Error(`dependency cycle: ${[...chain, e.name].join(" -> ")}`);
  }
  state.set(e.name, "visiting");
  const publish = e.publishable || chain.some((n) => byName.get(n).publishable);
  for (const d of e.deps) visit(byName.get(d), [...chain, e.name]);
  state.set(e.name, "done");
  if (publish) out.push(e);
}
for (const e of byName.values()) visit(e, []);

// Fail loudly if a publishable's dependency never made the set (impossible
// post-closure, kept as the drift tripwire the closure law needs).
for (const e of out) {
  for (const d of e.deps) {
    if (!out.some((p) => p.name === d))
      throw new Error(`${e.name}: dep ${d} outside the publish set`);
  }
}
if (only) {
  const wanted = out.find((e) => e.name === only || e.dir === only.replace(/\/+$/, ""));
  if (!wanted) throw new Error(`--only ${only}: not a publishable package of this workspace`);
  const subtree = [];
  const seen = new Set();
  (function collect(e) {
    if (seen.has(e.name)) return;
    seen.add(e.name);
    for (const d of e.deps) collect(out.find((p) => p.name === d));
    subtree.push(e);
  })(wanted);
  out.length = 0;
  out.push(...subtree);
}
for (const e of out) console.log(relative(root, join(root, e.dir)));
