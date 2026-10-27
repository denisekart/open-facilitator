// The artifact capability against the in-process stub kernel (Plan 15):
// upload law (ticket in-band, proxied PUT, kernel-verified digest), the
// duplicate-ack ticket re-attach, the canonical manifest builder (emitted
// bytes == RFC 8785 form; digest recomputes), the completed-event payload
// law (four frozen fields + inline + ref + digest, schema-valid).
import { createHash } from "node:crypto";
import { canonicalizeManifest, computeManifestDigest, validateAgainstSchemaId } from "@ofac/protocol";
import { describe, expect, it } from "vitest";
import {
  artifactUri,
  buildCompletedEvent,
  buildResultManifest,
  createArtifactClient,
} from "../src/artifacts.js";
import { startStubKernel } from "../src/testing.js";
const BYTES = new TextEncoder().encode("# report\ncontent\n");
const SHA = "sha256:" + createHash("sha256").update(BYTES).digest("hex");

describe("createArtifactClient (stub kernel)", () => {
  it("uploads bytes: ticket in-band, proxied PUT, kernel digest back", async () => {
    const kernel = await startStubKernel();
    try {
      const client = createArtifactClient({ baseUrl: kernel.url, token: "t", fetchImpl: fetch });
      const result = await client.upload(BYTES, {
        mediaType: "text/markdown",
        workId: "work-1",
        assignmentId: "asg-1",
        retentionPolicy: "30d",
      });
      expect(result.digest).toBe(SHA);
      const stored = kernel.artifacts.get(result.artifactId);
      expect(stored?.bytes).toEqual(Buffer.from(BYTES));
      expect(stored?.mediaType).toBe("text/markdown");
      // The command envelope rode the link plane with the bearer token.
      const command = kernel.events[0];
      const commandData = command?.["data"] as Record<string, unknown> | undefined; // stub echoes the posted envelope
      expect(command?.["type"]).toBe("org.facilitator.command.artifact.request_upload.v1");
      expect(commandData?.["size"]).toBe(BYTES.byteLength);
      // The frozen schema REQUIRES actorEvidenceRef: the client rides a
      // non-authoritative workload pointer (unknown scheme ⇒ allow).
      expect(commandData?.["actorEvidenceRef"]).toBe("workload:adapter");
    } finally {
      await kernel.close();
    }
  });

  it("a repeated upload of identical bytes converges via the duplicate ticket", async () => {
    const kernel = await startStubKernel();
    try {
      const client = createArtifactClient({ baseUrl: kernel.url, token: "t" });
      const first = await client.upload(BYTES, { mediaType: "text/markdown", workId: "w" });
      const second = await client.upload(BYTES, { mediaType: "text/markdown", workId: "w" });
      // Content-derived commandId: the stub re-attached the live ticket.
      expect(second.artifactId).toBe(first.artifactId);
      expect(kernel.events.filter((e) => e["type"] === "org.facilitator.command.artifact.request_upload.v1")).toHaveLength(2);
      expect(kernel.events[1]?.["id"]).not.toBe(kernel.events[0]?.["id"]);
      expect(kernel.artifacts.get(first.artifactId)?.bytes).toEqual(Buffer.from(BYTES));
    } finally {
      await kernel.close();
    }
  });

  it("a digest-mismatched store refuses the upload", async () => {
    const kernel = await startStubKernel({ rejectDigest: true });
    try {
      const client = createArtifactClient({ baseUrl: kernel.url, token: "t" });
      await expect(client.upload(BYTES, { mediaType: "text/markdown" })).rejects.toThrow(/422/);
    } finally {
      await kernel.close();
    }
  });
});

describe("buildResultManifest (canonical law)", () => {
  const input = {
    manifestId: "man-exec-1",
    workId: "work-1",
    assignmentId: "asg-1",
    capability: "research.analyze",
    summary: "Two findings, one gap.",
    structuredResult: { zeta: 1, alpha: "naïve" },
    outputs: [
      {
        artifactId: "a1b2",
        mediaType: "text/markdown",
        size: BYTES.byteLength,
        digest: SHA,
        artifactUri: artifactUri("work-1", "asg-1", "a1b2"),
        kind: "analysis.report",
        classification: "internal",
        createdBy: "workspace:gw.child",
        createdAt: "2026-10-27T18:00:00Z",
      },
    ],
    externalRefs: ["https://git.example/merge/1"],
    sourceRefs: ["source://corpus/1"],
    createdAt: "2026-10-27T18:00:00Z",
  };

  it("emits bytes that ARE the canonical form and carry a recomputing digest", () => {
    const built = buildResultManifest(input);
    const parsed = JSON.parse(new TextDecoder().decode(built.document)) as Record<string, unknown>;
    expect(computeManifestDigest(parsed)).toBe(built.digest);
    // Stored-bytes law: re-canonicalizing the parsed doc yields the SAME bytes.
    expect(canonicalizeManifest(parsed)).toBe(new TextDecoder().decode(built.document));
    const keys = Object.keys(parsed);
    expect(keys).toEqual([...keys].sort());
  });

  it("the four output classes are representable on the wire", () => {
    const built = buildResultManifest(input);
    const verdict = validateAgainstSchemaId(
      "https://schemas.openfacilitator.org/v1/result-manifest.v1.json",
      JSON.parse(new TextDecoder().decode(built.document)),
    );
    expect(verdict).toEqual({ ok: true });
  });
});

describe("buildCompletedEvent (amendment law)", () => {
  it("stamps four frozen fields + inline + ref + digest, schema-valid", () => {
    const built = buildResultManifest({
      manifestId: "man-e1",
      workId: "work-9",
      assignmentId: "asg-9",
      outputs: [],
      createdAt: "2026-10-27T18:00:00Z",
    });
    const command = {
      specversion: "1.0",
      id: "cmd-1",
      source: "urn:workspace:kernel",
      type: "org.facilitator.command.work.assign.v1",
      subject: "work/work-9/assignment/asg-9",
      time: "2026-10-27T17:00:00Z",
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
      data: { workId: "work-9", assignmentId: "asg-9", capabilityId: "research.analyze", childWorkspaceId: "gw.team-alpha/deterministic" },
    } as const;
    const env = buildCompletedEvent(
      { executionId: "exec-1", executionGeneration: 2, command },
      { manifestId: "man-e1", workId: "work-9", assignmentId: "asg-9" },
      { manifestRef: artifactUri("work-9", "asg-9", "man-artifact-1"), digest: built.digest },
    );
    const data = env.data as Record<string, unknown>;
    expect(data["resultManifest"]).toEqual({ manifestId: "man-e1", outputs: [] });
    expect(data["resultManifestRef"]).toBe("artifact://work-9/asg-9/man-artifact-1");
    expect(data["resultDigest"]).toBe(built.digest);
    expect(String(data["resultManifestRef"]).split("/").at(-1)).toBe("man-artifact-1");
    const verdict = validateAgainstSchemaId(
      "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json",
      data,
    );
    expect(verdict).toEqual({ ok: true });
  });
});
