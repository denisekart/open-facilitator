// The public surface of `@ofac/adapter` (spec §8.3): the HarnessAdapter
// lifecycle contract as a TypeScript API over the shipped child wire
// (`connectChild` from @ofac/gateway, profiled by
// protocol/json-schema/adapter.v1.json). Documentation-grade types only —
// nothing here hits the wire beyond what adapter.v1.json already describes.
import type { CapabilityManifestV1, EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import type { LinkLogger } from "@ofac/workspace-link";

/**
 * §8.3 S:442's Adapter Descriptor, documentation-grade (NOT on the wire in
 * v1: registration carries the capability manifest, not the descriptor).
 */
export interface AdapterDescriptor {
  /** v1 ships the `sdk` invocation only (a library, not a spawned CLI). */
  readonly invocation: "sdk";
  readonly supports: {
    readonly steer: boolean;
    readonly interrupt: boolean;
    /** v1: no native-session resume; a restart reconciles from durable context. */
    readonly resume: "restart-with-context";
  };
}

/**
 * The persistence seam (S:442): a resumable harness plugs its
 * assignmentId/executionGeneration ↔ nativeRunId store in here. Nothing
 * in-repo implements it — `restart-with-context` is the sanctioned v1 path,
 * and the reconcile law (probe ⇒ `unknown` ⇒ `lost`) already covers honesty.
 */
export interface SessionStore {
  get(executionId: string): string | undefined | Promise<string | undefined>;
  set(executionId: string, sessionRef: string): void | Promise<void>;
  delete(executionId: string): void | Promise<void>;
}

/** Everything a harness run may reach: identity, events, steer, cancellation. */
export interface RunContext {
  readonly executionId: string;
  /** = start.connectionGeneration; stamped into every event payload this run publishes. */
  readonly executionGeneration: number;
  /** The work.assign envelope, verbatim. */
  readonly command: EnvelopeCommand;
  /** Fired by a `stop` frame (cooperative interrupt, S:439). */
  readonly signal: AbortSignal;
  /** Fire-and-forget relay of a FULL envelope upstream (`event` frame). */
  publishEvent(env: EnvelopeEvent): void;
  /** buildEvent sugar: work.progress.v1, generation stamped, subject = assignmentId. */
  publishProgress(note: string): void;
  /** Register once; steer frames invoke it. Unregistered ⇒ steer ⇒ `unsupported`. */
  setSteered(cb: (directive: string) => void): void;
  /**
   * Opaque native-run id (harness persistence hook). ChildReply has no
   * field for it, so the SDK keeps it in the registry; the harness sets it
   * itself (or via `sessionStore`) — the SDK never reads it.
   */
  sessionRef?: string | undefined;
}

export type RunOutcome =
  { readonly kind: "completed" } | { readonly kind: "failed"; readonly reason: string };

/** The harness half of the contract: the native run's lifecycle callbacks. */
export interface HarnessHooks {
  /**
   * The run itself. Resolving `{kind:"completed"}` means the harness ALREADY
   * published `assignment.completed.v1` itself (05 law: only child-published
   * completed/failed settle rows — the SDK never fabricates completion).
   * `{kind:"failed"}`/throw ⇒ the SDK publishes `assignment.failed.v1`
   * (retryable) — the single terminal event this package ever emits.
   */
  onExecute(ctx: RunContext): Promise<RunOutcome>;
  onSteer?(directive: string, ctx: RunContext): void;
  /** Cooperative interrupt (§8.3 S:439); hard termination is the runtime's job. */
  onCancel?(reason: string, ctx: RunContext): void;
  /** Reserved seam; v1's execution registry answers `status` itself. */
  statusProvider?(): { readonly activeCount: number };
  /** OPTIONAL persistence seam (see SessionStore); v1 in-repo: unimplemented. */
  sessionStore?: SessionStore;
}

export interface CreateAdapterOptions {
  /** `<gatewayId>/<name>` — Gateway-side law, validated here too (fail fast). */
  childWorkspaceId: string;
  /** Omitted ⇒ read `OFAC_CHILD_SECRET`; absent both ⇒ empty token. */
  token?: string;
  manifest: CapabilityManifestV1;
  transport: { readonly socketPath: string } | { readonly host: string; readonly port: number };
  descriptor: AdapterDescriptor;
  hooks: HarnessHooks;
  /** Gateway business cadence echoed on the wire; default 30 s. */
  heartbeatSeconds?: number;
  /** Restart backoff schedule (cap-loop forever on the last entry); `[]` disables auto-restart. */
  restartBackoffMs?: readonly number[];
  logger?: LinkLogger;
}

export interface AdapterStatus {
  readonly connected: boolean;
  readonly childGeneration: number;
  readonly activeExecutions: readonly string[];
}

export interface AdapterHandle {
  /** Resolves on the FIRST accepted register-ack; rejects `register denied: <reason>`. */
  ready(): Promise<void>;
  status(): AdapterStatus;
  close(): Promise<void>;
  readonly events: {
    on(event: "connected" | "disconnected" | "goaway", cb: (info?: unknown) => void): void;
  };
}
