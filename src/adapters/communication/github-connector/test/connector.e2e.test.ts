// E2E GitHub tier (plan §2A): the REAL github-connector binary + the adapter
// (in-process, same code the bin runs) + a scripted deterministic worker
// (zero LLM, test/e2e-local/worker-e2e.mjs) as fleet children of a live
// standalone Gateway + a dedicated Facilitator booted WITH the
// Providers:Github section, against the in-process fake GitHub. Proves the
// loop end-to-end, observable in the kernel:
//   Leg A: signed `issues` labeled fixture (ofac:work) → the kernel sink →
//     Work row + routing decision (routed) → scripted worker completes with
//     prRequest → connector opens EXACTLY ONE PR on the fake whose head
//     branch derives from the assignment marker; the fake fires the real
//     pull_request webhook → the head-branch resolver binds the marker to
//     the minted PR number (communication_bindings row).
//   Leg B: signed `pull_request_review` (@bot) on the bot's PR → steer →
//     re-dispatch delivers the directive to the scripted worker → the
//     folded "steer incorporated" comment lands on the fake via
//     vcs.change_request.comment.
//   Leg C: seeded assign rides vcs.discussion.reply → the connector posts a
//     review-thread reply with in_reply_to on the fake; the completed
//     event settles the inbox; the connector_actions row settled.
//   Ambiguity (killed createPR response) is covered at the unit tier
//   (ops.test.ts) against the same fake's loseNextPrResponse.
import { spawn, type ChildProcess } from "node:child_process";
import { randomUUID } from "node:crypto";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import {
  E2E,
  PG_URL,
  assignEnvelope,
  gatewayConfig,
  inboxRowsByType,
  psql,
  seedDelivery,
  startGateway,
  waitFor,
  type GatewayHandle,
  type GatewayHandleConfig,
} from "../../../../gateway/test/e2e/gateway-harness.js";
import { linkPost } from "../../../../workspace-link/test/e2e/harness.js";
import { startFakeGithub, type FakeGithub } from "./fake-github.js";
import {
  WEBHOOK_SECRET,
  issuesLabeled,
  pullRequestReviewSubmitted,
  sign,
} from "../../github/test/fixtures/payloads.js";
import { parseEnvConfig, run as runAdapter, type RunHandle } from "@ofac/github-adapter";

const ROOT = new URL("../../../../../", import.meta.url).pathname;
const CONNECTOR_BIN = join(
  ROOT,
  "src/adapters/communication/github-connector/dist/bin/connector.js",
);
const WORKER_SCRIPT = join(
  ROOT,
  "src/adapters/communication/github-connector/test/e2e-local/worker-e2e.mjs",
);
const KERNEL_DLL = join(ROOT, "src/facilitator/bin/Release/net10.0/Facilitator.dll");

const REPO = "octocat/Hello-World";

interface Kernel {
  baseUrl: string;
  dbUrl: string;
  stop(): Promise<void>;
}

/** Boot a dedicated Facilitator WITH the Providers:Github section (the
 *  shared useFacilitator only forwards Link__* keys, so this tier owns its
 *  server: harness idiom — CREATE DATABASE over psql with the admin-host
 *  rewrite, spawn, scrape the listening URL from stdout). */
