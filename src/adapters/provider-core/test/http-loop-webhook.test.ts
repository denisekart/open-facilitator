// The shared provider law (Step 7): 429 honors Retry-After, 403 is a wall
// that never retries, a stall is observable (degradation record), the loop
// budget stops an indirect bot conversation at the cap and a human reply
// resets it, and webhook registration converges (ensure/replace, no dupes).
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import Database from "better-sqlite3";
import {
  ActionLedger,
  createLoopBudget,
  degradationFrom,
  ensureWebhook,
  HttpError,
  httpErrorFrom,
  parseRetryAfter,
  replaceWebhook,
  withRetry,
  withLoopBudget,
  type WebhookRegistrationApi,
  type WebhookSpec,
} from "../src/index.js";

const NOW = Date.UTC(2026, 10, 11, 12, 0, 0);

function headers(pairs: Record<string, string>): { get(name: string): string | null } {
  const lower = new Map(Object.entries(pairs).map(([k, v]) => [k.toLowerCase(), v]));
  return { get: (name) => lower.get(name.toLowerCase()) ?? null };
}

describe("http law", () => {
  it("parses Retry-After in both legal shapes and rejects absurd waits", () => {
    expect(parseRetryAfter("17", NOW)).toBe(17);
    expect(parseRetryAfter(" 3 ", NOW)).toBe(3);
    expect(parseRetryAfter(new Date(NOW + 90_000).toUTCString(), NOW)).toBe(90);
    expect(parseRetryAfter(null, NOW)).toBeUndefined();
    expect(parseRetryAfter("not-a-date", NOW)).toBeUndefined();
    expect(parseRetryAfter("99999", NOW)).toBeUndefined();
  });

  it("429 waits exactly what the provider asked, capped", async () => {
    const waits: number[] = [];
    let calls = 0;
    const result = await withRetry(
      async () => {
        calls++;
        if (calls === 1) throw new HttpError(429, "slow down", 4);
        return "ok";
      },
      {
        backoffMs: [1000, 2000],
        maxRetryAfterMs: 60_000,
        sleep: async (ms) => {
          waits.push(ms);
        },
      },
    );
    expect(result).toBe("ok");
    expect(waits).toEqual([4000]);
  });

  it("a 403 is a wall: one attempt, the typed error propagates with its class", async () => {
    let calls = 0;
    const error = await withRetry(
      async () => {
        calls++;
        throw new HttpError(403, "bad credentials");
      },
      { backoffMs: [1000], sleep: async () => undefined },
    ).catch((e: unknown) => e as HttpError);
    expect(calls).toBe(1);
    expect(error.forbidden).toBe(true);
    expect(error.throttled).toBe(false);
  });

  it("5xx walks the backoff and the last error survives; a stall records the degradation", async () => {
    const waits: number[] = [];
    let calls = 0;
    const error = await withRetry(
      async () => {
        calls++;
        if (calls === 2) throw new HttpError(429, undefined, 7200); // capped by maxRetryAfterMs
        throw new HttpError(502);
      },
      {
        backoffMs: [1000, 2000],
        maxRetryAfterMs: 60_000,
        sleep: async (ms) => {
          waits.push(ms);
        },
      },
    ).catch((e: unknown) => e as HttpError);
    expect(calls).toBe(3);
    expect(waits).toEqual([1000, 60_000]); // 5xx backoff, then the capped 7200s
    expect(error.status).toBe(502);
    expect(degradationFrom(error)).toEqual({ status: 502, reason: "http-error" });
    expect(degradationFrom({ status: 429, retryAfterSeconds: 30 })).toEqual({
      status: 429,
      reason: "throttled",
      retryAfterSeconds: 30,
    });
    expect(degradationFrom({ status: 403 }).reason).toBe("forbidden");
  });

  it("maps a response onto the taxonomy, honoring the header", async () => {
    const error = await httpErrorFrom(
      { status: 429, headers: headers({ "retry-after": "12" }) },
      "rate limited",
      NOW,
    );
    expect(error.throttled).toBe(true);
    expect(error.retryAfterSeconds).toBe(12);
    expect(error.message).toContain("rate limited");
  });
});

