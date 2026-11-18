// The typed error surface + the two loggers + the 401 refresh hook: the small
// seams every other suite leans on. Error URIs map to the frozen taxonomy by
// leaf name, an undeclared URI falls back to the generic enrollment failure,
// and TokenRotation only ever re-proves (never re-enrolls).
import { describe, expect, it, vi } from "vitest";
import {
  DeliveryRejectedError,
  EnrollmentError,
  LinkError,
  ProtocolMismatchError,
  TransportError,
  createConsoleLogger,
  createMemoryLogger,
  parseProblem,
  trimSlash,
} from "../../src/index.js";
import { TokenRotation } from "../../src/core/token-rotation.js";
import { silentLogger } from "./fixtures.js";

describe("LinkError taxonomy", () => {
  it("carries the code and the name of the concrete class", () => {
    const error = new LinkError("closed", "shutting down");
    expect(error).toBeInstanceOf(Error);
    expect(error.name).toBe("LinkError");
    expect(error.code).toBe("closed");
    expect(error.message).toBe("shutting down");
  });

  it("ProtocolMismatchError has the declared default message", () => {
    const error = new ProtocolMismatchError();
    expect(error.code).toBe("no-common-version");
    expect(error.name).toBe("ProtocolMismatchError");
    expect(error.message).toBe("server answered hello with no-compatible-protocol-version");
    expect(new ProtocolMismatchError("custom").message).toBe("custom");
  });

  it("EnrollmentError maps a ProblemDetails type URI onto the taxonomy by leaf name", () => {
    const cases: [string, string][] = [
      ["https://errors.openfacilitator.org/unsupported", "unsupported"],
      ["https://errors.openfacilitator.org/session-not-found", "session-not-found"],
      ["https://errors.openfacilitator.org/delivery-not-found", "delivery-not-found"],
      ["https://errors.openfacilitator.org/capabilities-not-found", "capabilities-not-found"],
      ["https://errors.openfacilitator.org/team-mismatch", "team-mismatch"],
      ["https://errors.openfacilitator.org/enrollment-secret-invalid", "enrollment-secret-invalid"],
    ];
    for (const [type, code] of cases) {
      const error = new EnrollmentError(403, { type, detail: "nope" });
      expect(error.code).toBe(code);
      expect(error.status).toBe(403);
      expect(error.problem.type).toBe(type);
      expect(error.message).toBe(`enrollment refused with HTTP 403 (${type}): nope`);
    }
  });

  it("an undeclared type URI falls back to enrollment-failed", () => {
    const error = new EnrollmentError(500, {
      type: "https://errors.openfacilitator.org/brand-new",
      detail: "x",
    });
    expect(error.code).toBe("enrollment-failed");
  });

  it("a problem with no type/detail still reads as a refusal", () => {
    const error = new EnrollmentError(401, {});
    expect(error.code).toBe("enrollment-failed");
    expect(error.message).toBe("enrollment refused with HTTP 401: no detail");
    expect(new EnrollmentError(400, { title: "Bad Request" }).message).toContain("Bad Request");
  });

  it("DeliveryRejectedError carries the error class and optional problem", () => {
    const bare = new DeliveryRejectedError("rejected");
    expect(bare.code).toBe("rejected");
    expect(bare.message).toBe("envelope rejected: rejected");
    const withProblem = new DeliveryRejectedError("rejected", {
      type: "https://errors.openfacilitator.org/team-mismatch",
    });
    expect(withProblem.message).toBe(
      "envelope rejected: rejected (https://errors.openfacilitator.org/team-mismatch)",
    );
    expect(withProblem.errorClass).toBe("rejected");
    expect(withProblem.problem?.type).toContain("team-mismatch");
  });

  it("TransportError flags the 401 that triggers a re-prove", () => {
    const error = new TransportError("hub refused", { unauthorized: true });
    expect(error.code).toBe("unauthorized");
    expect(error.unauthorized).toBe(true);
    const plain = new TransportError("socket gone");
    expect(plain.code).toBe("transport");
    expect(plain.unauthorized).toBe(false);
    expect(new TransportError("x", { problem: { status: 401 } }).problem).toEqual({ status: 401 });
  });
});

