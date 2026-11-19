// ChildLink/ChildFleet unit tier: the wire is a fake (a recorded frame
// writer + a hand-fed parser), so the FIFO law, the timeout/disconnect
// rejection shapes, the orphan-reply drop, and the fleet shadow's edge
// branches are observable without a socket or a listener.
import { describe, expect, it, vi } from "vitest";
import type { CapabilityManifestV1 } from "@ofac/protocol";
import { createConsoleLogger } from "@ofac/workspace-link";
import {
  ChildFleet,
  ChildLink,
  ChildTransportError,
  digestOf,
  type FleetMember,
} from "../../src/child-link.js";
import { builtinChildManifest } from "../../src/child.js";
import type { ChildEntryConfig } from "../../src/config.js";
import type { ChildWireFrame, ReplyFrame } from "../../src/childwire.js";

const QUIET = createConsoleLogger({ level: "error" });

/** One macrotask: enough for a fire-and-forget publish rejection to land. */
function tick(): Promise<void> {
  const { promise, resolve } = Promise.withResolvers<void>();
  setImmediate(resolve);
  return promise;
}

/** Flush the FIFO chain: a queued send writes its frame one microtask later. */
async function flushed(): Promise<void> {
  await tick();
}

function manifestWith(capId: string): CapabilityManifestV1 {
  const base = builtinChildManifest("echo");
  return { ...base, capabilities: [{ ...base.capabilities[0], id: capId }] };
}

function entry(over: Partial<ChildEntryConfig> = {}): ChildEntryConfig {
  return { name: "unit", maxInFlight: 1, timeoutSeconds: 1, ...over };
}

interface Harness {
  link: ChildLink;
  frames: ChildWireFrame[];
  writeError: { error: Error | null };
  answer(reply: ReplyFrame["reply"]): void;
}

function linked(options: { timeoutSeconds?: number; generation?: number } = {}): Harness {
  const frames: ChildWireFrame[] = [];
  const writeError = { error: null as Error | null };
  const link = new ChildLink({
    childWorkspaceId: "gw.unit/alpha",
    manifest: builtinChildManifest("echo"),
    generation: options.generation ?? 1,
    entry: entry(
      options.timeoutSeconds === undefined ? {} : { timeoutSeconds: options.timeoutSeconds },
    ),
    write: (frame) => {
      if (writeError.error !== null) throw writeError.error;
      frames.push(frame);
    },
    logger: QUIET,
  });
  return {
    link,
    frames,
    writeError,
    answer: (reply) => {
      const command = frames.find((f) => f.type === "command");
      if (command === undefined || command.type !== "command") throw new Error("no command frame");
      link.onFrame({ v: 1, type: "reply", requestId: command.requestId, reply });
    },
  };
}

