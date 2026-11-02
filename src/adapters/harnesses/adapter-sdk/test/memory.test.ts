// The memory capability against the in-process stub kernel:
// the retain command envelope is schema-valid with the required evidence
// pointer, the receipt rides the ack `data`, recall parses the D8 response,
// attest publishes the frozen `memory.used` data, the candidate builder
// matches the frozen schema, and reflect reports honestly.
import { validateAgainstSchemaId } from "@ofac/protocol";
import { describe, expect, it } from "vitest";
import { createMemoryClient } from "../src/memory.js";
import { startStubKernel } from "../src/testing.js";

const RETAIN_SCHEMA = "https://schemas.openfacilitator.org/v1/command.memory.retain.v1.json";
const RECALL_SCHEMA = "https://schemas.openfacilitator.org/v1/command.memory.recall.v1.json";
const USED_SCHEMA = "https://schemas.openfacilitator.org/v1/event.memory.used.v1.json";
const CANDIDATE_SCHEMA = "https://schemas.openfacilitator.org/v1/event.memory.candidate.v1.json";

const provenance = {
  source: "workspace:gw.child",
  evidenceRef: "identity:evidence/ref-1",
  createdBy: "oidc:issuer|alice",
  createdAt: "2026-10-27T18:00:00Z",
};

describe("createMemoryClient (stub kernel)", () => {
  it("retain posts a schema-valid command and parses the in-band receipt", async () => {
    const kernel = await startStubKernel();
    try {
      const client = createMemoryClient({ baseUrl: kernel.url, token: "t" });
      const receipt = await client.retain({
        scopeSelector: "project:analysis-lab",
        type: "observed_fact",
        content: "The staging gateway rotates its link token nightly.",
        provenance,
      });
      expect(receipt.entryId).toMatch(/^[0-9a-f-]{36}$/);
      expect(receipt.status).toBe("candidate");
      expect(receipt.duplicate).toBe(false);

      const command = kernel.events[0];
      expect(command?.["type"]).toBe("org.facilitator.command.memory.retain.v1");
      const data = command?.["data"] as Record<string, unknown>;
      // The frozen schema REQUIRES actorEvidenceRef: the workload pointer rides by default.
      expect(data["actorEvidenceRef"]).toBe("workload:adapter");
      expect(String(data["commandId"])).toMatch(/^mem-retain-[0-9a-f]{16}$/);
      expect(validateAgainstSchemaId(RETAIN_SCHEMA, data)).toEqual({ ok: true });
    } finally {
      await kernel.close();
    }
  });

  it("recall parses the D8 response from the ack data", async () => {
    const kernel = await startStubKernel();
    try {
      const client = createMemoryClient({ baseUrl: kernel.url, token: "t" });
      await client.retain({
        scopeSelector: "team:alpha",
        type: "experience",
        content: "Token rotation runs at 02:00 UTC via the gateway cron.",
        provenance,
      });
      const response = await client.recall({ query: "rotation", purpose: "assignment.context" });
      expect(response.providerProfile).toBe("postgres-fts");
      expect(response.truncated).toBe(false);
      expect(response.entries).toHaveLength(1);
      expect(response.entries[0]?.content).toContain("rotation");
      const command = kernel.events[1];
      const data = command?.["data"] as Record<string, unknown>;
      expect(String(data["commandId"])).toMatch(/^mem-recall-/);
      expect(validateAgainstSchemaId(RECALL_SCHEMA, data)).toEqual({ ok: true });
    } finally {
      await kernel.close();
    }
  });

  it("attest publishes the frozen memory.used envelope", async () => {
    const kernel = await startStubKernel();
    try {
      const client = createMemoryClient({ baseUrl: kernel.url, token: "t" });
      await client.attest({
        memoryId: "11111111-2222-3333-4444-555555555555",
        consumer: "workload:adapter",
        justification: "assignment.context: rotation schedule",
      });
      const event = kernel.events[0];
      expect(event?.["type"]).toBe("org.facilitator.event.memory.used.v1");
      expect(
        validateAgainstSchemaId(USED_SCHEMA, event?.["data"] as Record<string, unknown>),
      ).toEqual({ ok: true });
    } finally {
      await kernel.close();
    }
  });

  it("buildMemoryCandidateEvent emits frozen-schema-valid data", async () => {
    const client = createMemoryClient({ baseUrl: "http://127.0.0.1:1", token: "t" });
    const event = client.buildMemoryCandidateEvent({
      scope: "workspace",
      contentSummary: "Retry the flaky checkout once before failing.",
      provenance,
      policyLabel: "team-only",
    });
    expect(event.type).toBe("org.facilitator.event.memory.candidate.v1");
    expect(
      validateAgainstSchemaId(CANDIDATE_SCHEMA, event.data as Record<string, unknown>),
    ).toEqual({ ok: true });
  });

  it("reflect reports unsupported honestly", () => {
    const client = createMemoryClient({ baseUrl: "http://127.0.0.1:1", token: "t" });
    expect(client.reflect()).toEqual({ unsupported: true });
  });
});
