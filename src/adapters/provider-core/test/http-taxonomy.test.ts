// The remaining http-core legs: the retry loop's BUILT-IN sleep (a real
// timer, no injected fake), the taxonomy's observation seam (register →
// count → unregister → silent), `httpErrorStatusLabel` as the one consumer
// label, and the ledger remainder (a foreign schema version fails closed;
// `all()` exposes the rows for reconcile introspection).
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it, vi } from "vitest";
import Database from "better-sqlite3";
import {
  ActionLedger,
  HttpError,
  classifyHttpStatus,
  httpErrorFrom,
  httpErrorStatusLabel,
  observeHttpStatus,
  payloadSha256,
  setHttpErrorObserver,
  withRetry,
} from "../src/index.js";

describe("the built-in sleep", () => {
  it("waits on a real timer when the caller injects none", async () => {
    vi.useFakeTimers();
    try {
      let calls = 0;
      const run = withRetry(
        async () => {
          calls += 1;
          if (calls === 1) throw new HttpError(502);
          return "ok";
        },
        { backoffMs: [25] },
      );
      await vi.advanceTimersByTimeAsync(25);
      await expect(run).resolves.toBe("ok");
      expect(calls).toBe(2);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("the taxonomy + observation seam", () => {
  it("classifyHttpStatus names 429/403/5xx and nothing else", () => {
    expect(classifyHttpStatus(429)).toBe("429");
    expect(classifyHttpStatus(403)).toBe("403");
    expect(classifyHttpStatus(500)).toBe("5xx");
    expect(classifyHttpStatus(503)).toBe("5xx");
    for (const status of [200, 400, 404, 422]) {
      expect(classifyHttpStatus(status)).toBeUndefined();
    }
  });

  it("httpErrorStatusLabel reads an HttpError through the same taxonomy", () => {
    expect(httpErrorStatusLabel(new HttpError(429))).toBe("429");
    expect(httpErrorStatusLabel(new HttpError(403, "wall"))).toBe("403");
    expect(httpErrorStatusLabel(new HttpError(500))).toBe("5xx");
    expect(httpErrorStatusLabel(new HttpError(404))).toBeUndefined();
  });

  it("the registered observer counts every classified status; unregistered is a no-op", async () => {
    const seen: string[] = [];
    setHttpErrorObserver((status) => {
      seen.push(status);
    });
    try {
      await httpErrorFrom({ status: 429, headers: { get: () => null } }, "slow");
      await httpErrorFrom({ status: 403, headers: { get: () => null } });
      await httpErrorFrom({ status: 404, headers: { get: () => null } }); // unclassified: no count
      observeHttpStatus(500);
      expect(seen).toEqual(["429", "403", "5xx"]);
    } finally {
      setHttpErrorObserver(undefined);
    }
    observeHttpStatus(429); // dark again: must not throw
  });

  it("a throwing observer never fails the operation", async () => {
    setHttpErrorObserver(() => {
      throw new Error("collector down");
    });
    try {
      await expect(
        httpErrorFrom({ status: 503, headers: { get: () => null } }),
      ).resolves.toBeInstanceOf(HttpError);
    } finally {
      setHttpErrorObserver(undefined);
    }
  });

  it("an HttpError from a 2xx-shape maps with detail and wait intact", async () => {
    const error = await httpErrorFrom(
      {
        status: 429,
        headers: {
          get: (name) => (name.toLowerCase() === "retry-after" ? "30" : null),
        },
      },
      "", // empty detail contributes nothing to the message
      Date.now(),
    );
    expect(error.message).toBe("HTTP 429 retry-after=30s");
    expect(error.forbidden).toBe(false);
  });
});

describe("ledger remainder", () => {
  it("a foreign schema version fails closed", () => {
    const path = join(mkdtempSync(join(tmpdir(), "ledger-ver-")), "a.sqlite3");
    const first = new ActionLedger(path);
    first.close();
    const raw = new Database(path);
    raw.pragma("user_version = 99");
    raw.close();
    expect(() => new ActionLedger(path)).toThrow(/unsupported schema version 99/);
  });

  it("all() exposes every row, oldest first (reconcile introspection)", () => {
    const ledger = new ActionLedger(join(mkdtempSync(join(tmpdir(), "ledger-all-")), "a.sqlite3"));
    ledger.insert("cmd-a", "vcs.issue.comment", payloadSha256({ a: 1 }));
    ledger.insert("cmd-b", "vcs.pr.create", payloadSha256({ b: 2 }));
    ledger.settle("cmd-b", "PR 1");
    const rows = ledger.all();
    expect(rows.map((row) => row.commandId)).toEqual(["cmd-a", "cmd-b"]);
    expect(rows[1]).toMatchObject({ state: "executed", providerObjectId: "PR 1" });
    ledger.close();
  });
});
