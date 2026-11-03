// The connector config fail-closed law (shared skeleton), the action-ledger
// replay/conflict law, the flag-filtered manifest, and the webhook fan-out
// receiver's decode/outcome law.
import { createHmac } from "node:crypto";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import type { EnvelopeCommand } from "@ofac/protocol";
import {
  ActionLedger,
  ActionLedgerConflictError,
  ConnectorConfigError,
  connectorManifest,
  parseSkeleton,
  payloadSha256,
  readYamlRoot,
  rejectUnknown,
} from "../src/index.js";
import {
  createWebhookReceiver,
  decodeWebhookCommand,
  verifyHmacSha256,
  verifyTokenToken,
} from "../src/node.js";

function skeleton(yaml: string, env: Record<string, string | undefined> = {}) {
  const dir = mkdtempSync(join(tmpdir(), "provider-core-"));
  const file = join(dir, "connector.yaml");
  writeFileSync(file, yaml);
  const { root, baseDir } = readYamlRoot(file);
  return parseSkeleton(root, { baseDir, env }, "connector.sqlite3");
}

const BASE_YAML = `
childWorkspaceId: gw-1/forge
team: team-alpha
transport:
  tcp:
    host: 127.0.0.1
    port: 9999
`;

describe("config law", () => {
  it("parses the skeleton and resolves relative state paths", () => {
    const config = skeleton(BASE_YAML);
    expect(config.childWorkspaceId).toBe("gw-1/forge");
    expect(config.transport).toEqual({ host: "127.0.0.1", port: 9999 });
    expect(config.statePath).toMatch(/connector\.sqlite3$/);
    expect(config.kernelUrl).toBe("http://127.0.0.1:8080");
    expect(config.restartBackoffMs.length).toBeGreaterThan(0);
  });

  it("env overlay wins: child secret, kernel url", () => {
    const config = skeleton(BASE_YAML, {
      OFAC_CHILD_SECRET: "fleet-secret",
      OFAC_KERNEL_URL: "http://kernel:8080",
    });
    expect(config.token).toBe("fleet-secret");
    expect(config.kernelUrl).toBe("http://kernel:8080");
  });

  it("rejects unknown keys by name", () => {
    const dir = mkdtempSync(join(tmpdir(), "provider-core-"));
    const file = join(dir, "c.yaml");
    writeFileSync(file, `${BASE_YAML}surprise: 1\n`);
    const { root, baseDir } = readYamlRoot(file);
    try {
      rejectUnknown(root, ["childWorkspaceId", "team", "transport"], "file");
      throw new Error("should have thrown");
    } catch (error) {
      expect(error).toBeInstanceOf(ConnectorConfigError);
      expect((error as Error).message).toContain("file.surprise");
    }
    expect(baseDir).toBeTruthy();
  });

  it("transport: exactly one of socket/tcp", () => {
    expect(() =>
      skeleton(
        `childWorkspaceId: g/c\nteam: t\ntransport:\n  socket:\n    path: /tmp/s\n  tcp:\n    port: 1\n`,
      ),
    ).toThrow(/mutually exclusive/);
    expect(() => skeleton(`childWorkspaceId: g/c\nteam: t\ntransport: {}\n`)).toThrow(
      /exactly one of socket\/tcp/,
    );
  });
});

describe("action ledger", () => {
  const open = (): ActionLedger =>
    new ActionLedger(join(mkdtempSync(join(tmpdir(), "ledger-")), "a.sqlite3"));

  it("replays an executed intent with the recorded object id", () => {
    const ledger = open();
    const sha = payloadSha256({ repo: "a/b", issue: 1 });
    ledger.insert("cmd-1", "vcs.issue.comment", sha);
    expect(ledger.find("cmd-1")?.state).toBe("pending");
    ledger.settle("cmd-1", "comment 42");
    const row = ledger.find("cmd-1");
    expect(row).toMatchObject({ state: "executed", providerObjectId: "comment 42" });
    ledger.close();
  });

  it("a same-commandId different-payload is a loud conflict", () => {
    const ledger = open();
    ledger.insert("cmd-1", "vcs.issue.comment", payloadSha256({ a: 1 }));
    expect(() => ledger.insert("cmd-1", "vcs.issue.comment", payloadSha256({ a: 2 }))).toThrow(
      ActionLedgerConflictError,
    );
    ledger.close();
  });

  it("payloadSha256 is key-order stable", () => {
    expect(payloadSha256({ b: 2, a: 1 })).toBe(payloadSha256({ a: 1, b: 2 }));
    expect(payloadSha256({ a: 1 })).toMatch(/^sha256:[0-9a-f]{64}$/);
  });

  it("survives reopen", () => {
    const path = join(mkdtempSync(join(tmpdir(), "ledger-")), "a.sqlite3");
    const first = new ActionLedger(path);
    first.insert("cmd-2", "op", "sha");
    first.close();
    const second = new ActionLedger(path);
    expect(second.find("cmd-2")?.state).toBe("pending");
    second.close();
  });
});

