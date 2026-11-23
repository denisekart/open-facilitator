// Playbook loader: YAML in, validated Playbook out. The strict law from
// config.ts, extended to every nesting level: an unknown key at ANY level
// fails, naming the full dotted path of the offending key.
import { readFileSync } from "node:fs";
import { parse } from "yaml";
import type { CapabilityDescriptor, CapabilityManifestV1 } from "@ofac/protocol";
import { PlaybookError } from "./schema.js";
import type {
  AskUserInject,
  Playbook,
  PlaybookCapability,
  PlaybookStep,
  PromptMarker,
  StepAsk,
  StepCapture,
  StepExpect,
  StepRetry,
  StepRun,
} from "./schema.js";

export { PlaybookError };

const TOP_KEYS: Record<string, true> = {
  apiVersion: true,
  kind: true,
  ownerTeam: true,
  capabilities: true,
};
const CAP_KEYS: Record<string, true> = {
  id: true,
  version: true,
  execution: true,
  acceptedContexts: true,
  outputKinds: true,
  outputMediaTypes: true,
  allowedEnvKeys: true,
  vars: true,
  askUserInject: true,
  steps: true,
};
const STEP_KEYS: Record<string, true> = {
  name: true,
  run: true,
  tty: true,
  timeoutMs: true,
  expect: true,
  promptMarkers: true,
  feed: true,
  ask: true,
  capture: true,
  retry: true,
  onFail: true,
  progress: true,
};
const RUN_KEYS: Record<string, true> = {
  command: true,
  args: true,
  cwd: true,
  env: true,
  shell: true,
};
const EXPECT_KEYS: Record<string, true> = {
  exit: true,
  stdout: true,
  stderr: true,
  fileExists: true,
  fileAbsent: true,
  maxDurationMs: true,
};
const MARKER_KEYS: Record<string, true> = { match: true, answer: true, optional: true };
const ASK_KEYS: Record<string, true> = { question: true, choices: true, contextRef: true };
const CAPTURE_KEYS: Record<string, true> = { as: true, from: true, mediaType: true, path: true };
const RETRY_KEYS: Record<string, true> = { attempts: true, backoffMs: true, retryOn: true };
const INJECT_KEYS: Record<string, true> = { file: true, format: true };
const ACCEPTED_CONTEXTS: Record<string, true> = {
  "uri-reference": true,
  "inline-text": true,
  "artifact-reference": true,
};
const CAPABILITY_ID_RE = /^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)+$/;
const STEP_NAME_RE = /^[a-z][a-z0-9_-]*$/;
const ENV_KEY_RE = /^[A-Z_][A-Z0-9_]*$/;

function fail(key: string, message: string): never {
  throw new PlaybookError(key, message);
}

function asRecord(value: unknown, key: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    fail(key, "must be a mapping");
  }
  return value as Record<string, unknown>;
}

function rejectUnknown(doc: Record<string, unknown>, allowed: Record<string, true>, where: string): void {
  for (const key of Object.keys(doc)) {
    if (!(key in allowed)) fail(`${where}.${key}`, "unknown key");
  }
}

function requireString(doc: Record<string, unknown>, key: string, where: string): string {
  const value = doc[key];
  if (typeof value !== "string" || value.length === 0) {
    fail(`${where}.${key}`, "must be a non-empty string");
  }
  return value;
}

function optionalString(
  doc: Record<string, unknown>,
  key: string,
  where: string,
): string | undefined {
  const value = doc[key];
  if (value === undefined || value === null) return undefined;
  if (typeof value !== "string" || value.length === 0) {
    fail(`${where}.${key}`, "must be a non-empty string");
  }
  return value;
}

function requireInt(
  doc: Record<string, unknown>,
  key: string,
  where: string,
  min: number,
  max: number,
): number {
  const value = doc[key];
  if (typeof value !== "number" || !Number.isInteger(value) || value < min || value > max) {
    fail(`${where}.${key}`, `must be an integer in ${min}..${max}`);
  }
  return value;
}

function optionalInt(
  doc: Record<string, unknown>,
  key: string,
  where: string,
  min: number,
  max: number,
  fallback: number,
): number {
  if (doc[key] === undefined || doc[key] === null) return fallback;
  return requireInt(doc, key, where, min, max);
}

function optionalBool(
  doc: Record<string, unknown>,
  key: string,
  where: string,
  fallback: boolean,
): boolean {
  const value = doc[key];
  if (value === undefined || value === null) return fallback;
  if (typeof value !== "boolean") fail(`${where}.${key}`, "must be a boolean");
  return value;
}

