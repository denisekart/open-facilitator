// E2E tier: the full GitLab loop on a live kernel + Gateway + the REAL
// gitlab-connector binary + the gitlab adapter (in-process, adapter face)
// against the in-process fake GitLab. Deterministic scripted behavior — no
// LLM anywhere: the "worker" is the connector's own op table.
//
// S1  signed Issue-Hook fixture (work label) → kernel sink 202 → fan-out
//     command.provider.webhook.v1 → adapter normalizes → provider_events +
//     Work + binding + routing decision → funnel assigns vcs.issue.read to
//     the gateway child → connector reads the issue on the fake → completed
//     inbox row; the kernel's ack reply comes back as an "On it." note.
// S2  Note Hook on the bound issue → work_events provider.input.
// S3  seeded MR binding + Note Hook on the bot's MR → steer timeline row.
// S4  vcs.discussion.reply + vcs.discussion.resolve assigns execute against
//     a seeded discussion thread (reply lands, thread resolves).
// S5  ambiguity: first vcs.change_request.create loses its HTTP answer (504
//     after the row landed); the retry reconciles by READ — exactly ONE MR,
//     exactly ONE POST.
import { spawn, type ChildProcess } from "node:child_process";
import { randomUUID } from "node:crypto";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { createConsoleLogger } from "@ofac/workspace-link";
import { parseEnvConfig, run as runAdapter, type RunHandle } from "@ofac/gitlab-adapter";
import {
  E2E,
  assignEnvelope,
  freePort,
  gatewayConfig,
  inboxRowsByType,
  psql,
  seedDelivery,
  startGateway,
  useFacilitator,
  waitFor,
  type GatewayHandle,
  type GatewayHandleConfig,
  type Harness,
} from "../../../../gateway/test/e2e/gateway-harness.js";
import { startFakeGitLab, type FakeGitLab } from "./fake-gitlab.js";
import { issueLabeled, mentionOnIssue, mentionOnMr } from "../../gitlab/test/fixtures/payloads.js";

const ROOT = new URL("../../../../../", import.meta.url).pathname;
const CONNECTOR_BIN = join(
  ROOT,
  "src/adapters/communication/gitlab-connector/dist/bin/connector.js",
);

const REPO = "gitlab-org/gitlab-test";
const WEBHOOK_TOKEN = "s3cret-hook";

// Kernel config arrives as pure env (the section IS the provider): the
// harness spreads process.env into the spawned kernel, so module-scope
// mutation before useFacilitator's beforeAll is the injection point.
process.env["Providers__Gitlab__Enabled"] = "true";
process.env["Providers__Gitlab__EndpointId"] = "gitlab-adapter";
process.env["Providers__Gitlab__Tenant"] = "gitlab-e2e";
process.env["Providers__Gitlab__BotUserId"] = "facilitator-bot";
process.env["Providers__Gitlab__AssignRules__0__Label"] = "ofac:work";
process.env["Providers__Gitlab__AssignRules__0__CapabilityId"] = "vcs.issue.read";

// Fleet-honesty + first-match-wins: every op the e2e routes is a plain
// allow row; kernel-originated assigns carry no verifiable actor, so the
// rows carry an empty actorAnyOf (matches anything).
const POLICY_YAML = `
owner: team-alpha
policies:
  - capability: vcs.issue.read
    resources: ["*"]
    actorAnyOf: []
    effect: allow
  - capability: vcs.change_request.create
    resources: ["*"]
    actorAnyOf: []
    effect: allow
  - capability: vcs.discussion.reply
    resources: ["*"]
    actorAnyOf: []
    effect: allow
  - capability: vcs.discussion.resolve
    resources: ["*"]
    actorAnyOf: []
    effect: allow
`;

/** Set when the file-scoped harness boots (helpers close over it). */
let KERNEL: Harness = undefined as unknown as Harness;

function connectorReady(child: ChildProcess): Promise<string> {
  return new Promise((resolve, reject) => {
    let buffer = "";
    child.stdout?.on("data", (c: Buffer) => {
      buffer += c.toString();
      const line = buffer.split("\n")[0];
      if (line !== undefined && line.startsWith("connector ready")) resolve(line);
    });
    child.once("exit", (code) =>
      reject(new Error(`connector exited early: ${String(code)}\n${buffer}`)),
    );
  });
}