describe("manifest", () => {
  it("flags filter the advertised domains", () => {
    const manifest = connectorManifest("team-a", {
      issues: ["vcs.issue.read", "vcs.issue.comment"],
      mrs: ["vcs.change_request.create", "vcs.issue.read"],
      pipeline: [],
    });
    expect(manifest.ownerTeam).toBe("team-a");
    expect(manifest.capabilities.map((c) => c.id).sort()).toEqual([
      "vcs.change_request.create",
      "vcs.issue.comment",
      "vcs.issue.read",
    ]);
    for (const c of manifest.capabilities) {
      expect(c).toMatchObject({ version: 1, execution: "asynchronous" });
    }
  });

  it("zero enabled domains fails", () => {
    expect(() => connectorManifest("team-a", {})).toThrow(/no op domain enabled/);
  });
});

describe("webhook receiver", () => {
  const cmd = (data: unknown): EnvelopeCommand =>
    ({
      specversion: "1.0",
      id: "e1",
      source: "urn:facilitator:kernel",
      type: "org.facilitator.command.provider.webhook.v1",
      subject: "provider/github/webhook",
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/command.provider.webhook.v1.json",
      data,
    }) as unknown as EnvelopeCommand;

  const ctx = { deliveryId: "d", onStream: () => undefined } as never;
  const logger = { debug() {}, info() {}, warn() {}, error() {} };

  it("decodes verbatim bytes + lowercased headers", () => {
    const delivery = decodeWebhookCommand(
      cmd({
        commandId: "c",
        provider: "github",
        contentType: "application/json",
        body: Buffer.from(`{"a":1}`).toString("base64"),
        headers: { "X-Hub-Signature-256": "sha256=ff", "Content-Type": "application/json" },
      }),
    );
    expect(delivery?.rawBody.toString("utf8")).toBe(`{"a":1}`);
    expect(delivery?.headers["x-hub-signature-256"]).toBe("sha256=ff");
  });

  it("bad signature ⇒ rejected receipt, zero publishes", async () => {
    const published: unknown[] = [];
    const handler = createWebhookReceiver({
      normalize: () => ({ kind: "bad-signature" }),
      publish: async (e) => {
        published.push(e);
      },
      logger,
    });
    await expect(
      handler.onCommand?.(
        cmd({
          commandId: "c",
          provider: "github",
          contentType: "application/json",
          body: "",
          headers: {},
        }),
        ctx,
      ),
    ).resolves.toEqual({ kind: "rejected", reason: "bad-signature" });
    expect(published).toHaveLength(0);
  });

  it("normalization publishes each event and accepts; non-webhook delegates", async () => {
    const published: string[] = [];
    const delegated: string[] = [];
    const handler = createWebhookReceiver({
      normalize: () => ({
        kind: "normalized",
        events: ["d1", "d2"].map((d) => ({
          type: "org.facilitator.event.provider.issue.v1",
          data: {
            reference: {
              provider: "forgejo",
              providerInstanceId: "i",
              tenantOrOrganizationId: "t",
              channelOrResourceId: "r",
              conversationKind: "comment",
              authorProviderAccountId: "a",
            },
            provider: "forgejo",
            providerDeliveryId: d,
            actorProviderAccountId: "a",
            action: "opened",
            workTrigger: false,
          },
        })),
      }),
      publish: async (e) => {
        published.push(e.data.providerDeliveryId);
      },
      logger,
      handler: {
        async onCommand(c) {
          delegated.push(c.type);
          return { kind: "accepted" };
        },
      },
    });
    await expect(
      handler.onCommand?.(
        cmd({
          commandId: "c",
          provider: "github",
          contentType: "application/json",
          body: Buffer.from("x").toString("base64"),
          headers: {},
        }),
        ctx,
      ),
    ).resolves.toEqual({ kind: "accepted" });
    expect(published).toEqual(["d1", "d2"]);
    await expect(
      handler.onCommand?.({ ...cmd({}), type: "org.facilitator.command.work.steer.v1" }, ctx),
    ).resolves.toEqual({ kind: "accepted" });
    expect(delegated).toEqual(["org.facilitator.command.work.steer.v1"]);
  });

  it("malformed data ⇒ rejected; a throwing normalizer ⇒ rejected, never a throw", async () => {
    const handler = createWebhookReceiver({
      normalize: () => {
        throw new Error("boom");
      },
      publish: async () => undefined,
      logger,
    });
    await expect(handler.onCommand?.(cmd({ nope: true }) as never, ctx)).resolves.toEqual({
      kind: "rejected",
      reason: "malformed-webhook-data",
    });
    await expect(
      handler.onCommand?.(
        cmd({
          commandId: "c",
          provider: "github",
          contentType: "application/json",
          body: "",
          headers: {},
        }) as never,
        ctx,
      ),
    ).resolves.toEqual({ kind: "rejected", reason: "normalize-failed" });
  });

  it("HMAC verifies both header forms; token compare is exact", () => {
    const body = Buffer.from(`{"a":1}`);
    const hex = createHmac("sha256", "s3cret").update(body).digest("hex");
    expect(verifyHmacSha256(body, "s3cret", hex)).toBe(true);
    expect(verifyHmacSha256(body, "s3cret", `sha256=${hex}`)).toBe(true);
    expect(verifyHmacSha256(body, "s3cret", `sha256=${hex.slice(0, 8)}`)).toBe(false);
    expect(verifyHmacSha256(body, "s3cret", undefined)).toBe(false);
    expect(verifyTokenToken("tok", "tok")).toBe(true);
    expect(verifyTokenToken("tok!", "tok")).toBe(false);
  });
});
