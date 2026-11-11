#!/usr/bin/env node
// The scenario runner: turns the reviewed bindings manifest into the audit
// record. Verdicts live in the manifest (hand-reviewed, and the xUnit
// ScenarioBindingsTests proves its evidence ids exist and it covers the
// catalog); this runner only reshapes and optionally cross-checks a run.
//
//   scenario-runner                       every row, TSV
//   scenario-runner --scenario A5         one row's JSON
//   scenario-runner --json                all rows as JSON
//   scenario-runner --md                  the audit-results.md table
//   scenario-runner --dotnet-trx f.trx    downgrade rows whose bound test
//                                         did not PASS in that run to
//                                         `divergent` (verdict honesty over
//                                         belief: a red test is a red signal);
//                                         repeatable — pass one per test
//                                         assembly (gate trx + walks trx)
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, "..", "..", "..");
const doc = JSON.parse(
  readFileSync(path.join(repo, "docs", "scenarios", "scenario-bindings.json"), "utf8"),
);

const args = process.argv.slice(2);
const flag = (name) => {
  const i = args.indexOf(name);
  return i === -1 ? null : i + 1 < args.length ? args[i + 1] : true;
};

// A trx cross-check: which bound FQNs RAN and which PASSED. VSTest layout:
// <UnitTestResult testId=... outcome="..."> joins <UnitTest id=...>
// <TestMethod className= name=/> in TestDefinitions. The flag is repeatable:
// every occurrence is read and unioned into the same ran/passed sets (the
// gate assembly and the walk assembly write one trx each).
const passed = new Set();
const ran = new Set();
const trxPaths = args.reduce(
  (acc, a, i) => (a === "--dotnet-trx" && i + 1 < args.length ? [...acc, args[i + 1]] : acc),
  [],
);
for (const trxPath of trxPaths) {
  const trx = readFileSync(trxPath, "utf8");
  const fqnById = new Map();
  for (const u of trx.matchAll(/<UnitTest\b[^>]*\bid="([^"]+)"[^>]*>([\s\S]*?)<\/UnitTest>/g)) {
    const cls = /className="([^"]+)"/.exec(u[2]);
    const name = /<TestMethod[^>]*\bname="([^"]+)"/.exec(u[2]);
    if (cls && name) fqnById.set(u[1], `${cls[1]}.${name[1]}`);
  }
  for (const r of trx.matchAll(/<UnitTestResult\b[^>]*>/g)) {
    const id = /\btestId="([^"]+)"/.exec(r[0]);
    const fqn = id ? fqnById.get(id[1]) : undefined;
    if (!fqn) continue;
    ran.add(fqn);
    if (r[0].includes('outcome="Passed"')) passed.add(fqn);
  }
}
const haveTrx = trxPaths.length > 0;

// Evidence id -> FQN: the one allowed prefix routes to the owning assembly
// ("ScenarioWalks:Cls.M" lives in Facilitator.ScenarioWalks; a bare
// Class.Method keeps resolving against Facilitator.Tests).
const toFqn = (e) =>
  e.startsWith("ScenarioWalks:")
    ? `Facilitator.ScenarioWalks.${e.slice("ScenarioWalks:".length)}`
    : `Facilitator.Tests.${e}`;

function row(s) {
  let verdict = s.verdict;
  const dotnet = s.evidence.filter((e) => !e.startsWith("js:"));
  // Verdict honesty over belief, but only over what the run OBSERVED: a
  // filtered run proves nothing about the tests it did not run.
  if (haveTrx && (verdict === "covered" || verdict === "partial")) {
    const fqns = dotnet.map(toFqn);
    const observed = fqns.filter((f) => ran.has(f));
    const failed = observed.filter((f) => !passed.has(f));
    if (failed.length === observed.length && observed.length > 0) verdict = "divergent";
    else if (failed.length > 0) verdict = "partial";
  }
  return {
    id: s.id,
    verdict,
    lane: s.lane,
    tests: s.evidence,
    note: s.note,
    ...(s.negative ? { negative: true } : {}),
  };
}

let rows = doc.scenarios.map(row);
const only = flag("--scenario");
if (typeof only === "string") rows = rows.filter((r) => r.id === only);

if (flag("--json")) {
  console.log(JSON.stringify(rows, null, 2));
} else if (flag("--md")) {
  console.log("| Scenario | Verdict | Lane | Codified test | Evidence |");
  console.log("|---|---|---|---|---|");
  for (const r of rows) {
    console.log(
      `| ${r.id} | ${r.verdict} | ${r.lane} | ${r.tests.join(" · ") || "—"} | ${r.note || "—"} |`,
    );
  }
} else if (typeof only === "string") {
  console.log(JSON.stringify(rows[0] ?? null, null, 2));
} else {
  for (const r of rows) console.log(`${r.id}\t${r.verdict}\t${r.lane}\t${r.tests.join(",")}`);
}
