// runAdapterLifecycleContract — the adapter lifecycle conformance kit
// (`@ofac/adapter/testing`). A third-party adapter implementation proves
// itself by pointing this suite at its own factory: an in-process
// `startChildListener` (the real 06 Gateway side — no Facilitator needed)
// drives the locked lifecycle law over the wire:
//
// - register: hello → accepted ack → the Gateway sees the child's manifest;
// - start: accepted immediately; progress events ride the relay (full
//   envelopes, Gateway stamps nothing); completion is the HARNESS's own
//   published event with a result-manifest-valid payload;
// - dedupe: the same commandId never executes twice;
// - steer: lands on the running execution (or reports `unsupported`,
//   never reinterpreted — flip `steerReply` for a no-steer adapter);
// - stop: accepted, cooperative abort, NO terminal event after it;
// - status: honest at every phase (running while held, completed after,
//   `unknown` for a foreign execution);
// - restart: a dead listener mid-run reconnects on the adapter's own
//   schedule; the re-register probe gets `running` for a run that survived.
//
// Determinism contract: the kit hands `startAdapter` a RunGate. The
// consumer's onExecute MUST `await Promise.race([gate.hold(ctx),
// untilAborted(ctx.signal)])` before publishing its terminal event: tests
// that need an in-flight execution hold it, the kit releases it, and a
// cancelled run unwinds through the abort signal.
//
// Call inside a vitest file (it registers describe/it/beforeEach itself).
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { ChildFleet, startChildListener, type ChildLink, type ChildListener } from "@ofac/gateway";
import { validateAgainstSchemaId } from "@ofac/protocol";
import type { EnvelopeEvent } from "@ofac/protocol";
import { createConsoleLogger } from "@ofac/workspace-link";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import type { AdapterHandle, RunContext } from "./types.js";

/** The determinism seam the consumer's onExecute awaits. */
export interface AdapterRunGate {
  /** Resolves when the kit releases this run (immediately unless held). */
  hold(ctx: RunContext): Promise<void>;
}

/** Resolves when `signal` aborts — the cooperative-cancel half of the race. */
export function untilAborted(signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal.aborted) resolve();
    else signal.addEventListener("abort", () => resolve(), { once: true });
  });
}

export interface AdapterLifecycleContractOptions {
  /** Boot the adapter-under-test against `opts.socketPath`. */
  startAdapter(gate: AdapterRunGate): Promise<{ handle: AdapterHandle; stop(): Promise<void> }>;
  socketPath: string;
  gatewayId: string;
  /** Expected steer answer for THIS adapter implementation (default: accepted). */
  steerReply?: "accepted" | "unsupported";
  /** Consumer-side proof the steer reached the harness (throw/expect inside). */
  afterSteer?(directive: string): void | Promise<void>;
  /** Consumer-side proof the stop aborted the run (throw/expect inside). */
  afterCancel?(): void | Promise<void>;
}

const RESULT_MANIFEST_ID = "https://schemas.openfacilitator.org/v1/result-manifest.v1.json";
const COMPLETED_PAYLOAD_ID =
  "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json";
const PROGRESS_TYPE = "org.facilitator.event.work.progress.v1";
const COMPLETED_TYPE = "org.facilitator.event.assignment.completed.v1";

let caseCounter = 0;

function assignEnvelope(
  commandId: string,
  workId: string,
  assignmentId: string,
): Record<string, unknown> {
  return {
    specversion: "1.0",
    id: `kit-${commandId}`,
    source: "urn:facilitator:instance:kit",
    type: "org.facilitator.command.work.assign.v1",
    subject: `work/${workId}/assignment/${assignmentId}`,
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    traceparent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
    data: {
      commandId,
      actorEvidenceRef: "identity-evidence:kit",
      workId,
      targetId: "kit",
      capabilityId: "research.analyze",
      expectedContextVersion: 1,
      inputs: { objective: "kit", sourceRefs: [] },
      expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
      deadline: "2036-10-23T18:00:00Z",
      assignmentId,
    },
  };
}

