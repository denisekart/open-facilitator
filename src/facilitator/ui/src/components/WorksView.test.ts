import { flushSync, mount, unmount } from "svelte";
import type { User } from "oidc-client-ts";
import { afterEach, describe, expect, it, vi } from "vitest";

import { session } from "../lib/auth.svelte.js";
import { health } from "../lib/health.svelte.js";
import { parse, route, startRouting } from "../lib/route.svelte.js";
import type { AdminSummary, AdminWorkRow } from "../lib/types.js";
import WorksView from "./WorksView.svelte";

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

function row(over: Partial<AdminWorkRow> = {}): AdminWorkRow {
  return {
    workId: "0194f0a0-0000-7000-8000-000000000001",
    objective: "triage the stuck consent",
    state: "blocked",
    version: 4,
    createdAt: "2026-11-04T11:00:00Z",
    updatedAt: "2026-11-04T11:30:00Z",
    teamSlug: "home",
    ...over,
  };
}

let container: HTMLDivElement | null = null;
let app: Record<string, unknown> | null = null;

function mountComponent(component: object, props: Record<string, unknown> = {}): HTMLDivElement {
  container = document.createElement("div");
  document.body.append(container);
  app = mount(component as never, { target: container, props }) as unknown as Record<
    string,
    unknown
  >;
  flushSync();
  return container;
}

function cleanup(): void {
  if (app !== null) {
    unmount(app as never);
    app = null;
  }
  container?.remove();
  container = null;
  session.user = null;
  health.summary = null;
  location.hash = "";
  route.route = parse("");
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
}

afterEach(cleanup);

function byTestId<T extends HTMLElement>(id: string): T {
  const el = container?.querySelector(`[data-testid="${id}"]`) as T | undefined;
  if (!el) {
    throw new Error(`no element with data-testid="${id}"`);
  }
  return el;
}

/** Navigate the way main.ts does, minus the async hashchange timing. */
function navigate(hash: string): void {
  location.hash = hash;
  route.route = parse(location.hash);
  flushSync();
}

// go() writes location.hash; the store follows it through the hashchange
// listener main.ts installs — the tests need the same wiring.
startRouting();

/** Sign in and let the shared summary store answer the team dropdown. */
function signIn(roles: string[]): void {
  session.user = { access_token: jwt(roles) } as User;
  health.summary = { teams: 1, teamSlugs: ["home"] } as AdminSummary;
  flushSync();
}

describe("WorksView", () => {
  it("renders a queue row for the hash filter and badges its state", async () => {
    signIn(["framework-admin"]);
    const calls: string[] = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (path: string) => {
        calls.push(String(path));
        return json({ items: [row()] });
      }),
    );
    navigate("#/works?state=blocked");
    mountComponent(WorksView);

    await vi.waitFor(() =>
      expect(byTestId("work-row-0194f0a0").textContent).toContain("triage the stuck consent"),
    );
    expect(byTestId("work-state-0194f0a0").textContent).toContain("blocked");
    expect(calls).toContain("/api/admin/works?state=blocked&limit=50");
    expect(byTestId<HTMLButtonElement>("works-filter-blocked").getAttribute("aria-pressed")).toBe(
      "true",
    );
  });

  it("says so when the filter matches nothing", async () => {
    signIn(["framework-admin"]);
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => json({ items: [] })),
    );
    navigate("#/works?state=review");

    mountComponent(WorksView);

    await vi.waitFor(() =>
      expect(byTestId("works-view").textContent).toContain("Nothing here — the queue is clear."),
    );
  });

  it("rewrites the hash on a chip click, keeping the queue shareable", async () => {
    signIn(["framework-admin"]);
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => json({ items: [] })),
    );
    navigate("#/works");

    mountComponent(WorksView);
    await vi.waitFor(() =>
      expect(byTestId("works-filter-all").getAttribute("aria-pressed")).toBe("true"),
    );

    byTestId("works-filter-review").click();

    expect(location.hash).toBe("#/works?state=review");
    await vi.waitFor(() =>
      expect(byTestId<HTMLButtonElement>("works-filter-review").getAttribute("aria-pressed")).toBe(
        "true",
      ),
    );
  });
});
