// The playbook grammar (v1 — frozen table). `apiVersion:
// facilitator.framework/v1`, `kind: Playbook`: an assignment's
// `capabilityId` selects a capability's step list; each step is
// spawn/assert/upload/publish. Validation law mirrors config.ts: strict,
// unknown keys rejected by name at every level, every error names its key.
import type { CapabilityDescriptor, CapabilityManifestV1 } from "@ofac/protocol";

export class PlaybookError extends Error {
  constructor(key: string, message: string) {
    super(`${key}: ${message}`);
    this.name = "PlaybookError";
  }
}

/** One ordered stdin answer for a prompt the step must see (each fires
 * once on its first de-ansified match). */
export interface PromptMarker {
  readonly match: RegExp;
  readonly answer: string;
  /** When true, an unmatched marker at exit is tolerated. */
  readonly optional: boolean;
}

/** `expect: {exit?, stdout?, stderr?, fileExists?, fileAbsent?, maxDurationMs?}`
 * — regexes run on the de-ansified streams; a miss fails the step
 * (retryable) with the step's name in the reason. */
export interface StepExpect {
  readonly exit?: number | undefined;
  readonly stdout?: RegExp | undefined;
  readonly stderr?: RegExp | undefined;
  readonly fileExists?: string | undefined;
  readonly fileAbsent?: string | undefined;
  readonly maxDurationMs?: number | undefined;
}

/** `retry: {attempts, backoffMs, retryOn: [exitCode...]}` — one
 * `work.progress.v1` note per retry. */
export interface StepRetry {
  readonly attempts: number;
  readonly backoffMs: number;
  readonly retryOn: readonly number[];
}

/** `ask: {question, choices?, contextRef?}` — ctx.requestInput; the answer
 * is exposed to later steps as `{{steps.<name>.answer}}`. */
export interface StepAsk {
  readonly question: string;
  readonly choices?: readonly string[] | undefined;
  readonly contextRef?: string | undefined;
}

/** `capture: {as, from: "stdout"|"file"|"stderr", mediaType}` — upload via
 * the artifact client, declared in the Result Manifest, referenced by the
 * completed event. `path` names the file for `from: file`. */
export interface StepCapture {
  readonly as: string;
  readonly from: "stdout" | "file" | "stderr";
  readonly mediaType: string;
  readonly path?: string | undefined;
}

/** `run: {command, args[], cwd?, env?, shell?: false}` — argv array, no
 * implicit shell; the binary must be one the capability's steps name. */
export interface StepRun {
  readonly command: string;
  readonly args: readonly string[];
  readonly cwd?: string | undefined;
  readonly env?: Readonly<Record<string, string>> | undefined;
  readonly shell: boolean;
}

export interface PlaybookStep {
  readonly name: string;
  readonly run?: StepRun | undefined;
  readonly tty: boolean;
  readonly timeoutMs: number;
  readonly expect?: StepExpect | undefined;
  readonly promptMarkers: readonly PromptMarker[];
  readonly feed?: string | undefined;
  readonly ask?: StepAsk | undefined;
  readonly capture?: StepCapture | undefined;
  readonly retry?: StepRetry | undefined;
  readonly onFail: "abort" | "continue";
  readonly progress?: string | undefined;
}

/** Optional MCP seeding for a hosted CLI that can ask_user unprompted:
 * seed `file` (relative to the step cwd/home) pointing the CLI's
 * MCP-loader at the shim; `format` picks the file dialect. */
export interface AskUserInject {
  readonly file: string;
  readonly format: "json" | "toml";
}

export interface PlaybookCapability {
  readonly id: string;
  readonly version: number;
  readonly execution: "asynchronous";
  readonly acceptedContexts: readonly string[];
  readonly outputKinds: readonly string[];
  readonly outputMediaTypes: readonly string[];
  /** Env var names this capability's steps may define/interpolate; unset
   * means steps may not define any env of their own. */
  readonly allowedEnvKeys?: readonly string[] | undefined;
  /** Declared defaults for `{{vars.*}}`; an assignment's inputs.vars wins. */
  readonly vars?: Readonly<Record<string, string>> | undefined;
  readonly askUserInject?: AskUserInject | undefined;
  readonly steps: readonly PlaybookStep[];
  /** The manifest face: derived fields for the advertised CapabilityManifestV1
   * (steering is always false — fire-per-step, no mid-run channel). */
  readonly manifestEntry: CapabilityDescriptor;
}

export interface Playbook {
  readonly capabilities: ReadonlyMap<string, PlaybookCapability>;
  /** The advertised manifest: one entry per capability, playbook-derived. */
  readonly manifest: CapabilityManifestV1;
  /** Every binary any step may run (allow-listed; the payload surface). */
  readonly binaries: ReadonlySet<string>;
}
