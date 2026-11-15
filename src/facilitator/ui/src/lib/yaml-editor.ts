// Schema-driven CodeMirror 6 extension for the flow YAML face — no
// yaml-language-server, no worker: the grammar is the SAME documents the
// 422 validator compiles (GET /api/admin/workflows/schema), the closed
// value sets are the live /registry, and step ids are harvested from the
// document itself. Violations are warnings: POST /definitions/validate
// stays the save authority (the 422 remains law).

import {
  autocompletion,
  type Completion,
  type CompletionContext,
  type CompletionResult,
} from "@codemirror/autocomplete";
import { linter } from "@codemirror/lint";
import type { Diagnostic } from "@codemirror/lint";
import { yaml as yamlExtension } from "@codemirror/lang-yaml";
import { isScalar, isSeq, parseDocument, type Node } from "yaml";
import type { Extension } from "@codemirror/state";

export type SchemaDoc = Record<string, unknown>;

export interface YamlEditorSources {
  /** workflow-definition.v1.json, verbatim from /schema. */
  definition: SchemaDoc;
  /** registry closed sets, verbatim from /registry. */
  registry: {
    eventTypes: string[];
    commands: string[];
    capabilities: string[];
    callable: { id: string }[];
    teams?: string[];
  };
}

// ---- schema walking -------------------------------------------------------

function deref(node: unknown, root: SchemaDoc): SchemaDoc | null {
  if (!node || typeof node !== "object") {
    return null;
  }
  const n = node as SchemaDoc;
  const ref = typeof n["$ref"] === "string" ? n["$ref"] : null;
  if (ref?.startsWith("#/")) {
    let cur: unknown = root;
    for (const raw of ref.slice(2).split("/")) {
      const key = raw.replace(/~1/g, "/").replace(/~0/g, "~");
      if (!cur || typeof cur !== "object") {
        return null;
      }
      cur = (cur as SchemaDoc)[key];
    }
    return (cur as SchemaDoc) ?? null;
  }
  return n;
}

function follow(root: SchemaDoc, path: (string | number)[]): SchemaDoc | null {
  let cur: unknown = root;
  for (const seg of path) {
    const n = deref(cur, root);
    if (!n) {
      return null;
    }
    if (typeof seg === "number") {
      cur = deref(n["items"], root);
    } else {
      const props = n["properties"] as Record<string, unknown> | undefined;
      cur = props?.[seg] ?? null;
    }
  }
  return deref(cur, root);
}

/** Property names legal at a schema path, unioning oneOf/anyOf arms. */
function propsAt(root: SchemaDoc, path: (string | number)[]): Record<string, SchemaDoc> | null {
  const n = follow(root, path);
  if (!n) {
    return null;
  }
  const out: Record<string, SchemaDoc> = {
    ...((n["properties"] as Record<string, SchemaDoc> | undefined) ?? {}),
  };
  for (const arm of [
    ...((n["oneOf"] ?? []) as unknown as SchemaDoc[]),
    ...((n["anyOf"] ?? []) as unknown as SchemaDoc[]),
  ]) {
    const a = deref(arm, root);
    if (!a) {
      continue;
    }
    Object.assign(out, (a["properties"] as Record<string, SchemaDoc> | undefined) ?? {});
    for (const req of (a["required"] as string[] | undefined) ?? []) {
      out[req] ??= {};
    }
  }
  return Object.keys(out).length > 0 ? out : null;
}

// ---- document key index ----------------------------------------------------

export type KeyHit = {
  path: (string | number)[];
  from: number;
  to: number;
  /** Column of the key's line start position (its indentation). */
  col: number;
};

export type DocIndex = {
  keys: KeyHit[];
  stepIds: string[];
  stepRanges: Map<string, { from: number; to: number }>;
  errors: { pos: [number, number]; message: string }[];
};

