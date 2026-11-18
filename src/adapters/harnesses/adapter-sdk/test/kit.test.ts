// Direct unit tier for the conformance kit's own exports: the determinism
// seam's abort half (`untilAborted`), the recommended temp socket path
// (`kitSocketPath`), and the stub kernel's full surface — every route, every
// refusal, every leg (artifact allocation + proxied PUT law, presigned
// direct-to-store + confirm law, the memory retain/recall legs, the honest
// 404/422 fall-throughs). These pin the kit's own machinery; the lifecycle
// contract itself runs against a real adapter in `lifecycle.test.ts`.
import { existsSync } from "node:fs";
import { createHash } from "node:crypto";
import { describe, expect, it } from "vitest";
import { kitSocketPath, startStubKernel, untilAborted } from "../src/testing.js";

const sha = (bytes: Uint8Array): string =>
  "sha256:" + createHash("sha256").update(bytes).digest("hex");

async function postEvent(
  url: string,
  type: string,
  data: Record<string, unknown>,
): Promise<Record<string, unknown>> {
  const response = await fetch(`${url}/api/link/v1/events`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      specversion: "1.0",
      id: `env-${Math.random().toString(36).slice(2)}`,
      source: "urn:facilitator:instance:unit",
      type,
      subject: `unit/${String(data["commandId"] ?? "x")}`,
      time: new Date().toISOString(),
      datacontenttype: "application/json",
      data,
    }),
  });
  return (await response.json()) as Record<string, unknown>;
}

describe("untilAborted", () => {
  it("resolves immediately for an already-aborted signal", async () => {
    const controller = new AbortController();
    controller.abort();
    await expect(untilAborted(controller.signal)).resolves.toBeUndefined();
  });

  it("resolves when a live signal aborts later", async () => {
    const controller = new AbortController();
    const settled = expect(untilAborted(controller.signal)).resolves.toBeUndefined();
    controller.abort();
    await settled;
  });

  it("stays pending (no early resolve) while a live signal never aborts", async () => {
    const controller = new AbortController();
    const settled = Promise.withResolvers<void>();
    void untilAborted(controller.signal).then(settled.resolve);
    // Microtask flushes only: a resolve would have fired; nothing can.
    for (let tick = 0; tick < 3; tick += 1) await Promise.resolve();
    expect(settled.promise).toBeDefined();
    // Abort now: the once-listener still carries the resolution.
    controller.abort();
    await expect(settled.promise).resolves.toBeUndefined();
  });
});

describe("kitSocketPath", () => {
  it("mints a fresh, existing temp dir per call under a child.sock name", () => {
    const first = kitSocketPath();
    const second = kitSocketPath();
    expect(first).not.toBe(second);
    expect(first.endsWith("child.sock")).toBe(true);
    expect(existsSync(first.slice(0, first.lastIndexOf("/")))).toBe(true);
    // Short enough for the sun_path budget.
    expect(first.length).toBeLessThan(108);
  });
});

describe("startStubKernel lifecycle", () => {
  it("listens on a loopback port, reports its url, and closes cleanly", async () => {
    const kernel = await startStubKernel();
    try {
      expect(kernel.url).toMatch(/^http:\/\/127\.0\.0\.1:\d+$/);
      expect(kernel.artifacts.size).toBe(0);
      expect(kernel.events).toEqual([]);
    } finally {
      await kernel.close();
    }
  });

  it("unknown routes and envelope types answer honestly (404 fall-through, blind-accept 202)", async () => {
    const kernel = await startStubKernel();
    try {
      const stray = await fetch(`${kernel.url}/nonsense`, { method: "DELETE" });
      expect(stray.status).toBe(404);
      const strayBody = (await stray.json()) as Record<string, unknown>;
      expect(strayBody["detail"]).toBe("stub: DELETE /nonsense");

      const ack = await postEvent(kernel.url, "org.facilitator.command.unknown.v1", {
        commandId: "cmd-stray",
      });
      expect(ack["status"]).toBe("accepted");
      expect(String(ack["deliveryId"]).length).toBeGreaterThan(0);
      // Every envelope is retained for consumer assertions.
      expect(kernel.events).toHaveLength(1);
      expect(kernel.events[0]?.["type"]).toBe("org.facilitator.command.unknown.v1");
    } finally {
      await kernel.close();
    }
  });
});