function stringList(
  doc: Record<string, unknown>,
  key: string,
  where: string,
  { required }: { required: boolean },
): readonly string[] {
  const value = doc[key];
  if (value === undefined || value === null) {
    if (required) fail(`${where}.${key}`, "required (a non-empty list of strings)");
    return [];
  }
  if (!Array.isArray(value) || value.length === 0) {
    fail(`${where}.${key}`, "must be a non-empty list of strings");
  }
  return value.map((entry, index) => {
    if (typeof entry !== "string" || entry.length === 0) {
      fail(`${where}.${key}[${String(index)}]`, "must be a non-empty string");
    }
    return entry;
  });
}

function parseEnv(
  doc: Record<string, unknown>,
  key: string,
  where: string,
  allowed: ReadonlySet<string> | undefined,
): Record<string, string> | undefined {
  const value = doc[key];
  if (value === undefined || value === null) return undefined;
  const envDoc = asRecord(value, `${where}.${key}`);
  const env: Record<string, string> = {};
  for (const [name, raw] of Object.entries(envDoc)) {
    if (!ENV_KEY_RE.test(name)) fail(`${where}.${key}.${name}`, "must be an env var name");
    if (allowed !== undefined && !allowed.has(name)) {
      fail(`${where}.${key}.${name}`, "not in the capability's allowedEnvKeys");
    }
    if (typeof raw !== "string") fail(`${where}.${key}.${name}`, "must be a string");
    env[name] = raw;
  }
  return env;
}

function parseStringMap(
  doc: Record<string, unknown>,
  key: string,
  where: string,
): Record<string, string> | undefined {
  const value = doc[key];
  if (value === undefined || value === null) return undefined;
  const mapDoc = asRecord(value, `${where}.${key}`);
  const out: Record<string, string> = {};
  for (const [name, raw] of Object.entries(mapDoc)) {
    if (typeof raw !== "string") fail(`${where}.${key}.${name}`, "must be a string");
    out[name] = raw;
  }
  return out;
}

function parseRegex(value: unknown, key: string): RegExp {
  if (typeof value !== "string" || value.length === 0) fail(key, "must be a non-empty regex string");
  try {
    return new RegExp(value);
  } catch (error) {
    fail(key, `not a valid regex: ${(error as Error).message}`);
  }
}

function parseExpect(doc: Record<string, unknown>, where: string): StepExpect {
  rejectUnknown(doc, EXPECT_KEYS, where);
  const maxDurationMs =
    doc["maxDurationMs"] === undefined || doc["maxDurationMs"] === null
      ? undefined
      : requireInt(doc, "maxDurationMs", where, 1, 3_600_000);
  return {
    ...(doc["exit"] !== undefined && doc["exit"] !== null
      ? { exit: requireInt(doc, "exit", where, 0, 255) }
      : {}),
    ...(doc["stdout"] !== undefined && doc["stdout"] !== null
      ? { stdout: parseRegex(doc["stdout"], `${where}.stdout`) }
      : {}),
    ...(doc["stderr"] !== undefined && doc["stderr"] !== null
      ? { stderr: parseRegex(doc["stderr"], `${where}.stderr`) }
      : {}),
    ...(optionalString(doc, "fileExists", where) !== undefined
      ? { fileExists: optionalString(doc, "fileExists", where) }
      : {}),
    ...(optionalString(doc, "fileAbsent", where) !== undefined
      ? { fileAbsent: optionalString(doc, "fileAbsent", where) }
      : {}),
    ...(maxDurationMs !== undefined ? { maxDurationMs } : {}),
  };
}

function parseMarkers(doc: Record<string, unknown>, where: string): PromptMarker[] {
  const value = doc["promptMarkers"];
  if (value === undefined || value === null) return [];
  if (!Array.isArray(value) || value.length === 0) {
    fail(`${where}.promptMarkers`, "must be a non-empty list");
  }
  return value.map((entry, index) => {
    const at = `${where}.promptMarkers[${String(index)}]`;
    const marker = asRecord(entry, at);
    rejectUnknown(marker, MARKER_KEYS, at);
    const answer = marker["answer"];
    if (typeof answer !== "string") fail(`${at}.answer`, "must be a string");
    return {
      match: parseRegex(marker["match"], `${at}.match`),
      answer,
      optional: optionalBool(marker, "optional", at, false),
    };
  });
}

