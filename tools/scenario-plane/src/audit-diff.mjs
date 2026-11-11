#!/usr/bin/env node
// The audit record's regression gate: compare the committed record
// (docs/scenarios/audit-results.md, from HEAD in CI) against the table the
// runner just regenerated (stdin). Identical verdict sets pass; a verdict
// that moved DOWN its ladder fails (the record may not rot silently); a
// verdict that moved UP warns and passes (improvements get triaged by a
// human re-run, not by this gate). Rows appearing or disappearing at all
// fail: the catalog boundary is exact.
//
//   node runner.mjs --md | node audit-diff.mjs committed-audit.md
import { readFileSync } from "node:fs";

const RANK = {
  covered: 3,
  partial: 2,
  divergent: 1,
  "silent-absent": 1,
  absent: 1,
  unexpressible: 0,
  "environment-blocked": 0,
};

function parse(text) {
  const rows = new Map();
  for (const m of text.matchAll(/^\| (\S+) \| (\S+) \|/gm)) {
    if (m[1] === "Scenario" || m[1] === "---") continue;
    rows.set(m[1], m[2]);
  }
  return rows;
}

const committed = parse(readFileSync(process.argv[2] ?? "", "utf8"));
const fresh = parse(readFileSync(0, "utf8"));

const moved = [];
const missing = [];
const added = [];
for (const [id, oldVerdict] of committed) {
  const newVerdict = fresh.get(id);
  if (newVerdict === undefined) {
    missing.push(`- ${id}: row disappeared (was ${oldVerdict})`);
    continue;
  }
  const a = RANK[oldVerdict] ?? -1;
  const b = RANK[newVerdict] ?? -1;
  if (a !== b) moved.push({ id, oldVerdict, newVerdict, up: b > a });
}
for (const id of fresh.keys()) {
  if (!committed.has(id)) added.push(`+ ${id}: new row (${fresh.get(id)})`);
}

const up = moved.filter((m) => m.up);
const down = moved.filter((m) => !m.up);
const lines = (arr) =>
  arr.map((m) => `~ ${m.id}: ${m.oldVerdict} -> ${m.newVerdict}`).join("\n");

const histogram = {};
for (const v of fresh.values()) histogram[v] = (histogram[v] ?? 0) + 1;
if (process.env.GITHUB_STEP_SUMMARY) {
  const { appendFileSync } = await import("node:fs");
  appendFileSync(
    process.env.GITHUB_STEP_SUMMARY,
    `### Scenario audit diff\n\n` +
      Object.entries(histogram)
        .map(([v, n]) => `- ${v}: ${n}`)
        .join("\n") +
      "\n\n" +
      [...down, ...up].map((m) => `- ${m.id}: ${m.oldVerdict} -> ${m.newVerdict}`).join("\n") +
      "\n",
  );
}

if (missing.length || added.length) {
  console.error("audit row set changed:\n" + [...missing, ...added].join("\n"));
  process.exit(1);
}
if (down.length) {
  console.error("verdict regressions:\n" + lines(down));
  process.exit(1);
}
if (up.length) {
  console.error("verdict improvements (review + re-record):\n" + lines(up));
}
process.exit(0);
