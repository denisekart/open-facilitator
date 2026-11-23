#!/usr/bin/env node
// The parity fake: a deterministic, zero-AI stand-in for a research CLI.
// Reproduces what the deterministic worker's two-step script produced:
// a markdown report for the objective, and (publish flavor) a
// research-publish.result body. Reads the attachment files the harness
// dropped from `inputs.sourceRefs` (so the inbound-media leg is
// exercised) and embeds a passed --answer (the human leg's ride).
import { existsSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const mode = process.argv[2] ?? "analyze"; // analyze | publish
const out = process.argv[3]; // report path

function arg(flag) {
  const index = process.argv.indexOf(flag);
  return index >= 0 ? process.argv[index + 1] : undefined;
}

const objective = arg("--objective") ?? "unknown";
const attachDir = arg("--attachments") ?? "attachments";
const answer = arg("--answer");

process.stdout.write("analyzing: source review\n");

const attachmentLines = [];
if (existsSync(attachDir)) {
  for (const name of readdirSync(attachDir).sort()) {
    const bytes = readFileSync(join(attachDir, name));
    const head = Buffer.from(bytes.subarray(0, 8)).toString("hex");
    attachmentLines.push(`attachment ${name}: ${String(bytes.byteLength)}B ${head}`);
  }
}

const publishing = mode === "publish";
const suffix = publishing ? "published" : "report";
const answerLine = answer === undefined || answer === "" ? "" : `\n- input answered: ${answer}`;
const attachmentSection =
  attachmentLines.length === 0 ? "" : `\n## Attachments\n${attachmentLines.join("\n")}`;
const report = `# Research ${objective}
Objective: ${objective}
## Steps
- step 1 complete (analyze)
- step 2 complete (draft)
## Steering directives
none${answerLine}${attachmentSection}
## Conclusion
Deterministic ${publishing ? "publish" : "analysis"} complete.
`;

if (out !== undefined) writeFileSync(out, report);
process.stdout.write(`fake-research-tool ${suffix} ok\n`);
