import { flushSync, mount, unmount } from "svelte";
import type { User } from "oidc-client-ts";
import { afterEach, describe, expect, it, vi } from "vitest";

import { session } from "../lib/auth.svelte.js";
import { parse, route, startRouting } from "../lib/route.svelte.js";
import type { AdminAuditRow } from "../lib/types.js";
import AuditView from "./AuditView.svelte";

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

function auditRow(over: Partial<AdminAuditRow> = {}): AdminAuditRow {
  return {
    auditId: "0194f0a0-0000-7000-8000-000000000009",
    occurredAt: "2026-11-04T11:00:00Z",
    actor: "bob",
    action: "work.create",
    resource: "/api/works",
    decision: "deny",
    reason: null,
    detail: {},
    traceId: null,
    correlationId: "abc",
    ...over,
  };
}

let container: HTMLDivElement | null = null;
let app: Record<string, unknown> | null = null;
const calls: string[] = [];

function navigate(hash: string): void {
  location.hash = hash;
  route.route = parse(location.hash);
  flushSync();
}

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
  location.hash = "";
  route.route = parse("");
  calls.length = 0;
  vi.unstubAllGlobals();
});

function byTestId<T extends HTMLElement>(id: string): T {
  const el = container?.querySelector(`[data-testid="${id}"]`) as T | undefined;
  if (!el) {
    throw new Error(`no element with data-testid="${id}"`);
  }
  return el;
}

function signIn(): void {
  session.user = { access_token: jwt(["framework-admin"]) } as User;
  flushSync();
}

// go() writes location.hash; the store follows it through the hashchange
// listener main.ts installs — the tests need the same wiring.
startRouting();

function stub(rows: AdminAuditRow[]): void {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (path: string) => {
      calls.push(String(path));
      return json(rows);
    }),
  );
}

describe("AuditView", () => {
  it("keeps the decision cell on the wire word and links the correlation", async () => {
    signIn();
    stub([auditRow()]);
    navigate("#/audit?decision=deny&correlation=abc");

    mountView(AuditView);

    await vi.waitFor(() =>
      expect(byTestId("audit-decision-0194f0a0").textContent?.trim()).toBe("deny"),
    );
    expect(byTestId("audit-row-0194f0a0").textContent).toContain("work.create");
    expect(byTestId("audit-correlation-0194f0a0").getAttribute("href")).toBe(
      "#/audit?correlation=abc",
    );
    expect(byTestId("audit-export").getAttribute("href")).toBe(
      "/api/admin/audit/export?format=csv",
    );
    // the correlation box opens pre-filled from the link
    expect(byTestId<HTMLInputElement>("audit-correlation").value).toBe("abc");
    expect(calls[0]).toBe("/api/admin/audit?decision=deny&correlation=abc&limit=100");
  });

  it("a decision chip rewrites the hash, so a filtered trail stays a link", async () => {
    signIn();
    stub([]);
    navigate("#/audit");

    mountView(AuditView);
    await vi.waitFor(() =>
      expect(byTestId("audit-filter-all").getAttribute("aria-pressed")).toBe("true"),
    );

    byTestId("audit-filter-deny").click();

    expect(location.hash).toBe("#/audit?decision=deny");
    await vi.waitFor(() => expect(calls).toContain("/api/admin/audit?decision=deny&limit=100"));
  });

  it("a route carrying traceId filters the trail by that exact trace", async () => {
    const traceId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    signIn();
    stub([auditRow({ traceId })]);
    navigate(`#/audit?decision=deny&traceId=${traceId}`);

    mountView(AuditView);

    await vi.waitFor(() => expect(calls[0]).toBe(`/api/admin/audit?decision=deny&traceId=${traceId}&limit=100`));
    // the trace box opens pre-filled from the link, and the row links back
    expect(byTestId<HTMLInputElement>("audit-trace").value).toBe(traceId);
    expect(byTestId(`audit-trace-0194f0a0`).getAttribute("href")).toBe(
      `#/audit?traceId=${traceId}`,
    );
  });
});
