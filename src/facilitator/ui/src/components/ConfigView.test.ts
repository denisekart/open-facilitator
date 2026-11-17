import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushSync, mount, unmount } from "svelte";
import type { User } from "oidc-client-ts";
import ConfigView from "./ConfigView.svelte";
import "../lib/test-harness.js";
import { session } from "../lib/auth.svelte.js";

describe("ConfigView", () => {
  let container: HTMLDivElement | null = null;
  let component: ReturnType<typeof mount> | null = null;

  beforeEach(() => {
    container = document.createElement("div");
    document.body.appendChild(container);
    const payload = btoa(JSON.stringify({ realm_access: { roles: ["framework-admin"] } }));
    session.user = { access_token: `header.${payload}.sig` } as User;
  });

  afterEach(() => {
    if (component) {
      unmount(component);
      component = null;
    }
    container?.remove();
    container = null;
    vi.restoreAllMocks();
  });

  it("renders categorized configuration policies", async () => {
    vi.spyOn(globalThis, "fetch").mockImplementation(async (info) => {
      const url = String(info);
      if (url.includes("/api/admin/config")) {
        return new Response(
          JSON.stringify([
            { resource: "retry-policy", exists: true, rowVersion: 1 },
            { resource: "nudge-policy", exists: false, rowVersion: 0 },
          ]),
          { status: 200, headers: { "Content-Type": "application/json" } },
        );
      }
      return new Response("{}", { status: 404 });
    });

    component = mount(ConfigView, { target: container! });
    flushSync();

    await vi.waitFor(() => {
      expect(container?.querySelector("[data-testid='config-view']")).toBeTruthy();
    });

    expect(container?.textContent).toContain("Operational & Delivery Policies");
    expect(container?.textContent).toContain("Routing & Operator Feedback");
    expect(container?.textContent).toContain("Assistant Runtime & Intelligence");
    expect(container?.querySelector("[data-testid='config-open-retry-policy']")).toBeTruthy();
  });
});