describe("startStubKernel artifact legs", () => {
  const BYTES = new TextEncoder().encode("stub kernel bytes");

  it("request_upload allocates a slot; a pending slot and a never-seen id both 404 the GET", async () => {
    const kernel = await startStubKernel();
    try {
      const ack = await postEvent(kernel.url, "org.facilitator.command.artifact.request_upload.v1", {
        commandId: "cmd-alloc-1",
        mediaType: "text/plain",
        digest: sha(BYTES),
      });
      expect(ack["status"]).toBe("accepted");
      const data = ack["data"] as Record<string, unknown>;
      const artifactId = String(data["artifactId"]);
      expect(artifactId).toMatch(/^[0-9a-f]{32}$/);
      expect(data["uploadLocation"]).toBe(`/api/link/v1/artifacts/${artifactId}/content`);
      expect(data["method"]).toBe("POST");
      expect(typeof data["expiresAt"]).toBe("string");

      // Pending allocation: the row exists, the bytes never landed.
      const pending = await fetch(`${kernel.url}/api/link/v1/artifacts/${artifactId}/content`);
      expect(pending.status).toBe(404);
      // Never-seen id.
      expect(
        (await fetch(`${kernel.url}/api/link/v1/artifacts/0000000000000000000000000000000f/content`))
          .status,
      ).toBe(404);

      // Re-delivery of the same commandId re-attaches the live ticket.
      const again = await postEvent(kernel.url, "org.facilitator.command.artifact.request_upload.v1", {
        commandId: "cmd-alloc-1",
        mediaType: "text/plain",
        digest: sha(BYTES),
      });
      expect(again["status"]).toBe("duplicate");
      expect((again["data"] as Record<string, unknown>)["artifactId"]).toBe(artifactId);
    } finally {
      await kernel.close();
    }
  });

  it("proxied PUT: stores on a matching digest, 404s an unallocated id, 422s a mismatch", async () => {
    const kernel = await startStubKernel();
    try {
      const ack = await postEvent(kernel.url, "org.facilitator.command.artifact.request_upload.v1", {
        commandId: "cmd-put-1",
        mediaType: "application/octet-stream",
        digest: sha(BYTES),
      });
      const artifactId = String((ack["data"] as Record<string, unknown>)["artifactId"]);
      const put = await fetch(`${kernel.url}/api/link/v1/artifacts/${artifactId}/content`, {
        method: "PUT",
        headers: { "content-type": "text/markdown; charset=utf-8" },
        body: BYTES,
      });
      expect(put.status).toBe(200);
      const stored = (await put.json()) as Record<string, unknown>;
      expect(stored["digest"]).toBe(sha(BYTES));
      expect(stored["state"]).toBe("stored");
      expect(stored["size"]).toBe(BYTES.byteLength);
      // The declared content-type wins (parameter stripped), like the kernel.
      expect(kernel.artifacts.get(artifactId)?.mediaType).toBe("text/markdown");
      // The stored row now streams on the link-plane GET.
      const get = await fetch(`${kernel.url}/api/link/v1/artifacts/${artifactId}/content`);
      expect(get.status).toBe(200);
      expect(get.headers.get("content-type")).toBe("text/markdown");
      expect(new Uint8Array(await get.arrayBuffer())).toEqual(BYTES);

      // Unallocated id: no slot, no store.
      const orphan = await fetch(
        `${kernel.url}/api/link/v1/artifacts/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/content`,
        { method: "PUT", body: BYTES },
      );
      expect(orphan.status).toBe(404);

      // Digest mismatch: the kernel's law refuses the bytes.
      const ack2 = await postEvent(kernel.url, "org.facilitator.command.artifact.request_upload.v1", {
        commandId: "cmd-put-2",
        mediaType: "text/plain",
        digest: sha(BYTES),
      });
      const id2 = String((ack2["data"] as Record<string, unknown>)["artifactId"]);
      const bad = await fetch(`${kernel.url}/api/link/v1/artifacts/${id2}/content`, {
        method: "PUT",
        body: new TextEncoder().encode("different bytes"),
      });
      expect(bad.status).toBe(422);
      expect(((await bad.json()) as Record<string, unknown>)["detail"]).toContain("digest mismatch");
    } finally {
      await kernel.close();
    }
  });

  it("configured to reject even a matching digest (failure legs are testable)", async () => {
    const kernel = await startStubKernel({ rejectDigest: true });
    try {
      const ack = await postEvent(kernel.url, "org.facilitator.command.artifact.request_upload.v1", {
        commandId: "cmd-rej-1",
        mediaType: "text/plain",
        digest: sha(BYTES),
      });
      const artifactId = String((ack["data"] as Record<string, unknown>)["artifactId"]);
      const put = await fetch(`${kernel.url}/api/link/v1/artifacts/${artifactId}/content`, {
        method: "PUT",
        body: BYTES,
      });
      expect(put.status).toBe(422);
      expect(((await put.json()) as Record<string, unknown>)["detail"]).toBe(
        "stub configured to reject",
      );
    } finally {
      await kernel.close();
    }
  });

  it("presigned: bytes land with no authority, confirm re-hashes and settles", async () => {
    const kernel = await startStubKernel({ presign: true });
    try {
      const ack = await postEvent(kernel.url, "org.facilitator.command.artifact.request_upload.v1", {
        commandId: "cmd-pre-1",
        mediaType: "text/plain",
        digest: sha(BYTES),
      });
      const data = ack["data"] as Record<string, unknown>;
      const artifactId = String(data["artifactId"]);
      expect(data["method"]).toBe("PUT");
      expect(data["uploadUrl"]).toBe(`${kernel.url}/presigned/${artifactId}`);

      const put = await fetch(String(data["uploadUrl"]), { method: "PUT", body: BYTES });
      expect(put.status).toBe(200);
      // Direct-to-store law: before the confirm the slot is NOT stored.
      expect(
        (await fetch(`${kernel.url}/api/link/v1/artifacts/${artifactId}/content`)).status,
      ).toBe(404);

      const confirm = await postEvent(kernel.url, "org.facilitator.command.artifact.confirm_upload.v1", {
        commandId: "cmd-pre-cfm-1",
        artifactId,
      });
      expect(confirm["status"]).toBe("confirmed");
      expect(kernel.artifacts.get(artifactId)?.bytes).toEqual(Buffer.from(BYTES));
    } finally {
      await kernel.close();
    }
  });

  it("confirm refuses an unsettled slot: no bytes, or a re-hash mismatch", async () => {
    const kernel = await startStubKernel({ presign: true });
    try {
      // No direct bytes ever landed for this id (unknown row too).
      const ghost = await postEvent(kernel.url, "org.facilitator.command.artifact.confirm_upload.v1", {
        commandId: "cmd-ghost",
        artifactId: "ffffffffffffffffffffffffffffffff",
      });
      expect(ghost["status"]).toBe("failed");

      // Bytes landed but differ from the declared digest.
      const ack = await postEvent(kernel.url, "org.facilitator.command.artifact.request_upload.v1", {
        commandId: "cmd-pre-2",
        mediaType: "text/plain",
        digest: sha(BYTES),
      });
      const artifactId = String((ack["data"] as Record<string, unknown>)["artifactId"]);
      await fetch(`${kernel.url}/presigned/${artifactId}`, {
        method: "PUT",
        body: new TextEncoder().encode("tampered bytes"),
      });
      const mismatch = await postEvent(kernel.url, "org.facilitator.command.artifact.confirm_upload.v1", {
        commandId: "cmd-pre-cfm-2",
        artifactId,
      });
      expect(mismatch["status"]).toBe("failed");
      // The slot never settled: the link-plane GET still has nothing to stream.
      expect(
        (await fetch(`${kernel.url}/api/link/v1/artifacts/${artifactId}/content`)).status,
      ).toBe(404);
    } finally {
      await kernel.close();
    }
  });
});