describe("parseProblem / trimSlash", () => {
  it("reads a ProblemDetails body and stamps the status from the response", async () => {
    const response = new Response(JSON.stringify({ type: "t", detail: "d" }), {
      status: 422,
      headers: { "content-type": "application/problem+json" },
    });
    expect(await parseProblem(response)).toEqual({ type: "t", detail: "d", status: 422 });
  });

  it("keeps a body-carried status over the response status", async () => {
    const response = new Response(JSON.stringify({ status: 409 }), { status: 500 });
    expect(await parseProblem(response)).toEqual({ status: 409 });
  });

  it("an empty body yields the bare status", async () => {
    expect(await parseProblem(new Response("", { status: 502 }))).toEqual({ status: 502 });
  });

  it("a non-JSON body says so instead of throwing", async () => {
    const problem = await parseProblem(new Response("<html/>", { status: 502 }));
    expect(problem).toEqual({ status: 502, detail: "non-JSON error body" });
  });

  it("trimSlash trims exactly one trailing slash", () => {
    expect(trimSlash("https://k.test/")).toBe("https://k.test");
    expect(trimSlash("https://k.test")).toBe("https://k.test");
    expect(trimSlash("")).toBe("");
  });
});

describe("createConsoleLogger", () => {
  it("emits JSON lines with the call-site fields, filtered by level", () => {
    const chunks: string[] = [];
    const logger = createConsoleLogger({
      level: "warn",
      stream: { write: (chunk) => chunks.push(chunk) },
    });
    logger.debug("quiet", { a: 1 });
    logger.info("also quiet");
    logger.warn("correlated", { workId: "work-1", deliveryId: "del-1" });
    logger.error("boom", { reason: "socket gone" });
    expect(chunks).toHaveLength(2);
    const warn = JSON.parse(chunks[0]!) as Record<string, unknown>;
    expect(warn["level"]).toBe("warn");
    expect(warn["msg"]).toBe("correlated");
    expect(warn["workId"]).toBe("work-1");
    expect(warn["deliveryId"]).toBe("del-1");
    expect(typeof warn["ts"]).toBe("string");
    expect((JSON.parse(chunks[1]!) as Record<string, unknown>)["level"]).toBe("error");
    // Every line is one JSON document ending in a newline.
    for (const chunk of chunks) expect(chunk.endsWith("\n")).toBe(true);
  });

  it("defaults to info and never drops error lines", () => {
    const chunks: string[] = [];
    const logger = createConsoleLogger({ stream: { write: (chunk) => chunks.push(chunk) } });
    logger.debug("dropped");
    logger.info("kept");
    logger.error("kept");
    expect(chunks).toHaveLength(2);
  });
});

describe("createMemoryLogger", () => {
  it("collects every level as a JSON line", () => {
    const logger = createMemoryLogger();
    logger.debug("d", { x: 1 });
    logger.info("i");
    logger.warn("w");
    logger.error("e");
    expect(logger.lines).toHaveLength(4);
    expect(JSON.parse(logger.lines[0]!)).toEqual({ level: "debug", msg: "d", x: 1 });
    expect(JSON.parse(logger.lines[3]!)).toEqual({ level: "error", msg: "e" });
  });
});

describe("TokenRotation", () => {
  it("re-proves once on an unauthorized transport failure", async () => {
    const refresh = vi.fn(async () => {});
    const logger = createMemoryLogger();
    const rotation = new TokenRotation({
      token: { getToken: async () => "t", refresh },
      logger,
      endpointId: "gw-1",
    });
    await rotation.maybeRefresh(new TransportError("401", { unauthorized: true }));
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(logger.lines.some((l) => l.includes("link token refreshed after 401"))).toBe(true);
  });

  it("leaves every other error alone", async () => {
    const refresh = vi.fn(async () => {});
    const rotation = new TokenRotation({
      token: { getToken: async () => "t", refresh },
      logger: silentLogger(),
      endpointId: "gw-1",
    });
    await rotation.maybeRefresh(new TransportError("socket gone"));
    await rotation.maybeRefresh(new Error("anything else"));
    await rotation.maybeRefresh("a string");
    expect(refresh).not.toHaveBeenCalled();
  });

  it("swallows a refresh failure (the caller's retry ladder owns the resend)", async () => {
    const logger = createMemoryLogger();
    const rotation = new TokenRotation({
      token: {
        getToken: async () => "t",
        refresh: async () => {
          throw new Error("kernel unreachable");
        },
      },
      logger,
      endpointId: "gw-1",
    });
    await expect(
      rotation.maybeRefresh(new TransportError("401", { unauthorized: true })),
    ).resolves.toBeUndefined();
    expect(logger.lines.some((l) => l.includes("token refresh failed"))).toBe(true);
  });

  it("a provider with no refresh hook is left as-is", async () => {
    await expect(
      new TokenRotation({
        token: { getToken: async () => "t" },
        logger: silentLogger(),
        endpointId: "gw-1",
      }).maybeRefresh(new TransportError("401", { unauthorized: true })),
    ).resolves.toBeUndefined();
  });
});