/** One verbatim system-hook delivery to the kernel sink (GitLab sends
 * X-Gitlab-Token + X-Request-Id; the sink rides them untouched). */
async function postHook(
  body: unknown,
  gitlabEvent: string,
): Promise<{ status: number; commandId: string }> {
  const response = await fetch(`${KERNEL.baseUrl}/api/ingress/webhooks/gitlab`, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      "x-gitlab-token": WEBHOOK_TOKEN,
      "x-gitlab-event": gitlabEvent,
      "x-request-id": randomUUID(),
    },
    body: JSON.stringify(body),
  });
  const text = await response.text();
  const commandId = text.includes("commandId")
    ? String((JSON.parse(text) as { commandId: string }).commandId)
    : "";
  return { status: response.status, commandId };
}

async function workEvents(workId: string, kind: string): Promise<boolean> {
  const rows = await psql(
    KERNEL.dbUrl,
    `SELECT 1 FROM facilitator.work_events WHERE work_id = '${workId}' AND kind = '${kind}' LIMIT 1`,
  );
  return rows.length > 0;
}

async function bindingWork(root: string): Promise<string | undefined> {
  const rows = await psql(
    KERNEL.dbUrl,
    `SELECT work_id FROM facilitator.communication_bindings WHERE provider = 'gitlab' AND channel = '${REPO}' AND root_message_id = '${root}' LIMIT 1`,
  );
  return rows[0];
}

