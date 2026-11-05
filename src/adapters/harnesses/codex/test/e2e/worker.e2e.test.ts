// E2E worker tier (E2E=1): the REAL ofac-codex-worker binary against a
// standalone Gateway + a dedicated Facilitator, driving the fake codex CLI
// through OFAC_CODEX_BIN. Proven end-to-end:
// 1. boot → register → the union capability POST carries coding.codex
//    (supportsSteering false, supportsInteraction true);
// 2. seeded work.assign → progress rows → completed row carrying
//    resultManifestRef + resultDigest; the manifest document fetched back
//    through the kernel artifact path re-hashes to the event's digest and
//    the final-answer artifact's served bytes match their declared digest;
// 3. SIGKILL mid-run → re-spawn → the probe settles the lost execution as
//    child-lost (crash law, real harness).
import { createHash, randomUUID } from "node:crypto";
import { spawn, type ChildProcess } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { computeManifestDigest, validateAgainstSchemaId } from "@ofac/protocol";
import { describe, expect, it } from "vitest";
import {
  E2E,
  gatewayConfig,
  gatewayStatus,
  gatewayToken,
  inboxRowsByType,
  kernelToken,
  psql,
  seedDelivery,
  startGateway,
  useFacilitator,
  userToken,
  waitFor,
  type Harness,
} from "../../../../../gateway/test/e2e/gateway-harness.js";
import { linkGet, linkPost } from "../../../../../workspace-link/test/e2e/harness.js";

const ROOT = new URL("../../../../../../", import.meta.url).pathname;
const WORKER_BIN = join(ROOT, "src/adapters/harnesses/codex/dist/bin/worker.js");
const MANIFEST_FILE = join(ROOT, "src/adapters/harnesses/codex/sample/capabilities.yaml");
const FAKE = new URL("../fake-codex.mjs", import.meta.url).pathname;

interface WorkerHandle {
  child: ChildProcess;
  log(): string;
  ready(): Promise<string>;
}

/** Spawn the worker binary against the Gateway's child socket. */
function startWorker(options: {
  childWorkspaceId: string;
  socket: string;
  workDir: string;
  kernelUrl: string;
  token: string;
}): WorkerHandle {
  const dir = mkdtempSync(join(tmpdir(), "cx-worker-e2e-"));
  const configPath = join(dir, "codex-harness.yaml");
  writeFileSync(
    configPath,
    `apiVersion: facilitator.framework/v1
kind: HarnessAdapter
childWorkspaceId: ${options.childWorkspaceId}
token: ${options.token}
kernelUrl: ${options.kernelUrl}
transport:
  socket:
    path: ${options.socket}
manifestFile: ${MANIFEST_FILE}
workDir: ${options.workDir}
interactivity: off
heartbeatSeconds: 1
`,
  );
  const child = spawn("node", [WORKER_BIN, "run", "--config", configPath], {
    stdio: ["ignore", "pipe", "pipe"],
    env: { ...process.env, OFAC_CODEX_BIN: FAKE },
  });
  let log = "";
  child.stdout?.on("data", (c: Buffer) => {
    log += c.toString();
  });
  child.stderr?.on("data", (c: Buffer) => {
    log += c.toString();
  });
  return {
    child,
    log: () => log,
    ready() {
      return new Promise((resolve, reject) => {
        const onData = (): void => {
          if (/^worker ready childId=\S+ socket=\S+$/m.test(log)) {
            resolve(log.split("\n").find((l) => l.startsWith("worker ready")) ?? "");
          }
        };
        child.stdout?.on("data", onData);
        child.once("exit", (code) =>
          reject(new Error(`worker exited early (${String(code)}): ${log}`)),
        );
        onData();
      });
    },
  };
}

function assignEnvelopeFor(
  commandId: string,
  workId: string,
  assignmentId: string,
  objective: string,
): Record<string, unknown> {
  return {
    specversion: "1.0",
    id: `e2e-${commandId}`,
    source: "urn:facilitator:instance:e2e",
    type: "org.facilitator.command.work.assign.v1",
    subject: `work/${workId}/assignment/${assignmentId}`,
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    traceparent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
    data: {
      commandId,
      actorEvidenceRef: "identity-evidence:e2e",
      workId,
      targetId: "e2e",
      capabilityId: "coding.codex",
      expectedContextVersion: 1,
      executionGeneration: 1,
      inputs: { objective, sourceRefs: [] },
      expectedDeliverables: [{ kind: "coding.final", mediaType: "text/markdown" }],
      deadline: "2036-10-23T18:00:00Z",
      assignmentId,
    },
  };
}