async function bootKernel(siteUrl: string): Promise<Kernel> {
  const dbName = `e2e_gh_${Math.random().toString(36).slice(2, 10)}`;
  const perFile = new URL(PG_URL);
  perFile.pathname = `/${dbName}`;
  perFile.search = "";
  const dbUrl = perFile.toString();
  const adminBase = process.env["PG_ADMIN_URL"] ?? PG_URL;
  // CREATE runs against the ADMIN db (the per-file db does not exist yet);
  // psql() rewrites the host to the admin endpoint either way.
  await psql(adminBase, `CREATE DATABASE ${dbName}`);

  const parts = new URL(PG_URL);
  const server = spawn("dotnet", [KERNEL_DLL], {
    cwd: join(ROOT, "src/facilitator"),
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: "Development",
      ASPNETCORE_URLS: "http://127.0.0.1:0",
      ConnectionStrings__Facilitator: `Host=${parts.hostname};Port=${String(parts.port || 5432)};Database=${dbName};Username=${parts.username || "postgres"};Password=${decodeURIComponent(parts.password || "")}`,
      DOTNET_ENVIRONMENT: "Development",
      Identity__Oidc__Authority: `${process.env["KEYCLOAK_URL"] ?? ""}/realms/facilitator`,
      Identity__Oidc__Audience: "facilitator-api",
      Identity__Oidc__RequireHttps: "false",
      Providers__Github__Enabled: "true",
      Providers__Github__EndpointId: "github-adapter",
      Providers__Github__Tenant: "127.0.0.1",
      Providers__Github__SiteUrl: siteUrl,
      Providers__Github__BotUserId: "ofac-bot",
      Providers__Github__AssignEndpointId: "gw.team-alpha",
      Providers__Github__AssignCapabilityId: "research.analyze",
      Providers__Github__PrRequestCapability: "research.analyze",
      Providers__Github__AssignRules__0__Label: "ofac:work",
      Providers__Github__AssignRules__0__CapabilityId: "research.analyze",
      Providers__Github__AssignRules__0__EndpointId: "gw.team-alpha",
    },
  });
  let buffer = "";
  // The wait is the event itself (the listening line on stdout); the timer
  // is a boot-dead failure guard only.
  const url = await new Promise<string>((resolve, reject) => {
    const onData = (chunk: Buffer): void => {
      buffer += chunk.toString();
      const m = /Now listening on: (http:\/\/127\.0\.0\.1:\d+)/.exec(buffer);
      if (m?.[1] !== undefined) resolve(m[1]);
    };
    server.stdout?.on("data", onData);
    server.stderr?.on("data", onData);
    server.once("exit", (code) =>
      reject(new Error(`kernel exited early: ${String(code)}\n${buffer}`)),
    );
    setTimeout(() => reject(new Error(`kernel never listened:\n${buffer}`)), 90_000).unref();
  });
  return {
    baseUrl: url,
    dbUrl,
    async stop() {
      server.kill("SIGTERM");
      await Promise.race([
        new Promise((resolve) => server.once("exit", resolve)),
        new Promise((resolve) => setTimeout(resolve, 8_000).unref()),
      ]);
      await psql(adminBase, `DROP DATABASE ${dbName} WITH (FORCE)`).catch(() => []);
    },
  };
}

function readyLine(child: ChildProcess, prefix: string, label: string): Promise<string> {
  return new Promise((resolve, reject) => {
    let buffer = "";
    const onData = (chunk: Buffer): void => {
      buffer += chunk.toString();
      const found = buffer.split("\n").find((l) => l.startsWith(prefix));
      if (found !== undefined) resolve(found);
    };
    child.stdout?.on("data", onData);
    child.stderr?.on("data", onData);
    child.once("exit", (code) =>
      reject(new Error(`${label} exited early: ${String(code)}\n${buffer}`)),
    );
  });
}

async function postWebhook(
  baseUrl: string,
  event: string,
  deliveryId: string,
  payload: unknown,
): Promise<number> {
  // ofac_nonce: the sink dedupes by body hash, so each POST is unique bytes.
  const body = JSON.stringify({ ...(payload as object), ofac_nonce: randomUUID() });
  const response = await fetch(`${baseUrl}/api/ingress/webhooks/github`, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "x-github-event": event,
      "x-github-delivery": deliveryId,
      "x-hub-signature-256": sign(body),
    },
    body,
  });
  return response.status;
}

async function rows(dbUrl: string, sql: string): Promise<string[][]> {
  return (await psql(dbUrl, sql)).map((line) => line.split("|"));
}

async function hasRow(dbUrl: string, sql: string): Promise<boolean> {
  return (await rows(dbUrl, sql)).length > 0;
}