function parseAsk(doc: Record<string, unknown>, where: string): StepAsk | undefined {
  const value = doc["ask"];
  if (value === undefined || value === null) return undefined;
  const askDoc = asRecord(value, `${where}.ask`);
  rejectUnknown(askDoc, ASK_KEYS, `${where}.ask`);
  const choices =
    askDoc["choices"] === undefined || askDoc["choices"] === null
      ? undefined
      : stringList(askDoc, "choices", `${where}.ask`, { required: true });
  return {
    question: requireString(askDoc, "question", `${where}.ask`),
    ...(choices !== undefined ? { choices } : {}),
    ...(optionalString(askDoc, "contextRef", `${where}.ask`) !== undefined
      ? { contextRef: optionalString(askDoc, "contextRef", `${where}.ask`) }
      : {}),
  };
}

function parseCapture(doc: Record<string, unknown>, where: string): StepCapture | undefined {
  const value = doc["capture"];
  if (value === undefined || value === null) return undefined;
  const captureDoc = asRecord(value, `${where}.capture`);
  rejectUnknown(captureDoc, CAPTURE_KEYS, `${where}.capture`);
  const from = requireString(captureDoc, "from", `${where}.capture`);
  if (from !== "stdout" && from !== "file" && from !== "stderr") {
    fail(`${where}.capture.from`, "must be 'stdout', 'file' or 'stderr'");
  }
  const path = optionalString(captureDoc, "path", `${where}.capture`);
  if (from === "file" && path === undefined) fail(`${where}.capture.path`, "required for from: file");
  if (from !== "file" && path !== undefined) {
    fail(`${where}.capture.path`, "only valid with from: file");
  }
  return {
    as: requireString(captureDoc, "as", `${where}.capture`),
    from,
    mediaType: requireString(captureDoc, "mediaType", `${where}.capture`),
    ...(path !== undefined ? { path } : {}),
  };
}

function parseRetry(doc: Record<string, unknown>, where: string): StepRetry | undefined {
  const value = doc["retry"];
  if (value === undefined || value === null) return undefined;
  const retryDoc = asRecord(value, `${where}.retry`);
  rejectUnknown(retryDoc, RETRY_KEYS, `${where}.retry`);
  const retryOn = retryDoc["retryOn"];
  if (!Array.isArray(retryOn) || retryOn.length === 0) {
    fail(`${where}.retry.retryOn`, "must be a non-empty list of exit codes");
  }
  return {
    attempts: requireInt(retryDoc, "attempts", `${where}.retry`, 1, 100),
    backoffMs: optionalInt(retryDoc, "backoffMs", `${where}.retry`, 0, 600_000, 0),
    retryOn: retryOn.map((entry, index) => {
      if (typeof entry !== "number" || !Number.isInteger(entry) || entry < 0 || entry > 255) {
        fail(`${where}.retry.retryOn[${String(index)}]`, "must be an integer 0..255");
      }
      return entry;
    }),
  };
}

function parseRun(
  doc: Record<string, unknown>,
  where: string,
  allowedEnv: ReadonlySet<string> | undefined,
): StepRun | undefined {
  const value = doc["run"];
  if (value === undefined || value === null) return undefined;
  const runDoc = asRecord(value, `${where}.run`);
  rejectUnknown(runDoc, RUN_KEYS, `${where}.run`);
  const shell = optionalBool(runDoc, "shell", `${where}.run`, false);
  if (shell !== false) fail(`${where}.run.shell`, "v1 supports only shell: false (argv, no implicit shell)");
  const args = runDoc["args"];
  if (args !== undefined && args !== null && (!Array.isArray(args) || args.some((a) => typeof a !== "string"))) {
    fail(`${where}.run.args`, "must be a list of strings");
  }
  const env = parseEnv(runDoc, "env", `${where}.run`, allowedEnv);
  return {
    command: requireString(runDoc, "command", `${where}.run`),
    args: (args as readonly string[] | undefined) ?? [],
    ...(optionalString(runDoc, "cwd", `${where}.run`) !== undefined
      ? { cwd: optionalString(runDoc, "cwd", `${where}.run`) }
      : {}),
    ...(env !== undefined ? { env } : {}),
    shell: false,
  };
}

