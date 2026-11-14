import { flushSync, mount, unmount } from "svelte";
import type { User } from "oidc-client-ts";
import { afterEach, describe, expect, it, vi } from "vitest";

import { session } from "../lib/auth.svelte.js";
import type {
  WorkflowDefinitionRow,
  WorkflowInstanceRow,
  WorkflowInstanceDetail,
  WorkflowStepLogRow,
} from "../lib/types.js";
import FlowDetail from "./FlowDetail.svelte";
import FlowsView from "./FlowsView.svelte";

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

const live: WorkflowDefinitionRow = {
  id: "review-fork",
  version: 3,
  title: "Review fork",
  description: "fork a review onto a second endpoint",
  source: "gallery",
  enabled: true,
  shadow: false,
  team: null,
  digest: "sha256:aaaa",
  instancesRunning: 2,
};

const shadow: WorkflowDefinitionRow = {
  ...live,
  id: "assign-rule-forgejo-1",
  title: "Forgejo issue #1",
  source: "import",
  shadow: true,
  instancesRunning: 0,
};

const off: WorkflowDefinitionRow = {
  ...live,
  id: "pr-on-manifest",
  title: "PR on manifest",
  enabled: false,
  instancesRunning: 0,
};

const runningRow: WorkflowInstanceRow = {
  instanceId: "11111111-1111-4111-8111-111111111111",
  definitionId: "review-fork",
  definitionVersion: 3,
  status: "running",
  currentStep: "fork",
  depth: 0,
  parentInstanceId: null,
  primaryWorkId: "aaaaaaaa-0000-4000-8000-000000000001",
  endReason: null,
  startedAt: "2026-11-14T09:00:00Z",
  updatedAt: "2026-11-14T09:05:00Z",
};

const doneRow: WorkflowInstanceRow = {
  ...runningRow,
  instanceId: "22222222-2222-4222-8222-222222222222",
  definitionId: "assign-rule-forgejo-1",
  definitionVersion: 1,
  status: "completed",
  currentStep: "ack",
  endReason: "steps-exhausted",
};

function detailOf(overrides: Partial<WorkflowInstanceDetail> = {}): WorkflowInstanceDetail {
  return {
    ...runningRow,
    invocationPath: "$",
    output: null,
    ancestors: [],
    children: [],
    ...overrides,
  };
}

const stepLog: { items: WorkflowStepLogRow[] } = {
  items: [
    {
      seq: 1,
      stepId: "fork",
      status: "completed",
      effectCommandId: "wf-1:fork",
      output: '{"workId":"w-2"}',
      startedAt: "2026-11-14T09:00:00Z",
      updatedAt: "2026-11-14T09:01:00Z",
    },
    {
      seq: 2,
      stepId: "wait-review",
      status: "waiting",
      effectCommandId: null,
      output: null,
      startedAt: "2026-11-14T09:01:00Z",
      updatedAt: "2026-11-14T09:01:00Z",
    },
  ],
};

interface Call {
  path: string;
  method: string;
  headers: Record<string, string>;
}

let container: HTMLDivElement | null = null;
let app: Record<string, unknown> | null = null;
let calls: Call[] = [];

function mountView(component: object, props?: object): HTMLDivElement {
  container = document.createElement("div");
  document.body.append(container);
  app = mount(component as never, {
    target: container,
    props: props as never,
  }) as unknown as Record<string, unknown>;
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
  location.hash = "";
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

function stubFlows(opts: { detail?: WorkflowInstanceDetail; cancelStatus?: number } = {}): void {
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
      const url = String(path);
      if (method === "POST") {
        return opts.cancelStatus === 409
          ? json({ title: "Workflow instance not cancellable", code: "not-cancellable" }, 409)
          : json({ instanceId: runningRow.instanceId, cancelled: true });
      }
      if (url.includes("/definitions")) {
        return json([live, shadow, off]);
      }
      if (url.endsWith("/log")) {
        return json(stepLog);
      }
      if (url.includes("/instances/")) {
        return json(opts.detail ?? detailOf());
      }
      if (url.includes("/instances")) {
        return json({ items: [runningRow, doneRow] });
      }
      return json(null, 404);
    }),
  );
}