describe("loop budget", () => {
  it("stops at the cap, says so once, and a human resets the exchange", () => {
    const budget = createLoopBudget({ maxExchangesPerConversation: 2 });
    const conv = "chan-1";
    expect(
      withLoopBudget(budget, { conversationId: conv, actorProviderAccountId: "bot" }, "bot").drop,
    ).toBe(false);
    expect(
      withLoopBudget(budget, { conversationId: conv, actorProviderAccountId: "bot" }, "bot").drop,
    ).toBe(false);
    // third bot exchange: over the cap, and the drop itself carries the
    // one-time notice (the caller that stops must be the one that speaks)
    const stopped = withLoopBudget(
      budget,
      { conversationId: conv, actorProviderAccountId: "bot" },
      "bot",
    );
    expect(stopped.drop).toBe(true);
    expect(stopped.notice).toContain("Loop budget reached");
    expect(budget.notice(conv)).toBeUndefined(); // exactly once
    // a human reply restarts the clock
    expect(
      withLoopBudget(budget, { conversationId: conv, actorProviderAccountId: "alice" }, "bot").drop,
    ).toBe(false);
    expect(budget.exhausted(conv)).toBe(false);
  });

  it("conversations are separate budgets", () => {
    const budget = createLoopBudget({ maxExchangesPerConversation: 1 });
    budget.charge("chan-a");
    expect(budget.charge("chan-a")).toBe(false);
    expect(budget.charge("chan-b")).toBe(true);
  });
});

describe("webhook registration", () => {
  function fakeApi(initial: Array<{ id: string; url: string }> = []): WebhookRegistrationApi & {
    hooks: Array<{ id: string; url: string }>;
    created: WebhookSpec[];
  } {
    const hooks = [...initial];
    let next = 1;
    return {
      hooks,
      created: [],
      async list() {
        return hooks.map((h) => ({ ...h }));
      },
      async create(spec) {
        const id = `hook-${next++}`;
        hooks.push({ id, url: spec.url });
        this.created.push(spec);
        return id;
      },
      async remove(id) {
        hooks.splice(
          hooks.findIndex((h) => h.id === id),
          1,
        );
      },
    };
  }

  const spec: WebhookSpec = {
    url: "https://k.example/api/ingress/webhooks/github",
    events: ["pull_request"],
    secret: "s3cret",
  };

  it("ensure is idempotent and respects a human-made hook", async () => {
    const api = fakeApi();
    expect(await ensureWebhook(api, spec)).toEqual({ action: "registered", id: "hook-1" });
    expect(await ensureWebhook(api, spec)).toEqual({ action: "already-registered", id: "hook-1" });
    expect(api.created).toHaveLength(1);

    const byHand = fakeApi([{ id: "human-1", url: spec.url }]);
    expect(await ensureWebhook(byHand, spec)).toEqual({
      action: "already-registered",
      id: "human-1",
    });
    expect(byHand.created).toHaveLength(0);
  });

  it("replace removes every hook for the URL, then registers this one", async () => {
    const api = fakeApi([
      { id: "old-1", url: spec.url },
      { id: "old-2", url: spec.url },
      { id: "other", url: "https://someone.else/hook" },
    ]);
    const outcome = await replaceWebhook(api, spec);
    expect(outcome.removed).toEqual(["old-1", "old-2"]);
    expect(api.hooks).toEqual([
      { id: "other", url: "https://someone.else/hook" },
      { id: "hook-1", url: spec.url },
    ]);
  });
});

describe("ledger GC", () => {
  it("compacts settled rows past the window, keeps pending truth", async () => {
    const dir = mkdtempSync(join(tmpdir(), "provider-core-gc-"));
    const ledger = new ActionLedger(join(dir, "actions.sqlite3"));
    ledger.insert("cmd-old", "vcs.issue.comment", "sha256:old");
    ledger.settle("cmd-old", "comment 1");
    ledger.insert("cmd-pending", "vcs.issue.comment", "sha256:pending");
    // Age the settled row past any window through a second connection to
    // the same file (WAL allows it - the ledger itself keeps no test hook).
    const raw = new Database(join(dir, "actions.sqlite3"));
    raw
      .prepare(
        "UPDATE connector_actions SET updated_at = '2000-01-01T00:00:00.000Z' WHERE command_id = ?",
      )
      .run("cmd-old");
    raw.close();
    expect(ledger.compact(90, 100)).toBe(1);
    expect(ledger.find("cmd-old")).toBeUndefined();
    expect(ledger.find("cmd-pending")?.state).toBe("pending"); // untouched
    ledger.close();
  });
});
