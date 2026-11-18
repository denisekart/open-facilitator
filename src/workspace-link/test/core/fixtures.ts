// Shared fixtures for the core unit tier: a temp-dir keystore identity (the
// keystore is the only Ed25519 code in the fleet, so proofs are signed by a
// real key) plus the envelope/manifest shapes the dispatcher and capability
// tests need. Temp dirs are registered for removal by `cleanUp()`, which each
// suite calls from afterAll.
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { CapabilityManifestV1, EnvelopeCommand, LinkDeliveryV1 } from "@ofac/protocol";
import { loadOrCreateIdentity, type EndpointIdentity, type LinkLogger } from "../../src/index.js";

const created: string[] = [];

/** A fresh temp dir (removed by `cleanUp`). */
export function tempDir(): string {
  const dir = mkdtempSync(join(tmpdir(), "ofac-core-test-"));
  created.push(dir);
  return dir;
}

export function cleanUp(): void {
  while (created.length > 0) {
    const dir = created.pop();
    if (dir !== undefined) rmSync(dir, { recursive: true, force: true });
  }
}

/** A minted-in-temp-dir identity: a real key, so signatures are real. */
export function makeIdentity(): EndpointIdentity {
  return loadOrCreateIdentity(join(tempDir(), "state"));
}

export const ASSIGN_TYPE = "org.facilitator.command.work.assign.v1";
export const DECIDED_TYPE = "org.facilitator.event.approval.decided.v1";

/** A `work.assign.v1` command with the fields the correlation logging reads. */
export function assignEnvelope(commandId = "cmd-1"): EnvelopeCommand {
  return {
    specversion: "1.0",
    id: `env-${commandId}`,
    source: "urn:facilitator:instance:main",
    type: ASSIGN_TYPE,
    subject: `work/work-123/assignment/${commandId}`,
    time: "2026-10-22T18:00:00Z",
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    traceparent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
    data: {
      commandId,
      workId: "work-123",
      assignmentId: commandId,
      targetId: "gw-1",
      capabilityId: "research.analyze",
      expectedContextVersion: 1,
      executionGeneration: 2,
      actorEvidenceRef: "identity-evidence:ref-123",
      deadline: "2026-10-23T18:00:00Z",
      inputs: { objective: "compare", sourceRefs: ["artifact://s/1"] },
      expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
    },
  };
}

export function deliveryOf(deliveryId: string, envelope: EnvelopeCommand): LinkDeliveryV1 {
  return { deliveryId, envelope, deliveredAt: "2026-10-22T18:00:01Z" };
}

/** The smallest manifest capability-manifest.v1.json admits. */
export function manifest(team = "team-alpha"): CapabilityManifestV1 {
  return {
    apiVersion: "facilitator.framework/v1",
    kind: "CapabilityManifest",
    ownerTeam: team,
    capabilities: [
      {
        id: "research.analyze",
        version: 1,
        execution: "synchronous",
        acceptedContexts: ["uri-reference"],
        outputKinds: ["analysis.report"],
        outputMediaTypes: ["text/markdown"],
        supportsSteering: false,
        supportsCancellation: false,
        supportsNudge: false,
      },
    ],
  };
}

/** A `Response` over arbitrary status/text — the scripted-kernel answer. */
export function responseOf(status: number, body: string): Response {
  // 204/304 forbid a body; every other status carries the text verbatim.
  const init: ResponseInit = { status, headers: { "content-type": "application/json" } };
  return new Response(status === 204 || status === 304 ? null : body, init);
}

/** A logger that swallows everything, for suites that assert on behavior only. */
export function silentLogger(): LinkLogger {
  return { debug() {}, info() {}, warn() {}, error() {} };
}
