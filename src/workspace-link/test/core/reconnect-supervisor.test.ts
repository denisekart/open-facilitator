// The reconnect supervisor as a pure state machine over injected hooks: the
// backoff ladder, the attempt counter (and its reset on a successful connect),
// the terminal answers (ProtocolMismatch and a bounded attempt budget), and
// the session-death path that counts a reconnect without an error. `sleep`
// rides timers.ts/`setTimeout`, so the ladder is advanced with fake timers —
// nothing here waits on the wall clock.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ReconnectSupervisor, type SupervisorHooks } from "../../src/core/reconnect-supervisor.js";
import { ProtocolMismatchError, type LinkSession } from "../../src/index.js";

const SESSION: LinkSession = {
  sessionId: "sess-1",
  leaseEpoch: 1,
  maxInFlight: 16,
  heartbeatSeconds: 30,
  protocolVersion: "v1",
};

interface Recorder extends SupervisorHooks {
  log: string[];
  connectResults: ((attempt: number) => Promise<LinkSession>)[];
  deathTriggers: (() => void)[];
  stopFlag: boolean;
}

function recorder(): Recorder {
  const r: Recorder = {
    log: [],
    connectResults: [],
    deathTriggers: [],
    stopFlag: false,
    connectAndHello: async () => {
      const next = r.connectResults.shift();
      if (next === undefined) throw new Error("connectResults exhausted");
      return next(r.log.filter((l) => l === "reconnect").length);
    },
    waitForSessionDeath: () =>
      new Promise<void>((resolve) => {
        r.deathTriggers.push(resolve);
      }),
    drainOutbox: async () => {
      r.log.push("drain");
    },
    onSessionLost: () => {
      r.log.push("lost");
    },
    setStateReconnecting: () => {
      r.log.push("reconnecting");
    },
    resumeQueue: () => {
      r.log.push("resume");
    },
    pauseQueue: () => {
      r.log.push("pause");
    },
    recordReconnect: () => {
      r.log.push("reconnect");
    },
    isStopped: () => r.stopFlag,
    hasSession: () => false,
    terminate: (error) => {
      r.log.push(`terminate:${error.message}`);
    },
    logDown: (info) => {
      r.log.push(`down:${info.attempt}:${info.backoffMs}:${info.reason}`);
    },
  };
  return r;
}

const ok: () => Promise<LinkSession> = async () => SESSION;
function failWith(error: unknown): () => Promise<LinkSession> {
  return async () => {
    throw error;
  };
}

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

/** Flush the microtask queue so the supervisor reaches its next await point. */
async function settle(): Promise<void> {
  await vi.advanceTimersByTimeAsync(0);
}