describe("ChildLink seam", () => {
  it("member identity comes from the wire (childWorkspaceId, entry, connected)", () => {
    const { link } = linked();
    expect(link.childWorkspaceId).toBe("gw.unit/alpha");
    expect(link.childId).toBe("gw.unit/alpha");
    expect(link.entry).toMatchObject({ name: "unit", timeoutSeconds: 1 });
    expect(link.connected).toBe(true);
    expect(link.generation).toBe(1);
  });

  it("register answers accepted with the child's own manifest", async () => {
    const { link } = linked();
    const msg = await link.register();
    expect(msg.kind).toBe("register");
    expect(msg.childWorkspaceId).toBe("gw.unit/alpha");
    await expect(
      link.send({
        kind: "register",
        childWorkspaceId: "gw.unit/alpha",
        manifest: builtinChildManifest("echo"),
      }),
    ).resolves.toEqual({ kind: "accepted", executionId: "" });
  });

  it("a disconnected link rejects send with ChildTransportError", async () => {
    const { link } = linked();
    link.notifyDisconnected();
    expect(link.connected).toBe(false);
    await expect(link.send({ kind: "status", executionId: "exec-1" })).rejects.toBeInstanceOf(
      ChildTransportError,
    );
    await expect(link.send({ kind: "status", executionId: "exec-1" })).rejects.toThrow(
      /child-disconnected/,
    );
  });

  it("an in-flight reply resolves the waiter and clears the pending slot", async () => {
    const { link, frames, answer } = linked();
    const send = link.send({ kind: "status", executionId: "exec-1" });
    await flushed(); // the FIFO chain writes the frame one microtask later
    answer({ kind: "status", executionId: "exec-1", state: "running" });
    await expect(send).resolves.toEqual({
      kind: "status",
      executionId: "exec-1",
      state: "running",
    });
    expect(frames.filter((f) => f.type === "command")).toHaveLength(1);
    // The slot is gone: a second reply for the same requestId is an orphan.
    const first = frames[0];
    if (first === undefined || first.type !== "command") throw new Error("missing command");
    link.onFrame({
      v: 1,
      type: "reply",
      requestId: first.requestId,
      reply: { kind: "accepted", executionId: "" },
    });
  });

  it("a queued send that meets a dead link rejects before writing", async () => {
    const { link, answer } = linked();
    const first = link.send({ kind: "status", executionId: "exec-1" });
    const second = link.send({ kind: "status", executionId: "exec-2" }); // behind the FIFO
    await flushed(); // first frame rides; the second waits behind it
    answer({ kind: "accepted", executionId: "exec-1" });
    link.notifyDisconnected(); // dies in the same tick the first reply lands
    await expect(first).resolves.toMatchObject({ kind: "accepted" });
    await expect(second).rejects.toThrow(/child-disconnected/);
  });

  it("no reply inside timeoutSeconds: child-timeout (fake timers)", async () => {
    vi.useFakeTimers();
    try {
      const { link } = linked({ timeoutSeconds: 1 });
      const send = link.send({ kind: "status", executionId: "exec-1" });
      const settled = vi.fn();
      void send.catch(settled);
      await vi.advanceTimersByTimeAsync(900);
      expect(settled).not.toHaveBeenCalled();
      await vi.advanceTimersByTimeAsync(200);
      await expect(send).rejects.toThrow(/child-timeout/);
    } finally {
      vi.useRealTimers();
    }
  });

  it("a write that throws surfaces as ChildTransportError(write-failed)", async () => {
    const { link, writeError } = linked();
    writeError.error = new Error("socket rotted");
    await expect(link.send({ kind: "status", executionId: "exec-1" })).rejects.toThrow(
      /child transport: socket rotted/,
    );
  });

  it("goaway is best-effort: a throwing writer changes nothing", () => {
    const { link, writeError } = linked();
    writeError.error = new Error("gone");
    expect(() => link.goaway("gateway-shutdown")).not.toThrow();
    expect(link.connected).toBe(false);
  });

  it("close() fails every waiter honestly and seals the link", async () => {
    const { link } = linked();
    const send = link.send({ kind: "status", executionId: "exec-1" });
    await link.close();
    await expect(send).rejects.toBeInstanceOf(ChildTransportError);
    expect(link.connected).toBe(false);
    await expect(link.send({ kind: "status", executionId: "exec-2" })).rejects.toThrow(
      /child-disconnected/,
    );
  });
});

describe("ChildLink inbound frames", () => {
  const completed = {
    specversion: "1.0" as const,
    id: "evt-1",
    source: "urn:workspace:gw.unit/alpha",
    type: "org.facilitator.event.assignment.completed.v1",
    subject: "work/w/assignment/a",
    time: new Date().toISOString(),
    datacontenttype: "application/json" as const,
    dataschema: "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json",
    data: { executionId: "exec-1" },
  };

  it("an orphan reply is logged and dropped", async () => {
    const warnings: string[] = [];
    const link = new ChildLink({
      childWorkspaceId: "gw.unit/alpha",
      manifest: builtinChildManifest("echo"),
      generation: 1,
      entry: entry(),
      write: () => undefined,
      logger: { ...QUIET, warn: (m) => warnings.push(m) },
    });
    link.onFrame({
      v: 1,
      type: "reply",
      requestId: "ch-unknown",
      reply: { kind: "accepted", executionId: "" },
    });
    expect(warnings).toContain("orphan-child-reply-dropped");
  });

  it("an event without a publisher queues for drain; with a publisher it forwards", async () => {
    const { link } = linked();
    link.onFrame({ v: 1, type: "event", requestId: "ev-1", envelope: completed });
    expect(link.drainEvents()).toEqual([completed]);

    const forwarded: unknown[] = [];
    link.setPublisher(async (env) => {
      forwarded.push(env);
    });
    link.onFrame({ v: 1, type: "event", requestId: "ev-2", envelope: completed });
    await tick();
    expect(forwarded).toEqual([completed]);
    expect(link.drainEvents()).toHaveLength(0);
  });

  it("a rejecting publisher logs child-event-forward-failed (no throw onward)", async () => {
    const warnings: string[] = [];
    const link = new ChildLink({
      childWorkspaceId: "gw.unit/alpha",
      manifest: builtinChildManifest("echo"),
      generation: 1,
      entry: entry(),
      write: () => undefined,
      logger: { ...QUIET, warn: (m) => warnings.push(m) },
    });
    link.setPublisher(async () => {
      throw new Error("kernel refused");
    });
    link.onFrame({ v: 1, type: "event", requestId: "ev-1", envelope: completed });
    await tick();
    expect(warnings).toContain("child-event-forward-failed");
  });
});