function steerEnvelope(
  commandId: string,
  workId: string,
  assignmentId: string,
  directive: string,
): Record<string, unknown> {
  return {
    specversion: "1.0",
    id: `kit-${commandId}`,
    source: "urn:facilitator:instance:kit",
    type: "org.facilitator.command.work.steer.v1",
    subject: `work/${workId}/assignment/${assignmentId}`,
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.steer.v1.json",
    data: {
      commandId,
      workId,
      assignmentId,
      targetId: "kit",
      actorEvidenceRef: "identity-evidence:kit",
      deadline: "2036-10-23T18:00:00Z",
      directive,
      expectedContextVersion: 1,
    },
  };
}

async function waitFor<T>(
  what: string,
  probe: () => T | null | false | Promise<T | null | false>,
  timeoutMs = 15_000,
): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await probe();
    if (value) return value;
    if (Date.now() > deadline) throw new Error(`timeout waiting for ${what}`);
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}

export function runAdapterLifecycleContract(opts: AdapterLifecycleContractOptions): void {
  const steerExpectation = opts.steerReply ?? "accepted";

  interface Harness {
    listener: ChildListener;
    link: ChildLink;
    events: EnvelopeEvent[];
  }

  let harness: Harness | null = null;
  let stops: (() => Promise<void>)[] = [];
  let heldPrefix: string | null = null;
  const resolvers = new Map<string, () => void>();

  const gate: AdapterRunGate = {
    hold(ctx: RunContext): Promise<void> {
      if (heldPrefix === null || !ctx.executionId.startsWith(heldPrefix)) {
        return Promise.resolve();
      }
      return new Promise<void>((resolve) => {
        resolvers.set(ctx.executionId, resolve);
      });
    },
  };

  function holdRuns(prefix: string): void {
    heldPrefix = prefix;
  }

  function release(executionId: string): void {
    resolvers.get(executionId)?.();
    resolvers.delete(executionId);
  }

  function releaseAll(): void {
    for (const resolve of resolvers.values()) resolve();
    resolvers.clear();
    heldPrefix = null;
  }

  /** Boot listener + adapter together (the listener exists before hello). */
  async function attached(): Promise<Harness> {
    const linkReady = Promise.withResolvers<ChildLink>();
    const listener = await startChildListener(
      {
        socket: { path: opts.socketPath },
        handshakeTimeoutSeconds: 5,
        leaseMissedBeats: 1000,
      },
      {
        fleet: new ChildFleet(createConsoleLogger({ level: "error" })),
        logger: createConsoleLogger({ level: "error" }),
        gatewayId: opts.gatewayId,
        heartbeatSeconds: 30,
        onRegistered: (link) => linkReady.resolve(link),
      },
    );
    const started = await opts.startAdapter(gate);
    stops.push(started.stop);
    await started.handle.ready();
    const link = await linkReady.promise;
    const events: EnvelopeEvent[] = [];
    link.setPublisher(async (env) => {
      events.push(env);
    });
    for (const env of link.drainEvents()) events.push(env);
    harness = { listener, link, events };
    return harness;
  }

  beforeEach(() => {
    stops = [];
    heldPrefix = null;
  });

  afterEach(async () => {
    for (const stop of stops) await stop();
    releaseAll();
    await harness?.listener.close();
    harness = null;
  });

  describe("adapter lifecycle contract", () => {
    it("registers: the Gateway sees the child's manifest on hello", async () => {
      const h = await attached();
      expect(h.link.manifest.capabilities.length).toBeGreaterThan(0);
      expect(h.link.connected).toBe(true);
    });

    it("start replies accepted, relays progress, and the harness's own completed event carries a valid manifest", async () => {
      const h = await attached();
      caseCounter += 1;
      const executionId = `kit-exec-${caseCounter}`;
      const workId = `kit-work-${caseCounter}`;
      const assignmentId = `kit-assign-${caseCounter}`;
      const reply = await h.link.send({
        kind: "start",
        executionId,
        executionGeneration: 7,
        command: assignEnvelope(`kit-cmd-${caseCounter}`, workId, assignmentId) as never,
      });
      expect(reply).toEqual({ kind: "accepted", executionId });

      const progress = await waitFor("progress event", () => {
        const found = h.events.find((e) => e.type === PROGRESS_TYPE);
        return found ?? false;
      });
      const progressData = progress.data as Record<string, unknown>;
      expect(progressData["workId"]).toBe(workId);
      expect(progressData["assignmentId"]).toBe(assignmentId);
      // Locked: the stamp equals the START frame's connectionGeneration.
      expect(progressData["executionGeneration"]).toBe(7);
      expect(progress.source).toMatch(/^urn:workspace:/);

      const completed = await waitFor("completed event", () => {
        const found = h.events.find((e) => e.type === COMPLETED_TYPE);
        return found ?? false;
      });
      const completedData = completed.data as Record<string, unknown>;
      expect(
        validateAgainstSchemaId(COMPLETED_PAYLOAD_ID, completedData).ok,
        "completed payload validates against event.assignment.completed.v1",
      ).toBe(true);
      expect(
        validateAgainstSchemaId(RESULT_MANIFEST_ID, completedData["resultManifest"]).ok,
        "resultManifest validates against result-manifest.v1",
      ).toBe(true);
      expect(completedData["executionId"]).toBe(executionId);
    });

    it("duplicate start (same commandId, new delivery) executes once", async () => {
      const h = await attached();
      caseCounter += 1;
      const commandId = `kit-dedupe-${caseCounter}`;
      const workId = `kit-work-${caseCounter}`;
      const assignmentId = `kit-assign-${caseCounter}`;
      const envelope = assignEnvelope(commandId, workId, assignmentId);
      const first = await h.link.send({
        kind: "start",
        executionId: `kit-exec-${caseCounter}a`,
        executionGeneration: 1,
        command: envelope as never,
      });
      expect(first.kind).toBe("accepted");
      const completedCount = () => h.events.filter((e) => e.type === COMPLETED_TYPE).length;
      await waitFor("first completed", () => completedCount() >= 1);
      const duplicate = await h.link.send({
        kind: "start",
        executionId: `kit-exec-${caseCounter}b`,
        executionGeneration: 1,
        command: envelope as never,
      });
      expect(duplicate).toEqual({ kind: "accepted", executionId: `kit-exec-${caseCounter}b` });
      await new Promise((resolve) => setTimeout(resolve, 200));
      expect(completedCount()).toBe(1); // the command never ran twice
    });

    it("steer lands on the running execution with the locked reply", async () => {
      const h = await attached();
      caseCounter += 1;
      const executionId = `kit-steer-${caseCounter}`;
      const workId = `kit-work-${caseCounter}`;
      const assignmentId = `kit-assign-${caseCounter}`;
      const directive = `focus-on-costs-${caseCounter}`;
      holdRuns(executionId);
      const reply = await h.link.send({
        kind: "start",
        executionId,
        executionGeneration: 2,
        command: assignEnvelope(`kit-steercmd-${caseCounter}`, workId, assignmentId) as never,
      });
      expect(reply.kind).toBe("accepted");
      const steerReply = await h.link.send({
        kind: "steer",
        executionId,
        command: steerEnvelope(
          `kit-steerdirective-${caseCounter}`,
          workId,
          assignmentId,
          directive,
        ) as never,
      });
      expect(steerReply.kind).toBe(steerExpectation);
      if (steerExpectation === "accepted") await opts.afterSteer?.(directive);
      const foreign = await h.link.send({
        kind: "steer",
        executionId: "kit-exec-never-seen",
        command: steerEnvelope(`kit-foreign-${caseCounter}`, "w", "a", "x") as never,
      });
      expect(foreign).toEqual({ kind: "unknown", executionId: "kit-exec-never-seen" });
      release(executionId);
    });

    it("stop aborts cooperatively: accepted, no terminal event, then unknown", async () => {
      const h = await attached();
      caseCounter += 1;
      const executionId = `kit-stop-${caseCounter}`;
      const workId = `kit-work-${caseCounter}`;
      const assignmentId = `kit-assign-${caseCounter}`;
      holdRuns(executionId);
      const reply = await h.link.send({
        kind: "start",
        executionId,
        executionGeneration: 3,
        command: assignEnvelope(`kit-stopcmd-${caseCounter}`, workId, assignmentId) as never,
      });
      expect(reply.kind).toBe("accepted");
      const running = await h.link.send({ kind: "status", executionId });
      expect(running).toEqual({ kind: "status", executionId, state: "running" });
      const stop = await h.link.send({ kind: "stop", executionId, reason: "kit-cancel" });
      expect(stop).toEqual({ kind: "accepted", executionId });
      await opts.afterCancel?.();
      await new Promise((resolve) => setTimeout(resolve, 250));
      // No terminal event after a cancel (the accepted stop reply settled the row).
      expect(h.events.filter((e) => e.type === COMPLETED_TYPE).length).toBe(0);
      const after = await h.link.send({ kind: "status", executionId });
      expect(after).toEqual({ kind: "unknown", executionId });
    });

    it("status: running while held, completed after release, unknown for a foreign id", async () => {
      const h = await attached();
      caseCounter += 1;
      const executionId = `kit-status-${caseCounter}`;
      const workId = `kit-work-${caseCounter}`;
      const assignmentId = `kit-assign-${caseCounter}`;
      holdRuns(executionId);
      await h.link.send({
        kind: "start",
        executionId,
        executionGeneration: 1,
        command: assignEnvelope(`kit-statuscmd-${caseCounter}`, workId, assignmentId) as never,
      });
      const running = await h.link.send({ kind: "status", executionId });
      expect(running).toEqual({ kind: "status", executionId, state: "running" });
      release(executionId);
      await waitFor("completion", () => h.events.some((e) => e.type === COMPLETED_TYPE));
      const done = await h.link.send({ kind: "status", executionId });
      expect(done).toEqual({ kind: "status", executionId, state: "completed" });
      const foreign = await h.link.send({ kind: "status", executionId: "kit-foreign" });
      expect(foreign).toEqual({ kind: "unknown", executionId: "kit-foreign" });
    });

    it("restart law: a dead listener mid-run reconnects; the probe gets running", async () => {
      const h = await attached();
      caseCounter += 1;
      const executionId = `kit-restart-${caseCounter}`;
      const workId = `kit-work-${caseCounter}`;
      const assignmentId = `kit-assign-${caseCounter}`;
      holdRuns(executionId);
      const reply = await h.link.send({
        kind: "start",
        executionId,
        executionGeneration: 4,
        command: assignEnvelope(`kit-restartcmd-${caseCounter}`, workId, assignmentId) as never,
      });
      expect(reply.kind).toBe("accepted");

      // Kill the Gateway mid-run; the adapter must survive (its own policy).
      await h.listener.close();
      const linkReady = Promise.withResolvers<ChildLink>();
      const listener2 = await startChildListener(
        {
          socket: { path: opts.socketPath },
          handshakeTimeoutSeconds: 5,
          leaseMissedBeats: 1000,
        },
        {
          fleet: new ChildFleet(createConsoleLogger({ level: "error" })),
          logger: createConsoleLogger({ level: "error" }),
          gatewayId: opts.gatewayId,
          heartbeatSeconds: 30,
          onRegistered: (link) => linkReady.resolve(link),
        },
      );
      stops.push(async () => {
        await listener2.close();
      });
      const link2 = await linkReady.promise; // adapter re-registered on its own
      const probe = await waitFor("re-register probe answer", async () => {
        try {
          return await link2.send({ kind: "status", executionId });
        } catch {
          return false;
        }
      });
      // The run survived (same process, registry survived) ⇒ honest running.
      expect(probe).toEqual({ kind: "status", executionId, state: "running" });
      harness = { listener: listener2, link: link2, events: h.events };
      release(executionId);
    });
  });
}