describe("ReconnectSupervisor", () => {
  it("stops before connecting when the loop is already stopped", async () => {
    const h = recorder();
    h.stopFlag = true;
    await new ReconnectSupervisor(h, { backoffMs: [1] }).run();
    expect(h.log).toEqual([]);
  });

  it("connects, resumes, drains, parks; a session death re-hellos after the first rung", async () => {
    const h = recorder();
    h.connectResults.push(ok, ok);
    const running = new ReconnectSupervisor(h, { backoffMs: [1_000, 2_000] }).run();

    await settle();
    expect(h.log).toEqual(["resume", "drain"]);
    expect(h.deathTriggers).toHaveLength(1);

    h.deathTriggers[0]!(); // the socket died
    await settle();
    expect(h.log).toEqual(["resume", "drain", "reconnect", "pause", "lost"]);
    await vi.advanceTimersByTimeAsync(999); // still backing off
    expect(h.deathTriggers).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(h.log).toEqual(["resume", "drain", "reconnect", "pause", "lost", "resume", "drain"]);
    expect(h.deathTriggers).toHaveLength(2); // parked again

    h.stopFlag = true;
    h.deathTriggers[1]!();
    await running;
    expect(h.log).toHaveLength(7); // the stop short-circuits before any reconnect
  });

  it("walks the ladder and clamps at the last rung", async () => {
    const h = recorder();
    h.connectResults.push(failWith(new Error("e1")), failWith(new Error("e2")));
    const running = new ReconnectSupervisor(h, { backoffMs: [1_000, 2_000] }).run();
    await settle();
    await vi.advanceTimersByTimeAsync(1_000); // after rung 1 → attempt 2
    await vi.advanceTimersByTimeAsync(2_000); // after rung 2 → attempt 3 (clamped)
    // The scripted connects ran out, so the third failure is the recorder's
    // own error — the point is the ladder: 1000, 2000, then CLAMPED at 2000.
    expect(h.log).toEqual([
      "reconnect",
      "pause",
      "reconnecting",
      "down:1:1000:e1",
      "reconnect",
      "pause",
      "reconnecting",
      "down:2:2000:e2",
      "reconnect",
      "pause",
      "reconnecting",
      "down:3:2000:connectResults exhausted",
    ]);

    h.stopFlag = true;
    await vi.advanceTimersByTimeAsync(2_000);
    await running;
    expect(h.log.filter((l) => l === "reconnect")).toHaveLength(3);
  });

  it("a successful connect resets the ladder", async () => {
    const h = recorder();
    h.connectResults.push(
      failWith(new Error("e1")),
      failWith(new Error("e2")),
      ok,
      failWith(new Error("e3")),
    );
    const running = new ReconnectSupervisor(h, { backoffMs: [1_000, 2_000, 4_000] }).run();
    await settle();
    await vi.advanceTimersByTimeAsync(1_000); // attempt 2
    await vi.advanceTimersByTimeAsync(2_000); // connect succeeds → attempt resets → park
    expect(h.log).toContain("drain");
    h.deathTriggers.at(-1)?.(); // the session dies (counts attempt 1)
    await vi.advanceTimersByTimeAsync(1_000);
    // The next failure is attempt 2 — not 4, which is what an unreset ladder
    // would report after two earlier failures plus a session death.
    expect(h.log).toContain("down:2:2000:e3");
    expect(h.log).not.toContain("down:4:4000:e3");
    h.stopFlag = true;
    await vi.advanceTimersByTimeAsync(2_000);
    await running;
  });

  it("ProtocolMismatchError is terminal (a policy answer, never retried)", async () => {
    const h = recorder();
    h.connectResults.push(failWith(new ProtocolMismatchError()));
    await new ReconnectSupervisor(h, { backoffMs: [1] }).run();
    expect(h.log).toEqual(["terminate:server answered hello with no-compatible-protocol-version"]);
    expect(h.log).not.toContain("reconnect");
  });

  it("a bounded budget ends with the last error surfaced", async () => {
    const h = recorder();
    h.connectResults.push(failWith(new Error("down-1")), failWith(new Error("down-2")));
    const running = new ReconnectSupervisor(h, { maxReconnectAttempts: 1, backoffMs: [1] }).run();
    await settle();
    await vi.advanceTimersByTimeAsync(1);
    await running;
    expect(h.log).toContain("down:1:1:down-1"); // attempt 1 backs off…
    expect(h.log).toContain("terminate:down-2"); // …attempt 2 exceeds the budget
    expect(h.log).not.toContain("drain");
  });

  it("a bounded budget with a non-Error failure surfaces a typed exhaustion error", async () => {
    const h = recorder();
    h.connectResults.push(failWith("string failure"), failWith("string failure"));
    const running = new ReconnectSupervisor(h, { maxReconnectAttempts: 1, backoffMs: [1] }).run();
    await settle();
    await vi.advanceTimersByTimeAsync(1);
    await running;
    expect(h.log).toContain("down:1:1:string failure");
    expect(h.log).toContain("terminate:reconnect attempts exhausted");
  });

  it("a stop observed while the connect fails ends the loop without backing off", async () => {
    const h = recorder();
    let attempts = 0;
    h.connectResults.push(async () => {
      attempts += 1;
      h.stopFlag = true; // stop() landed underneath the connect
      throw new Error("closed underneath us");
    });
    await new ReconnectSupervisor(h, { backoffMs: [1] }).run();
    expect(attempts).toBe(1);
    expect(h.log).toEqual([]); // nothing recorded after a stop
  });

  it("a stop observed the moment the session dies ends the loop", async () => {
    const h = recorder();
    h.connectResults.push(ok);
    const running = new ReconnectSupervisor(h, { backoffMs: [1] }).run();
    await settle();
    h.stopFlag = true;
    h.deathTriggers[0]!();
    await running;
    expect(h.log).toEqual(["resume", "drain"]); // no reconnect counted
  });
});
