// E2E worker tier: the REAL
// ofac-deterministic-worker binary against a standalone Gateway + a
// dedicated Facilitator. Proven end-to-end, observable in the kernel:
// 1. boot → register → the union capability POST (ownerTeam rewritten);
// 2. seeded work.assign → 2 progress rows → completed row carrying
//    resultManifestRef + resultDigest; the manifest document fetched back
//    through the kernel artifact path re-hashes to the event's digest, the
//    report artifact's served bytes match the manifest's declared digest,
//    and facilitator.result_manifests holds revision 1 + a manifest.recorded
//    timeline row; a re-published completed event chains revision 2 and
//    supersedes revision 1;
// 3. steer mid-run lands on the RUNNING execution (visible inside the
//    served report bytes under "## Steering directives");
// 4. SIGKILL mid-run → re-spawn → probe `unknown` ⇒ exactly ONE
//    child-lost-execution assignment.failed (06's crash law, real harness).
import { createHash, randomUUID } from "node:crypto";
import { spawn, type ChildProcess } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  computeManifestDigest,
  validateAgainstSchemaId,
  type EnvelopeCommand,
} from "@ofac/protocol";
import {
  artifactUri,
  buildCompletedEvent,
  buildResultManifest,
  createArtifactClient,
} from "@ofac/adapter";
import { describe, expect, it } from "vitest";
import {
  E2E,
  assignEnvelope,
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

const WORKER_BIN = join(ROOT, "src/adapters/harnesses/deterministic/dist/bin/worker.js");

const MANIFEST_FILE = join(ROOT, "src/adapters/harnesses/deterministic/sample/capabilities.yaml");

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
  stepMs: number;
  kernelUrl: string;
  token: string;
}): WorkerHandle {
  const dir = mkdtempSync(join(tmpdir(), "worker-e2e-"));
  const configPath = join(dir, "worker.yaml");
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
stepMs: ${String(options.stepMs)}
heartbeatSeconds: 1
`,
  );
  const child = spawn("node", [WORKER_BIN, "run", "--config", configPath], {
    stdio: ["ignore", "pipe", "pipe"],
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

function steerEnvelopeFor(
  workId: string,
  assignmentId: string,
  directive: string,
): Record<string, unknown> {
  return {
    specversion: "1.0",
    id: `e2e-steer-${assignmentId}`,
    source: "urn:facilitator:instance:e2e",
    type: "org.facilitator.command.work.steer.v1",
    subject: `work/${workId}/assignment/${assignmentId}`,
    time: new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.steer.v1.json",
    data: {
      commandId: `steer-${assignmentId}`,
      workId,
      assignmentId,
      targetId: "e2e",
      actorEvidenceRef: "identity-evidence:e2e",
      deadline: "2036-10-23T18:00:00Z",
      directive,
      expectedContextVersion: 1,
      // Required by the frozen command.work.steer.v1 schema (generation
      // fencing): the link delivery pipeline fail-closed-validates every
      // seeded envelope, and a schema-violating steer is rejected before
      // the gateway ever forwards it to the running child.
      executionGeneration: 1,
    },
  };
}

describe.skipIf(!E2E)("deterministic worker e2e", () => {
  const harness: Harness = useFacilitator();

  it("register → completed+digest → steer-while-running → kill → lost", async () => {
    const gw = await gatewayConfig({
      facilitatorUrl: harness.baseUrl,
      fleet: [],
      gatewayMode: "standalone",
      heartbeatSeconds: 2,
    });
    const workDir = mkdtempSync(join(tmpdir(), "worker-e2e-work-"));
    const dump = (label: string): string =>
      `${label}\nGATEWAY LOG:\n${gateway.log()}\nWORKER LOG:\n${worker?.log() ?? "(worker not spawned yet)"}\n`;
    const withDump = <T>(p: Promise<T>, label: string): Promise<T> => {
      let timer: NodeJS.Timeout;
      const guard = new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error(`stalled: ${label}\n${dump(label)}`)), 25_000);
      });
      return Promise.race([p, guard]).finally(() => clearTimeout(timer!));
    };
    // The worker's kernel credential is a CHILD enrollment — mode
    // (a) with the dev enrollment secret. The empty-fleet standalone Gateway
    // admits any hello token, so the SAME link token rides both the child
    // wire and the kernel link-plane HTTP (the verbatim-token law).
    const childToken = await (async () => {
      const r = await linkPost(harness.baseUrl, "/api/link/v1/enroll", "enroll-dev", {
        token: "enroll-dev",
        endpointId: "det-e2e-child",
        instanceId: "e2e-worker",
        trustMode: "isolated-executor",
        team: "team-alpha",
        role: "child",
      });
      if (r.status !== 201) throw new Error(`child enroll failed: ${r.status}`);
      return String((r.body as { linkToken: string }).linkToken);
    })();
    // Real works on a team-alpha PROJECT (the ledger resolves team scope via
    // work → project → team; a projectless work answers manifest-not-found):
    // seed the project row over psql, create works over kernel REST. GUID
    // ids: the ledger's work_id/assignment_id are uuid.
    const projectRows = await psql(
      harness.dbUrl,
      `INSERT INTO facilitator.projects (project_id, team_id, name, config, created_at)
       VALUES (gen_random_uuid(), (SELECT team_id FROM facilitator.teams WHERE slug = 'team-alpha'),
               'e2e-worker-proj', '{}', now())
       RETURNING project_id`,
    );
    const projectId = projectRows[0]!;
    const workIdOf = async (objective: string): Promise<string> => {
      const r = await linkPost(harness.baseUrl, "/api/works", await kernelToken(), {
        objective,
        capabilityId: "research.analyze",
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
    const workB = await workIdOf("worker e2e steered run");
    // GUID assignment ids: the ledger's assignment_id is uuid.
    const asgA = randomUUID();
    const asgB = randomUUID();
    const gateway = startGateway(gw.configPath);
    // Declared here, spawned only after the gateway's ready line (below).
    let worker: WorkerHandle | undefined;
    try {
      // ── 1. boot + register + capability union (ownerTeam rewritten) ──
      const ready = await withDump(gateway.ready(), "awaiting gateway ready line");
      expect(ready.children).toBe(0); // listener boots empty: fleet modes POST nothing
      // Spawn AFTER the ready line, never alongside it: a worker launched
      // at boot can attach before the gateway prints its snapshot, so the
      // empty-listener assertion above would race the child registration
      // (children=1 on a warm runner). Ordering makes it deterministic.
      worker = startWorker({
        childWorkspaceId: `${gw.gatewayId}/deterministic`,
        socket: gw.socketPath ?? "",
        workDir,
        stepMs: 4000, // a wide steer/kill window per step
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
        const m = r.body as { capabilities: { id: string }[]; ownerTeam: string };
        return m.capabilities.some((c) => c.id === "research.analyze") ? m : false;
      });
      expect(caps.ownerTeam).toBe("team-alpha");
      const online = await waitFor("worker connected", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        return s.children?.some((c) => c.state === "connected") ? s : false;
      });
      expect(online.mode).toBe("standalone");

      // ── 2. plain assign: 2 progress + completed, manifest validates ──
      await seedDelivery(
        harness.baseUrl,
        gw.gatewayId, // the endpointId is the gatewayId (06 idiom)
        assignEnvelope({
          commandId: "e2e-det-1",
          workId: workA,
          assignmentId: asgA,
        }),
      );
      await waitFor("two progress rows", async () => {
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
      const envelope = JSON.parse(completed.payload) as {
        data: Record<string, unknown>;
      };
      expect(
        validateAgainstSchemaId(
          "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json",
          envelope.data,
        ).ok,
      ).toBe(true);
      // The amendment's shape: ref + digest ride together; the inline
      // manifest is the deprecated empty shell.
      const ref = String(envelope.data["resultManifestRef"]);
      const resultDigest = String(envelope.data["resultDigest"]);
      expect(ref.startsWith("artifact://")).toBe(true);
      expect(resultDigest).toMatch(/^sha256:[0-9a-f]{64}$/);
      expect((envelope.data["resultManifest"] as { outputs: unknown[] }).outputs).toEqual([]);
      // The manifest document rides the artifact content path (last path
      // segment = kernel artifact GUID), stored ALREADY canonical.
      const manifestArtifactId = ref.split("/").pop()!;
      const docBytes = await fetchContent(`/api/artifacts/${manifestArtifactId}/content`, alice);
      expect(Buffer.from(docBytes).toString("utf8")).toBe(
        Buffer.from(docBytes).toString("utf8").trim(),
      );
      const doc = JSON.parse(docBytes.toString("utf8")) as Record<string, unknown>;
      expect(computeManifestDigest(doc)).toBe(resultDigest); // event digest = document bytes
      expect(String(doc["manifestId"])).toBe(`man-${String(envelope.data["executionId"])}`); // man-{executionId}: the wire execution id, echoed from the event
      const docOutput = (doc["outputs"] as Record<string, unknown>[])[0]!;
      const reportBytes = await fetchContent(
        `/api/artifacts/${String(docOutput["artifactId"])}/content`,
        alice,
      );
      expect("sha256:" + createHash("sha256").update(reportBytes).digest("hex")).toBe(
        String(docOutput["digest"]),
      ); // declared == served
      expect(docOutput["kind"]).toBe("analysis.report");
      // Ledger truth: revision-1 row, timeline, and the GET pair.
      await waitFor("result_manifests revision 1", async () => {
        const rows = await psql(
          harness.dbUrl,
          `SELECT digest || '|' || state FROM facilitator.result_manifests WHERE assignment_id = '${asgA}'`,
        );
        return rows[0] === `${resultDigest}|recorded` ? true : false;
      });
      const kinds = await psql(
        harness.dbUrl,
        `SELECT DISTINCT kind FROM facilitator.work_events WHERE work_id = '${workA}'`,
      );
      expect(kinds).toContain("manifest.recorded");
      const listed = await linkGet(harness.baseUrl, `/api/works/${workA}/manifests`, alice);
      expect(listed.status).toBe(200);
      const visible = listed.body as Array<Record<string, unknown>>;
      expect(visible).toHaveLength(1);
      expect(visible[0]!).toMatchObject({
        revision: 1,
        state: "recorded",
        digest: resultDigest,
        artifactId: manifestArtifactId,
      });

      // ── 2b. correction: a second completed event chains revision 2 ──
      // Published straight at the kernel over the link plane (the same
      // child credential); a fresh manifest document, the same assignment.
      const client = createArtifactClient({ baseUrl: harness.baseUrl, token: childToken });
      const correction = buildResultManifest({
        manifestId: "man-correction-det-1",
        workId: workA,
        assignmentId: asgA,
        revisionOf: String(doc["manifestId"]),
        summary: "corrected summary",
        outputs: [
          {
            artifactId: String(docOutput["artifactId"]),
            mediaType: "text/markdown",
            size: reportBytes.length,
            digest: String(docOutput["digest"]),
            artifactUri: artifactUri(workA, asgA, String(docOutput["artifactId"])),
            createdBy: "workspace:gw.team-alpha/deterministic",
            createdAt: String(docOutput["createdAt"]),
          },
        ],
        createdAt: new Date().toISOString(),
      });
      const correctionDoc = await client.upload(correction.document, {
        mediaType: "application/json",
        workId: workA,
        assignmentId: asgA,
        retentionPolicy: "30d",
      });
      const correctionEvent = buildCompletedEvent(
        {
          executionId: "exec-correction",
          executionGeneration: 1,
          command: {
            specversion: "1.0",
            id: `corr-${randomUUID()}`,
            source: "urn:workspace:gw.team-alpha/deterministic",
            type: "org.facilitator.command.work.assign.v1",
            subject: `work/${workA}/assignment/${asgA}`,
            time: new Date().toISOString(),
            datacontenttype: "application/json",
            dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
            data: {},
          } as EnvelopeCommand,
        },
        { manifestId: "man-correction-det-1", workId: workA, assignmentId: asgA },
        {
          manifestRef: artifactUri(workA, asgA, correctionDoc.artifactId),
          digest: correction.digest,
        },
      );
      const reposted = await linkPost(
        harness.baseUrl,
        "/api/link/v1/events",
        childToken,
        correctionEvent,
      );
      expect(reposted.status).toBe(202);
      const revisions = await waitFor("revision 2 recorded, revision 1 superseded", async () => {
        const rows = await psql(
          harness.dbUrl,
          `SELECT revision || ':' || state FROM facilitator.result_manifests WHERE work_id = '${workA}' ORDER BY revision`,
        );
        return rows.join(",") === "1:superseded,2:recorded" ? rows : false;
      });
      expect(revisions).toHaveLength(2);
      const listed2 = await linkGet(harness.baseUrl, `/api/works/${workA}/manifests`, alice);
      const visible2 = listed2.body as Array<Record<string, unknown>>;
      expect(visible2).toHaveLength(2); // newest-revision-first
      expect(visible2[0]).toMatchObject({
        revision: 2,
        revisionOf: String(doc["manifestId"]),
        state: "recorded",
        digest: correction.digest,
      });
      expect(visible2[1]).toMatchObject({ revision: 1, state: "superseded" });

      // ── 3. steer mid-run: lands on the RUNNING execution, in the SERVED bytes ──
      await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        assignEnvelope({
          commandId: "e2e-det-2",
          workId: workB,
          assignmentId: asgB,
        }),
      );
      // Wait for the run to be live (progress row seen), THEN steer it.
      await waitFor("steer-run progress", async () => {
        const rows = await inboxRowsByType(harness.dbUrl, "org.facilitator.event.work.progress.v1");
        return rows.some((r) => r.payload.includes(asgB)) ? true : false;
      });
      await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        steerEnvelopeFor(workB, asgB, "focus on costs"),
      );
      await waitFor("steered run completes", async () => {
        const rows = await inboxRowsByType(
          harness.dbUrl,
          "org.facilitator.event.assignment.completed.v1",
        );
        return rows.find((r) => r.payload.includes(asgB)) ?? false;
      });
      const report = await waitFor("steer visible in the served report", async () => {
        const row = (
          await inboxRowsByType(harness.dbUrl, "org.facilitator.event.assignment.completed.v1")
        ).find((r) => r.payload.includes(asgB));
        if (row === undefined) return false;
        const data = (JSON.parse(row.payload) as { data: Record<string, unknown> }).data;
        const steerRef = String(data["resultManifestRef"]);
        const steerDocBytes = await fetchContent(
          `/api/artifacts/${steerRef.split("/").pop()}/content`,
          alice,
        );
        const steerDoc = JSON.parse(steerDocBytes.toString("utf8")) as Record<string, unknown>;
        if (computeManifestDigest(steerDoc) !== String(data["resultDigest"])) return false;
        const text = (
          await fetchContent(
            `/api/artifacts/${String((steerDoc["outputs"] as Record<string, unknown>[])[0]!["artifactId"])}/content`,
            alice,
          )
        ).toString("utf8");
        return text.includes("## Steering directives") &&
          /## Steering directives\n- \+\d+ms: focus on costs/.test(text)
          ? text
          : false;
      });
      expect(report).toContain("focus on costs");

      // ── 4. crash law: SIGKILL mid-run ⇒ ONE child-lost failed row ──
      const failedBefore = (
        await inboxRowsByType(harness.dbUrl, "org.facilitator.event.assignment.failed.v1")
      ).filter((r) => r.payload.includes("child-lost-execution")).length;
      expect(failedBefore).toBe(0);
      await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        assignEnvelope({
          commandId: "e2e-det-3",
          workId: "work-det-3",
          assignmentId: "asg-det-3",
        }),
      );
      await waitFor("crash-run progress", async () => {
        const rows = await inboxRowsByType(harness.dbUrl, "org.facilitator.event.work.progress.v1");
        return rows.some((r) => r.payload.includes("asg-det-3")) ? true : false;
      });
      worker.child.kill("SIGKILL"); // die mid-step-1 sleep: no terminal event
      worker = startWorker({
        childWorkspaceId: `${gw.gatewayId}/deterministic`, // SAME id: probe path
        socket: gw.socketPath ?? "",
        workDir,
        stepMs: 4000,
        kernelUrl: harness.baseUrl,
        token: childToken,
      });
      await worker.ready();
      const lost = await waitFor("probe settles the row lost", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        const binding = s.bindings.find((b) => b.assignmentId === "asg-det-3");
        return binding !== undefined && binding.state === "lost" ? s : false;
      });
      expect(lost.executions.lost).toBe(1);
      const failed = await waitFor("exactly one child-lost failed row", async () => {
        const rows = await inboxRowsByType(
          harness.dbUrl,
          "org.facilitator.event.assignment.failed.v1",
        );
        const lostRows = rows.filter((r) => r.payload.includes("child-lost-execution"));
        return lostRows.length === 1 ? lostRows[0]! : false;
      });
      const failedData = (JSON.parse(failed.payload) as { data: Record<string, unknown> }).data;
      expect(failedData["retryable"]).toBe(true);
      expect(failedData["reason"]).toBe("child-lost-execution");
    } finally {
      worker?.child.kill("SIGKILL");
      await gateway.kill();
    }
  }, 110_000);
});