/** Index every mapping key with its schema-ish path and offsets. */
export function indexYaml(text: string): DocIndex {
  const keys: KeyHit[] = [];
  const stepIds: string[] = [];
  const stepRanges = new Map<string, { from: number; to: number }>();
  const errors: { pos: [number, number]; message: string }[] = [];
  const doc = parseDocument(text, { prettyErrors: false });
  for (const err of doc.errors) {
    errors.push({
      pos: [
        Math.min(err.pos[0], text.length),
        Math.min(Math.max(err.pos[1], err.pos[0] + 1), text.length),
      ],
      message: err.message,
    });
  }
  const lineStarts = computeLineStarts(text);
  const colOf = (offset: number): number => {
    let lo = 0;
    let hi = lineStarts.length - 1;
    while (lo < hi) {
      const mid = Math.ceil((lo + hi) / 2);
      if (lineStarts[mid] <= offset) {
        lo = mid;
      } else {
        hi = mid - 1;
      }
    }
    return offset - lineStarts[lo];
  };

  const walkMapNode = (node: Node, path: (string | number)[]): void => {
    const holder = node as { items?: { key: Node; value: Node | null; type?: string }[] };
    for (const pair of holder.items ?? []) {
      const asPair = pair as unknown as { key: Node; value: Node | null };
      if (!isScalar(asPair.key)) {
        continue;
      }
      const name = String((asPair.key as { value?: unknown }).value ?? "");
      const range = (asPair.key as unknown as { range: [number, number] }).range;
      const here = [...path, name];
      keys.push({ path: here, from: range[0], to: range[1], col: colOf(range[0]) });
      const v = asPair.value;
      if (!v || isScalar(v)) {
        continue;
      }
      if (isSeq(v)) {
        if (name === "steps") {
          (v.items ?? []).forEach((item, i) => {
            const r = (item as unknown as { range?: [number, number] }).range;
            const pairs = (item as { items?: { key: Node; value: Node | null }[] }).items ?? [];
            const idPair = pairs.find(
              (p) => isScalar(p.key) && String((p.key as { value?: unknown }).value) === "id",
            );
            if (idPair && idPair.value && isScalar(idPair.value)) {
              const sid = String((idPair.value as { value?: unknown }).value ?? "");
              if (sid) {
                stepIds.push(sid);
                if (r) {
                  stepRanges.set(sid, { from: r[0], to: r[1] });
                }
              }
            }
            void i;
          });
        }
        (v.items ?? []).forEach((item, i) => {
          if (item && typeof item === "object" && "items" in item) {
            walkMapNode(item as Node, [...here, i]);
          }
        });
        continue;
      }
      walkMapNode(v, here);
    }
  };

  if (doc.contents) {
    walkMapNode(doc.contents, []);
  }
  keys.sort((a, b) => a.from - b.from);
  return { keys, stepIds, stepRanges, errors };
}

function computeLineStarts(text: string): number[] {
  const starts = [0];
  for (let i = 0; i < text.length; i += 1) {
    if (text[i] === "\n") {
      starts.push(i + 1);
    }
  }
  return starts;
}

// ---- completion ------------------------------------------------------------

/** Schema path of the map that encloses `pos`, given the key index. */
export function enclosingPathAt(index: DocIndex, text: string, pos: number): (string | number)[] {
  const lineStarts = computeLineStarts(text);
  let lo = 0;
  let hi = lineStarts.length - 1;
  while (lo < hi) {
    const mid = Math.ceil((lo + hi) / 2);
    if (lineStarts[mid] <= pos) {
      lo = mid;
    } else {
      hi = mid - 1;
    }
  }
  const lineFrom = lineStarts[lo];
  const lineText = text.slice(lineFrom, pos);
  const col = lineText.length - lineText.trimStart().length;
  const dashItem = /^\s*-(\s|$)/.test(lineText);

  // Closest preceding key at a SMALLER column, skipping keys deeper than
  // the cursor's line. On a "- " item line, the enclosing map is one level
  // INSIDE the key whose value is the seq — append the item index.
  let best: KeyHit | null = null;
  for (const k of index.keys) {
    if (k.to > pos) {
      break;
    }
    if (k.col < col || (dashItem && k.col === col)) {
      if (!best || k.from >= best.from) {
        best = k;
      }
    }
  }
  if (!best) {
    return [];
  }
  if (!dashItem) {
    return best.path;
  }
  // We're an item of the seq that is best.path's value: count siblings.
  const idx = seqIndexBefore(text, best, pos);
  return [...best.path, idx];
}

function seqIndexBefore(text: string, owner: KeyHit, pos: number): number {
  // Items of owner's seq are lines at column owner.col (or deeper) starting
  // with '-' after owner.from. Count those strictly before pos.
  let count = 0;
  const lineStarts = computeLineStarts(text);
  for (const start of lineStarts) {
    if (start <= owner.to || start >= pos) {
      continue;
    }
    const end = text.indexOf("\n", start);
    const line = text.slice(start, end === -1 ? text.length : end);
    const trimmed = line.trimStart();
    const indent = line.length - trimmed.length;
    if (trimmed.startsWith("- ") || trimmed === "-") {
      if (indent <= owner.col + 2) {
        count += 1;
      }
    } else if (indent <= owner.col && trimmed.length > 0 && !trimmed.startsWith("#")) {
      break; // left the seq: a sibling key at or above owner level
    }
  }
  return Math.max(0, count);
}

