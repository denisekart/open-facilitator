// Unit tier for @ofac/adapter: an in-process startChildListener IS the
// Gateway peer (no Facilitator). Tests drive the adapter through a real
// ChildLink (the same object the Gateway runtime uses) and, for the wire
// law (frame shapes, denial literals), through raw socket frames.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { connect } from "node:net";
import {
  ChildFleet,
  createFrameParser,
  encodeFrame,
  startChildListener,
  type ChildLink,
  type ChildListener,
  type ChildWireFrame,
} from "@ofac/gateway";
import { createConsoleLogger } from "@ofac/workspace-link";
import { validateAgainstSchemaId } from "@ofac/protocol";
import type { CapabilityManifestV1, EnvelopeCommand, EnvelopeEvent } from "@ofac/protocol";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  ADAPTER_PACKAGE,
  DEFAULT_RESTART_BACKOFF_MS,
  ExecutionRegistry,
  TERMINAL_TTL_MS,
  createAdapter,
  type AdapterHandle,
  type CreateAdapterOptions,
  type RunContext,
} from "../src/index.js";

const MANIFEST: CapabilityManifestV1 = {
  apiVersion: "facilitator.framework/v1",
  kind: "CapabilityManifest",
  ownerTeam: "team-test",
  capabilities: [
    {
      id: "research.analyze",
      version: 1,
      execution: "asynchronous",
      acceptedContexts: ["uri-reference"],
      outputKinds: ["analysis.report"],
      outputMediaTypes: ["text/markdown"],
      supportsSteering: true,
      supportsCancellation: true,
      supportsNudge: false,
    },
  ],
};

const DESCRIPTOR = {
  invocation: "sdk",
  supports: { steer: true, interrupt: true, resume: "restart-with-context" },
} as const;

const INPUT_REQUIRED = "org.facilitator.event.assignment.input.required.v1";
const PROGRESS = "org.facilitator.event.work.progress.v1";
const FAILED = "org.facilitator.event.assignment.failed.v1";

function assign(commandId: string, workId = "w-1", assignmentId = "a-1"): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: `env-${commandId}`,
    source: "urn:facilitator:instance:test",
    type: "org.facilitator.command.work.assign.v1",
    subject: `work/${workId}/assignment/${assignmentId}`,
    time: "2036-10-23T18:00:00Z",
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    traceparent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
    data: {
      commandId,
      actorEvidenceRef: "identity-evidence:test",
      workId,
      targetId: "t-1",
      capabilityId: "research.analyze",
      expectedContextVersion: 1,
      inputs: { objective: "unit", sourceRefs: [] },
      expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
      deadline: "2036-10-23T18:00:00Z",
      assignmentId,
    },
  } as EnvelopeCommand;
}

function socketPath(): string {
  return join(mkdtempSync(join(tmpdir(), "ofac-ad-")), "c.sock");
}

function listen(
  path: string,
  childSecret?: string,
): Promise<{
  listener: ChildListener;
  link: Promise<ChildLink>;
}> {
  const linkReady = Promise.withResolvers<ChildLink>();
  return startChildListener(
    { socket: { path }, handshakeTimeoutSeconds: 5, leaseMissedBeats: 1000 },
    {
      fleet: new ChildFleet(createConsoleLogger({ level: "error" })),
      logger: createConsoleLogger({ level: "error" }),
      gatewayId: "gw.test",
      heartbeatSeconds: 30,
      ...(childSecret !== undefined ? { childSecret } : {}),
      onRegistered: (link) => linkReady.resolve(link),
    },
  ).then((listener) => ({ listener, link: linkReady.promise }));
}

function adapter(
  path: string,
  hooks: CreateAdapterOptions["hooks"],
  extra: Partial<CreateAdapterOptions> = {},
): Promise<AdapterHandle> {
  return createAdapter({
    childWorkspaceId: "gw.test/unit",
    manifest: MANIFEST,
    transport: { socketPath: path },
    kernel: { baseUrl: "http://kernel.test" },
    descriptor: DESCRIPTOR,
    hooks,
    restartBackoffMs: [],
    logger: createConsoleLogger({ level: "error" }),
    ...extra,
  });
}

let cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const c of cleanups) await c().catch(() => undefined);
  cleanups = [];
});

/** Listener + adapter + wired publisher, both handles live for the test. */
async function attached(
  path: string,
  hooks: CreateAdapterOptions["hooks"],
  extra: Partial<CreateAdapterOptions> = {},
): Promise<{ handle: AdapterHandle; listener: ChildListener; link: ChildLink; events: never[] }> {
  const { listener, link } = await listen(path);
  const handle = await adapter(path, hooks, extra);
  cleanups.push(async () => {
    await handle.close();
    await listener.close();
  });
  await handle.ready();
  const childLink = await link;
  const events: never[] = [];
  childLink.setPublisher(async (env) => {
    events.push(env as never);
  });
  return { handle, listener, link: childLink, events };
}

describe("adapter package surface", () => {
  it("exports the package name and the locked default backoff", () => {
    expect(ADAPTER_PACKAGE).toBe("@ofac/adapter");
    expect(DEFAULT_RESTART_BACKOFF_MS).toEqual([100, 250, 500, 1000, 2000, 5000]);
  });

  it("rejects a childWorkspaceId outside the <gatewayId>/<name> law", async () => {
    await expect(
      adapter("/tmp/unused.sock", { onExecute: async () => ({ kind: "completed" }) }),
    ).resolves.toBeTruthy();
    await expect(
      createAdapter({
        childWorkspaceId: "gw.test/Nope_Bad!",
        manifest: MANIFEST,
        transport: { socketPath: "/tmp/unused.sock" },
        kernel: { baseUrl: "http://kernel.test" },
        descriptor: DESCRIPTOR,
        hooks: { onExecute: async () => ({ kind: "completed" }) },
        restartBackoffMs: [],
      }),
    ).rejects.toThrow(/invalid childWorkspaceId/);
  });
});

describe("execution registry", () => {
  it("terminal entries answer status for TERMINAL_TTL_MS, then vanish", () => {
    const registry = new ExecutionRegistry();
    registry.track("e-1", "c-1");
    registry.settle("e-1", "completed");
    const entry = registry.find("e-1");
    expect(entry?.state).toBe("completed");
    (entry as { settledAt: number } | undefined)!.settledAt = Date.now() - TERMINAL_TTL_MS - 1;
    expect(registry.find("e-1")).toBeUndefined();
    expect(registry.findByCommandId("c-1")).toBeUndefined();
  });
});

