import { afterEach, describe, expect, it, vi } from "vitest";

import {
  byTestIdIn,
  cleanup,
  detailOf,
  mountView,
  runningRow,
  signIn,
  stubFlows,
  type Call,
} from "./flows-test-harness.js";
import FlowDetail from "./FlowDetail.svelte";

// FlowDetail: the run's vertical checklist, lineage and the honest
// mutations. Lives at #/monitor/runs/<id> since the shell overhaul.

let container: HTMLDivElement | null = null;
let app: Record<string, unknown> | null = null;
let calls: Call[] = [];

afterEach(() => {
  cleanup(app, container);
  app = null;
  container = null;
  calls = [];
});

function mountDetail(props?: object): HTMLDivElement {
  const mounted = mountView(FlowDetail, props);
  container = mounted.container;
  app = mounted.app;
  return container;
}

describe("FlowDetail", () => {
  it("renders the vertical checklist and the lineage", async () => {
    signIn(["framework-admin"]);
    stubFlows(calls, {
      detail: detailOf({
        children: [
          {
            instanceId: "33333333-3333-4333-8333-333333333333",
            definitionId: "pr-on-manifest",
            definitionVersion: 2,
            status: "completed",
            currentStep: "pr",
            depth: 1,
            endReason: "steps-exhausted",
          },
        ],
      }),
    });

    mountDetail({ instanceId: runningRow.instanceId, onclose: () => undefined });

    await vi.waitFor(() =>
      expect(byTestIdIn(container!, "step-log").textContent).toContain("wait-review"),
    );
    const log = byTestIdIn(container!, "step-log").textContent ?? "";
    expect(log).toContain("fork");
    expect(log).toContain("completed");
    expect(log).toContain("waiting");
    await vi.waitFor(() =>
      expect(byTestIdIn(container!, "lineage").textContent).toContain("pr-on-manifest"),
    );
  });

  it("cancel posts with a reason and reports a refusal honestly", async () => {
    signIn(["framework-admin"]);
    stubFlows(calls, { cancelStatus: 409 });

    mountDetail({ instanceId: runningRow.instanceId, onclose: () => undefined });
    await vi.waitFor(() => expect(byTestIdIn(container!, "flow-cancel")).toBeTruthy());

    byTestIdIn<HTMLButtonElement>(container!, "flow-cancel").click();

    await vi.waitFor(() =>
      expect(calls.some((c) => c.method === "POST" && c.path.endsWith("/cancel"))).toBe(true),
    );
    const post = calls.find((c) => c.method === "POST");
    expect(post?.headers["x-change-reason"]).toBeTruthy();
    await vi.waitFor(() =>
      expect(byTestIdIn(container!, "flow-notice").textContent).toContain("not cancellable"),
    );
  });

  it("a read-only principal sees no cancel button", async () => {
    signIn(["ai-operator"]);
    stubFlows(calls);

    mountDetail({ instanceId: runningRow.instanceId, onclose: () => undefined });
    await vi.waitFor(() =>
      expect(byTestIdIn(container!, "step-log").textContent).toContain("wait-review"),
    );

    expect(container?.querySelector("[data-testid='flow-cancel']")).toBeNull();
  });
});
