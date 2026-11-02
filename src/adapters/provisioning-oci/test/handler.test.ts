// The launcher law, pinned handler-level against the fake Docker Engine over
// a real unix socket (the forgejo-adapter precedent: handler.onCommand drives
// the whole command path; receipts are the CommandOutcome the client ships).
// Pins: start creates EXACTLY ONE container with the exact label/env/bind/
// network payload; the $VAR env-expansion law (value set ⇒ expanded, missing
// ⇒ honest rejected receipt, never an empty secret); the idempotent re-offer
// no-op; stop stops+removes the matching set and no-ops on empty; GC removes
// ONLY stale ofac-labeled exited containers; the allowlist gate rejects with
// the reason.
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import type { EnvelopeCommand } from "@ofac/protocol";
import type { CommandContext, LinkLogger } from "@ofac/workspace-link";
import type { LauncherConfig } from "../src/config.js";
import {
  CMD_PROVISION_START,
  CMD_PROVISION_STOP,
  createProvisionHandler,
  launcherManifest,
} from "../src/handler.js";
import { FakeDocker, type RecordedRequest } from "./fake-docker.js";

const silent: LinkLogger = {
  debug: () => undefined,
  info: () => undefined,
  warn: () => undefined,
  error: () => undefined,
};

const NOW_UNIX = 1_730_000_000; // fixed clock: GC ages are exact

function baseConfig(overrides: Partial<LauncherConfig> = {}): LauncherConfig {
  return {
    kernelUrl: "http://kernel:9000",
    enrollSecret: "enroll-dev",
    endpointId: "oci-launcher",
    team: "team-alpha",
    dockerHost: "unix:///var/run/docker.sock",
    statePath: ":memory:",
    gcMinutes: 60,
    stopGraceSeconds: 10,
    allowedImages: ["worker/*", "alpine:3.20"],
    allowedNetworks: ["ofac-net", "team-*"],
    logLevel: "info",
    ...overrides,
  };
}

const ctx = { publishEvent: async () => undefined } as unknown as CommandContext;

function startEnvelope(data: Record<string, unknown>): EnvelopeCommand {
  return { type: CMD_PROVISION_START, data } as unknown as EnvelopeCommand;
}

function stopEnvelope(data: Record<string, unknown>): EnvelopeCommand {
  return { type: CMD_PROVISION_STOP, data } as unknown as EnvelopeCommand;
}

function startData(
  spec: Record<string, unknown>,
  over: Record<string, unknown> = {},
): Record<string, unknown> {
  return {
    commandId: "cmd-1",
    operationId: "op-1",
    workloadId: "wl-42",
    team: "team-alpha",
    actorEvidenceRef: "evidence:assign-1",
    deadline: new Date(Date.now() + 60_000).toISOString(),
    spec: { image: "worker/demo:1", ...spec },
    ...over,
  };
}

function stopData(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    commandId: "cmd-stop-1",
    operationId: "op-stop-1",
    workloadId: "wl-42",
    team: "team-alpha",
    actorEvidenceRef: "evidence:assign-1",
    deadline: new Date(Date.now() + 60_000).toISOString(),
    ...over,
  };
}