function parseStep(
  entry: unknown,
  index: number,
  where: string,
  allowedEnv: ReadonlySet<string> | undefined,
  defaultTimeoutMs: number,
): PlaybookStep {
  const at = `${where}.steps[${String(index)}]`;
  const stepDoc = asRecord(entry, at);
  rejectUnknown(stepDoc, STEP_KEYS, at);
  const name = requireString(stepDoc, "name", at);
  if (!STEP_NAME_RE.test(name)) {
    fail(`${at}.name`, "must match ^[a-z][a-z0-9_-]*$");
  }
  const stepAt = `${at}('${name}')`;
  const onFail = optionalString(stepDoc, "onFail", at) ?? "abort";
  if (onFail !== "abort" && onFail !== "continue") {
    fail(`${at}.onFail`, "must be 'abort' or 'continue'");
  }
  const expectDoc = stepDoc["expect"];
  return {
    name,
    ...(parseRun(stepDoc, at, allowedEnv) !== undefined ? { run: parseRun(stepDoc, at, allowedEnv) } : {}),
    tty: optionalBool(stepDoc, "tty", at, false),
    timeoutMs: optionalInt(stepDoc, "timeoutMs", at, 1, 3_600_000, defaultTimeoutMs),
    ...(expectDoc !== undefined && expectDoc !== null
      ? { expect: parseExpect(asRecord(expectDoc, `${at}.expect`), `${at}.expect`) }
      : {}),
    promptMarkers: parseMarkers(stepDoc, at),
    ...(optionalString(stepDoc, "feed", at) !== undefined ? { feed: optionalString(stepDoc, "feed", at) } : {}),
    ...(parseAsk(stepDoc, at) !== undefined ? { ask: parseAsk(stepDoc, at) } : {}),
    ...(parseCapture(stepDoc, at) !== undefined ? { capture: parseCapture(stepDoc, at) } : {}),
    ...(parseRetry(stepDoc, at) !== undefined ? { retry: parseRetry(stepDoc, at) } : {}),
    onFail,
    ...(optionalString(stepDoc, "progress", at) !== undefined
      ? { progress: optionalString(stepDoc, "progress", at) }
      : {}),
    ...{},
  };
}

function parseInject(doc: Record<string, unknown>, where: string): AskUserInject | undefined {
  const value = doc["askUserInject"];
  if (value === undefined || value === null) return undefined;
  const injectDoc = asRecord(value, `${where}.askUserInject`);
  rejectUnknown(injectDoc, INJECT_KEYS, `${where}.askUserInject`);
  const format = requireString(injectDoc, "format", `${where}.askUserInject`);
  if (format !== "json" && format !== "toml") {
    fail(`${where}.askUserInject.format`, "must be 'json' or 'toml'");
  }
  return { file: requireString(injectDoc, "file", `${where}.askUserInject`), format };
}

