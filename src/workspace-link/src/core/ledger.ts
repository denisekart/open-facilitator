// The durable local delivery ledger seam. SQLite implementation in `"./node"`;
// an in-memory implementation ships in core so consumers (and Plan 05's
// in-process harness) can run the client without a file. Law (S:301): the
// inbox row is persisted BEFORE the `accepted` receipt leaves the client.
export type LedgerState = "received" | "processed" | "failed" | "unsupported";

export type InsertResult = "fresh" | "duplicate";

export interface LedgerRow {
  deliveryId: string;
  state: LedgerState;
}

export interface Ledger {
  /**
   * Atomic check-and-insert on the dedupe identity: `commandId` for known
   * command types, CloudEvents `(source,id)` otherwise (server law S:301).
   * "duplicate" answers on a `delivery_id` OR `dedupe_key` hit — the handler
   * must NOT run and a `duplicate` receipt is the answer. EXCEPTION
   * (retryable-rejection law): a `failed` row was rejected without side
   * effects — the kernel re-offers exactly child-busy/child-disconnected/
   * child-offline receipts — so a re-offer re-runs: the row resets to
   * `received` and "fresh" answers.
   */
  insertIfNew(deliveryId: string, envelope: unknown, dedupeKey: string): InsertResult;
  markState(deliveryId: string, state: LedgerState, error?: string): void;
  /** Newest `processed` delivery ids (the hello `lastKnownAck` hint), by updated_at desc. */
  processedDeliveries(limit: number): string[];
  /** Rows still awaiting a receipt (post-crash reconciliation reports). */
  pending(): LedgerRow[];
  stats(): { received: number; processed: number; failed: number; unsupported: number };
  /** Queue of outbound event envelopes unsent across a partition (Step 6 law). */
  queueEvent(id: string, envelope: unknown): void;
  unsentEvents(limit: number): { id: string; envelope: unknown }[];
  markEventSent(id: string): void;
  close(): void;
}

/** Dedupe identity for a delivered envelope (mirrors LinkIngress on the server). */
export function dedupeKeyOf(
  envelope: { type: string; source: string; id: string; data?: Record<string, unknown> },
  knownCommandTypes: readonly string[],
): string {
  if (knownCommandTypes.includes(envelope.type)) {
    const commandId = envelope.data?.["commandId"];
    if (typeof commandId === "string" && commandId.length > 0) {
      return commandId;
    }
  }
  // Non-command identity: prefix-guarded join so ('a','b|c') ≠ ('a|b','c').
  return `\u241f${envelope.source}\u241f${envelope.id}`;
}
