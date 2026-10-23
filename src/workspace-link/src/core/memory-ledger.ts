// In-memory Ledger for unit tests and the Plan 05 in-process harness. Mirrors
// SqliteLedger semantics (delivery_id OR dedupe_key ⇒ duplicate) exactly.
import type { InsertResult, Ledger, LedgerRow, LedgerState } from "./ledger.js";

interface MemRow {
  deliveryId: string;
  state: LedgerState;
  envelope: unknown;
  error?: string;
  updatedAt: number;
}

export class MemoryLedger implements Ledger {
  private readonly rows = new Map<string, MemRow>();
  private readonly byKey = new Map<string, string>();
  private readonly events = new Map<string, { id: string; envelope: unknown; sentAt?: number }>();
  private clock = 0;

  insertIfNew(deliveryId: string, envelope: unknown, dedupeKey: string): InsertResult {
    if (this.rows.has(deliveryId) || this.byKey.has(dedupeKey)) return "duplicate";
    this.rows.set(deliveryId, {
      deliveryId,
      state: "received",
      envelope,
      updatedAt: ++this.clock,
    });
    this.byKey.set(dedupeKey, deliveryId);
    return "fresh";
  }

  markState(deliveryId: string, state: LedgerState, error?: string): void {
    const row = this.rows.get(deliveryId);
    if (!row) throw new Error(`ledger: unknown delivery ${deliveryId}`);
    row.state = state;
    row.error = error;
    row.updatedAt = ++this.clock;
  }

  processedDeliveries(limit: number): string[] {
    return [...this.rows.values()]
      .filter((r) => r.state === "processed")
      .sort((a, b) => b.updatedAt - a.updatedAt)
      .slice(0, limit)
      .map((r) => r.deliveryId);
  }

  pending(): LedgerRow[] {
    return [...this.rows.values()]
      .filter((r) => r.state === "received")
      .map((r) => ({ deliveryId: r.deliveryId, state: r.state }));
  }

  stats(): { received: number; processed: number; failed: number; unsupported: number } {
    const out = { received: 0, processed: 0, failed: 0, unsupported: 0 };
    for (const row of this.rows.values()) out[row.state] += 1;
    return out;
  }

  queueEvent(id: string, envelope: unknown): void {
    this.events.set(id, { id, envelope });
  }

  unsentEvents(limit: number): { id: string; envelope: unknown }[] {
    return [...this.events.values()]
      .filter((e) => e.sentAt === undefined)
      .slice(0, limit)
      .map((e) => ({ id: e.id, envelope: e.envelope }));
  }

  markEventSent(id: string): void {
    const e = this.events.get(id);
    if (e) e.sentAt = ++this.clock;
  }

  close(): void {
    /* nothing durable */
  }
}
