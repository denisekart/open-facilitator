// Capability-manifest publication: digest discipline (the digest MUST cover
// the exact POSTed bytes), schema validation before send, the bearer riding
// the request, and the 204-only law with ProblemDetails surfaced. Scripted
// fetch — no kernel. Lives in the core tier because the digest/POST law is
// core behavior; only the fetch seam is node-level.
import { createHash } from "node:crypto";
import { afterAll, describe, expect, it } from "vitest";
import type { CapabilityManifestV1 } from "@ofac/protocol";
import { digestOfManifest, postCapabilities } from "../../src/node/capabilities.js";
import { LinkError } from "../../src/index.js";
import type { CapabilitiesTransport } from "../../src/node/index.js";
import { cleanUp, manifest, responseOf } from "./fixtures.js";

afterAll(() => {
  cleanUp();
});

interface Call {
  url: string;
  init: RequestInit;
}

function transportFor(
  response: Response,
  calls: Call[],
): { transport: CapabilitiesTransport; tokenCalls: () => number } {
  let tokens = 0;
  return {
    tokenCalls: () => tokens,
    transport: {
      baseUrl: "https://kernel.test/",
      getToken: async () => {
        tokens += 1;
        return `jwt-${tokens}`;
      },
      fetchImpl: (async (url: unknown, init?: RequestInit) => {
        calls.push({ url: String(url), init: init ?? {} });
        return response;
      }) as unknown as typeof fetch,
    },
  };
}

describe("digestOfManifest", () => {
  it("is sha256 over the exact UTF-8 serialized bytes", () => {
    const text = JSON.stringify(manifest());
    expect(digestOfManifest(text)).toBe(
      `sha256:${createHash("sha256").update(text, "utf8").digest("hex")}`,
    );
    // A single-byte change moves the digest (hello's gate is exact).
    expect(digestOfManifest(`${text} `)).not.toBe(digestOfManifest(text));
  });
});

describe("postCapabilities", () => {
  it("POSTs the canonical JSON once, bearing the session token, and digests those bytes", async () => {
    const calls: Call[] = [];
    const { transport, tokenCalls } = transportFor(responseOf(204, ""), calls);
    const m: CapabilityManifestV1 = manifest();
    const result = await postCapabilities(m, transport);
    expect(calls).toHaveLength(1);
    expect(calls[0]!.url).toBe("https://kernel.test/api/link/v1/capabilities");
    expect(calls[0]!.init.method).toBe("POST");
    const headers = calls[0]!.init.headers as Record<string, string>;
    expect(headers["authorization"]).toBe("Bearer jwt-1");
    expect(headers["content-type"]).toBe("application/json");
    expect(tokenCalls()).toBe(1);
    // The digest covers the EXACT body bytes — the server stores plain hex of
    // what it received, and hello's digest must match it.
    const body = String(calls[0]!.init.body);
    expect(result.body).toBe(body);
    expect(result.digest).toBe(digestOfManifest(body));
    expect(result.digest).toBe(digestOfManifest(JSON.stringify(m)));
  });

  it("refuses a manifest that violates the frozen schema before any network call", async () => {
    const calls: Call[] = [];
    const { transport } = transportFor(responseOf(204, ""), calls);
    const broken = { ...manifest(), kind: "Wrong" } as unknown as CapabilityManifestV1;
    const error = await postCapabilities(broken, transport).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(LinkError);
    expect((error as LinkError).message).toContain("capability-manifest.v1.json");
    expect(calls).toHaveLength(0);
  });

  it("a non-204 answer becomes capabilities-refused with the ProblemDetails detail", async () => {
    const calls: Call[] = [];
    const { transport } = transportFor(
      responseOf(403, JSON.stringify({ type: "team-mismatch", detail: "not your team" })),
      calls,
    );
    const error = await postCapabilities(manifest(), transport).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(LinkError);
    expect((error as LinkError).code).toBe("capabilities-refused");
    expect((error as LinkError).message).toContain("403");
    expect((error as LinkError).message).toContain("(team-mismatch)");
    expect((error as LinkError).message).toContain("not your team");
  });

  it("a non-JSON error body still reports the status", async () => {
    const calls: Call[] = [];
    const { transport } = transportFor(
      new Response("<html>gateway</html>", { status: 502 }),
      calls,
    );
    const error = await postCapabilities(manifest(), transport).catch((e: unknown) => e);
    expect((error as LinkError).message).toContain("502");
    expect((error as LinkError).message).toContain("non-JSON error body");
  });
});
