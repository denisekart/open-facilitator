import { afterEach, describe, expect, it, vi } from "vitest";

import { runningRow } from "./flows-test-harness.js";
import {
  byTestIdIn,
  cleanup,
  doneRow,
  mountView,
  signIn,
  stubFlows,
  type Call,
} from "./flows-test-harness.js";
import MonitorView from "./MonitorView.svelte";

// The Monitor owns the instance table and the ONE global live toggle. The
// fake-timer legs below are the proof of the live law: re-polls every 3 s
// only while live AND something runs or waits.

let container: HTMLDivElement | null = null;
let app: Record<string, unknown> | null = null;
let calls: Call[] = [];

afterEach(() => {
  cleanup(app, container);
  app = null;
  container = null;
  calls = [];
  vi.useRealTimers();
});

function mountIt(props?: object): HTMLDivElement {
  const mounted = mountView(MonitorView, props);
  container = mounted.container;
  app = mounted.app;
  return container;
}

const instanceCalls = (): number =>
  calls.filter((c) => c.path.includes("/instances") && !c.path.includes("/instances/")).length;

describe("MonitorView", () => {
  it("lists instances with the run vocabulary", async () => {
    signIn(["framework-admin"]);
    stubFlows(calls);

    mountIt();

    await vi.waitFor(() =>
      expect(byTestIdIn(container!, "instance-list").textContent).toContain("review-fork@3"),
    );
    const listed = byTestIdIn(container!, "instance-list").textContent ?? "";
    expect(listed).toContain("running");
    expect(listed).toContain("fork"); // the running badge names the current step
    expect(listed).toContain("steps-exhausted"); // a terminal row names its end reason
  });

  it("an instance row opens the monitor deep link", async () => {
    signIn(["framework-admin"]);
    stubFlows(calls);

    mountIt();
    await vi.waitFor(() =>
      expect(byTestIdIn(container!, "instance-list").textContent).toContain("review-fork"),
    );

    const row = [
      ...container!.querySelectorAll("[data-testid='instance-list'] tbody tr"),
    ][0] as HTMLTableRowElement;
    row.click();

    expect(location.hash).toBe(`#/monitor/runs/${runningRow.instanceId}`);
  });

  it("a def filter rides the request", async () => {
    signIn(["framework-admin"]);
    stubFlows(calls);

    mountIt({ defFilter: "assign-rule-forgejo-1" });

    await vi.waitFor(() =>
      expect(calls.some((c) => c.path.includes("/instances?def=assign-rule-forgejo-1"))).toBe(true),
    );
  });

  it("live AND running: the list re-polls every 3 s", async () => {
    vi.useFakeTimers();
    signIn(["framework-admin"]);
    stubFlows(calls, { items: [runningRow, doneRow] });

    mountIt();
    await vi.waitFor(() => expect(instanceCalls()).toBe(1));

    await vi.advanceTimersByTimeAsync(3000);
    await vi.waitFor(() => expect(instanceCalls()).toBe(2));
    await vi.advanceTimersByTimeAsync(3000);
    await vi.waitFor(() => expect(instanceCalls()).toBe(3));
  });

  it("live but NOTHING running: no poll at all", async () => {
    vi.useFakeTimers();
    signIn(["framework-admin"]);
    stubFlows(calls, { items: [doneRow] });

    mountIt();
    await vi.waitFor(() => expect(instanceCalls()).toBe(1));

    await vi.advanceTimersByTimeAsync(10_000);
    expect(instanceCalls()).toBe(1);
  });

  it("toggle OFF stops every further poll", async () => {
    vi.useFakeTimers();
    signIn(["framework-admin"]);
    stubFlows(calls, { items: [runningRow, doneRow] });

    mountIt();
    await vi.waitFor(() => expect(instanceCalls()).toBe(1));

    byTestIdIn<HTMLButtonElement>(container!, "live-toggle").click();
    await vi.advanceTimersByTimeAsync(10_000);
    expect(instanceCalls()).toBe(1);
    expect(
      byTestIdIn<HTMLButtonElement>(container!, "live-toggle").getAttribute("aria-pressed"),
    ).toBe("false");
  });
});
