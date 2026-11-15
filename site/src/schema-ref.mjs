// Build-time reference generator: the step palette described by the
// schema that actually validates stored definitions — no hand re-encode.
// Reads the repo's own protocol schema (precedent: build.mjs reads repo
// docs) and the gallery JSON the WorkflowGallerySeeder embeds.

import { readFile } from "node:fs/promises";
import { join } from "node:path";

const esc = (s) =>
  String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

const PAYLOAD_ORDER = [
  "emit",
  "wait",
  "timer",
  "approval",
  "decide",
  "select",
  "fork-work",
  "work-create",
  "escalate",
  "fan-out",
  "signal",
  "invoke",
  "join",
  "memory",
];

function fieldsOf(schema) {
  const props = schema?.properties ?? {};
  const required = new Set(schema?.required ?? []);
  return Object.entries(props).map(([name, s]) => ({
    name,
    type:
      Array.isArray(s?.type)
        ? s.type.join(" | ")
        : (s?.type ?? (s?.$ref ? "object" : "any")),
    required: required.has(name),
    description: typeof s?.description === "string" ? s.description : "",
  }));
}

export async function paletteReference(root) {
  const schemaPath = join(root, "protocol/json-schema/workflow-definition.v1.json");
  const schema = JSON.parse(await readFile(schemaPath, "utf8"));
  const step = schema.$defs?.step;
  if (!step?.properties) throw new Error("schema: $defs.step.properties missing");
  const out = [];
  for (const kind of PAYLOAD_ORDER) {
    const p = step.properties[kind];
    if (!p) continue;
    const desc = typeof p.description === "string" ? p.description : "";
    const fields = fieldsOf(p);
    out.push(
      `<h3 id="step-${esc(kind)}"><code>${esc(kind)}</code></h3>` +
        (desc ? `<p>${esc(desc)}</p>` : "") +
        (fields.length
          ? `<table class="ref"><thead><tr><th>field</th><th>type</th><th>meaning</th></tr></thead><tbody>` +
            fields
              .map(
                (f) =>
                  `<tr><td><code>${esc(f.name)}</code>${f.required ? " *" : ""}</td>` +
                  `<td class="mono">${esc(f.type)}</td><td>${esc(f.description)}</td></tr>`,
              )
              .join("") +
            `</tbody></table>`
          : ""),
    );
  }
  return out.join("\n");
}

// The cookbook embeds the gallery's own files — the bytes the seeder
// stores, not a transcription.
export async function galleryDoc(root, id) {
  const { stringify } = await import("yaml");
  const p = join(root, "src/facilitator/Kernel/Workflows/Gallery", `${id}.json`);
  const doc = JSON.parse(await readFile(p, "utf8"));
  return { doc, yaml: stringify(doc) };
}
