// The op-table contract: input law (a missing/blank string and a
// non-positive/non-integer number are named op failures, never silent
// defaults), the idempotency stamp both writer and reader share, the ONE
// stall→degradation mapping, and the replay law reading the ledger's truth.
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import {
  ActionLedger,
  CMD_MARKER,
  HttpError,
  OpError,
  degradationFrom,
  payloadSha256,
  replayFrom,
  required,
  requiredNumber,
  stampedBody,
} from "../src/index.js";

const open = (): ActionLedger =>
  new ActionLedger(join(mkdtempSync(join(tmpdir(), "ops-core-")), "a.sqlite3"));

describe("input law", () => {
  it("required: a non-empty string passes, everything else is an OpError naming key + op", () => {
    expect(required({ body: "hi" }, "body", "vcs.issue.comment")).toBe("hi");
    for (const inputs of [{}, { body: "" }, { body: 3 }, { body: null }, { body: undefined }]) {
      expect(() => required(inputs, "body", "vcs.issue.comment")).toThrow(OpError);
      try {
        required(inputs, "body", "vcs.issue.comment");
        throw new Error("should have thrown");
      } catch (error) {
        expect((error as OpError).name).toBe("OpError");
        expect((error as Error).message).toBe("vcs.issue.comment: input 'body' required");
      }
    }
  });

  it("requiredNumber: integers and numeric strings pass", () => {
    expect(requiredNumber({ number: 7 }, "number", "op")).toBe(7);
    expect(requiredNumber({ number: "7" }, "number", "op")).toBe(7);
  });

  it("requiredNumber: every other shape is a positive-integer failure", () => {
    for (const value of [0, -1, 1.5, "0", "abc", "", true, null, undefined, {}]) {
      expect(() => requiredNumber({ number: value }, "number", "op")).toThrow(
        "op: input 'number' must be a positive integer",
      );
    }
  });
});

describe("the commandId stamp", () => {
  it("the marker rides the body and is findable by the reconciler", () => {
    const body = stampedBody("hello", "cmd-1");
    expect(body).toBe("hello\n\n<!-- ofac-cmd:cmd-1 -->");
    expect(body).toContain(CMD_MARKER("cmd-1"));
    expect(body).not.toContain(CMD_MARKER("cmd-2"));
  });
});

describe("replay law", () => {
  it("an executed row answers with the recorded id, full stop", () => {
    const ledger = open();
    ledger.insert("cmd-1", "vcs.issue.comment", payloadSha256({ a: 1 }));
    ledger.settle("cmd-1", "comment 42");
    expect(replayFrom(ledger, "cmd-1")).toEqual({
      providerObjectId: "comment 42",
      structured: { replayed: true, providerObjectId: "comment 42" },
    });
    ledger.close();
  });

  it("absent or pending answers undefined: the domain proceeds and reconciles", () => {
    const ledger = open();
    expect(replayFrom(ledger, "nope")).toBeUndefined();
    ledger.insert("cmd-2", "vcs.issue.comment", payloadSha256({ a: 1 }));
    expect(replayFrom(ledger, "cmd-2")).toBeUndefined();
    ledger.close();
  });
});

describe("degradation mapping", () => {
  it("429 and 403 are the named classes; anything else is a bare http-error", () => {
    expect(degradationFrom(new HttpError(429, "slow", 5))).toEqual({
      status: 429,
      reason: "throttled",
      retryAfterSeconds: 5,
    });
    expect(degradationFrom(new HttpError(403, "denied"))).toEqual({
      status: 403,
      reason: "forbidden",
    });
    expect(degradationFrom(new HttpError(503))).toEqual({ status: 503, reason: "http-error" });
  });
});