describe.skipIf(!E2E)("codex worker e2e", () => {
  const harness: Harness = useFacilitator();

  it("register → completed+digest → SIGKILL crash law", async () => {
    const gw = await gatewayConfig({
      facilitatorUrl: harness.baseUrl,
      fleet: [],
      gatewayMode: "standalone",
      heartbeatSeconds: 2,
    });
    const workDir = mkdtempSync(join(tmpdir(), "cx-worker-e2e-work-"));
    const dump = (label: string): string =>
      `${label}\nGATEWAY LOG:\n${gateway.log()}\nWORKER LOG:\n${worker?.log() ?? "(worker not spawned yet)"}\n`;
    const withDump = <T>(p: Promise<T>, label: string): Promise<T> => {
      let timer: NodeJS.Timeout;
      const guard = new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error(`stalled: ${label}\n${dump(label)}`)), 25_000);
      });
      return Promise.race([p, guard]).finally(() => clearTimeout(timer!));
    };
    // Child enrollment over the link plane: the verbatim-token law lets the
    // SAME link token ride the child wire and the kernel artifact uploads.
    const childToken = await (async () => {
      const r = await linkPost(harness.baseUrl, "/api/link/v1/enroll", "enroll-dev", {
        token: "enroll-dev",
        endpointId: "cx-e2e-child",
        instanceId: "e2e-codex-worker",
        trustMode: "isolated-executor",
        team: "team-alpha",
        role: "child",
      });
      if (r.status !== 201) throw new Error(`child enroll failed: ${r.status}`);
      return String((r.body as { linkToken: string }).linkToken);
    })();
    // Real works on a team-alpha project (the ledger resolves team scope via
    // work → project → team).
    const projectRows = await psql(
      harness.dbUrl,
      `INSERT INTO facilitator.projects (project_id, team_id, name, config, created_at)
       VALUES (gen_random_uuid(), (SELECT team_id FROM facilitator.teams WHERE slug = 'team-alpha'),
               'e2e-codex-proj', '{}', now())
       RETURNING project_id`,
    );
    const projectId = projectRows[0]!;
    const workIdOf = async (objective: string): Promise<string> => {
      const r = await linkPost(harness.baseUrl, "/api/works", await kernelToken(), {
        objective,
        capabilityId: "coding.codex",
        projectId,
      });
      if (r.status !== 201) throw new Error(`work create failed: ${r.status}`);
      return String((r.body as { workId: string }).workId);
    };
    const fetchContent = async (path: string, token: string): Promise<Buffer> => {
      const r = await fetch(`${harness.baseUrl}${path}`, {
        headers: { authorization: `Bearer ${token}` },
      });
      if (!r.ok) throw new Error(`GET ${path} -> ${r.status}`);
      return Buffer.from(await r.arrayBuffer());
    };
    const alice = await userToken("alice", "alice");
    const workA = await workIdOf("worker e2e plain run");
    const workB = await workIdOf("worker e2e crash run SLEEP(30000)");
    const asgA = randomUUID();
    const asgB = randomUUID();
    const gateway = startGateway(gw.configPath);
    let worker: WorkerHandle | undefined;
    try {
      // ── 1. boot + register + capability union ──
      const ready = await withDump(gateway.ready(), "awaiting gateway ready line");
      expect(ready.children).toBe(0);
      worker = startWorker({
        childWorkspaceId: `${gw.gatewayId}/codex`,
        socket: gw.socketPath ?? "",
        workDir,
        kernelUrl: harness.baseUrl,
        token: childToken,
      });
      await withDump(worker.ready(), "awaiting worker ready line");
      const caps = await waitFor("capability union POST", async () => {
        const r = await linkGet(
          harness.baseUrl,
          "/api/link/v1/capabilities",
          gatewayToken(gw.stateDir),
        );
        if (r.status !== 200) return false;
        const m = r.body as {
          capabilities: { id: string; supportsSteering?: boolean; supportsInteraction?: boolean }[];
          ownerTeam: string;
        };
        return m.capabilities.some((c) => c.id === "coding.codex") ? m : false;
      });
      expect(caps.ownerTeam).toBe("team-alpha");
      const descriptor = caps.capabilities.find((c) => c.id === "coding.codex")!;
      expect(descriptor.supportsSteering).toBe(false);
      expect(descriptor.supportsInteraction).toBe(true);

      // ── 2. plain assign: progress + completed, digests honest ──
      await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        assignEnvelopeFor("e2e-cx-1", workA, asgA, "worker e2e plain run"),
      );
      await waitFor("progress rows", async () => {
        const rows = await inboxRowsByType(harness.dbUrl, "org.facilitator.event.work.progress.v1");
        return rows.filter((r) => r.payload.includes(asgA)).length >= 2 ? true : false;
      });
      const completed = await waitFor("completed row lands", async () => {
        const rows = await inboxRowsByType(
          harness.dbUrl,
          "org.facilitator.event.assignment.completed.v1",
        );
        return rows.find((r) => r.payload.includes(asgA)) ?? false;
      }).catch((e: unknown) => {
        throw new Error(`${String(e)}\n${dump("no completed row")}`, { cause: e });
      });
      const envelope = JSON.parse(completed.payload) as { data: Record<string, unknown> };
      expect(
        validateAgainstSchemaId(
          "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json",
          envelope.data,
        ).ok,
      ).toBe(true);
      const ref = String(envelope.data["resultManifestRef"]);
      const resultDigest = String(envelope.data["resultDigest"]);
      expect(ref.startsWith("artifact://")).toBe(true);
      expect(resultDigest).toMatch(/^sha256:[0-9a-f]{64}$/);
      const manifestArtifactId = ref.split("/").pop()!;
      const docBytes = await fetchContent(`/api/artifacts/${manifestArtifactId}/content`, alice);
      const doc = JSON.parse(docBytes.toString("utf8")) as Record<string, unknown>;
      expect(computeManifestDigest(doc)).toBe(resultDigest);
      expect(String(doc["manifestId"])).toBe(`man-${String(envelope.data["executionId"])}`);
      const finalOutput = (doc["outputs"] as Record<string, unknown>[])[0]!;
      const finalBytes = await fetchContent(
        `/api/artifacts/${String(finalOutput["artifactId"])}/content`,
        alice,
      );
      expect("sha256:" + createHash("sha256").update(finalBytes).digest("hex")).toBe(
        String(finalOutput["digest"]),
      );
      expect(String(finalOutput["kind"])).toBe("coding.final");
      expect(finalBytes.toString("utf8")).toContain("fake codex final answer");

      // ── 3. SIGKILL mid-run: the crash law on a real harness ──
      await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        assignEnvelopeFor("e2e-cx-2", workB, asgB, "worker e2e crash run SLEEP(30000)"),
      );
      await waitFor("crash run started", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        return s.children?.some((c) => c.state === "connected" && c.inFlight === 1) ? s : false;
      });
      worker.child.kill("SIGKILL"); // die mid-run: no terminal event
      worker = startWorker({
        childWorkspaceId: `${gw.gatewayId}/codex`, // SAME id: probe path
        socket: gw.socketPath ?? "",
        workDir,
        kernelUrl: harness.baseUrl,
        token: childToken,
      });
      await worker.ready();
      // The re-registration probe settles the orphaned execution as
      // child-lost (the gateway's lease/lost law) — exactly one failed
      // row for asgB.
      const failed = await waitFor("child-lost failed row", async () => {
        const rows = await inboxRowsByType(
          harness.dbUrl,
          "org.facilitator.event.assignment.failed.v1",
        );
        return rows.find((r) => r.payload.includes(asgB)) ?? false;
      });
      expect(String(failed.payload)).toContain("child-lost");
      // And no completed may EVER land for the crashed run.
      const completedRows = await inboxRowsByType(
        harness.dbUrl,
        "org.facilitator.event.assignment.completed.v1",
      );
      expect(completedRows.some((r) => r.payload.includes(asgB))).toBe(false);
    } finally {
      worker?.child.kill("SIGKILL");
      await gateway.kill();
    }
  }, 120_000);
});