/** The temp-dir-per-suite socket the kit recommends (short enough for sun_path). */
export function kitSocketPath(): string {
  return join(mkdtempSync(join(tmpdir(), "ofac-kit-")), "child.sock");
}

// ---------------------------------------------------------------------------
// startStubKernel — the in-process kernel stand-in for the artifact client
// (Plan 15). Implements the link-plane byte path the client speaks: POST
// /api/link/v1/events answers a request_upload command with 202 + the ticket
// in-band on `data` (fs-style by default: no presigned uploadUrl, so the
// client PUTs to uploadLocation); the matching PUT stores the bytes and
// answers with the kernel-verified digest (counting + hashing, like
// ArtifactLedger.StoreAsync). `opts.presign` mints an uploadUrl instead:
// bytes land at PUT /presigned/<id> (no authority), and only a following
// confirm_upload command HEADs + re-hashes them and settles the slot (the
// real kernel's direct-to-store law). A duplicate commandId re-attaches the
// live ticket, exercising the client's re-attach path. `opts.rejectDigest`
// answers a digest-mismatched PUT with 422 (the kernel's law) so failure
// legs are testable too.
// ---------------------------------------------------------------------------
import { createServer } from "node:http";
import { createHash, randomUUID } from "node:crypto";

export interface StubKernel {
  readonly url: string;
  /** artifactId (lowercase guid, no dashes) → stored bytes + mediaType + kernel digest. */
  readonly artifacts: Map<string, { bytes: Buffer; mediaType: string; digest: string }>;
  /** entryId → retained memory content (Plan 16 stub leg). */
  readonly memories: Map<string, { content: string; scopeSelector: string }>;
  /** Every envelope the stub received (assert completed-event legs against it). */
  readonly events: Array<Record<string, unknown>>;
  close(): Promise<void>;
}

