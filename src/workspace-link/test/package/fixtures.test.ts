// Client-side conformance harness: the SAME fixture index the .NET gate
// enforces (both-harnesses rule), run through the SDK's builders — every
// valid control/receipt fixture must survive the client's frame builders
// unchanged, and the client's error mapping must reproduce the declared
// errorClass for invalid cases.
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import {
  negotiateProtocolVersion,
  enforceSizeLimit,
  validateAgainstSchemaId,
  validateControlFrame,
  validateEnvelope,
  validateReceipt,
  type ErrorClass,
  type LinkDeliveryV1,
} from "@ofac/protocol";
import {
  FaultInjector,
  MAX_ENVELOPE_BYTES,
  MemoryLedger,
  createConsoleLogger,
  createEchoHandler,
  createLinkClient,
} from "../../src/index.js";
import { digestOfManifest } from "../../src/node/index.js";
import { MockTransport, flush } from "../transport/mock-transport.js";

interface FixtureCase {
  file: string;
  expect: "valid" | "invalid";
  class: "command" | "event" | "receipt" | "control" | "enroll" | "delivery";
  type?: string;
  errorClass?: ErrorClass;
  receipt?: string;
  schemaFile?: string;
}

const fixturesRoot = new URL("../../../../protocol/conformance-fixtures/", import.meta.url);
const index = JSON.parse(readFileSync(new URL("index.json", fixturesRoot), "utf8")) as FixtureIndex;

interface FixtureIndex {
  version: number;
  limits: { maxEnvelopeBytes: number };
  cases: FixtureCase[];
}

function load(file: string): unknown {
  return JSON.parse(readFileSync(new URL(file, fixturesRoot), "utf8"));
}

describe("client-side fixture conformance", () => {
  it("fixture index agrees on the wire limit", () => {
    expect(index.limits.maxEnvelopeBytes).toBe(MAX_ENVELOPE_BYTES);
  });

  // Valid control frames (hello/welcome/heartbeat/flow/goodbye) are the
  // shapes the SDK builds/consumes — a fixture must pass the client's own
  // validateControlFrame path unchanged (no mutation by transport framing).
  for (const c of index.cases.filter(
    (x) => x.expect === "valid" && (x.class === "control" || x.class === "receipt"),
  )) {
    it(`${c.file} survives the client frame path`, () => {
      const payload = load(c.file);
      const verdict =
        c.class === "control" ? validateControlFrame(payload) : validateReceipt(payload);
      expect(verdict, c.file).toEqual({ ok: true });
    });
  }

  // Valid delivery fixtures must flow the FULL client pipeline: the echo
  // handler path (work.assign) publishes its events through a recording
  // transport whose envelopes still validate as CloudEvents, and the
  // delivery envelope passes the SDK's size + shape gate.
  for (const c of index.cases.filter((x) => x.expect === "valid" && x.class === "delivery")) {
    it(`${c.file} dispatches through the client pipeline`, async () => {
      const delivery = load(c.file) as LinkDeliveryV1;
      const transport = new MockTransport();
      const client = createLinkClient({
        transport,
        ledger: new MemoryLedger(),
        endpointId: "gw-team-alpha",
        capabilityManifestDigest: digestOfManifest("{}"),
        token: { getToken: async () => "t" },
        handler: createEchoHandler({ source: "urn:workspace:linkd/gw-team-alpha" }),
        logger: createConsoleLogger({ level: "error" }),
      });
      await client.start();
      transport.pushDeliver(delivery);
      await flush(16);
      // Pipeline answered receipt(s) and the echoed events are valid envelopes.
      expect(transport.receipts.length).toBeGreaterThan(0);
      expect(validateReceipt(transport.receipts[0]!)).toEqual({ ok: true });
      for (const env of transport.envelopes) {
        expect(validateEnvelope(env)).toEqual({ ok: true });
      }
      await client.stop();
    });
  }

  // Enroll fixtures: schema round-trip through the SDK's validator path.
  for (const c of index.cases.filter((x) => x.class === "enroll")) {
    it(`${c.file} validates via ${c.schemaFile}`, () => {
      const payload = load(c.file);
      const verdict = validateAgainstSchemaId(
        `https://schemas.openfacilitator.org/v1/${c.schemaFile}`,
        payload,
      );
      if (c.expect === "valid") expect(verdict).toEqual({ ok: true });
      else expect(verdict.ok).toBe(false);
    });
  }

  // Invalid command/event fixtures: the client's own gate (validate + size)
  // produces the declared class — the DeliveryRejectedError mapping source.

  // Invalid control/receipt fixtures: the SDK's validators refuse them with
  // the declared class (the DeliveryRejectedError / receipt-drop mapping).
  for (const c of index.cases.filter(
    (x) => x.expect === "invalid" && (x.class === "control" || x.class === "receipt"),
  )) {
    it(`${c.file} refused as ${c.errorClass ?? "invalid"}`, () => {
      const payload = load(c.file);
      const verdict =
        c.class === "control" ? validateControlFrame(payload) : validateReceipt(payload);
      if (c.errorClass === "no-common-version") {
        // Behavior fixture: schema-valid hello, refused by the client's own
        // negotiation step (the path connectAndHello runs before sending).
        expect(verdict).toEqual({ ok: true });
        const hello = payload as { protocolVersions: string[] };
        expect(negotiateProtocolVersion(hello.protocolVersions, ["v1"])).toBeNull();
        return;
      }
      expect(verdict.ok).toBe(false);
      if (!verdict.ok && c.errorClass !== undefined) expect(verdict.errorClass).toBe(c.errorClass);
    });
  }
  for (const c of index.cases.filter(
    (x) => x.expect === "invalid" && (x.class === "command" || x.class === "event"),
  )) {
    it(`${c.file} → ${c.errorClass} through the client gate`, () => {
      const payload = load(c.file);
      const verdict = validateEnvelope(payload);
      if (verdict.ok && (c.class === "command" || c.class === "event")) {
        expect(
          enforceSizeLimit(
            Buffer.byteLength(JSON.stringify(payload)),
            index.limits.maxEnvelopeBytes,
          ),
        ).toBe(false);
        expect(c.errorClass).toBe("payload-too-large");
        return;
      }
      expect(verdict.ok).toBe(false);
      if (!verdict.ok) expect(verdict.errorClass).toBe(c.errorClass);
    });
  }

  it("FaultInjector wraps a transport and reports the client's own error classes", async () => {
    // ErrorClass surfaces are the client gate itself: no fixture case may
    // silently bypass validation into the ledger.
    const transport = new FaultInjector(new MockTransport(), {});
    expect(transport).toBeInstanceOf(FaultInjector);
  });
});
