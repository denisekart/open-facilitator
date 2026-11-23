// E2E worker tier: the REAL ofac-cli-worker binary against a standalone
// Gateway + a dedicated Facilitator. The parity gate over the deterministic
// worker's tier:
// 1. boot → register (manifest DERIVED FROM THE PLAYBOOK) → the union
//    capability POST (ownerTeam rewritten);
// 2. seeded work.assign → 2 progress rows → completed row carrying
//    resultManifestRef + resultDigest; the manifest fetched back through
//    the kernel artifact path re-hashes to the event's digest, and the
//    served report artifact (analysis.report / text/markdown) contains the
//    objective — the deterministic worker's exact completion shape;
// 3. the publish leg: an assign of research.publish completes with a
//    research-publish.result output kind;
// 4. SIGKILL mid-run → re-spawn → probe `unknown` ⇒ exactly ONE
//    child-lost-execution assignment.failed (06's crash law, real harness).
import { randomUUID } from "node:crypto";
import { spawn, type ChildProcess } from "node:child_process";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { computeManifestDigest, validateAgainstSchemaId } from "@ofac/protocol";
import { describe, expect, it } from "vitest";
import {
  E2E,
  assignEnvelope,
  gatewayConfig,
  gatewayStatus,
  gatewaySession,
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

const WORKER_BIN = join(ROOT, "src/adapters/harnesses/cli/dist/bin/worker.js");
const PLAYBOOK_FILE = join(ROOT, "src/adapters/harnesses/cli/sample/research.playbook.yaml");

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
  kernelEndpoint: string;
  enrollSecret: string;
}): WorkerHandle {
  const dir = mkdtempSync(join(tmpdir(), "cli-worker-e2e-"));
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
playbookFile: ${PLAYBOOK_FILE}
workDir: ${options.workDir}
heartbeatSeconds: 1
`,
  );
  const child = spawn(process.execPath, [WORKER_BIN, "run", "--config", configPath], {
    stdio: ["ignore", "pipe", "pipe"],
    env: {
      ...process.env,
      OFAC_ADAPTER_LOG: "debug",
      OFAC_KERNEL_ENDPOINT: options.kernelEndpoint,
      OFAC_ENROLL_SECRET: options.enrollSecret,
      // The config file is the harness's truth: an OFAC_KERNEL_URL leaked
      // from the dev shell would silently override the scraped origin.
      OFAC_KERNEL_URL: options.kernelUrl,
    },
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
  capabilityId: string,
  objective: string,
  vars?: Record<string, string>,
): Record<string, unknown> {
  const base = assignEnvelope({ commandId, workId, assignmentId });
  const data = base["data"] as Record<string, unknown>;
  return {
    ...base,
    data: {
      ...data,
      capabilityId,
      inputs: { objective, sourceRefs: [], ...(vars !== undefined ? { vars } : {}) },
      expectedDeliverables:
        capabilityId === "research.publish"
          ? [{ kind: "research-publish.result", mediaType: "text/markdown" }]
          : data["expectedDeliverables"],
    },
  };
}

describe.skipIf(!E2E)("cli worker e2e", () => {
  const harness: Harness = useFacilitator();

  it("register → completed+digest → publish leg → kill → lost", async () => {
    const gw = await gatewayConfig({
      facilitatorUrl: harness.baseUrl,
      fleet: [],
      gatewayMode: "standalone",
      heartbeatSeconds: 2,
    });
    const workDir = mkdtempSync(join(tmpdir(), "cli-worker-e2e-work-"));
    const dump = (label: string): string =>
      `${label}\nGATEWAY LOG:\n${gateway.log()}\nWORKER LOG:\n${worker?.log() ?? "(worker not spawned yet)"}\n`;
    const withDump = <T>(p: Promise<T>, label: string): Promise<T> =>
      p.catch((e: unknown) => {
        throw new Error(`${String(e)}\n${dump(label)}`, { cause: e });
      });
    const fetchContent = async (path: string, token: string): Promise<Buffer> => {
      const r = await fetch(`${harness.baseUrl}${path}`, {
        headers: { authorization: `Bearer ${token}` },
      });
      if (!r.ok) throw new Error(`GET ${path} -> ${r.status}`);
      return Buffer.from(await r.arrayBuffer());
    };
    // Real works on a team-alpha PROJECT (the ledger resolves team scope via
    // work → project → team; a projectless work answers manifest-not-found).
    const projectRows = await psql(
      harness.dbUrl,
      `INSERT INTO facilitator.projects (project_id, team_id, name, config, created_at)
       VALUES (gen_random_uuid(), (SELECT team_id FROM facilitator.teams WHERE slug = 'team-alpha'),
               'e2e-cli-proj', '{}', now())
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
    const alice = await userToken("alice", "alice");
    const workA = await workIdOf("cli parity objective");
    const workP = await workIdOf("publish leg objective");
    const workK = await workIdOf("kill leg objective");
    // GUID assignment ids: the ledger's assignment_id is uuid.
    const asgA = randomUUID();
    const asgP = randomUUID();
    const asgK = randomUUID();
    const gateway = startGateway(gw.configPath);
    // Declared here, spawned only after the gateway's ready line (below).
    let worker: WorkerHandle | undefined;
    try {
      // ── 1. boot + register + capability union (ownerTeam rewritten) ──
      const ready = await withDump(gateway.ready(), "awaiting gateway ready line");
      expect(ready.children).toBe(0); // listener boots empty: fleet modes POST nothing
      // Spawn AFTER the gateway's ready line, never alongside it (the
      // empty-listener assertion must not race the child registration).
      worker = startWorker({
        childWorkspaceId: `${gw.gatewayId}/cli`,
        socket: String(gw.socketPath),
        workDir,
        kernelUrl: harness.baseUrl,
        token: "enroll-dev",
        kernelEndpoint: "cli-e2e-child",
        enrollSecret: "enroll-dev",
      });
      await withDump(worker.ready(), "awaiting worker ready line");
      const caps = await waitFor("capability union POST", async () => {
        const r = await linkGet(
          harness.baseUrl,
          "/api/link/v1/capabilities",
          await gatewaySession(gw.stateDir, harness.baseUrl),
        );
        if (r.status !== 200) return false;
        const m = r.body as {
          capabilities: { id: string; supportsSteering?: boolean }[];
          ownerTeam: string;
        };
        return m.capabilities.some((c) => c.id === "research.analyze") ? m : false;
      });
      expect(caps.ownerTeam).toBe("team-alpha");
      // The playbook-derived face: fire-per-step never claims steering.
      expect(
        caps.capabilities.find((c) => c.id === "research.analyze")?.supportsSteering,
      ).toBe(false);
      const online = await waitFor("worker connected", async () => {
        const s = await gatewayStatus(gw.diagnosticsPort);
        return s.children?.some((c) => c.state === "connected") ? s : false;
      });
      expect(online.children?.[0]?.state).toBe("connected");

      // ── 2. analyze leg: progress notes + completed + digest truth ──
      await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        assignEnvelopeFor("e2e-cli-1", workA, asgA, "research.analyze", "cli parity objective"),
      );
      const completed = await waitFor("completed row", async () => {
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
      const ref = String(envelope.data["resultManifestRef"]);
      const resultDigest = String(envelope.data["resultDigest"]);
      expect(ref.startsWith("artifact://")).toBe(true);
      const docBytes = await fetchContent(
        `/api/artifacts/${ref.split("/").pop()}/content`,
        alice,
      );
      const doc = JSON.parse(docBytes.toString("utf8")) as Record<string, unknown>;
      expect(computeManifestDigest(doc)).toBe(resultDigest);
      const docOutput = (doc["outputs"] as Record<string, unknown>[])[0]!;
      expect(docOutput["kind"]).toBe("analysis.report");
      expect(docOutput["mediaType"]).toBe("text/markdown");
      const reportBytes = await fetchContent(
        `/api/artifacts/${String(docOutput["artifactId"])}/content`,
        alice,
      );
      // The parity assertion: the served report carries the objective and
      // the deterministic conclusion line the old worker wrote.
      const report = reportBytes.toString("utf8");
      expect(report).toContain("cli parity objective");
      expect(report).toContain("Deterministic analysis complete.");
      // Two progress notes rode the run (analyze accepted + drafting).
      const progress = await inboxRowsByType(
        harness.dbUrl,
        "org.facilitator.event.work.progress.v1",
      );
      expect(progress.filter((r) => r.payload.includes(asgA)).length).toBeGreaterThanOrEqual(2);

      // ── 3. publish leg: the confirm-scoped flavor completes too ──
      await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        assignEnvelopeFor("e2e-cli-2", workP, asgP, "research.publish", "publish leg objective"),
      );
      const published = await waitFor("publish completed row", async () => {
        const rows = await inboxRowsByType(
          harness.dbUrl,
          "org.facilitator.event.assignment.completed.v1",
        );
        return rows.find((r) => r.payload.includes(asgP)) ?? false;
      }).catch((e: unknown) => {
        throw new Error(`${String(e)}\n${dump("no publish completed row")}`, { cause: e });
      });
      const pubData = (JSON.parse(published.payload) as { data: Record<string, unknown> }).data;
      const pubDoc = JSON.parse(
        (
          await fetchContent(
            `/api/artifacts/${String(pubData["resultManifestRef"]).split("/").pop()}/content`,
            alice,
          )
        ).toString("utf8"),
      ) as Record<string, unknown>;
      expect(computeManifestDigest(pubDoc)).toBe(String(pubData["resultDigest"]));
      expect(
        (pubDoc["outputs"] as Record<string, unknown>[])[0]?.["kind"],
      ).toBe("research-publish.result");

      // ── 4. crash law: SIGKILL mid-run ⇒ ONE child-lost failed row ──
      const lostBefore = (
        await inboxRowsByType(harness.dbUrl, "org.facilitator.event.assignment.failed.v1")
      ).filter((r) => r.payload.includes("child-lost-execution")).length;
      await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        assignEnvelopeFor("e2e-cli-3", workK, asgK, "research.analyze", "killed run objective", {
          holdMs: "30000",
        }),
      );
      // The assignment's holdMs var gives the kill a 30s window; the run
      // must be live (progress row seen) before the kill.
      await waitFor("kill-run progress", async () => {
        const rows = await inboxRowsByType(
          harness.dbUrl,
          "org.facilitator.event.work.progress.v1",
        );
        return rows.some((r) => r.payload.includes(asgK)) ? true : false;
      });
      worker.child.kill("SIGKILL"); // die mid-step: no terminal event
      const respawn = startWorker({
        childWorkspaceId: `${gw.gatewayId}/cli`, // SAME id: probe path
        socket: String(gw.socketPath),
        workDir,
        kernelUrl: harness.baseUrl,
        token: "enroll-dev",
        kernelEndpoint: "cli-e2e-child",
        enrollSecret: "enroll-dev",
      });
      try {
        await respawn.ready();
        const failed = await waitFor("child-lost failed row", async () => {
          const rows = await inboxRowsByType(
            harness.dbUrl,
            "org.facilitator.event.assignment.failed.v1",
          );
          return (
            rows.find((r) => r.payload.includes(asgK) && r.payload.includes("child-lost-execution")) ??
            false
          );
        }).catch((e: unknown) => {
          throw new Error(`${String(e)}\n${dump("no child-lost row")}`, { cause: e });
        });
        expect(failed).toBeTruthy();
        // Exactly one terminal event for the killed run: no completed row.
        const completedRows = await inboxRowsByType(
          harness.dbUrl,
          "org.facilitator.event.assignment.completed.v1",
        );
        expect(completedRows.some((r) => r.payload.includes(asgK))).toBe(false);
        const lostAfter = (
          await inboxRowsByType(harness.dbUrl, "org.facilitator.event.assignment.failed.v1")
        ).filter((r) => r.payload.includes("child-lost-execution")).length;
        expect(lostAfter).toBe(lostBefore + 1);
      } finally {
        respawn.child.kill("SIGTERM");
      }
    } finally {
      worker?.child.kill("SIGTERM");
      gateway.child.kill("SIGTERM");
    }
  });
});
