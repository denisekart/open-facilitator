// E2E worker tier (Plan 07 step 5): the REAL ofac-deterministic-worker
// binary against a standalone Gateway + a dedicated Facilitator. Proven
// end-to-end, observable in the kernel inbox + on disk:
// 1. boot → register → the union capability POST (ownerTeam rewritten);
// 2. seeded work.assign → 2 progress rows → completed row whose
//    resultManifest validates, whose file:// artifact exists on disk, and
//    whose sha256 digest matches the bytes;
// 3. steer mid-run lands on the RUNNING execution (visible inside the
//    report file under "## Steering directives");
// 4. SIGKILL mid-run → re-spawn → probe `unknown` ⇒ exactly ONE
//    child-lost-execution assignment.failed (06's crash law, real harness).
import { createHash } from "node:crypto";
import { spawn, type ChildProcess } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { validateAgainstSchemaId } from "@ofac/protocol";
import { describe, expect, it } from "vitest";
import {
  E2E,
  assignEnvelope,
  gatewayConfig,
  gatewayStatus,
  gatewayToken,
  inboxRowsByType,
  seedDelivery,
  startGateway,
  useFacilitator,
  waitFor,
  type Harness,
} from "../../../../../gateway/test/e2e/gateway-harness.js";
import { linkGet } from "../../../../../workspace-link/test/e2e/harness.js";

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
}): WorkerHandle {
  const dir = mkdtempSync(join(tmpdir(), "worker-e2e-"));
  const configPath = join(dir, "worker.yaml");
  writeFileSync(
    configPath,
    `apiVersion: facilitator.framework/v1
kind: HarnessAdapter
childWorkspaceId: ${options.childWorkspaceId}
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
    const gateway = startGateway(gw.configPath);
    let worker = startWorker({
      childWorkspaceId: `${gw.gatewayId}/deterministic`,
      socket: gw.socketPath ?? "",
      workDir,
      stepMs: 4000, // a wide steer/kill window per step
    });
    const dump = (label: string): string =>
      `${label}\nGATEWAY LOG:\n${gateway.log()}\nWORKER LOG:\n${worker.log()}\n`;
    const withDump = <T>(p: Promise<T>, label: string): Promise<T> => {
      let timer: NodeJS.Timeout;
      const guard = new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error(`stalled: ${label}\n${dump(label)}`)), 25_000);
      });
      return Promise.race([p, guard]).finally(() => clearTimeout(timer!));
    };
    try {
      // ── 1. boot + register + capability union (ownerTeam rewritten) ──
      const ready = await withDump(gateway.ready(), "awaiting gateway ready line");
      expect(ready.children).toBe(0); // listener boots empty: fleet modes POST nothing
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
      expect(caps.ownerTeam).toBe("default");
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
          workId: "work-det-1",
          assignmentId: "asg-det-1",
        }),
      );
      await waitFor("two progress rows", async () => {
        const rows = await inboxRowsByType(harness.dbUrl, "org.facilitator.event.work.progress.v1");
        return rows.filter((r) => r.payload.includes("asg-det-1")).length >= 2 ? true : false;
      });
      const completed = await waitFor("completed row lands", async () => {
        const rows = await inboxRowsByType(
          harness.dbUrl,
          "org.facilitator.event.assignment.completed.v1",
        );
        return rows.find((r) => r.payload.includes("asg-det-1")) ?? false;
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
      const output = (envelope.data["resultManifest"] as { outputs: Record<string, unknown>[] })
        .outputs[0] as Record<string, unknown>;
      const artifactPath = fileURLToPath(String(output["storageUrl"]));
      const bytes = readFileSync(artifactPath); // the file:// ref is REAL
      expect(output["digest"]).toBe(`sha256:${createHash("sha256").update(bytes).digest("hex")}`);

      // ── 3. steer mid-run: lands on the RUNNING execution, in the file ──
      await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        assignEnvelope({
          commandId: "e2e-det-2",
          workId: "work-det-2",
          assignmentId: "asg-det-2",
        }),
      );
      // Wait for the run to be live (progress row seen), THEN steer it.
      await waitFor("steer-run progress", async () => {
        const rows = await inboxRowsByType(harness.dbUrl, "org.facilitator.event.work.progress.v1");
        return rows.some((r) => r.payload.includes("asg-det-2")) ? true : false;
      });
      await seedDelivery(
        harness.baseUrl,
        gw.gatewayId,
        steerEnvelopeFor("work-det-2", "asg-det-2", "focus on costs"),
      );
      await waitFor("steered run completes", async () => {
        const rows = await inboxRowsByType(
          harness.dbUrl,
          "org.facilitator.event.assignment.completed.v1",
        );
        return rows.find((r) => r.payload.includes("asg-det-2")) ?? false;
      });
      const report = await waitFor("steer visible in the report", async () => {
        const row = (
          await inboxRowsByType(harness.dbUrl, "org.facilitator.event.assignment.completed.v1")
        ).find((r) => r.payload.includes("asg-det-2"));
        if (row === undefined) return false;
        const data = (JSON.parse(row.payload) as { data: Record<string, unknown> }).data;
        const url = String(
          (data["resultManifest"] as { outputs: Record<string, unknown>[] }).outputs[0]![
            "storageUrl"
          ],
        );
        const text = readFileSync(fileURLToPath(url), "utf8");
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
      worker.child.kill("SIGKILL");
      await gateway.kill();
    }
  }, 110_000);
});