function parseCapability(
  entry: unknown,
  index: number,
  defaultTimeoutMs: number,
): PlaybookCapability {
  const where = `capabilities[${String(index)}]`;
  const capDoc = asRecord(entry, where);
  rejectUnknown(capDoc, CAP_KEYS, where);
  const id = requireString(capDoc, "id", where);
  if (!CAPABILITY_ID_RE.test(id)) {
    fail(`${where}.id`, "must be a dotted reverse-style id like 'research.analyze'");
  }
  const at = `${where}('${id}')`;
  const execution = requireString(capDoc, "execution", where);
  if (execution !== "asynchronous") {
    fail(`${at}.execution`, "v1 playbooks run asynchronous capabilities only");
  }
  const acceptedContexts = stringList(capDoc, "acceptedContexts", at, { required: true });
  for (const context of acceptedContexts) {
    if (!(context in ACCEPTED_CONTEXTS)) {
      fail(`${at}.acceptedContexts`, `'${context}' is not a known context kind`);
    }
  }
  const outputKinds = stringList(capDoc, "outputKinds", at, { required: true });
  const outputMediaTypes = stringList(capDoc, "outputMediaTypes", at, { required: true });
  const allowedEnvKeys =
    capDoc["allowedEnvKeys"] === undefined || capDoc["allowedEnvKeys"] === null
      ? undefined
      : stringList(capDoc, "allowedEnvKeys", at, { required: true });
  if (allowedEnvKeys !== undefined) {
    for (const key of allowedEnvKeys) {
      if (!ENV_KEY_RE.test(key)) fail(`${at}.allowedEnvKeys`, `'${key}' is not an env var name`);
    }
  }
  const vars = parseStringMap(capDoc, "vars", at);
  const stepsRaw = capDoc["steps"];
  if (!Array.isArray(stepsRaw) || stepsRaw.length === 0) {
    fail(`${at}.steps`, "required (a non-empty list of steps)");
  }
  const allowedEnvSet = allowedEnvKeys === undefined ? undefined : new Set(allowedEnvKeys);
  const steps = stepsRaw.map((stepEntry, stepIndex) =>
    parseStep(stepEntry, stepIndex, at, allowedEnvSet, defaultTimeoutMs),
  );
  const seen = new Set<string>();
  for (const step of steps) {
    if (seen.has(step.name)) fail(`${at}.steps`, `duplicate step name '${step.name}'`);
    seen.add(step.name);
    if (step.run === undefined && step.ask === undefined) {
      fail(`${at}.steps('${step.name}')`, "needs 'run' and/or 'ask' (a step does something)");
    }
    if (step.retry !== undefined && step.expect?.exit === undefined && step.retry.retryOn.length === 0) {
      fail(`${at}.steps('${step.name}').retry`, "retryOn is empty and no expect.exit to key off");
    }
  }
  const askUserInject = parseInject(capDoc, at);
  const manifestEntry: CapabilityDescriptor = {
    id,
    version: requireInt(capDoc, "version", where, 1, 1000),
    execution: "asynchronous",
    acceptedContexts: acceptedContexts as CapabilityDescriptor["acceptedContexts"],
    outputKinds: [...outputKinds],
    outputMediaTypes: [...outputMediaTypes],
    // Fire-per-step: no mid-run channel, so never steerable; the group kill
    // is always there, and input.required works (ask steps + the shim).
    supportsSteering: false,
    supportsCancellation: true,
    supportsNudge: false,
    supportsInteraction: true,
  };
  return {
    id,
    version: manifestEntry.version,
    execution: "asynchronous",
    acceptedContexts,
    outputKinds,
    outputMediaTypes,
    ...(allowedEnvKeys !== undefined ? { allowedEnvKeys } : {}),
    ...(vars !== undefined ? { vars } : {}),
    ...(askUserInject !== undefined ? { askUserInject } : {}),
    steps,
    manifestEntry,
  };
}

export interface ParsePlaybookOptions {
  /** Ceiling for steps without an explicit timeoutMs (the worker config's). */
  defaultTimeoutMs?: number | undefined;
}

export function parsePlaybook(
  yamlText: string,
  options: ParsePlaybookOptions = {},
): Playbook {
  const defaultTimeoutMs = options.defaultTimeoutMs ?? 600_000;
  let doc: unknown;
  try {
    doc = parse(yamlText);
  } catch (error) {
    fail("file", `not parseable YAML: ${(error as Error).message}`);
  }
  const root = asRecord(doc, "file");
  rejectUnknown(root, TOP_KEYS, "file");
  const apiVersion = optionalString(root, "apiVersion", "file");
  if (apiVersion !== undefined && apiVersion !== "facilitator.framework/v1") {
    fail("apiVersion", "must be 'facilitator.framework/v1'");
  }
  const kind = optionalString(root, "kind", "file");
  if (kind !== undefined && kind !== "Playbook") {
    fail("kind", "must be 'Playbook'");
  }
  const ownerTeam = optionalString(root, "ownerTeam", "file") ?? "team-alpha";
  const capabilitiesRaw = root["capabilities"];
  if (!Array.isArray(capabilitiesRaw) || capabilitiesRaw.length === 0) {
    fail("capabilities", "required (a non-empty list)");
  }
  const capabilities = new Map<string, PlaybookCapability>();
  const binaries = new Set<string>();
  for (const [index, entry] of capabilitiesRaw.entries()) {
    const capability = parseCapability(entry, index, defaultTimeoutMs);
    if (capabilities.has(capability.id)) {
      fail("capabilities", `duplicate capability id '${capability.id}'`);
    }
    for (const step of capability.steps) {
      if (step.run !== undefined) binaries.add(step.run.command);
    }
    capabilities.set(capability.id, capability);
  }
  const manifest: CapabilityManifestV1 = {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam,
    capabilities: [...capabilities.values()].map((capability) => capability.manifestEntry),
  };
  return { capabilities, manifest, binaries };
}

export function loadPlaybookFile(
  path: string,
  options: ParsePlaybookOptions = {},
): Playbook {
  let text: string;
  try {
    text = readFileSync(path, "utf8");
  } catch (error) {
    throw new PlaybookError("playbookFile", `cannot read ${path}: ${(error as Error).message}`);
  }
  return parsePlaybook(text, options);
}