export async function startStubKernel(opts?: {
  rejectDigest?: boolean;
  presign?: boolean;
}): Promise<StubKernel> {
  const artifacts = new Map<string, { bytes: Buffer; mediaType: string; digest: string }>();
  // commandId → allocated artifactId (the pending slot).
  const pending = new Map<string, string>();
  // presigned direct uploads: artifactId → bytes awaiting confirm_upload.
  const directBytes = new Map<string, Buffer>();
  const memories = new Map<string, { content: string; scopeSelector: string }>();
  const events: Array<Record<string, unknown>> = [];
  const server = createServer((request, response) => {
    const chunks: Buffer[] = [];
    request.on("data", (c: Buffer) => chunks.push(c));
    request.on("end", () => {
      const body = Buffer.concat(chunks);
      const url = request.url ?? "";
      const send = (status: number, obj: unknown): void => {
        response.writeHead(status, { "content-type": "application/json" });
        response.end(JSON.stringify(obj));
      };
      if (request.method === "POST" && url === "/api/link/v1/events") {
        const env = JSON.parse(body.toString("utf8")) as Record<string, unknown>;
        const data = (env["data"] ?? {}) as Record<string, unknown>;
        events.push(env);
        if (env["type"] === "org.facilitator.command.artifact.request_upload.v1") {
          const commandId = String(data["commandId"] ?? "");
          const declared = String(data["digest"] ?? "");
          const existing = pending.get(commandId);
          const artifactId = existing ?? randomUUID().replaceAll("-", "");
          pending.set(commandId, artifactId);
          artifacts.set(artifactId, {
            bytes: Buffer.alloc(0),
            mediaType: String(data["mediaType"] ?? "application/octet-stream"),
            digest: declared,
          });
          send(202, {
            deliveryId: randomUUID(),
            status: existing === undefined ? "accepted" : "duplicate",
            processedAt: new Date().toISOString(),
            data: {
              artifactId,
              uploadLocation: `/api/link/v1/artifacts/${artifactId}/content`,
              ...(opts?.presign === true
                ? {
                    uploadUrl: `http://${String(request.headers["host"])}/presigned/${artifactId}`,
                    method: "PUT",
                  }
                : { method: "POST" }),
              expiresAt: new Date(Date.now() + 900_000).toISOString(),
            },
          });
          return;
        }
        if (env["type"] === "org.facilitator.command.artifact.confirm_upload.v1") {
          // The kernel's direct-to-store law: HEAD the slot, re-hash, settle.
          const id = String(data["artifactId"] ?? "");
          const row = artifacts.get(id);
          const direct = directBytes.get(id);
          if (row === undefined || direct === undefined) {
            send(202, {
              deliveryId: randomUUID(),
              status: "failed",
              processedAt: new Date().toISOString(),
            });
            return;
          }
          const digest = "sha256:" + createHash("sha256").update(direct).digest("hex");
          if (digest !== row.digest || opts?.rejectDigest === true) {
            send(202, {
              deliveryId: randomUUID(),
              status: "failed",
              processedAt: new Date().toISOString(),
            });
            return;
          }
          artifacts.set(id, { bytes: direct, mediaType: row.mediaType, digest });
          send(202, {
            deliveryId: randomUUID(),
            status: "confirmed",
            processedAt: new Date().toISOString(),
          });
          return;
        }
        // Plan 16 memory legs: retain stores in-memory and answers the
        // receipt in-band (candidate, like the kernel's D4 law); recall
        // substring-matches the store and answers the D8 shape.
        if (env["type"] === "org.facilitator.command.memory.retain.v1") {
          const entryId = randomUUID();
          memories.set(entryId, {
            content: String(data["content"] ?? ""),
            scopeSelector: String(data["scopeSelector"] ?? ""),
          });
          send(202, {
            deliveryId: randomUUID(),
            status: "accepted",
            processedAt: new Date().toISOString(),
            data: { entryId, status: "candidate", duplicate: false },
          });
          return;
        }
        if (env["type"] === "org.facilitator.command.memory.recall.v1") {
          const query = String(data["query"] ?? "").toLowerCase();
          send(202, {
            deliveryId: randomUUID(),
            status: "accepted",
            processedAt: new Date().toISOString(),
            data: {
              requestId: randomUUID(),
              providerProfile: "postgres-fts",
              entries: [...memories.entries()]
                .filter(([, row]) => row.content.toLowerCase().includes(query))
                .map(([entryId, row]) => ({
                  entryId,
                  scopeId: row.scopeSelector,
                  type: "observed_fact",
                  content: row.content,
                  contentHash: "sha256:" + "0".repeat(64),
                  provenance: {},
                  observedAt: new Date().toISOString(),
                  revision: 1,
                  status: "candidate",
                  policyLabel: "team-only",
                })),
              truncated: false,
              asOf: new Date().toISOString(),
            },
          });
          return;
        }
        send(202, {
          deliveryId: randomUUID(),
          status: "accepted",
          processedAt: new Date().toISOString(),
        });
        return;
      }
      const direct = /^\/presigned\/([0-9a-f-]+)$/.exec(url);
      if (request.method === "PUT" && direct !== null) {
        // The store stand-in: bytes land, no verdict until confirm_upload.
        directBytes.set(direct[1]!.replaceAll("-", ""), body);
        response.writeHead(200);
        response.end();
        return;
      }
      const put = /^\/api\/link\/v1\/artifacts\/([0-9a-f]+)\/content$/.exec(url);
      if (request.method === "PUT" && put !== null) {
        const id = put[1]!;
        const row = artifacts.get(id);
        if (row === undefined) {
          send(404, { status: 404, detail: "no such allocation" });
          return;
        }
        const digest = "sha256:" + createHash("sha256").update(body).digest("hex");
        if (digest !== row.digest) {
          send(422, { status: 422, detail: `digest mismatch: ${digest}` });
          return;
        }
        if (opts?.rejectDigest === true) {
          send(422, { status: 422, detail: "stub configured to reject" });
          return;
        }
        artifacts.set(id, { bytes: body, mediaType: row.mediaType, digest });
        send(200, { artifactId: id, digest, size: body.byteLength, state: "stored" });
        return;
      }
      send(404, { status: 404, detail: `stub: ${request.method} ${url}` });
    });
  });
  const listening = Promise.withResolvers<void>();
  server.listen(0, "127.0.0.1", () => listening.resolve());
  await listening.promise;
  const address = server.address();
  const url = `http://127.0.0.1:${typeof address === "object" && address !== null ? address.port : 0}`;
  const closing = Promise.withResolvers<void>();
  server.on("close", () => closing.resolve());
  return {
    url,
    artifacts,
    events,
    memories,
    close: () => {
      server.close();
      return closing.promise;
    },
  };
}
