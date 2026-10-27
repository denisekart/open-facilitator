// Artifact store e2e: a scripted fake-link (no Gateway process needed) requests an
// upload over the Link plane against a real kernel + Postgres, PUTs bytes to the
// ticket's uploadLocation, declares the kernel-stored artifact available, then a
// human fetches the bytes back and psql confirms the stored/verified row.
import { createHash, randomUUID } from "node:crypto";
import { describe, expect, it } from "vitest";
import {
  E2E,
  kernelToken,
  psql,
  useFacilitator,
  userToken,
  waitFor,
  workloadToken,
  type Harness,
} from "./gateway-harness.js";
import { linkPost } from "../../../workspace-link/test/e2e/harness.js";

const BYTES = new TextEncoder().encode("# e2e artifact\nuploaded through the kernel boundary\n");
const DIGEST = "sha256:" + createHash("sha256").update(BYTES).digest("hex");

describe.skipIf(!E2E)("artifact store e2e", () => {
  const harness: Harness = useFacilitator();

  it("request-upload -> PUT bytes -> available -> read back", async () => {
    // A real work so the timeline assertions are meaningful.
    const created = await linkPost(harness.baseUrl, "/api/works", await kernelToken(), {
      objective: "artifact e2e",
      capabilityId: "research.analyze",
    });
    expect(created.status).toBe(201);
    const workId = String((created.body as { workId: string }).workId);

    // Mode-(b) enrollment: the gateway-alpha workload identity on its bound endpoint.
    const enrolled = await linkPost(harness.baseUrl, "/api/link/v1/enroll", await workloadToken(), {
      token: await workloadToken(),
      endpointId: "gw.team-alpha",
      instanceId: "e2e-artifact",
      trustMode: "isolated-executor",
    });
    expect(enrolled.status).toBe(201);
    const linkToken = String((enrolled.body as { linkToken: string }).linkToken);

    // 1. Allocate: the ticket rides back in-band on the ack `data`.
    const commandId = `cmd-art-${randomUUID().slice(0, 8)}`;
    const ack = await linkPost(harness.baseUrl, "/api/link/v1/events", linkToken, {
      specversion: "1.0",
      id: `env-art-${commandId}`,
      source: "urn:workspace:e2e",
      type: "org.facilitator.command.artifact.request_upload.v1",
      subject: `work/${workId}`,
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/command.artifact.request_upload.v1.json",
      data: {
        commandId,
        mediaType: "text/markdown",
        size: BYTES.length,
        digest: DIGEST,
        actorEvidenceRef: "identity-evidence:e2e",
        workId,
      },
    });
    expect(ack.status).toBe(202);
    const ticket = (ack.body as { data?: Record<string, string> }).data;
    expect(ticket).toBeTruthy();
    const artifactId = String(ticket?.["artifactId"]);
    const uploadLocation = String(ticket?.["uploadLocation"]);
    expect(uploadLocation).toBe(`/api/link/v1/artifacts/${artifactId}/content`);

    // 2. Bytes travel by PUT — kernel counts + hashes them.
    const put = await fetch(`${harness.baseUrl}${uploadLocation}`, {
      method: "PUT",
      headers: { authorization: `Bearer ${linkToken}` },
      body: BYTES,
    });
    expect(put.status).toBe(200);

    // 3. The kernel-stored declaration confirms (no rewrite).
    const declared = await linkPost(harness.baseUrl, "/api/link/v1/events", linkToken, {
      specversion: "1.0",
      id: `env-art-avail-${commandId}`,
      source: "urn:workspace:e2e",
      type: "org.facilitator.event.artifact.available.v1",
      subject: `work/${workId}`,
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/event.artifact.available.v1.json",
      data: {
        artifactId,
        mediaType: "text/markdown",
        size: BYTES.length,
        digest: DIGEST,
        workId,
      },
    });
    expect(declared.status).toBe(202);

    // 4. Human read-back: owning team fetches the exact bytes.
    const alice = await userToken("alice", "alice");
    const served = await fetch(`${harness.baseUrl}/api/artifacts/${artifactId}/content`, {
      headers: { authorization: `Bearer ${alice}` },
    });
    expect(served.status).toBe(200);
    const fetched = new Uint8Array(await served.arrayBuffer());
    expect("sha256:" + createHash("sha256").update(fetched).digest("hex")).toBe(DIGEST);

    // 5. Ledger truth: stored + verified, timeline notes both transitions.
    await waitFor("artifact row stored", async () => {
      const rows = await psql(
        harness.dbUrl,
        `SELECT state || '/' || verification FROM facilitator.artifacts WHERE artifact_id = '${artifactId}'`,
      );
      return rows[0] === "stored/verified" ? true : false;
    });
    const kinds = await psql(
      harness.dbUrl,
      `SELECT DISTINCT kind FROM facilitator.work_events WHERE work_id = '${workId}' AND kind LIKE 'artifact.%'`,
    );
    expect(kinds).toContain("artifact.stored");
    expect(kinds).toContain("artifact.available");
  }, 120_000);
});