describe.skipIf(!E2E)(
  "gitlab provider loop e2e (kernel sink → adapter → funnel → connector)",
  () => {
    KERNEL = useFacilitator();
    let gitlab: FakeGitLab;
    let gw: GatewayHandleConfig;
    let handle: GatewayHandle;
    let connector: ChildProcess;
    let adapter: RunHandle;
    let stateDir: string;
    let workId = "";

    beforeAll(async () => {
      gitlab = await startFakeGitLab();
      gw = await gatewayConfig({
        facilitatorUrl: KERNEL.baseUrl,
        policy: POLICY_YAML,
        fleet: [{ name: "gitlab" }],
      });
      handle = startGateway(gw.configPath);
      await handle.ready();

      stateDir = mkdtempSync(join(tmpdir(), "gl-e2e-"));
      const configPath = join(stateDir, "connector.yaml");
      writeFileSync(
        configPath,
        `childWorkspaceId: gw.team-alpha/gitlab
team: team-alpha
kernelUrl: ${KERNEL.baseUrl}
statePath: ${join(stateDir, "actions.sqlite3")}
transport:
  socket:
    path: ${gw.socketPath as string}
heartbeatSeconds: 5
`,
      );
      connector = spawn("node", [CONNECTOR_BIN, "run", "--config", configPath], {
        env: {
          ...process.env,
          GITLAB_URL: gitlab.url,
          GITLAB_TOKEN: "bot-pat",
          GITLAB_ENABLE_ISSUES: "true",
          GITLAB_ENABLE_VCS: "true",
          GITLAB_ENABLE_MRS: "true",
          GITLAB_ENABLE_PIPELINE: "true",
          OFAC_CHILD_SECRET: "tok",
        },
        stdio: ["ignore", "pipe", "pipe"],
      });
      await connectorReady(connector);

      // The adapter rides the provider-core adapter face in-process (the same
      // code the bin runs); its webhook edge is the kernel fan-out, not a
      // listening server.
      adapter = await runAdapter(
        parseEnvConfig({
          OFAC_KERNEL_URL: KERNEL.baseUrl,
          OFAC_ENROLL_SECRET: "enroll-dev",
          OFAC_ENDPOINT_ID: "gitlab-adapter",
          OFAC_TEAM: "team-alpha",
          OFAC_PROVIDER_INSTANCE_ID: "e2e-gitlab",
          GITLAB_URL: gitlab.url,
          GITLAB_TOKEN: "bot-pat",
          GITLAB_WEBHOOK_TOKEN: WEBHOOK_TOKEN,
          GITLAB_BOT_USER: "facilitator-bot",
          GITLAB_WORK_LABELS: "ofac:work",
          OFAC_STATE_PATH: join(stateDir, "adapter.sqlite3"),
          PORT: String(await freePort()),
          OFAC_ADAPTER_LOG: "warn",
        }),
        createConsoleLogger({ level: "warn" }),
      );
      await adapter.ready;
    }, 180_000);

    afterAll(async () => {
      connector?.kill("SIGKILL");
      await adapter?.shutdown().catch(() => undefined);
      await handle?.kill();
      await gitlab?.close();
      rmSync(stateDir, { recursive: true, force: true });
    });

    it("S1: labeled issue becomes a Work, routes vcs.issue.read, acks on GitLab", async () => {
      const hook = await postHook(issueLabeled, "Issue Hook");
      expect(hook.status).toBe(202);
      expect(hook.commandId).toMatch(/^webhook:gitlab:[0-9a-f]{64}$/);

      // The fan-out command row rode to the adapter (kernel outbox, verbatim
      // headers) and the normalizer published issue.v1 → provider_events.
      await waitFor(
        "provider_events row for the delivery",
        () =>
          psql(
            KERNEL.dbUrl,
            `SELECT 1 FROM facilitator.provider_events WHERE provider = 'gitlab' AND provider_delivery_id IS NOT NULL LIMIT 1`,
          ).then((rows) => (rows.length > 0 ? true : false)),
        60_000,
      );

      // Work + binding + the routing decision to the gateway fleet.
      workId =
        (await waitFor(
          "communication binding for the labeled issue",
          () => bindingWork("23"),
          60_000,
        )) ?? "";
      expect(workId).not.toBe("");
      await waitFor(
        "routing decision routed to gw.team-alpha",
        () =>
          psql(
            KERNEL.dbUrl,
            `SELECT 1 FROM facilitator.routing_decisions WHERE work_id = '${workId}' AND status = 'routed' AND chosen_endpoint_id = 'gw.team-alpha' LIMIT 1`,
          ).then((rows) => (rows.length > 0 ? true : false)),
        60_000,
      );

      // The connector child executed vcs.issue.read on the fake and settled
      // with its own completed event (the scripted worker's answer).
      const completed = await waitFor(
        "connector completed event",
        async () => {
          const rows = await inboxRowsByType(
            KERNEL.dbUrl,
            "org.facilitator.event.assignment.completed.v1",
          );
          return (
            rows.find((r) => r.payload.includes(`vcs.issue.read -> issue ${REPO}#23`)) ?? false
          );
        },
        60_000,
      );
      expect(completed.state).toBe("processed");

      // Ack reply law: the kernel answered the issue with a bot note through
      // the adapter's reply handler.
      await waitFor(
        "ack note on the fake issue",
        () =>
          gitlab.notes.some((n) => !n.mr && n.iid === 23 && n.body.includes("On it."))
            ? true
            : false,
        30_000,
      );
    }, 120_000);

    it("S2: note on the bound issue lands as provider.input", async () => {
      await postHook(mentionOnIssue, "Note Hook");
      await waitFor(
        "provider.input timeline row with the note text",
        () =>
          psql(
            KERNEL.dbUrl,
            `SELECT 1 FROM facilitator.work_events WHERE work_id = '${workId}' AND kind = 'provider.input' AND detail::text LIKE '%please summarize%' LIMIT 1`,
          ).then((rows) => (rows.length > 0 ? true : false)),
        60_000,
      );
    }, 90_000);

    it("S3: human review note on the bot's MR steers the work", async () => {
      // The PR-number binding cannot exist at manifest time in this leg (no
      // worker opened the MR), so the leg seeds it exactly like verify-full's
      // forgejo review-steer leg does.
      await psql(
        KERNEL.dbUrl,
        `INSERT INTO facilitator.communication_bindings
         (binding_id, provider, channel, root_message_id, work_id, idempotency_key, created_at)
       VALUES (gen_random_uuid(), 'gitlab', '${REPO}', '16', '${workId}', 'e2e-pr-link-16', now())`,
      );
      await postHook(mentionOnMr, "Note Hook");
      await waitFor(
        "steer timeline row for the review note",
        async () => await workEvents(workId, "steer"),
        60_000,
      );
    }, 90_000);

    it("S4: discussion reply and resolve execute against the fake", async () => {
      gitlab.seedDiscussion(REPO, 16, "d1");
      const mk = (capabilityId: string, extra: Record<string, unknown>) => {
        const commandId = `cmd-${randomUUID().slice(0, 8)}`;
        const env = assignEnvelope({
          commandId,
          workId: randomUUID(),
          assignmentId: `asg-${commandId}`,
        });
        return {
          ...env,
          data: {
            ...(env.data as Record<string, unknown>),
            capabilityId,
            inputs: {
              repo: REPO,
              issue: 16,
              discussionId: "d1",
              body: "reworked as asked",
              resource: `repository:${REPO}`,
              ...extra,
            },
          },
        };
      };

      const reply = mk("vcs.discussion.reply", {});
      const replyCmd = String((reply.data as Record<string, unknown>)["assignmentId"]);
      await seedDelivery(KERNEL.baseUrl, "gw.team-alpha", reply);
      await waitFor(
        "discussion reply completed",
        async () => {
          const rows = await inboxRowsByType(
            KERNEL.dbUrl,
            "org.facilitator.event.assignment.completed.v1",
          );
          return rows.find((r) => r.payload.includes(replyCmd)) ?? false;
        },
        60_000,
      );
      const thread = gitlab.discussions.get(`${REPO}!16:d1`);
      expect(thread?.replies).toContain("reworked as asked");

      const resolve = mk("vcs.discussion.resolve", {});
      const resolveCmd = String((resolve.data as Record<string, unknown>)["assignmentId"]);
      await seedDelivery(KERNEL.baseUrl, "gw.team-alpha", resolve);
      await waitFor(
        "discussion resolve completed",
        async () => {
          const rows = await inboxRowsByType(
            KERNEL.dbUrl,
            "org.facilitator.event.assignment.completed.v1",
          );
          return rows.find((r) => r.payload.includes(resolveCmd)) ?? false;
        },
        60_000,
      );
      expect(gitlab.discussions.get(`${REPO}!16:d1`)?.resolved).toBe(true);
      expect(gitlab.discussionPosts).toBe(1);
      expect(gitlab.discussionPuts).toBe(1);
    }, 120_000);

    it("S5: lost MR-create answer reconciles on retry — one MR, one POST", async () => {
      const inputs = {
        repo: REPO,
        head: "feature/booking-validation",
        base: "main",
        title: "Validate bookings",
        body: "generated",
        resource: `repository:${REPO}`,
      };
      const mkAssign = () => {
        const commandId = `cmd-${randomUUID().slice(0, 8)}`;
        const env = assignEnvelope({
          commandId,
          workId: randomUUID(),
          assignmentId: `asg-${commandId}`,
        });
        return {
          cmd: commandId,
          asg: `asg-${commandId}`,
          envelope: {
            ...env,
            data: {
              ...(env.data as Record<string, unknown>),
              capabilityId: "vcs.change_request.create",
              inputs,
            },
          },
        };
      };

      // First attempt: the fake creates the row, then loses the answer (504).
      gitlab.loseNextMrResponse = true;
      const first = mkAssign();
      await seedDelivery(KERNEL.baseUrl, "gw.team-alpha", first.envelope);
      await waitFor(
        "first attempt settled (failed event)",
        async () => {
          const failed = await inboxRowsByType(
            KERNEL.dbUrl,
            "org.facilitator.event.assignment.failed.v1",
          );
          return failed.find((r) => r.payload.includes(first.asg)) ?? false;
        },
        60_000,
      );
      expect(gitlab.mrs).toHaveLength(1);

      // Retry with fresh ids: read-before-fire finds the open MR, settles it,
      // and NEVER re-POSTs.
      const second = mkAssign();
      await seedDelivery(KERNEL.baseUrl, "gw.team-alpha", second.envelope);
      await waitFor(
        "retry completed via reconcile",
        async () => {
          const rows = await inboxRowsByType(
            KERNEL.dbUrl,
            "org.facilitator.event.assignment.completed.v1",
          );
          return rows.find((r) => r.payload.includes(second.asg)) ?? false;
        },
        60_000,
      );
      expect(gitlab.mrs).toHaveLength(1);
      expect(gitlab.mrPosts).toBe(1);
    }, 120_000);
  },
);
