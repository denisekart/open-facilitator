// The link-plane error legs of the SDK's kernel-facing clients: every
// honest refusal path the happy-path suites can't reach against a healthy
// stub — failed event posts, ticketless and receiptless acks, a confirm the
// kernel refuses, a content GET on a missing row, the unreadable-body
// fallback, the pending/partial context-fetch ack shapes, and the
// env-triggered kernel leg standing down when the env names no endpoint.
import { describe, expect, it } from "vitest";
import { createArtifactClient, fetchContext } from "../src/artifacts.js";
import { createMemoryClient } from "../src/memory.js";
import { kernelLegFromEnv } from "../src/kernel-leg.js";
import { startStubKernel } from "../src/testing.js";

/** A fetch that answers fixed shapes without touching the network. */
function scriptedFetch(
  handler: (url: string, init?: RequestInit) => Response,
): typeof fetch {
  return (async (input: RequestInfo | URL, init?: RequestInit) =>
    handler(String(input), init)) as unknown as typeof fetch;
}

/** A response whose body cannot be read (the safeBody fallback). */
const UNREADABLE = {
  ok: false,
  status: 503,
  text: () => Promise.reject(new Error("connection reset")),
} as unknown as Response;

const BYTES = new TextEncoder().encode("error-leg bytes");

describe("artifact client failure legs", () => {
  it("a failed event post throws with the status and body", async () => {
    const client = createArtifactClient({
      baseUrl: "http://kernel.test",
      token: "t",
      fetchImpl: scriptedFetch(() => new Response("kernel on fire", { status: 500 })),
    });
    await expect(client.upload(BYTES, { mediaType: "text/plain" })).rejects.toThrow(
      /link-plane event post failed: 500 kernel on fire/,
    );
  });

  it("an ack with no ticket is refused before any bytes move", async () => {
    const client = createArtifactClient({
      baseUrl: "http://kernel.test",
      token: "t",
      fetchImpl: scriptedFetch(() => Response.json({ status: "accepted" }, { status: 202 })),
    });
    await expect(client.upload(BYTES, { mediaType: "text/plain" })).rejects.toThrow(
      /request_upload ack carried no ticket/,
    );
  });

  it("a refused response with an unreadable body still reports honestly", async () => {
    const client = createArtifactClient({
      baseUrl: "http://kernel.test",
      token: "t",
      fetchImpl: scriptedFetch(() => UNREADABLE),
    });
    await expect(client.upload(BYTES, { mediaType: "text/plain" })).rejects.toThrow(
      /link-plane event post failed: 503 <no body>/,
    );
  });

  it("presigned upload the kernel refuses at confirm time throws", async () => {
    const kernel = await startStubKernel({ presign: true, rejectDigest: true });
    try {
      const client = createArtifactClient({ baseUrl: kernel.url, token: "t" });
      await expect(client.upload(BYTES, { mediaType: "text/plain" })).rejects.toThrow(
        /artifact confirm failed/,
      );
    } finally {
      await kernel.close();
    }
  });

  it("download reads a stored row back and throws on a missing one", async () => {
    const kernel = await startStubKernel();
    try {
      const client = createArtifactClient({ baseUrl: kernel.url, token: "t" });
      const upload = await client.upload(BYTES, { mediaType: "text/plain" });
      expect(new Uint8Array(await client.download(upload.artifactId))).toEqual(BYTES);
      await expect(client.download("0000000000000000000000000000000a")).rejects.toThrow(
        /artifact GET failed: 404/,
      );
    } finally {
      await kernel.close();
    }
  });
});

describe("fetchContext", () => {
  it("parses inline items and marks pending thread legs", async () => {
    const client = {
      baseUrl: "http://kernel.test",
      token: async () => "live",
      fetchImpl: scriptedFetch(() =>
        Response.json(
          {
            status: "accepted",
            data: {
              fetchId: "fetch-1",
              items: [{ uri: "memory:/entry-1", content: { a: 1 } }],
              pending: [{ uri: "thread://chat/1" }],
            },
          },
          { status: 202 },
        ),
      ),
    };
    const ack = await fetchContext(client, ["memory:/entry-1", "thread://chat/1"], {
      workId: "work-1",
      targetId: "team-alpha",
    });
    expect(ack.fetchId).toBe("fetch-1");
    expect(ack.items).toHaveLength(2);
    expect(ack.items[0]?.content).toEqual({ a: 1 });
    expect(ack.items[1]).toEqual({ uri: "thread://chat/1", pending: true });
  });

  it("a bare ack parses to an empty honest answer", async () => {
    const kernel = await startStubKernel(); // the stub blind-accepts the command
    try {
      const ack = await fetchContext({ baseUrl: kernel.url, token: "t" }, ["memory:/x"]);
      expect(ack).toEqual({ fetchId: "", items: [] });
      expect(kernel.events[0]?.["type"]).toBe("org.facilitator.command.context.fetch.v1");
    } finally {
      await kernel.close();
    }
  });

  it("a failed post throws with the status", async () => {
    await expect(
      fetchContext(
        {
          baseUrl: "http://kernel.test",
          token: "t",
          fetchImpl: scriptedFetch(() => UNREADABLE),
        },
        ["memory:/x"],
      ),
    ).rejects.toThrow(/context fetch post failed: 503 <no body>/);
  });
});

describe("memory client failure legs", () => {
  const provenance = {
    source: "workspace:gw.child",
    evidenceRef: "identity:evidence/ref-1",
    createdBy: "oidc:issuer|alice",
    createdAt: "2026-10-27T18:00:00Z",
  };

  it("a refused post reports the unreadable body honestly", async () => {
    const client = createMemoryClient({
      baseUrl: "http://kernel.test",
      token: "t",
      fetchImpl: scriptedFetch(() => UNREADABLE),
    });
    await expect(
      client.retain({
        scopeSelector: "team:alpha",
        type: "observed_fact",
        content: "x",
        provenance,
      }),
    ).rejects.toThrow(/link-plane event post failed: 503 <no body>/);
  });

  it("a retain ack without a receipt throws", async () => {
    const client = createMemoryClient({
      baseUrl: "http://kernel.test",
      token: "t",
      fetchImpl: scriptedFetch(() => Response.json({ status: "accepted" }, { status: 202 })),
    });
    await expect(
      client.retain({
        scopeSelector: "team:alpha",
        type: "observed_fact",
        content: "x",
        provenance,
      }),
    ).rejects.toThrow(/retain ack carried no receipt/);
  });

  it("a recall ack without a response throws", async () => {
    const client = createMemoryClient({
      baseUrl: "http://kernel.test",
      token: "t",
      fetchImpl: scriptedFetch(() => Response.json({ status: "accepted" }, { status: 202 })),
    });
    await expect(client.recall({ query: "x", purpose: "assignment.context" })).rejects.toThrow(
      /recall ack carried no response/,
    );
  });
});

describe("kernelLegFromEnv", () => {
  it("stands down (undefined) when the env names no kernel endpoint", async () => {
    expect(
      await kernelLegFromEnv({
        stateDir: "/tmp",
        kernelUrl: "http://127.0.0.1:1",
        env: { OFAC_KERNEL_ENDPOINT: "" },
      }),
    ).toBeUndefined();
    expect(
      await kernelLegFromEnv({ stateDir: "/tmp", kernelUrl: "http://127.0.0.1:1", env: {} }),
    ).toBeUndefined();
  });
});
