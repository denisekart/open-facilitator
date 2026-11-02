import { ApiError, getJson, type Fetcher } from "./api.js";
import type { AdminMetrics, AdminSummary } from "./types.js";

/**
 * Shared health data: one store drives every view —
 * summary every 15 s, metrics every 60 s, no websockets.
 */
class HealthStore {
  summary = $state<AdminSummary | null>(null);
  metrics = $state<AdminMetrics | null>(null);
  error = $state<string | null>(null);
  #started = false;

  start(fetcher: Fetcher): void {
    if (this.#started) {
      return;
    }
    this.#started = true;
    const refreshSummary = (): void => {
      void getJson<AdminSummary>(fetcher, "/api/admin/summary").then(
        (summary) => {
          this.summary = summary;
          this.error = null;
        },
        (err: unknown) => {
          this.error = err instanceof ApiError ? err.code : "fetch-failed";
        },
      );
    };
    const refreshMetrics = (): void => {
      void getJson<AdminMetrics>(fetcher, "/api/admin/metrics").then(
        (metrics) => {
          this.metrics = metrics;
        },
        () => undefined,
      );
    };
    refreshSummary();
    refreshMetrics();
    setInterval(refreshSummary, 15_000);
    setInterval(refreshMetrics, 60_000);
  }
}

export const health = new HealthStore();

/** Render a state-count dictionary as compact `state: n` pairs. */
export function countPairs(byState: Record<string, number>): string {
  const entries = Object.entries(byState);
  return entries.length === 0 ? "—" : entries.map(([state, n]) => `${state} ${n}`).join(" · ");
}
