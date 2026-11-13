import { flushSync, mount, unmount } from "svelte";
import type { User } from "oidc-client-ts";
import { afterEach, describe, expect, it, vi } from "vitest";

import { session } from "../lib/auth.svelte.js";
import HitDetail from "./HitDetail.svelte";

const ARTIFACT = "0194f0a0-0000-7000-8000-0000000000ab";

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

let container: HTMLDivElement | null = null;
let app: Record<string, unknown> | null = null;
let closed = 0;

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
  closed = 0;
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

function signIn(): void {
  session.user = { access_token: jwt(["framework-admin"]) } as User;
  flushSync();
}

describe("HitDetail", () => {
  it("renders artifact metadata and a plain content link", async () => {
    signIn();
    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        json({
          artifactId: ARTIFACT,
          mediaType: "application/json",
          size: 2048,
          state: "available",
        }),
      ),
    );

    mountComponent(HitDetail, { kind: "artifacts", id: ARTIFACT, onclose: () => (closed += 1) });

    await vi.waitFor(() =>
      expect(byTestId("hit-artifact-meta").textContent).toContain("application/json"),
    );
    expect(byTestId("hit-artifact-meta").textContent).toContain("2048 B");
    expect(byTestId<HTMLAnchorElement>("artifact-content-link").getAttribute("href")).toBe(
      `/api/artifacts/${ARTIFACT}/content`,
    );
    expect(byTestId<HTMLAnchorElement>("artifact-content-link").getAttribute("href")).toMatch(
      /\/content$/,
    );
  });

  it("names the kind it cannot show instead of rendering nothing", async () => {
    signIn();
    const fetcher = vi.fn(async () => json({}));
    vi.stubGlobal("fetch", fetcher);

    mountComponent(HitDetail, { kind: "assignments", id: ARTIFACT, onclose: () => (closed += 1) });

    expect(byTestId("hit-no-detail").textContent).toBe("No detail surface for this kind yet.");
    expect(fetcher).not.toHaveBeenCalled();
  });

  it("closes on the close button", async () => {
    signIn();
    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        json({ artifactId: ARTIFACT, mediaType: "text/plain", size: 1, state: "available" }),
      ),
    );

    mountComponent(HitDetail, { kind: "artifacts", id: ARTIFACT, onclose: () => (closed += 1) });
    await vi.waitFor(() => expect(byTestId("hit-detail")).toBeTruthy());

    byTestId("hit-detail-close").click();
    expect(closed).toBe(1);
  });
});
