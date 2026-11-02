// Conformance harness (TS side): every fixture in
// protocol/conformance-fixtures/index.json must validate exactly as declared.
// Mirrors the semantics of protocol/tools/Validate.Fixtures (.NET side); the
// shared index + canonical golden cross-check the two implementations.
import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";
import canonicalize from "canonicalize";
import { describe, expect, it } from "vitest";
import {
  canonicalizeManifest,
  computeActionDigest,
  computeManifestDigest,
  enforceSizeLimit,
  negotiateProtocolVersion,
  validateControlFrame,
  validateEnvelope,
  validateReceipt,
  validateAgainstSchemaId,
  type ErrorClass,
  type ValidationResult,
} from "../src/index.js";

interface FixtureCase {
  file: string;
  expect: "valid" | "invalid";
  class: "command" | "event" | "receipt" | "control" | "enroll" | "delivery" | "adapter";
  type?: string;
  errorClass?: ErrorClass;
  receipt?: string;
  /** For enroll/delivery/adapter classes: schema filename under the contract $id base. */
  schemaFile?: string;
}
interface FixtureIndex {
  version: number;
  limits: { maxEnvelopeBytes: number };
  cases: FixtureCase[];
}

const fixturesRoot = new URL("../../../protocol/conformance-fixtures/", import.meta.url);
const index = JSON.parse(readFileSync(new URL("index.json", fixturesRoot), "utf8")) as FixtureIndex;

function load(file: string): unknown {
  return JSON.parse(readFileSync(new URL(file, fixturesRoot), "utf8"));
}

function wireBytes(payload: unknown): number {
  return Buffer.byteLength(JSON.stringify(payload));
}

function runCase(c: FixtureCase): ValidationResult {
  const payload = load(c.file);
  const result =
    c.class === "receipt"
      ? validateReceipt(payload)
      : c.class === "control"
        ? validateControlFrame(payload)
        : c.class === "enroll" || c.class === "delivery" || c.class === "adapter"
          ? validateAgainstSchemaId(
              `https://schemas.openfacilitator.org/v1/${c.schemaFile}`,
              payload,
            )
          : validateEnvelope(payload);
  // Payload-size gate applies to envelope wire bytes (fixture pipeline never
  // pretty-prints: JSON.stringify of the parsed payload is the wire form).
  if (result.ok && (c.class === "command" || c.class === "event")) {
    if (!enforceSizeLimit(wireBytes(payload), index.limits.maxEnvelopeBytes)) {
      return { ok: false, errorClass: "payload-too-large", message: "wire bytes exceed limit" };
    }
  }
  // Behavior fixtures: version negotiation only becomes observable on hello.
  if (result.ok && c.class === "control" && (payload as { kind?: string }).kind === "hello") {
    const hello = payload as { protocolVersions: string[] };
    if (negotiateProtocolVersion(hello.protocolVersions, ["v1"]) === null) {
      return {
        ok: false,
        errorClass: "no-common-version",
        message: "hello offers no compatible version",
      };
    }
  }
  return result;
}

describe("conformance fixtures", () => {
  it("index declares at least one case per expectation", () => {
    expect(index.version).toBe(1);
    expect(index.cases.length).toBeGreaterThanOrEqual(40);
  });

  for (const c of index.cases) {
    it(`${c.file} → ${c.expect}${c.errorClass ? ` (${c.errorClass})` : ""}`, () => {
      const result = runCase(c);
      if (c.expect === "valid") {
        expect(result).toEqual({ ok: true });
      } else {
        expect(result.ok).toBe(false);
        if (result.ok === false) {
          expect(result.errorClass).toBe(c.errorClass);
        }
      }
      // Behavior fixtures name an expected response fixture that itself must
      // be a valid receipt/control frame (unknown-command → unsupported
      // receipt; no-common-version → goodbye frame).
      if (c.receipt !== undefined) {
        const response = load(c.receipt);
        const r =
          c.receipt.startsWith("valid/receipt.") || c.receipt.startsWith("invalid/receipt.")
            ? validateReceipt(response)
            : validateControlFrame(response);
        expect(r, `response fixture ${c.receipt}`).toEqual({ ok: true });
      }
    });
  }
});

describe("canonicalization golden (RFC 8785)", () => {
  it("canonical/approval-prehash.json hashes to the committed golden", () => {
    const raw = readFileSync(new URL("canonical/approval-prehash.json", fixturesRoot), "utf8");
    const canonical = canonicalize(JSON.parse(raw) as object);
    expect(canonical).toBeDefined();
    const digest =
      "sha256:" +
      createHash("sha256")
        .update(Buffer.from(canonical as string, "utf8"))
        .digest("hex");
    const golden = readFileSync(
      new URL("canonical/approval-prehash.sha256", fixturesRoot),
      "utf8",
    ).trim();
    expect(digest).toBe(golden);
  });
});

describe("approval-record digest golden — Gateway-shaped action", () => {
  const record = JSON.parse(
    readFileSync(new URL("canonical/approval-record-2.json", fixturesRoot), "utf8"),
  ) as {
    approvalId: string;
    action: Record<string, unknown>;
    eligibleActors: string[];
    expiresAt: string;
    createdAt: string;
  };
  const golden = readFileSync(
    new URL("canonical/approval-record-2.sha256", fixturesRoot),
    "utf8",
  ).trim();

  it("canonical/approval-record-2.json digests to the committed golden", () => {
    expect(computeActionDigest(record)).toBe(golden);
  });
});

describe("approval-record digest golden (appendix §7.2)", () => {
  const record = JSON.parse(
    readFileSync(new URL("canonical/approval-record-1.json", fixturesRoot), "utf8"),
  ) as {
    approvalId: string;
    action: Record<string, unknown>;
    eligibleActors: string[];
    expiresAt: string;
    createdAt: string;
  };
  const golden = readFileSync(
    new URL("canonical/approval-record-1.sha256", fixturesRoot),
    "utf8",
  ).trim();

  it("canonical/approval-record-1.json digests to the committed golden", () => {
    expect(computeActionDigest(record)).toBe(golden);
  });

  it("digest is invariant to action key order and NFD unicode spelling", () => {
    const flip = (value: unknown): unknown => {
      if (Array.isArray(value)) return value.map(flip);
      if (typeof value === "string") return value.normalize("NFD");
      if (value !== null && typeof value === "object") {
        const out: Record<string, unknown> = {};
        for (const key of Object.keys(value).reverse()) {
          out[key.normalize("NFD")] = flip((value as Record<string, unknown>)[key]);
        }
        return out;
      }
      return value;
    };
    expect(
      computeActionDigest({ ...record, action: flip(record.action) as Record<string, unknown> }),
    ).toBe(golden);
  });
});

describe("result-manifest digest golden (spec §15.3)", () => {
  const raw = readFileSync(new URL("canonical/result-manifest-1.json", fixturesRoot), "utf8");
  const golden = readFileSync(
    new URL("canonical/result-manifest-1.sha256", fixturesRoot),
    "utf8",
  ).trim();

  it("canonical/result-manifest-1.json digests to the committed golden", () => {
    expect(computeManifestDigest(JSON.parse(raw) as Record<string, unknown>)).toBe(golden);
  });

  it("the stored file bytes are the canonical form byte-for-byte", () => {
    // The kernel records the manifest digest over the STORED bytes and
    // rejects non-canonical submissions, so the golden must be stored
    // already-canonical (sorted keys, no insignificant whitespace, NFC).
    expect(canonicalizeManifest(JSON.parse(raw) as Record<string, unknown>)).toBe(raw);
  });
});
