import { afterEach, describe, expect, it, vi } from "vitest";

import FlowsView from "./FlowsView.svelte";
import {
  byTestIdIn,
  cleanup,
  mountView,
  signIn,
  stubFlows,
  type Call,
} from "./flows-test-harness.js";

// Flows is the editorial screen: definitions and their posture. Instances
// moved to the Monitor (MonitorView.test.ts proves that half).

let container: HTMLDivElement | null = null;
let app: Record<string, unknown> | null = null;
let calls: Call[] = [];

afterEach(() => {
  cleanup(app, container);
  app = null;
  container = null;
  calls = [];
});

function mountIt(): HTMLDivElement {
  const mounted = mountView(FlowsView);
  container = mounted.container;
  app = mounted.app;
  return container;
}

describe("FlowsView (definitions only)", () => {
  it("lists definitions with their posture, without any instance table", async () => {
    signIn(["framework-admin"]);
    stubFlows(calls);

    mountIt();

    await vi.waitFor(() =>
      expect(byTestIdIn(container!, "flows-view").textContent).toContain("review-fork"),
    );
    const defs = byTestIdIn(container!, "flows-view").textContent ?? "";
    expect(defs).toContain("live");
    expect(defs).toContain("shadow");
    expect(defs).toContain("off");
    // The instances table no longer belongs here.
    expect(container?.querySelector("[data-testid='instance-list']")).toBeNull();
    // Every row offers the two doors: the editor and the filtered monitor.
    const edit = [...container!.querySelectorAll("a")].find((a) => a.textContent === "edit");
    expect(edit?.getAttribute("href")).toBe("#/flows/review-fork");
    const runs = [...container!.querySelectorAll("a")].find((a) => a.textContent === "runs");
    expect(runs?.getAttribute("href")).toBe("#/monitor/runs?def=review-fork");
  });

  it("an author sees the new-flow door", () => {
    signIn(["framework-admin"]);
    stubFlows(calls);
    mountIt();
    const link = container!.querySelector("[data-testid='new-flow']") as HTMLAnchorElement;
    expect(link.getAttribute("href")).toBe("#/flows/new");
  });

  it("a read-only principal gets no authoring or edit doors", async () => {
    signIn(["ai-operator"]);
    stubFlows(calls);
    mountIt();
    await vi.waitFor(() =>
      expect(byTestIdIn(container!, "flows-view").textContent).toContain("review-fork"),
    );
    expect(container?.querySelector("[data-testid='new-flow']")).toBeNull();
    expect([...container!.querySelectorAll("a")].some((a) => a.textContent === "edit")).toBe(false);
  });
});