describe("provision handler (fake docker over unix socket)", () => {
  let fake: FakeDocker;

  beforeAll(async () => {
    fake = new FakeDocker();
    await fake.start();
  });

  afterAll(async () => {
    await fake.stop();
  });

  function harness(
    configOverrides: Partial<LauncherConfig> = {},
    env: Record<string, string | undefined> = {},
  ) {
    const config = baseConfig(configOverrides);
    return createProvisionHandler({
      docker: fake.client,
      config,
      logger: silent,
      env,
      now: () => NOW_UNIX,
    });
  }

  /** Every GET list + mutation since `mark`, in wire order. */
  const mutations = (mark: number): RecordedRequest[] =>
    fake.calls(mark).filter((r) => r.method !== "GET");

  /** Container-plane verbs only: a prior test's deadline-aborted pull can
   *  land its request event on the fake server after the abort already
   *  resolved (transport-plane lag under CPU starvation), so image-plane
   *  bleed into the window is scheduler noise, not handler behavior. */
  const containerOps = (mark: number): RecordedRequest[] =>
    mutations(mark).filter((r) => r.path.startsWith("/containers/"));

  describe("provision.start", () => {
    it("creates exactly one container with the exact create payload (existing image: ZERO pulls)", async () => {
      fake.inventory.length = 0;
      fake.images = ["worker/demo:1"]; // locally built — inspect-first must spare the pull
      const mark = fake.requests.length;
      const handler = harness({}, { MY_SECRET: "s3cr3t" });
      const outcome = await handler.onCommand(
        startEnvelope(
          startData(
            {
              entrypoint: ["/bin/worker", "--serve"],
              network: "ofac-net",
              env: { OFAC_ENROLL_SECRET: "$MY_SECRET", LOG_LEVEL: "debug" },
              volumes: [
                { source: "/data/cache", target: "/cache" },
                { source: "/data/ro", target: "/ro", readOnly: true },
              ],
              labels: { "ofac.assignment": "asg-7" },
            },
            { workloadId: "wl-100" },
          ),
        ),
        ctx,
      );

      expect(outcome).toEqual({ kind: "accepted" });
      const ops = mutations(mark);
      // Inspect-first: image present ⇒ create + start only, no pull.
      expect(ops).toHaveLength(2);
      expect(ops[0]?.path).toBe("/containers/create?name=ofac-wl-100");
      expect(ops[1]?.path).toMatch(/^\/containers\/fake-\d+\/start$/);
      const inspects = fake
        .calls(mark)
        .filter((r) => r.method === "GET" && !r.path.startsWith("/containers/json"));
      expect(inspects.map((r) => r.path)).toEqual(["/images/worker/demo:1/json"]);
      const create = ops[0] as RecordedRequest;
      expect(create.body).toEqual({
        Image: "worker/demo:1",
        Hostname: "ofac-wl-100",
        Entrypoint: ["/bin/worker", "--serve"],
        Env: ["OFAC_ENROLL_SECRET=s3cr3t", "LOG_LEVEL=debug"],
        Labels: {
          "ofac.workload": "wl-100",
          "ofac.operation": "op-1",
          "ofac.assignment": "asg-7",
        },
        HostConfig: {
          AutoRemove: false,
          RestartPolicy: { Name: "no" },
          NetworkMode: "ofac-net",
          Binds: ["/data/cache:/cache", "/data/ro:/ro:ro"],
        },
      });
    });

    it("expands $VAR spec.env from the launcher env; a missing var rejects honestly", async () => {
      fake.inventory.length = 0;
      const mark = fake.requests.length;
      const handler = harness();
      const outcome = await handler.onCommand(
        startEnvelope(
          startData({ env: { OFAC_ENROLL_SECRET: "$UNSET_SECRET" } }, { workloadId: "wl-101" }),
        ),
        ctx,
      );
      expect(outcome).toEqual({ kind: "rejected", reason: "env-unresolved:UNSET_SECRET" });
      // Not even a pull: the gate runs before any docker mutation.
      expect(mutations(mark)).toEqual([]);
    });

    it("the re-offer no-ops when the workload already runs (read-before-act)", async () => {
      fake.inventory.length = 0;
      fake.seed({
        id: "old-running",
        state: "running",
        finishedAt: 0,
        labels: { "ofac.workload": "wl-102", "ofac.operation": "op-0" },
      });
      const mark = fake.requests.length;
      const outcome = await harness().onCommand(
        startEnvelope(startData({}, { workloadId: "wl-102", commandId: "cmd-re" })),
        ctx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      expect(mutations(mark)).toEqual([]);
      expect(fake.inventory).toHaveLength(1); // the old container untouched
    });

    it("the kill-9 relaunch deletes the name-holding exited container before create", async () => {
      fake.inventory.length = 0;
      fake.images = ["worker/demo:1"];
      fake.seed({
        id: "crashed",
        state: "exited",
        finishedAt: NOW_UNIX - 30, // fresh: GC spares it, the relaunch sweep does not
        labels: { "ofac.workload": "wl-103", "ofac.operation": "op-0" },
      });
      const mark = fake.requests.length;
      const outcome = await harness().onCommand(
        startEnvelope(startData({}, { workloadId: "wl-103" })),
        ctx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      // The 409 law: the DELETE of the name-holder rides BEFORE the CREATE.
      const ops = mutations(mark).map((r) => `${r.method} ${r.path.split("?")[0]}`);
      expect(ops.slice(0, 2)).toEqual(["DELETE /containers/crashed", "POST /containers/create"]);
      const creates = mutations(mark).filter((r) => r.path.startsWith("/containers/create"));
      expect(creates).toHaveLength(1);
      // The continuity answer: same workload label, NEW operation label.
      const body = creates[0]?.body as { Labels: Record<string, string> };
      expect(body.Labels).toMatchObject({ "ofac.workload": "wl-103", "ofac.operation": "op-1" });
    });

    it("the approved-image gate rejects an unlisted image before any docker call", async () => {
      fake.inventory.length = 0;
      const mark = fake.requests.length;
      const outcome = await harness().onCommand(
        startEnvelope(startData({ image: "evil/image:tag" })),
        ctx,
      );
      expect(outcome).toEqual({ kind: "rejected", reason: "image-not-allowed" });
      expect(mutations(mark)).toEqual([]);
    });

    it("the approved-network gate rejects an unlisted network", async () => {
      fake.inventory.length = 0;
      const outcome = await harness().onCommand(
        startEnvelope(startData({ network: "default" })),
        ctx,
      );
      expect(outcome).toEqual({ kind: "rejected", reason: "network-not-allowed" });
    });

    it("a missing image is pulled exactly once, then creates", async () => {
      fake.inventory.length = 0;
      fake.images = [];
      const mark = fake.requests.length;
      const outcome = await harness().onCommand(
        startEnvelope(startData({}, { workloadId: "wl-104" })),
        ctx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      const pulls = fake
        .calls(mark)
        .filter((r) => r.method === "POST" && r.path.startsWith("/images/create"));
      expect(pulls).toHaveLength(1);
      expect(pulls[0]?.path).toBe("/images/create?fromImage=worker%2Fdemo%3A1");
      const creates = fake
        .calls(mark)
        .filter((r) => r.method === "POST" && r.path.startsWith("/containers/create"));
      expect(creates).toHaveLength(1);
    });

    it("a failed pull with the image still absent rejects honestly (image-missing)", async () => {
      fake.inventory.length = 0;
      fake.images = [];
      fake.pullFails = true;
      const mark = fake.requests.length;
      const outcome = await harness().onCommand(
        startEnvelope(startData({}, { workloadId: "wl-105" })),
        ctx,
      );
      expect(outcome).toEqual({ kind: "rejected", reason: "image-missing" });
      // Two inspects + one pull — the container was never created.
      expect(
        fake.calls(mark).filter((r) => r.method === "POST" && r.path.startsWith("/containers/")),
      ).toEqual([]);
      fake.pullFails = false;
    });

    it("an expired deadline answers rejected start-deadline (bounded pull)", async () => {
      fake.inventory.length = 0;
      fake.images = []; // force the pull path so the deadline has something to abort
      fake.pullDelayMs = 150;
      try {
        const outcome = await harness().onCommand(
          startEnvelope(startData({}, { deadline: new Date(Date.now() - 1000).toISOString() })),
          ctx,
        );
        expect(outcome).toEqual({ kind: "rejected", reason: "start-deadline" });
      } finally {
        fake.pullDelayMs = 0;
      }
    });

    it("an unknown command type answers unsupported", async () => {
      const outcome = await harness().onCommand(
        { type: "org.facilitator.command.work.assign.v1", data: {} } as unknown as EnvelopeCommand,
        ctx,
      );
      expect(outcome.kind).toBe("unsupported");
    });
  });

  describe("provision.stop", () => {
    it("stops and removes EVERY container with the workload label", async () => {
      fake.inventory.length = 0;
      fake.seed({
        id: "c1",
        state: "running",
        finishedAt: 0,
        labels: { "ofac.workload": "wl-200" },
      });
      fake.seed({
        id: "c2",
        state: "exited",
        finishedAt: NOW_UNIX,
        labels: { "ofac.workload": "wl-200" },
      });
      fake.seed({
        id: "other",
        state: "running",
        finishedAt: 0,
        labels: { "ofac.workload": "wl-other" },
      });
      const mark = fake.requests.length;
      const outcome = await harness().onCommand(
        stopEnvelope(stopData({ workloadId: "wl-200" })),
        ctx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      expect(containerOps(mark).map((r) => `${r.method} ${r.path}`)).toEqual([
        "POST /containers/c1/stop?t=10",
        "DELETE /containers/c1",
        "POST /containers/c2/stop?t=10",
        "DELETE /containers/c2",
      ]);
      expect(fake.inventory.map((c) => c.id)).toEqual(["other"]); // never touches foreign labels
    });

    it("no-op accepted when the workload has nothing", async () => {
      fake.inventory.length = 0;
      const mark = fake.requests.length;
      const outcome = await harness().onCommand(
        stopEnvelope(stopData({ workloadId: "wl-none" })),
        ctx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      expect(mutations(mark)).toEqual([]);
    });
  });

  describe("GC law (rides provision.stop)", () => {
    it("removes ONLY ofac-labeled exited/dead containers older than gcMinutes", async () => {
      fake.inventory.length = 0;
      const stale = NOW_UNIX - 61 * 60;
      fake.seed({
        id: "gc-old",
        state: "exited",
        finishedAt: stale,
        labels: { "ofac.workload": "wl-x" },
      });
      fake.seed({
        id: "gc-dead",
        state: "dead",
        finishedAt: stale,
        labels: { "ofac.workload": "wl-y" },
      });
      fake.seed({
        id: "gc-fresh",
        state: "exited",
        finishedAt: NOW_UNIX - 59 * 60,
        labels: { "ofac.workload": "wl-z" },
      });
      fake.seed({
        id: "gc-running",
        state: "running",
        finishedAt: 0,
        labels: { "ofac.workload": "wl-r" },
      });
      fake.seed({
        id: "compose",
        state: "exited",
        finishedAt: stale,
        labels: { "com.docker.compose": "x" },
      });
      const outcome = await harness().onCommand(
        stopEnvelope(stopData({ workloadId: "wl-none" })),
        ctx,
      );
      expect(outcome).toEqual({ kind: "accepted" });
      expect(fake.inventory.map((c) => c.id).sort()).toEqual(["compose", "gc-fresh", "gc-running"]);
    });
  });
});

describe("launcherManifest", () => {
  it("advertises exactly the two provision capabilities", () => {
    const manifest = launcherManifest("team-alpha");
    expect(manifest.capabilities.map((c) => c.id)).toEqual([
      CMD_PROVISION_START,
      CMD_PROVISION_STOP,
    ]);
    expect(manifest.ownerTeam).toBe("team-alpha");
  });
});
