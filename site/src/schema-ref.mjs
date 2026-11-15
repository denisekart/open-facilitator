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

// Human glossary for the schema's named types - the generator prints the
// schema's own wording, this explains the jargon the schema assumes.
const DEF_NOTES = {
  duration:
    'ISO-8601 duration, days/hours/minutes/seconds only — "PT30M" is 30 minutes, "PT2H" two hours, "P1D" one day; zero is refused.',
  ref: 'a literal string, or a run-context template: "$event.data.<path>", "$steps.<stepId>.output.<path>", "$input.<path>".',
};
const EXT_NOTES = {
  "common.v1.json#/$defs/idToken": "an identifier string (team slug, step id, capability id…)",
  "common.v1.json#/$defs/evidenceRef": "an evidence actor reference",
};

function fieldsOf(schema) {
  const props = schema?.properties ?? {};
  const required = new Set(schema?.required ?? []);
  return Object.entries(props).map(([name, s]) => {
    const ref = typeof s?.$ref === "string" ? s.$ref : null;
    let type = Array.isArray(s?.type)
      ? s.type.join(" | ")
      : (s?.type ?? (ref ? "object" : "any"));
    let description = typeof s?.description === "string" ? s.description : "";
    if (ref) {
      const defName = ref.startsWith("#/$defs/") ? ref.slice(8) : null;
      if (defName) {
        type = defName;
        description = [description, DEF_NOTES[defName]].filter(Boolean).join(" ");
      } else if (EXT_NOTES[ref]) {
        type = "id";
        description = [description, EXT_NOTES[ref]].filter(Boolean).join(" ");
      }
    }
    return { name, type, required: required.has(name), description };
  });
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