describe("ChildFleet edges", () => {
  it("seed: an unparseable manifest keeps the offline route, logs honestly", () => {
    const warnings: string[] = [];
    const fleet = new ChildFleet({ ...QUIET, warn: (m) => warnings.push(m) });
    fleet.seed([
      {
        childId: "gw.unit/broken",
        manifestDigest: "sha256:0",
        manifestJson: "{not json",
        childGeneration: 4,
        state: "offline",
        connectedAt: null,
        offlineAt: "2026-01-01T00:00:00Z",
      },
    ]);
    expect(warnings).toContain("child-state-manifest-unparseable");
    expect(fleet.generationOf("gw.unit/broken")).toBe(4);
    expect(fleet.route("research.analyze")).toBeUndefined(); // no manifest, no advert
    const rows = fleet.statusRows(new Map([["gw.unit/broken", 2]]));
    expect(rows).toEqual([
      {
        childWorkspaceId: "gw.unit/broken",
        state: "offline",
        childGeneration: 4,
        manifestDigest: "sha256:0",
        inFlight: 2,
      },
    ]);
  });

  it("registering an ONLINE child replaces it in place (same id, new member)", () => {
    const fleet = new ChildFleet(QUIET);
    const first: FleetMember = {
      childId: "gw.unit/alpha",
      manifest: manifestWith("cap.one"),
      executor: {
        childWorkspaceId: "gw.unit/alpha",
        register: async () => ({
          kind: "register",
          childWorkspaceId: "",
          manifest: manifestWith("cap.one"),
        }),
        send: async () => ({ kind: "accepted", executionId: "" }),
        drainEvents: () => [],
        close: async () => undefined,
      },
      generation: 1,
      connected: true,
      goaway: () => undefined,
    };
    const second: FleetMember = { ...first, generation: 2 };
    fleet.register(first);
    fleet.register(second);
    expect(fleet.member("gw.unit/alpha")).toBe(second);
    expect(fleet.generationOf("gw.unit/alpha")).toBe(2);
    expect(fleet.route("cap.one")).toEqual({ childId: "gw.unit/alpha", online: true });
    expect(fleet.unionManifest("default").capabilities.map((c) => c.id)).toEqual(["cap.one"]);
  });

  it("descriptorOf reads the offline shadow after a disconnect", () => {
    const fleet = new ChildFleet(QUIET);
    const member: FleetMember = {
      childId: "gw.unit/alpha",
      manifest: manifestWith("cap.one"),
      executor: {
        childWorkspaceId: "gw.unit/alpha",
        register: async () => ({
          kind: "register",
          childWorkspaceId: "",
          manifest: manifestWith("cap.one"),
        }),
        send: async () => ({ kind: "accepted", executionId: "" }),
        drainEvents: () => [],
        close: async () => undefined,
      },
      generation: 1,
      connected: true,
      goaway: () => undefined,
    };
    fleet.register(member);
    expect(fleet.descriptorOf("cap.one")?.id).toBe("cap.one");
    fleet.markOffline("gw.unit/alpha");
    // Honest route + descriptor survive the disconnect (offline shadow).
    expect(fleet.route("cap.one")).toEqual({ childId: "gw.unit/alpha", online: false });
    expect(fleet.descriptorOf("cap.one")?.id).toBe("cap.one");
    // An unknown capability has no shadow either.
    expect(fleet.descriptorOf("nope.cap")).toBeUndefined();
  });

  it("digestOf matches the manifest-bytes digest law", () => {
    expect(digestOf(builtinChildManifest("echo"))).toMatch(/^sha256:[0-9a-f]{64}$/);
  });
});
