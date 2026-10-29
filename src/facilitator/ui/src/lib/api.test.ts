import { describe, expect, it, vi } from "vitest";

import { ApiError, makeApiFetch, toApiError } from "./api.js";
import { describeConflict, localValidationError, CONFIG_RESOURCES } from "./config-shapes.js";

function problemResponse(
  status: number,
  code: string,
  title: string,
  detail: string | null,
): Response {
  return new Response(
    JSON.stringify({
      type: `https://errors.openfacilitator.org/v1/${code}`,
      title,
      status,
      ...(detail === null ? {} : { detail }),
    }),
    { status, headers: { "Content-Type": "application/problem+json" } },
  );
}

describe("toApiError", () => {
  it("maps ProblemDetails type tail to the code", async () => {
    const error = await toApiError(
      problemResponse(
        412,
        "config-version-conflict",
        "Config version conflict",
        "stored rowVersion 7",
      ),
    );
    expect(error).toBeInstanceOf(ApiError);
    expect(error.code).toBe("config-version-conflict");
    expect(error.title).toBe("Config version conflict");
    expect(error.detail).toBe("stored rowVersion 7");
    expect(error.status).toBe(412);
  });

  it("falls back to a synthetic code for non-JSON bodies", async () => {
    const error = await toApiError(new Response("boom", { status: 503 }));
    expect(error.code).toBe("unknown");
    expect(error.status).toBe(503);
  });
});

describe("makeApiFetch", () => {
  it("renews once on 401 and replays the request with the fresh token", async () => {
    const sent: (string | null)[] = [];
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      const auth = new Headers(init?.headers).get("Authorization");
      sent.push(auth);
      return auth === "Bearer fresh"
        ? new Response("{}", { status: 200 })
        : problemResponse(401, "unauthorized", "Unauthorized", null);
    });
    vi.stubGlobal("fetch", fetchMock);
    try {
      const fetcher = makeApiFetch(
        async () => "stale",
        async () => "fresh",
      );
      const response = await fetcher("/api/admin/summary");
      expect(response.status).toBe(200);
      expect(sent).toEqual(["Bearer stale", "Bearer fresh"]);
      expect(fetchMock).toHaveBeenCalledTimes(2);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("never retries twice when the renew also 401s", async () => {
    const fetchMock = vi.fn(async () => problemResponse(401, "unauthorized", "Unauthorized", null));
    vi.stubGlobal("fetch", fetchMock);
    try {
      const fetcher = makeApiFetch(
        async () => "stale",
        async () => "also-stale",
      );
      const response = await fetcher("/api/admin/summary");
      expect(response.status).toBe(401);
      expect(fetchMock).toHaveBeenCalledTimes(2);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});

describe("config state machine", () => {
  it("rejects minWorkers above maxWorkers before the wire", () => {
    const spec = CONFIG_RESOURCES.find((r) => r.resource === "workload-scale");
    expect(spec).toBeDefined();
    expect(localValidationError(spec!, { minWorkers: 5, maxWorkers: 2 })).toContain("minWorkers");
    expect(localValidationError(spec!, { minWorkers: 1, maxWorkers: 2 })).toBeNull();
  });

  it("explains a conflict with author and reason", () => {
    expect(
      describeConflict({
        kind: "conflict",
        author: "alice",
        reason: "tune staleness",
        rowVersion: 7,
      }),
    ).toBe("changed by alice (tune staleness) — re-apply your edit");
  });
});