describe.skipIf(!E2E)("github provider e2e", () => {
  let kernel: Kernel;
  let github: FakeGithub;
  let gw: GatewayHandleConfig;
  let gateway: GatewayHandle;
  let connector: ChildProcess;
  let worker: ChildProcess;
  let adapter: RunHandle;
  const stateDir = mkdtempSync(join(tmpdir(), "ghe2e-"));

  beforeAll(async () => {
    kernel = await bootKernel("http://127.0.0.1:9301");
    github = await startFakeGithub({
      webhook: { url: `${kernel.baseUrl}/api/ingress/webhooks/github`, secret: WEBHOOK_SECRET },
    });
    gw = await gatewayConfig({
      facilitatorUrl: kernel.baseUrl,
      fleet: [{ name: "github" }, { name: "coder" }],
      gatewayMode: "standalone",
    });
    gateway = startGateway(gw.configPath);
    await gateway.ready();

    // The worker's kernel credential (artifact upload) is a child enrollment;
    // its gateway credential is the fleet token.
    const enroll = await linkPost(kernel.baseUrl, "/api/link/v1/enroll", "enroll-dev", {
      token: "enroll-dev",
      endpointId: "coder-e2e",
      instanceId: "e2e-scripted",
      trustMode: "isolated-executor",
      team: "team-alpha",
      role: "child",
    });
    if (enroll.status !== 201) throw new Error(`worker enroll failed: ${String(enroll.status)}`);
    const workerKernelToken = String((enroll.body as { linkToken: string }).linkToken);

    const connectorConfig = join(stateDir, "connector.yaml");
    writeFileSync(
      connectorConfig,
      `childWorkspaceId: ${gw.gatewayId}/github
team: team-alpha
kernelUrl: ${kernel.baseUrl}
statePath: ${join(stateDir, "actions.sqlite3")}
transport:
  socket:
    path: ${gw.socketPath as string}
heartbeatSeconds: 5
`,
    );
    connector = spawn("node", [CONNECTOR_BIN, "run", "--config", connectorConfig], {
      env: {
        ...process.env,
        GITHUB_URL: github.url,
        GITHUB_TOKEN: "bot-pat",
        GITHUB_ENABLE_ISSUES: "true",
        GITHUB_ENABLE_VCS: "true",
        GITHUB_ENABLE_MRS: "true",
        GITHUB_ENABLE_PIPELINE: "true",
        OFAC_CHILD_SECRET: "tok",
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    await readyLine(connector, "connector ready", "connector");

    const workerConfig = join(stateDir, "worker.json");
    writeFileSync(
      workerConfig,
      JSON.stringify({
        childWorkspaceId: `${gw.gatewayId}/coder`,
        token: "tok",
        kernelUrl: kernel.baseUrl,
        kernelToken: workerKernelToken,
        socket: gw.socketPath,
      }),
    );
    worker = spawn("node", [WORKER_SCRIPT, "--config", workerConfig], {
      stdio: ["ignore", "pipe", "pipe"],
    });
    await readyLine(worker, "worker ready", "worker");

    adapter = await runAdapter(
      parseEnvConfig({
        OFAC_KERNEL_URL: kernel.baseUrl,
        OFAC_ENROLL_SECRET: "enroll-dev",
        OFAC_ENDPOINT_ID: "github-adapter",
        OFAC_TEAM: "team-alpha",
        GITHUB_URL: github.url,
        GITHUB_TOKEN: "bot-pat",
        GITHUB_WEBHOOK_SECRET: WEBHOOK_SECRET,
        GITHUB_BOT_USER: "ofac-bot",
        OFAC_STATE_PATH: join(stateDir, "adapter.sqlite3"),
        OFAC_HEALTH_PORT: "18068",
      }),
      {
        debug: () => undefined,
        info: () => undefined,
        warn: () => undefined,
        error: () => undefined,
      },
    );
    await adapter.ready;
  }, 180_000);

  afterAll(async () => {
    connector?.kill("SIGKILL");
    worker?.kill("SIGKILL");
    await adapter?.shutdown().catch(() => undefined);
    await gateway?.kill().catch(() => undefined);
    await github?.close().catch(() => undefined);
    await kernel?.stop().catch(() => undefined);
    rmSync(stateDir, { recursive: true, force: true });
  });

  it("labeled issue → Work → routed → worker prRequest → one PR, marker bound", async () => {
    const status = await postWebhook(kernel.baseUrl, "issues", `e2e-issues-${randomUUID()}`, {
      ...issuesLabeled,
      issue: {
        ...(issuesLabeled.issue as Record<string, unknown>),
        title: "Summarize the release notes",
        body: "Summarize the release notes.",
      },
    });
    expect(status).toBe(202);

    const workRows = await waitFor("work row for the labeled issue", async () => {
      const hit = await rows(
        kernel.dbUrl,
        "SELECT work_id::text FROM facilitator.works WHERE objective LIKE '%Summarize the release notes%'",
      );
      return hit.length > 0 ? hit : false;
    });
    const workId = workRows[0]?.[0];
    if (workId === undefined) throw new Error("no work row");

    await waitFor("routing decision routed", async () =>
      hasRow(
        kernel.dbUrl,
        `SELECT 1 FROM facilitator.routing_decisions WHERE work_id = '${workId}' AND status = 'routed'`,
      ).then(Boolean),
    );

    // The connector executed BOTH manifest legs: the worker's ack comment
    // and exactly one PR whose head branch derives from the assignment id.
    await waitFor("worker ack comment on the fake", async () =>
      github.comments.some((c) => c.body.includes("On it — work started")) ? true : false,
    );
    const pr = await waitFor(
      "PR created on the fake",
      async () => github.prs.find((p) => p.head.ref.startsWith("ofac/")) ?? false,
    );
    expect(github.prPosts).toBe(1);
    expect(pr.base).toBe("main");

    // The fake's real pull_request webhook resolved the pending head-branch
    // marker: a PR-number-keyed binding now exists.
    await waitFor("head-branch marker bound to the PR number", async () =>
      hasRow(
        kernel.dbUrl,
        `SELECT 1 FROM facilitator.communication_bindings WHERE provider = 'github' AND channel = '${REPO}' AND root_message_id = '${String(pr.number)}'`,
      ).then(Boolean),
    );
  }, 120_000);

  it("human review @bot on the bot PR steers the scripted worker", async () => {
    const review = structuredClone(pullRequestReviewSubmitted) as Record<string, unknown>;
    review["number"] = 1;
    (review["pull_request"] as Record<string, unknown>)["number"] = 1;
    (review["review"] as Record<string, unknown>)["body"] =
      "Please sort the table by risk @ofac-bot";
    (review["sender"] as Record<string, unknown>)["login"] = "reviewer-rhea";
    expect(
      await postWebhook(kernel.baseUrl, "pull_request_review", `e2e-rev-${randomUUID()}`, review),
    ).toBe(202);

    await waitFor("steer recorded on the work timeline", async () =>
      hasRow(kernel.dbUrl, "SELECT 1 FROM facilitator.work_events WHERE kind = 'steer'").then(
        Boolean,
      ),
    );
    // The re-dispatch delivered the directive to the scripted worker, whose
    // folded commentRequest leg came back through vcs.change_request.comment.
    await waitFor("steer-incorporated comment on the fake", async () =>
      github.comments.some((c) => c.body.includes("steer incorporated")) ? true : false,
    );
  }, 120_000);

  it("seeded vcs.discussion.reply assign posts an in_reply_to on the fake", async () => {
    const commandId = `cmd-${randomUUID().slice(0, 8)}`;
    const env = assignEnvelope({
      commandId,
      workId: randomUUID(),
      assignmentId: `asg-${commandId}`,
    });
    const assign = {
      ...env,
      data: {
        ...(env.data as Record<string, unknown>),
        capabilityId: "vcs.discussion.reply",
        inputs: {
          repo: REPO,
          issue: "1",
          commentId: "55",
          body: "Fixed in the follow-up push.",
        },
      },
    };
    await seedDelivery(kernel.baseUrl, "gw.team-alpha", assign);

    const reply = await waitFor(
      "review-thread reply recorded on the fake",
      async () => github.reviewComments.find((c) => c.in_reply_to_id === 55) ?? false,
    );
    expect(reply.body).toContain("Fixed in the follow-up push.");

    await waitFor("connector completed landed", async () => {
      const done = await inboxRowsByType(
        kernel.dbUrl,
        "org.facilitator.event.assignment.completed.v1",
      );
      return done.some((r) => r.payload.includes(`asg-${commandId}`)) ? true : false;
    });

    // The connector's own ledger row settled with the provider object id.
    const db = new DatabaseSync(join(stateDir, "actions.sqlite3"), { readOnly: true });
    const action = db
      .prepare("SELECT op, state, provider_object_id FROM connector_actions WHERE command_id = ?")
      .get(commandId) as { op: string; state: string; provider_object_id: string } | undefined;
    db.close();
    expect(action?.op).toBe("vcs.discussion.reply");
    expect(action?.state).toBe("executed");
    expect(action?.provider_object_id).toMatch(/^reply \d+$/);
  }, 120_000);
});
