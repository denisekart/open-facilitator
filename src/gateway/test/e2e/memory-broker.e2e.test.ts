// Memory broker e2e: a fake-link workload retains a project memory
// for team-alpha through the Link plane (alice-delegated evidence), a
// synthetic candidate event lands on the same work's lineage, the foreign team
// (bob) sees nothing until alice's share — then recalls the entry with its
// provenance + accessControl metadata (§6.3 law). psql confirms the ledger.
import { randomUUID } from "node:crypto";
import { describe, expect, it } from "vitest";
import {
  E2E,
  evidenceToken,
  kernelToken,
  psql,
  useFacilitator,
  userToken,
  workloadToken,
  type Harness,
} from "./gateway-harness.js";
import { linkPost } from "../../../workspace-link/test/e2e/harness.js";

const TERM = `e2emem${randomUUID().replace(/-/g, "").slice(0, 12)}`;

describe.skipIf(!E2E)("memory broker e2e", () => {
  const harness: Harness = useFacilitator();

  it("link retain + candidate event -> cross-team ACL -> share -> recall", async () => {
    // A team-alpha project over psql (the broker resolves project selectors
    // through the projects row; the teams exist from Link:Teams dev config).
    const projectRows = await psql(
      harness.dbUrl,
      `INSERT INTO facilitator.projects (project_id, team_id, name, config, created_at)
       VALUES (gen_random_uuid(), (SELECT team_id FROM facilitator.teams WHERE slug = 'team-alpha'),
               'e2e-memory-proj', '{}', now())
       RETURNING project_id`,
    );
    const projectId = String(projectRows[0]!);

    // Mode-(b) enrollment: gateway-alpha accepts delegated claims.
    const enrolled = await linkPost(harness.baseUrl, "/api/link/v1/enroll", await workloadToken(), {
      token: await workloadToken(),
      endpointId: "gw.team-alpha",
      instanceId: "e2e-memory",
      trustMode: "isolated-executor",
    });
    expect(enrolled.status).toBe(201);
    const linkToken = String((enrolled.body as { linkToken: string }).linkToken);
    const evidence = await evidenceToken("alice", "alice");

    // 1. Workload retain, alice-delegated: the receipt rides the ack `data`,
    // a workload write lands `candidate` (D4).
    const commandId = `cmd-mem-${randomUUID().slice(0, 8)}`;
    const ack = await linkPost(harness.baseUrl, "/api/link/v1/events", linkToken, {
      specversion: "1.0",
      id: `env-mem-${commandId}`,
      source: "urn:workspace:e2e",
      type: "org.facilitator.command.memory.retain.v1",
      subject: `project/${projectId}`,
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/command.memory.retain.v1.json",
      data: {
        commandId,
        actorEvidenceRef: `oidc:${evidence}`,
        scopeSelector: `project:${projectId}`,
        type: "observed_fact",
        content: `nightly ${TERM} rotation law verified on e2e`,
        provenance: {
          source: "workspace:gw.team-alpha",
          evidenceRef: "identity-evidence:e2e",
          createdBy: "oidc:alice",
          createdAt: new Date().toISOString(),
        },
      },
    });
    expect(ack.status).toBe(202);
    const receipt = (ack.body as { data?: Record<string, unknown> }).data;
    expect(receipt).toBeTruthy();
    const entryId = String(receipt?.["entryId"]);
    expect(receipt?.["status"]).toBe("candidate");

    // 2. A synthetic candidate event on the CE plane names no work: it may
    // land or swallow (no owner derivable), but the plane never refuses.
    const candidate = await linkPost(harness.baseUrl, "/api/ingress/events", await kernelToken(), {
      specversion: "1.0",
      id: `env-mem-cand-${commandId}`,
      source: "urn:workspace:e2e",
      type: "org.facilitator.event.memory.candidate.v1",
      subject: `memory/${randomUUID()}`,
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/event.memory.candidate.v1.json",
      data: {
        scope: "team",
        contentSummary: `candidate ${TERM} from the e2e producer`,
        provenance: {
          source: "workspace:gw.team-alpha",
          evidenceRef: "identity-evidence:e2e",
          createdBy: "oidc:alice",
          createdAt: new Date().toISOString(),
        },
      },
    });
    expect(candidate.status).toBe(202);

    // 3. Bob (team-beta): the retained term recalls zero entries — no
    // existence hint at all.
    const bob = await userToken("bob", "bob");
    const bobBefore = await fetch(`${harness.baseUrl}/api/memory/recall`, {
      method: "POST",
      headers: { authorization: `Bearer ${bob}`, "content-type": "application/json" },
      body: JSON.stringify({ query: TERM, purpose: "report.synthesis" }),
    });
    expect(bobBefore.status).toBe(200);
    expect(((await bobBefore.json()) as { entries: unknown[] }).entries).toHaveLength(0);

    // 4. Alice shares the entry to team-beta; bob now recalls it WITH the
    // §6.3 metadata (provenance + accessControl ride every result).
    const alice = await userToken("alice", "alice");
    const share = await fetch(`${harness.baseUrl}/api/memory/${entryId}/shares`, {
      method: "POST",
      headers: { authorization: `Bearer ${alice}`, "content-type": "application/json" },
      body: JSON.stringify({ granteeTeam: "team-beta" }),
    });
    expect(share.status).toBe(200);

    const bobAfter = await fetch(`${harness.baseUrl}/api/memory/recall`, {
      method: "POST",
      headers: { authorization: `Bearer ${bob}`, "content-type": "application/json" },
      body: JSON.stringify({ query: TERM, purpose: "report.synthesis" }),
    });
    expect(bobAfter.status).toBe(200);
    const seen = (
      (await bobAfter.json()) as {
        entries: { entryId: string; provenance?: unknown; accessControl?: unknown }[];
      }
    ).entries;
    const mine = seen.filter((e) => e.entryId === entryId);
    expect(mine).toHaveLength(1);
    expect(mine[0]!.provenance).toBeTruthy();
    expect(mine[0]!.accessControl).toBeTruthy();

    // 5. Ledger truth: the entry row + the share row exist for team-alpha.
    const rows = await psql(
      harness.dbUrl,
      `SELECT e.status FROM facilitator.memory_entries e
       WHERE e.entry_id = '${entryId}' AND e.team_slug = 'team-alpha'`,
    );
    expect(rows).toContain("candidate");
    const shares = await psql(
      harness.dbUrl,
      `SELECT grantee_team_slug FROM facilitator.memory_shares
       WHERE entry_id = '${entryId}' AND revoked_at IS NULL`,
    );
    expect(shares).toContain("team-beta");
  }, 120_000);
});
