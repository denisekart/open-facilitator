import { flushSync, mount, unmount } from "svelte";
import type { User } from "oidc-client-ts";
import { afterEach, describe, expect, it, vi } from "vitest";

import { session } from "../lib/auth.svelte.js";
import type { AdminProvisioningRow, AdminReplicasView } from "../lib/types.js";
import RuntimeView from "./RuntimeView.svelte";

function jwt(roles: string[]): string {
  const b64 = (input: string) =>
    btoa(input).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return `${b64('{"alg":"RS256"}')}.${b64(JSON.stringify({ realm_access: { roles } }))}.sig`;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

const replicas: AdminReplicasView = {
  answeringReplicaId: "r-1",
  replicas: [
    { replicaId: "r-1", liveSessions: 2, socketsHere: 1 },
    { replicaId: "r-2", liveSessions: 0, socketsHere: 0 },
  ],
  outboxPending: 3,
};

const ledger: AdminProvisioningRow = {
  operationId: "op-1",
  workloadId: "wl-1",
  team: "home",
  operation: "scale-out",
  state: "pending",
  workId: null,
  assignmentId: null,
  generation: null,
  reason: "queue depth",
  createdAt: "2026-11-04T11:00:00Z",
  updatedAt: "2026-11-04T11:00:00Z",
};

interface Call {
  path: string;
  method: string;
  headers: Record<string, string>;
}

let container: HTMLDivElement | null = null;
let app: Record<string, unknown> | null = null;
let calls: Call[] = [];

function mountView(component: object): HTMLDivElement {
  container = document.createElement("div");
  document.body.append(container);
  app = mount(component as never, { target: container }) as unknown as Record<string, unknown>;
  flushSync();
  return container;
}

afterEach(() => {
  if (app !== null) {
    unmount(app as never);
    app = null;
  }
  container?.remove();
  container = null;
  session.user = null;
  calls = [];
  vi.unstubAllGlobals();
});

function byTestId<T extends HTMLElement>(id: string): T {
  const el = container?.querySelector(`[data-testid="${id}"]`) as T | undefined;
  if (!el) {
    throw new Error(`no element with data-testid="${id}"`);
  }
  return el;
}

function signIn(roles: string[]): void {
  session.user = { access_token: jwt(roles) } as User;
  flushSync();
}

function stubReads(): void {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (path: string, init?: RequestInit) => {
      const method = init?.method ?? "GET";
      calls.push({
        path: String(path),
        method,
        headers: Object.fromEntries(
          [...new Headers(init?.headers).entries()].map(([k, v]) => [k.toLowerCase(), v]),
        ),
      });
      if (method === "POST") {
        return json({ sweep: String(path).split("/").pop(), effects: 2 });
      }
      return String(path).endsWith("/replicas") ? json(replicas) : json({ items: [ledger] });
    }),
  );
}

describe("RuntimeView", () => {
  it("marks the answering replica and lists the provisioning ledger", async () => {
    signIn(["framework-admin"]);
    stubReads();

    mountView(RuntimeView);

    await vi.waitFor(() => expect(byTestId("replica-r-1").textContent).toContain("answering"));
    expect(byTestId("replica-r-2").textContent).not.toContain("answering");
    await vi.waitFor(() =>
      expect(byTestId("provisioning-op-1").textContent).toContain("scale-out"),
    );
  });

  it("a sweep needs a reason and reports its effects", async () => {
    signIn(["framework-admin"]);
    stubReads();

    mountView(RuntimeView);
    await vi.waitFor(() => expect(byTestId("replica-r-1")).toBeTruthy());

    byTestId("sweep-nudge").click();
    flushSync();
    expect(byTestId<HTMLButtonElement>("sweep-confirm-nudge").disabled).toBe(true);

    const reason = byTestId<HTMLInputElement>("sweep-reason");
    reason.value = "manual triage";
    reason.dispatchEvent(new Event("input", { bubbles: true }));
    flushSync();
    expect(byTestId<HTMLButtonElement>("sweep-confirm-nudge").disabled).toBe(false);

    byTestId("sweep-confirm-nudge").click();

    await vi.waitFor(() =>
      expect(byTestId("sweep-notice").textContent?.trim()).toBe("ran nudge — 2 effects"),
    );
    const post = calls.find((c) => c.method === "POST");
    expect(post?.path).toBe("/api/admin/sweeps/nudge");
    expect(post?.headers["x-change-reason"]).toBe("manual triage");
  });

  it("renders no sweep buttons for a read-only principal", async () => {
    signIn(["ai-operator"]);
    stubReads();

    mountView(RuntimeView);
    await vi.waitFor(() => expect(byTestId("replica-r-1")).toBeTruthy());

    expect(container?.querySelector("[data-testid='sweep-nudge']")).toBeNull();
  });
});
