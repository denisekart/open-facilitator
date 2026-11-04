import { flushSync, mount, unmount } from "svelte";
import type { User } from "oidc-client-ts";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { Fetcher } from "../lib/api.js";
import { session } from "../lib/auth.svelte.js";
import { health } from "../lib/health.svelte.js";
import type { AdminSummary, EnrollmentTokenRow } from "../lib/types.js";
import EnrollmentTokensCard from "./EnrollmentTokensCard.svelte";
import LinksView from "./LinksView.svelte";

interface Call {
  path: string;
  method: string;
  body: string | null;
}

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

function row(over: Partial<EnrollmentTokenRow> = {}): EnrollmentTokenRow {
  return {
    id: "0194f0a0-0000-7000-8000-000000000001",
    label: "old box",
    team: "home",
    role: "gateway",
    createdAt: "2026-11-04T11:00:00Z",
    expiresAt: "2099-01-01T00:00:00Z",
    consumedAt: null,
    consumedEndpointId: null,
    revokedAt: null,
    ...over,
  };
}

let container: HTMLDivElement | null = null;
let app: Record<string, unknown> | null = null;

function mountComponent(component: object, props: Record<string, unknown> = {}): HTMLDivElement {
  container = document.createElement("div");
  document.body.append(container);
  app = mount(component as never, { target: container, props }) as unknown as Record<string, unknown>;
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

function setField(input: HTMLInputElement, value: string): void {
  input.value = value;
  input.dispatchEvent(new Event("input", { bubbles: true }));
  flushSync();
}

/** Sign in as the given realm roles and let the shared summary store answer. */
function signIn(roles: string[], teamSlugs: string[] = ["home"]): void {
  session.user = { access_token: jwt(roles) } as User;
  health.summary = { teams: teamSlugs.length, teamSlugs } as AdminSummary;
  flushSync();
}

describe("EnrollmentTokensCard", () => {
  it("mints: posts the form, shows the token once, warns, copies, refreshes the table", async () => {
    signIn(["framework-admin"]);
    const calls: Call[] = [];
    let listReads = 0;
    const fetcher: Fetcher = async (path, init) => {
      const method = init?.method ?? "GET";
      calls.push({ path, method, body: typeof init?.body === "string" ? init.body : null });
      if (method === "POST") {
        return json({ id: "t1", token: "et_abc123", expiresAt: "2026-11-04T12:00:00Z" }, 201);
      }
      listReads += 1;
      // the reload after mint must show the new row
      return json(listReads === 1 ? [] : [row({ id: "t1", label: "pc1" })]);
    };

    mountComponent(EnrollmentTokensCard, { fetcher });
    await vi.waitFor(() => expect(byTestId("token-table").textContent).toContain("no tokens"));

    setField(byTestId("token-label"), "pc1");
    expect(byTestId<HTMLButtonElement>("token-mint").disabled).toBe(false);
    byTestId("token-mint").click();

    // the literal renders exactly once, in its highlighted block, with the warning
    await vi.waitFor(() => expect(byTestId("minted-token-value").textContent).toBe("et_abc123"));
    expect(byTestId("minted-token-warning").textContent).toContain("shown only once");

    const post = calls.find((c) => c.method === "POST");
    expect(post?.path).toBe("/api/admin/enroll-tokens");
    expect(JSON.parse(post?.body ?? "{}")).toEqual({
      label: "pc1",
      team: "home",
      role: "gateway",
      ttlMinutes: 60,
    });

    // the list refreshed after mint and the new row reads as fresh
    await vi.waitFor(() => expect(byTestId("token-status-t1").textContent?.trim()).toBe("fresh"));
    expect(listReads).toBe(2);

    // copy wiring
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText },
    });
    byTestId("token-copy").click();
    await vi.waitFor(() => expect(byTestId("token-copy").textContent?.trim()).toBe("copied"));
    expect(writeText).toHaveBeenCalledWith("et_abc123");
  });

  it("revoke issues DELETE for the row id and reloads the list", async () => {
    signIn(["framework-admin"]);
    const calls: Call[] = [];
    const fetcher: Fetcher = async (path, init) => {
      const method = init?.method ?? "GET";
      calls.push({ path, method, body: null });
      return method === "DELETE" ? new Response(null, { status: 204 }) : json([row({ id: "t9" })]);
    };

    mountComponent(EnrollmentTokensCard, { fetcher });
    await vi.waitFor(() => expect(byTestId("token-revoke-t9")).toBeTruthy());

    byTestId("token-revoke-t9").click();
    await vi.waitFor(() =>
      expect(calls).toContainEqual({ path: "/api/admin/enroll-tokens/t9", method: "DELETE", body: null }),
    );
    // list loaded on mount + reloaded after the revoke
    await vi.waitFor(() =>
      expect(calls.filter((c) => c.method === "GET")).toHaveLength(2),
    );
  });

  it("surfaces a typed problem from a failed mint", async () => {
    signIn(["framework-admin"]);
    const fetcher: Fetcher = async (_path, init) =>
      (init?.method ?? "GET") === "POST"
        ? new Response(
            JSON.stringify({
              type: "https://errors.openfacilitator.org/v1/team-unknown",
              title: "Team unknown",
              status: 403,
              detail: "team 'attic' is not in the roster",
            }),
            { status: 403, headers: { "Content-Type": "application/problem+json" } },
          )
        : json([]);

    mountComponent(EnrollmentTokensCard, { fetcher });
    await vi.waitFor(() => expect(byTestId("token-table").textContent).toContain("no tokens"));

    setField(byTestId("token-label"), "attic box");
    byTestId("token-mint").click();

    await vi.waitFor(() =>
      expect(byTestId("forbidden").textContent).toContain("team-unknown"),
    );
    expect(container?.querySelector("[data-testid='minted-token']")).toBeNull();
  });
});

describe("LinksView enrollment card gating", () => {
  async function mountLinksViewAs(roles: string[]): Promise<void> {
    const fetcher = vi.fn(async (path: string) => {
      if (path.startsWith("/api/admin/enroll-tokens")) {
        throw new Error(`token list fetched for a non-admin (${roles.join(",")})`);
      }
      return json({ gateways: [], affectedAssignments: [] });
    });
    vi.stubGlobal("fetch", fetcher);
    signIn(roles);
    mountComponent(LinksView);
    flushSync();
  }

  it("renders no enrollment card for a kernel-operator-only principal", async () => {
    await mountLinksViewAs(["kernel-operator"]);
    expect(container?.querySelector("[data-testid='links-view']")).not.toBeNull();
    expect(container?.querySelector("[data-testid='enrollment-tokens-card']")).toBeNull();
  });

  it("renders the enrollment card for a framework-admin principal", async () => {
    await mountLinksViewAs(["framework-admin"]);
    await vi.waitFor(() =>
      expect(container?.querySelector("[data-testid='enrollment-tokens-card']")).not.toBeNull(),
    );
  });
});