function valueOptions(
  path: (string | number)[],
  keyName: string,
  sources: YamlEditorSources,
  stepIds: string[],
): Completion[] | null {
  const p = [...path, keyName];
  const insideStep = p[0] === "steps" && typeof p[1] === "number";
  const rel = insideStep ? p.slice(2) : p;
  const const_ = (v: string): Completion => ({ label: v, type: "constant" });
  if (!insideStep && rel[0] === "on" && rel[1] === "event") {
    return sources.registry.eventTypes.map(const_);
  }
  if (insideStep && rel[0] === "emit" && rel[1] === "command") {
    return sources.registry.commands.map(const_);
  }
  if (insideStep && rel[0] === "emit" && rel[1] === "capability") {
    return sources.registry.capabilities.map(const_);
  }
  if (insideStep && rel[0] === "invoke" && rel[1] === "workflow") {
    return sources.registry.callable.map((c) => const_(c.id));
  }
  if (insideStep && rel[0] === "on") {
    return [
      "always",
      "completed",
      "timeout",
      "failed",
      ...stepIds.map((s) => `${s}:completed`),
    ].map(const_);
  }
  if (rel[0] === "team" && !insideStep) {
    return (sources.registry.teams ?? []).map(const_);
  }
  return null;
}

function complete(context: CompletionContext, sources: YamlEditorSources): CompletionResult | null {
  const text = context.state.doc.toString();
  const index = indexYaml(text);
  const line = context.state.doc.lineAt(context.pos);
  const before = line.text.slice(0, context.pos - line.from);

  // Value position: "key: <partial>" on this line.
  const valueMatch = /^(\s*)(?:-\s+)?([\w-]+):(\s+)([^\s#]*)$/.exec(before);
  if (valueMatch) {
    const path = enclosingPathAt(index, text, line.from);
    const options = valueOptions(path, valueMatch[2], sources, index.stepIds);
    if (options && options.length > 0) {
      const from = line.from + valueMatch[0].length - valueMatch[4].length;
      return { from, options, validFor: /^[\w.:/-]*$/ };
    }
    return null;
  }

  // Key position: complete property names legal at the enclosing path.
  const word = context.matchBefore(/[\w-]*/);
  const at = word?.from ?? context.pos;
  const path = enclosingPathAt(index, text, at);
  const props = propsAt(sources.definition, path);
  if (!props) {
    return null;
  }
  return {
    from: word?.from ?? at,
    options: Object.keys(props).map((k): Completion => {
      const d = props[k]?.["description"];
      return typeof d === "string"
        ? { label: k, type: "property", detail: d.split(".")[0] + "." }
        : { label: k, type: "property" };
    }),
    validFor: /^[\w-]*$/,
  };
}

// ---- lint ------------------------------------------------------------------

export function yamlDiagnostics(text: string, sources: YamlEditorSources): Diagnostic[] {
  const out: Diagnostic[] = [];
  const index = indexYaml(text);
  for (const err of index.errors) {
    out.push({
      from: err.pos[0],
      to: err.pos[1],
      severity: "warning",
      message: err.message,
    });
  }
  // Unknown property against the schema. A step map is exempt for its one
  // kind key (the oneOf member), which the schema lists in properties too.
  for (const k of index.keys) {
    const parent = k.path.slice(0, -1);
    const props = propsAt(sources.definition, parent);
    if (!props) {
      continue;
    }
    const name = String(k.path[k.path.length - 1]);
    if (!(name in props)) {
      out.push({
        from: k.from,
        to: k.to,
        severity: "warning",
        message: `unknown property '${name}' (server validates on save)`,
      });
    }
  }
  return out;
}

// ---- the extension ---------------------------------------------------------

export function ofacYamlExtensions(sources: YamlEditorSources): Extension[] {
  return [
    yamlExtension(),
    autocompletion({ override: [(context) => complete(context, sources)] }),
    linter((view) => yamlDiagnostics(view.state.doc.toString(), sources), { delay: 400 }),
  ];
}
