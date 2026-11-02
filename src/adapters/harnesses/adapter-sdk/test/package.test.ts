// The published surface of @ofac/adapter: everything the locked §8.3 API
// names, reachable from the BUILT dist (what a third-party import sees).
import { describe, expect, it } from "vitest";

describe("adapter package surface", () => {
  it("the built entry exports the locked API", async () => {
    const mod = await import("../dist/index.js");
    expect(mod.ADAPTER_PACKAGE).toBe("@ofac/adapter");
    expect(typeof mod.createAdapter).toBe("function");
    expect(typeof mod.ExecutionRegistry).toBe("function");
    expect(mod.TERMINAL_TTL_MS).toBe(60_000);
    expect(mod.DEFAULT_RESTART_BACKOFF_MS).toEqual([100, 250, 500, 1000, 2000, 5000]);
    // The artifact capability (upload client + manifest builder).
    expect(typeof mod.createArtifactClient).toBe("function");
    expect(typeof mod.buildResultManifest).toBe("function");
    expect(typeof mod.buildCompletedEvent).toBe("function");
    expect(typeof mod.artifactUri).toBe("function");
    expect(mod.artifactUri("w", "a", "art-1")).toBe("artifact://w/a/art-1");
  });

  it("the ./testing entry exports the lifecycle contract kit", async () => {
    const mod = await import("../dist/testing.js");
    expect(typeof mod.runAdapterLifecycleContract).toBe("function");
    expect(typeof mod.kitSocketPath).toBe("function");
    expect(typeof mod.untilAborted).toBe("function");
  });
});