describe("startStubKernel memory legs", () => {
  it("retain stores in-band as a candidate; recall substring-matches the D8 shape", async () => {
    const kernel = await startStubKernel();
    try {
      const retained = await postEvent(kernel.url, "org.facilitator.command.memory.retain.v1", {
        commandId: "cmd-retain-1",
        content: "The kit stub stores content",
        scopeSelector: "team:kit",
      });
      expect(retained["status"]).toBe("accepted");
      const receipt = retained["data"] as Record<string, unknown>;
      expect(String(receipt["entryId"])).toMatch(/^[0-9a-f-]{36}$/);
      expect(receipt["status"]).toBe("candidate");
      expect(receipt["duplicate"]).toBe(false);
      expect(kernel.memories.get(String(receipt["entryId"]))?.scopeSelector).toBe("team:kit");

      await postEvent(kernel.url, "org.facilitator.command.memory.retain.v1", {
        commandId: "cmd-retain-2",
        content: "Unrelated note",
        scopeSelector: "team:other",
      });

      const recalled = await postEvent(kernel.url, "org.facilitator.command.memory.recall.v1", {
        commandId: "cmd-recall-1",
        query: "KIT STUB", // case-insensitive substring
      });
      const data = recalled["data"] as Record<string, unknown>;
      expect(String(data["requestId"]).length).toBeGreaterThan(0);
      expect(data["providerProfile"]).toBe("postgres-fts");
      expect(data["truncated"]).toBe(false);
      const entries = data["entries"] as Record<string, unknown>[];
      expect(entries).toHaveLength(1);
      expect(entries[0]?.["content"]).toBe("The kit stub stores content");
      expect(entries[0]?.["scopeId"]).toBe("team:kit");
      expect(entries[0]?.["status"]).toBe("candidate");
    } finally {
      await kernel.close();
    }
  });
});
