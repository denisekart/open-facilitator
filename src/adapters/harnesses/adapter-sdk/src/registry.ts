// The adapter-side execution registry: the ONLY place an adapter remembers
// what it is running. It answers the lifecycle questions the wire asks
// (dedupe on start, reconcile on status) and keeps the terminal snapshot
// window that lets a late `status` still tell the truth.
//
// Registry law (locked):
// - keyed by executionId (Gateway-issued, opaque); a secondary index by the
//   assign envelope's commandId powers the start-dedupe;
// - a settled entry keeps its terminal state for TERMINAL_TTL_MS, then the
//   entry is dropped and the registry honestly answers `unknown`;
// - the registry SURVIVES a socket restart (same process): an in-flight run
//   keeps running and the Gateway's re-register probe gets `running`.

/** How long a settled execution keeps answering `status` before `unknown`. */
export const TERMINAL_TTL_MS = 60_000;

export type RegistryState = "running" | "completed" | "failed";

export interface RegistryEntry {
  readonly executionId: string;
  readonly commandId: string;
  state: RegistryState;
  /** Epoch ms of the transition to a terminal state; 0 while running. */
  settledAt: number;
  /** The steer directives this run received, arrival order (harness-visible). */
  readonly steers: { directive: string; at: number }[];
  /** The harness's opaque native-run id (persistence hook; SDK never reads it). */
  sessionRef?: string | undefined;
}

export class ExecutionRegistry {
  private readonly byExecution = new Map<string, RegistryEntry>();
  private readonly byCommand = new Map<string, string>();

  /** The duplicate-start answer / the late-status answer. */
  find(executionId: string): RegistryEntry | undefined {
    return this.expire(this.byExecution.get(executionId));
  }

  /** 06's dedupe law: the same commandId re-delivered under a new executionId. */
  findByCommandId(commandId: string): RegistryEntry | undefined {
    const executionId = this.byCommand.get(commandId);
    return executionId === undefined ? undefined : this.expire(this.byExecution.get(executionId));
  }

  track(executionId: string, commandId: string): RegistryEntry {
    const entry: RegistryEntry = {
      executionId,
      commandId,
      state: "running",
      settledAt: 0,
      steers: [],
    };
    this.byExecution.set(executionId, entry);
    this.byCommand.set(commandId, executionId);
    return entry;
  }

  settle(executionId: string, state: Exclude<RegistryState, "running">): void {
    const entry = this.byExecution.get(executionId);
    if (entry === undefined) return;
    entry.state = state;
    entry.settledAt = Date.now();
  }

  drop(executionId: string): void {
    const entry = this.byExecution.get(executionId);
    if (entry === undefined) return;
    this.byExecution.delete(executionId);
    this.byCommand.delete(entry.commandId);
  }

  runningIds(): string[] {
    for (const executionId of [...this.byExecution.keys()]) {
      this.expire(this.byExecution.get(executionId));
    }
    return [...this.byExecution.values()]
      .filter((entry) => entry.state === "running")
      .map((entry) => entry.executionId);
  }

  /** Terminal entries answer `status` for TERMINAL_TTL_MS, then vanish. */
  private expire(entry: RegistryEntry | undefined): RegistryEntry | undefined {
    if (entry === undefined) return undefined;
    if (entry.state !== "running" && Date.now() - entry.settledAt > TERMINAL_TTL_MS) {
      this.drop(entry.executionId);
      return undefined;
    }
    return entry;
  }
}