describe("FlowsView", () => {
  it("lists definitions with their posture and the newest instances", async () => {
    signIn(["framework-admin"]);
    stubFlows();

    mountView(FlowsView);

    await vi.waitFor(() => expect(byTestId("flows-view").textContent).toContain("review-fork"));
    const defs = byTestId("flows-view").textContent ?? "";
    expect(defs).toContain("live");
    expect(defs).toContain("shadow");
    expect(defs).toContain("off");
    await vi.waitFor(() =>
      expect(byTestId("instance-list").textContent).toContain("review-fork@3"),
    );
    const listed = byTestId("instance-list").textContent ?? "";
    expect(listed).toContain("running");
    expect(listed).toContain("fork"); // the running badge names the current step
    expect(listed).toContain("steps-exhausted"); // a terminal row names its end reason
  });

  it("clicking a definition filters the instances by def", async () => {
    signIn(["framework-admin"]);
    stubFlows();

    mountView(FlowsView);
    await vi.waitFor(() => expect(byTestId("instance-list").textContent).toContain("review-fork"));

    const row = [...container!.querySelectorAll("tbody tr")].find((r) =>
      r.textContent?.includes("assign-rule-forgejo-1"),
    ) as HTMLTableRowElement;
    row.click();
    flushSync();

    await vi.waitFor(() =>
      expect(calls.some((c) => c.path.includes("/instances?def=assign-rule-forgejo-1"))).toBe(true),
    );
  });

  it("an instance row opens the deep link", async () => {
    signIn(["framework-admin"]);
    stubFlows();

    mountView(FlowsView);
    await vi.waitFor(() => expect(byTestId("instance-list").textContent).toContain("review-fork"));

    const row = [
      ...container!.querySelectorAll("[data-testid='instance-list'] tbody tr"),
    ][0] as HTMLTableRowElement;
    row.click();

    expect(location.hash).toBe(`#/flow/${runningRow.instanceId}`);
  });
});

describe("FlowDetail", () => {
  it("renders the vertical checklist and the lineage", async () => {
    signIn(["framework-admin"]);
    stubFlows({
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

    mountView(FlowDetail, { instanceId: runningRow.instanceId, onclose: () => undefined });

    await vi.waitFor(() => expect(byTestId("step-log").textContent).toContain("wait-review"));
    const log = byTestId("step-log").textContent ?? "";
    expect(log).toContain("fork");
    expect(log).toContain("completed");
    expect(log).toContain("waiting");
    await vi.waitFor(() => expect(byTestId("lineage").textContent).toContain("pr-on-manifest"));
  });

  it("cancel posts with a reason and reports a refusal honestly", async () => {
    signIn(["framework-admin"]);
    stubFlows({ cancelStatus: 409 });

    mountView(FlowDetail, { instanceId: runningRow.instanceId, onclose: () => undefined });
    await vi.waitFor(() => expect(byTestId("flow-cancel")).toBeTruthy());

    byTestId<HTMLButtonElement>("flow-cancel").click();

    await vi.waitFor(() =>
      expect(calls.some((c) => c.method === "POST" && c.path.endsWith("/cancel"))).toBe(true),
    );
    const post = calls.find((c) => c.method === "POST");
    expect(post?.headers["x-change-reason"]).toBeTruthy();
    await vi.waitFor(() =>
      expect(byTestId("flow-notice").textContent).toContain("not cancellable"),
    );
  });

  it("a read-only principal sees no cancel button", async () => {
    signIn(["ai-operator"]);
    stubFlows();

    mountView(FlowDetail, { instanceId: runningRow.instanceId, onclose: () => undefined });
    await vi.waitFor(() => expect(byTestId("step-log").textContent).toContain("wait-review"));

    expect(container?.querySelector("[data-testid='flow-cancel']")).toBeNull();
  });
});