describe("adapter lifecycle over the real listener", () => {
  it("registers: hello lands, the Gateway sees the manifest", async () => {
    const rig = await attached(socketPath(), { onExecute: async () => ({ kind: "completed" }) });
    expect(rig.link.connected).toBe(true);
    expect(rig.link.manifest.capabilities[0]?.id).toBe("research.analyze");
    expect(rig.handle.status().connected).toBe(true);
  });

  it("start: accepted immediately; a duplicate commandId replays accepted without re-executing", async () => {
    const path = socketPath();
    let executions = 0;
    const holdLatch = Promise.withResolvers<void>();
    const hold = holdLatch.promise;
    const rig = await attached(path, {
      onExecute: async (ctx: RunContext) => {
        executions += 1;
        if (executions === 1) await hold;
        ctx.publishEvent({
          specversion: "1.0",
          id: `done-${executions}`,
          source: "urn:workspace:gw.test/unit",
          type: "org.facilitator.event.assignment.completed.v1",
          time: new Date().toISOString(),
          datacontenttype: "application/json",
          data: {},
        } as never);
        return { kind: "completed" };
      },
    });
    const first = await rig.link.send({
      kind: "start",
      executionId: "e-dup",
      executionGeneration: 9,
      command: assign("cmd-dup"),
    });
    expect(first).toEqual({ kind: "accepted", executionId: "e-dup" });
    const dupe = await rig.link.send({
      kind: "start",
      executionId: "e-dup2",
      executionGeneration: 9,
      command: assign("cmd-dup"),
    });
    expect(dupe).toEqual({ kind: "accepted", executionId: "e-dup2" });
    expect(executions).toBe(1); // the duplicate never spawned a run
    holdLatch.resolve();
  });

  it("progress is relayed as a full envelope: stamped generation + copied traceparent", async () => {
    const path = socketPath();
    const latch = Promise.withResolvers<void>();
    const hold = latch.promise;
    const rig = await attached(path, {
      onExecute: async (ctx) => {
        ctx.publishProgress("analyzing");
        await hold;
        return { kind: "completed" };
      },
    });
    await rig.link.send({
      kind: "start",
      executionId: "e-prog",
      executionGeneration: 4,
      command: assign("cmd-prog"),
    });
    await vi.waitFor(() => expect(rig.events.length).toBe(1));
    const env = rig.events[0] as unknown as {
      type: string;
      traceparent?: string;
      data: Record<string, unknown>;
    };
    expect(env.type).toBe("org.facilitator.event.work.progress.v1");
    expect(env.data["executionGeneration"]).toBe(4); // = start.connectionGeneration
    expect(env.data["progressNote"]).toBe("analyzing");
    expect(env.data["workId"]).toBe("w-1");
    expect(env.traceparent).toBe("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
    latch.resolve();
  });

  it("steer: ctx.setSteered ⇒ invoked + accepted; foreign execution ⇒ unknown", async () => {
    const path = socketPath();
    const seen: string[] = [];
    const latch = Promise.withResolvers<void>();
    const hold = latch.promise;
    const rig = await attached(path, {
      onExecute: async (ctx) => {
        ctx.setSteered((directive) => seen.push(directive));
        await hold;
        return { kind: "completed" };
      },
    });
    await rig.link.send({
      kind: "start",
      executionId: "e-steer",
      executionGeneration: 1,
      command: assign("c1"),
    });
    const steer = {
      kind: "steer" as const,
      executionId: "e-steer",
      command: {
        ...assign("cmd-steer"),
        data: {
          ...(assign("cmd-steer").data as Record<string, unknown>),
          directive: "focus on costs",
        },
      } as EnvelopeCommand,
    };
    const reply = await rig.link.send(steer);
    expect(reply).toEqual({ kind: "accepted", executionId: "e-steer" });
    expect(seen).toEqual(["focus on costs"]); // the directive reached the callback
    const foreign = await rig.link.send({ ...steer, executionId: "nope" });
    expect(foreign).toEqual({ kind: "unknown", executionId: "nope" });
    latch.resolve();
  });

  it("steer with NO steering hook at all ⇒ {kind:'unsupported'}", async () => {
    const path = socketPath();
    const latch = Promise.withResolvers<void>();
    const hold = latch.promise;
    const rig = await attached(path, {
      onExecute: async () => {
        await hold;
        return { kind: "completed" };
      },
    });
    await rig.link.send({
      kind: "start",
      executionId: "e-uns",
      executionGeneration: 1,
      command: assign("c1"),
    });
    const reply = await rig.link.send({
      kind: "steer",
      executionId: "e-uns",
      command: assign("cmd-uns"),
    });
    expect(reply).toEqual({ kind: "unsupported" });
    latch.resolve();
  });

  it("stop: accepted + abort + onCancel; entry dropped ⇒ unknown, no terminal event", async () => {
    const path = socketPath();
    let cancelReason = "";
    let aborted = false;
    const rig = await attached(path, {
      onExecute: async (ctx) => {
        await new Promise((resolve) => ctx.signal.addEventListener("abort", resolve));
        return { kind: "completed" };
      },
      onCancel: (reason) => {
        cancelReason = reason;
        aborted = true;
      },
    });
    await rig.link.send({
      kind: "start",
      executionId: "e-stop",
      executionGeneration: 1,
      command: assign("c1"),
    });
    const stop = await rig.link.send({
      kind: "stop",
      executionId: "e-stop",
      reason: "user-cancel",
    });
    expect(stop).toEqual({ kind: "accepted", executionId: "e-stop" });
    expect(cancelReason).toBe("user-cancel");
    expect(aborted).toBe(true);
    // The run unwinds WITHOUT publishing; the entry is dropped:
    await vi.waitFor(async () => {
      const s = await rig.link.send({ kind: "status", executionId: "e-stop" });
      expect(s).toEqual({ kind: "unknown", executionId: "e-stop" });
    });
    expect(rig.events.length).toBe(0);
  });

  it("failed law: a throwing onExecute publishes exactly one retryable assignment.failed.v1", async () => {
    const path = socketPath();
    const rig = await attached(path, {
      onExecute: async () => {
        throw new Error("harness exploded");
      },
    });
    await rig.link.send({
      kind: "start",
      executionId: "e-fail",
      executionGeneration: 2,
      command: assign("c1"),
    });
    await vi.waitFor(() => expect(rig.events.length).toBe(1));
    const env = rig.events[0] as unknown as { type: string; data: Record<string, unknown> };
    expect(env.type).toBe("org.facilitator.event.assignment.failed.v1");
    expect(env.data["reason"]).toBe("harness exploded");
    expect(env.data["retryable"]).toBe(true);
    expect(env.data["executionId"]).toBe("e-fail");
    expect(env.data["executionGeneration"]).toBe(2);
    const settled = await rig.link.send({ kind: "status", executionId: "e-fail" });
    expect(settled).toEqual({ kind: "status", executionId: "e-fail", state: "failed" });
  });

  it("requestInput: publishes a schema-valid input.required; respond resolves + notes; stale id unknown", async () => {
    const path = socketPath();
    const asked = Promise.withResolvers<{ readonly text: string }>();
    const rig = await attached(path, {
      onExecute: async (ctx) => {
        const answer = await ctx.requestInput({
          question: "which framework?",
          choices: ["postgres", "sqlite"],
        });
        asked.resolve(answer);
        return { kind: "completed" };
      },
    });
    await rig.link.send({
      kind: "start",
      executionId: "e-ask",
      executionGeneration: 6,
      command: assign("cmd-ask"),
    });
    const askedEvent = await vi.waitFor(() => {
      const found = rig.events.find(
        (e) => (e as Partial<EnvelopeEvent> | undefined)?.type === INPUT_REQUIRED,
      ) as { data: Record<string, unknown> } | undefined;
      expect(found).toBeDefined();
      return found as { data: Record<string, unknown> };
    });
    expect(
      validateAgainstSchemaId(
        "https://schemas.openfacilitator.org/v1/event.assignment.input.required.v1.json",
        askedEvent.data,
      ).ok,
    ).toBe(true);
    expect(askedEvent.data["workId"]).toBe("w-1");
    expect(askedEvent.data["assignmentId"]).toBe("a-1");
    expect(askedEvent.data["executionId"]).toBe("e-ask");
    expect(askedEvent.data["executionGeneration"]).toBe(6);
    expect(askedEvent.data["question"]).toBe("which framework?");
    expect(askedEvent.data["choices"]).toEqual(["postgres", "sqlite"]);
    expect(String(askedEvent.data["requestId"])).toMatch(/^inp-e-ask-\d+$/);
    const requestId = String(askedEvent.data["requestId"]);

    // Pending: status stays honest; a stale requestId is `unknown`, not swallowed.
    expect(await rig.link.send({ kind: "status", executionId: "e-ask" })).toEqual({
      kind: "status",
      executionId: "e-ask",
      state: "running",
    });
    expect(
      await rig.link.send({
        kind: "respond",
        executionId: "e-ask",
        requestId: "inp-nope-9",
        text: "x",
      }),
    ).toEqual({ kind: "unknown", executionId: "e-ask" });

    // The answer: accepted, progress note, hook resolves with the text.
    expect(
      await rig.link.send({ kind: "respond", executionId: "e-ask", requestId, text: "postgres" }),
    ).toEqual({ kind: "accepted", executionId: "e-ask" });
    expect((await asked.promise).text).toBe("postgres");
    await vi.waitFor(() =>
      expect(
        rig.events.some(
          (e) =>
            (e as Partial<EnvelopeEvent> | undefined)?.type === PROGRESS &&
            ((e as EnvelopeEvent).data as Record<string, unknown>)["progressNote"] ===
              "input answered: postgres",
        ),
      ).toBe(true),
    );
    // Second respond for the settled question: unknown (the pending entry is gone).
    expect(
      await rig.link.send({ kind: "respond", executionId: "e-ask", requestId, text: "again" }),
    ).toEqual({ kind: "unknown", executionId: "e-ask" });
  });

  it("requestInput + stop: rejects AbortError, no terminal event (cancel law)", async () => {
    const path = socketPath();
    let rejection: unknown;
    const rig = await attached(path, {
      onExecute: async (ctx) => {
        try {
          await ctx.requestInput({ question: "unanswered?" });
          return { kind: "completed" };
        } catch (error) {
          rejection = error;
          return { kind: "completed" };
        }
      },
    });
    await rig.link.send({
      kind: "start",
      executionId: "e-askstop",
      executionGeneration: 1,
      command: assign("cmd-askstop"),
    });
    await vi.waitFor(() => expect(rig.events.length).toBe(1));
    const stop = await rig.link.send({
      kind: "stop",
      executionId: "e-askstop",
      reason: "user-cancel",
    });
    expect(stop).toEqual({ kind: "accepted", executionId: "e-askstop" });
    await vi.waitFor(() => expect(rejection).toBeDefined());
    const abort = rejection as Error;
    expect(abort).toBeInstanceOf(Error);
    expect(abort.name).toBe("AbortError");
    // Cancelled run: dropped without a terminal event; a late respond is unknown.
    await vi.waitFor(async () => {
      const s = await rig.link.send({ kind: "status", executionId: "e-askstop" });
      expect(s).toEqual({ kind: "unknown", executionId: "e-askstop" });
    });
    expect(
      rig.events.filter((e) => (e as Partial<EnvelopeEvent> | undefined)?.type === FAILED).length,
    ).toBe(0);
    expect(
      await rig.link.send({
        kind: "respond",
        executionId: "e-askstop",
        requestId: "inp-e-askstop-1",
        text: "late",
      }),
    ).toEqual({ kind: "unknown", executionId: "e-askstop" });
  });

  it("requestInput + adapter close: rejects 'adapter closed'", async () => {
    const path = socketPath();
    let rejection: unknown;
    const { listener, link } = await listen(path);
    const handle = await adapter(path, {
      onExecute: async (ctx) => {
        try {
          await ctx.requestInput({ question: "never answered" });
        } catch (error) {
          rejection = error;
        }
        return { kind: "completed" };
      },
    });
    await handle.ready();
    const childLink = await link;
    await childLink.send({
      kind: "start",
      executionId: "e-askclose",
      executionGeneration: 1,
      command: assign("cmd-askclose"),
    });
    await handle.close(); // a pending input must die with the adapter
    await listener.close();
    const closed = rejection as Error;
    expect(closed).toBeInstanceOf(Error);
    expect(closed.message).toBe("adapter closed");
  });

  it("a throwing hook ⇒ the locked rejected literal", async () => {
    const path = socketPath();
    const latch = Promise.withResolvers<void>();
    const hold = latch.promise;
    const rig = await attached(path, {
      onExecute: async () => {
        await hold;
        return { kind: "completed" };
      },
      onSteer: () => {
        throw new Error("steer hook exploded");
      },
    });
    await rig.link.send({
      kind: "start",
      executionId: "e-throw",
      executionGeneration: 1,
      command: assign("c1"),
    });
    const reply = await rig.link.send({
      kind: "steer",
      executionId: "e-throw",
      command: assign("c2"),
    });
    expect(reply).toEqual({ kind: "rejected", reason: "adapter handler failed" });
    latch.resolve();
  });

  it("restart law: listener swap + backoff [10,20] ⇒ reconnect, probe answers running", async () => {
    const path = socketPath();
    const latch = Promise.withResolvers<void>();
    const hold = latch.promise;
    const rig = await attached(
      path,
      {
        onExecute: async (ctx) => {
          await Promise.race([hold, new Promise((r) => ctx.signal.addEventListener("abort", r))]);
          return { kind: "completed" };
        },
      },
      { restartBackoffMs: [10, 20] },
    );
    await rig.link.send({
      kind: "start",
      executionId: "e-r",
      executionGeneration: 1,
      command: assign("cr"),
    });
    expect(rig.handle.status().activeExecutions).toEqual(["e-r"]);
    const genBefore = rig.handle.status().childGeneration;

    await rig.listener.close(); // Gateway dies mid-run
    const again = await listen(path); // fresh listener, SAME socket path
    cleanups.push(async () => again.listener.close());
    const link2 = await again.link; // adapter re-registers on its own schedule
    const probe = await link2.send({ kind: "status", executionId: "e-r" });
    expect(probe).toEqual({ kind: "status", executionId: "e-r", state: "running" }); // registry survived
    expect(rig.handle.status().childGeneration).toBeGreaterThan(genBefore);
    latch.resolve();
  });
});

describe("wire law (Gateway-side scripts)", () => {
  it("bad token ⇒ ready rejects with 'register denied: denied: bad-token' and never retries (backoff [])", async () => {
    const path = socketPath();
    const { listener } = await listen(path, "right-token");
    const handle = await adapter(
      path,
      { onExecute: async () => ({ kind: "completed" }) },
      {
        token: "wrong-token",
      },
    );
    await expect(handle.ready()).rejects.toThrow("register denied: denied: bad-token");
    await handle.close();
    await listener.close();
  });

  it("goaway reaches the adapter before the socket dies", async () => {
    const path = socketPath();
    const { listener, link } = await listen(path);
    const handle = await adapter(path, { onExecute: async () => ({ kind: "completed" }) });
    await handle.ready();
    await link;
    const notices: unknown[] = [];
    handle.events.on("goaway", (info) => notices.push(info));
    await listener.close(); // Gateway shutdown: goaway, then close
    await vi.waitFor(() => expect(notices.length).toBe(1));
    await handle.close();
  });

  it("raw hello handshake: exactly one accepted ack; unknown command kind ⇒ unsupported reply", async () => {
    const path = socketPath();
    const { listener } = await listen(path);
    const socket = connect(path);
    const frames: ChildWireFrame[] = [];
    const parser = createFrameParser(
      (f) => frames.push(f),
      () => undefined,
      { expect: "gateway" },
    );
    socket.on("data", (chunk) => parser.push(chunk));
    socket.write(
      encodeFrame({
        v: 1,
        type: "hello",
        childWorkspaceId: "gw.test/raw",
        token: "",
        manifest: MANIFEST,
      }),
    );
    await vi.waitFor(() => expect(frames.filter((f) => f.type === "register-ack").length).toBe(1));
    expect(frames.find((f) => f.type === "register-ack")).toEqual({
      v: 1,
      type: "register-ack",
      accepted: true,
    });
    socket.destroy();
    await listener.close(); // free the path for the adapter-side rebind
    // The adapter's answer law for a message kind outside the vocabulary:
    const rig = await attached(path, { onExecute: async () => ({ kind: "completed" }) });
    const odd = await rig.link.send({ kind: "teleport", executionId: "x" } as never);
    expect(odd).toEqual({ kind: "unsupported" });
  });
});
