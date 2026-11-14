import { flushSync, mount, unmount } from "svelte";
import type { User } from "oidc-client-ts";
import { afterEach, describe, expect, it, vi } from "vitest";

import { session } from "../lib/auth.svelte.js";
import type { WorkflowInstanceDetail, WorkflowRegistry, WorkflowStepLogRow } from "../lib/types.js";
import FlowDetail from "./FlowDetail.svelte";
import FlowEditor from "./FlowEditor.svelte";

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

const registry: WorkflowRegistry = {
  eventTypes: [
    "org.facilitator.event.provider.issue.v1",
    "org.facilitator.event.workspace.heartbeat.v1",
  ],
  commands: ["org.facilitator.command.communication.post.v1"],
  capabilities: ["git.fork"],
  callable: [{ id: "ensure-capacity", version: 1, title: "ensure capacity" }],
  teams: ["team-alpha"],
};

const storedDoc = {
  id: "review-fork",
  version: 3,
  title: "Review fork",
  description: "fork a review",
  enabled: false,
  on: { event: "org.facilitator.event.provider.review.v1", where: { "data.action": "submitted" } },
  steps: [
    {
      id: "guard",
      title: "terminal only",
      on: "always",
      decide: {
        source: "guard",
        guard: { field: "work.state", in: ["completed"] },
        fallback: "end",
      },
    },
    { id: "fork", title: "fork", on: "guard:completed", "fork-work": { noteRef: "a note" } },
  ],
};

const summary = {
  id: "review-fork",
  version: 3,
  digest: "sha256:beef",
  title: "Review fork",
  enabled: false,
  shadow: false,
};

interface Call {
  path: string;
  method: string;
  headers: Record<string, string>;
  body: string | null;
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

async function settle(): Promise<void> {
  await vi.waitFor(() => {
    flushSync();
    if (calls.length === 0) {
      throw new Error("boot fetches pending");
    }
  });
}

async function settleUntil(check: () => void): Promise<void> {
  await vi.waitFor(() => {
    flushSync();
    check();
  });
}

function stubEditor(opts: { putStatus?: number } = {}): void {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (path: string, init?: RequestInit) => {
      const method = init?.method ?? "GET";
      const url = String(path);
      calls.push({
        path: url,
        method,
        headers: Object.fromEntries(
          [...new Headers(init?.headers).entries()].map(([k, v]) => [k.toLowerCase(), v]),
        ),
        body: typeof init?.body === "string" ? init.body : null,
      });
      if (url.includes("/registry")) return json(registry);
      if (url.includes("/validate")) return json({ ok: true, errors: [] });
      if (method === "PUT") {
        if (opts.putStatus && opts.putStatus !== 200) {
          return json(
            {
              type: "https://errors.openfacilitator.org/v1/version-conflict",
              title: "Version conflict",
              detail: "digested moved",
            },
            opts.putStatus,
          );
        }
        return json({ ...summary, version: 4 });
      }
      if (url.includes("/definitions/review-fork")) {
        return json({ summary, document: storedDoc, yaml: "id: review-fork\n" });
      }
      if (url.includes("/instances/") && url.endsWith("/log")) {
        return json({ items: stepLog });
      }
      if (url.includes("/instances/")) {
        return json(instanceDetail);
      }
      throw new Error(`unstubbed ${method} ${url}`);
    }),
  );
}

describe("FlowEditor", () => {
  it("loads a definition into the form and shows its picture", async () => {
    signIn(["framework-admin"]);
    stubEditor();
    mountView(FlowEditor, { defId: "review-fork", onclose: () => {} });
    await settle();

    expect(byTestId<HTMLInputElement>("editor-title").value).toBe("Review fork");
    expect(byTestId<HTMLInputElement>("editor-id").disabled).toBe(true);
    // the graph mirrors the stored steps: trigger + two steps, no 'end'
    // (the editor has no run status, so no terminal edge is drawn)
    const svg = byTestId("flow-graph").querySelector("svg");
    expect(svg).not.toBeNull();
    const labels = [...(svg?.querySelectorAll("[role='button']") ?? [])].map((g) =>
      g.getAttribute("aria-label"),
    );
    expect(labels.some((l) => l?.startsWith("guard:"))).toBe(true);
    expect(labels.some((l) => l?.startsWith("fork:"))).toBe(true);
    expect(labels.some((l) => l?.startsWith("trigger:"))).toBe(true);
  });

  it("edits the title, shows the diff, and saves a new version with If-Match", async () => {
    signIn(["framework-admin"]);
    stubEditor();
    mountView(FlowEditor, { defId: "review-fork", onclose: () => {} });
    await settle();

    const title = byTestId<HTMLInputElement>("editor-title");
    title.value = "Review fork v2";
    title.dispatchEvent(new Event("input"));
    flushSync();

    const diff = byTestId("editor-diff");
    expect(diff.textContent).toContain("title");

    byTestId<HTMLButtonElement>("editor-save").click();
    await settleUntil(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));

    const put = calls.find((c) => c.method === "PUT");
    expect(put?.path).toBe("/api/admin/workflows/definitions/review-fork");
    expect(put?.headers["if-match"]).toBe("sha256:beef");
    expect(put?.headers["x-change-reason"]).toBeTruthy();
    const body = JSON.parse(put?.body ?? "{}") as { title: string; steps: unknown[] };
    expect(body.title).toBe("Review fork v2");
    expect(body.steps).toHaveLength(2);
    await settleUntil(() => expect(byTestId("editor-notice").textContent).toContain("version 4"));
  });

  it("a conflict comes back as an honest notice, not a silent no-op", async () => {
    signIn(["framework-admin"]);
    stubEditor({ putStatus: 412 });
    mountView(FlowEditor, { defId: "review-fork", onclose: () => {} });
    await settle();

    const title = byTestId<HTMLInputElement>("editor-title");
    title.value = "racing edit";
    title.dispatchEvent(new Event("input"));
    flushSync();
    byTestId<HTMLButtonElement>("editor-save").click();
    await settleUntil(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));

    expect(byTestId("editor-notice").textContent).toContain("version-conflict");
  });

  it("clone mode renames to <id>-copy and forces a new v1 (no If-Match)", async () => {
    signIn(["kernel-operator"]);
    stubEditor();
    mountView(FlowEditor, { defId: "review-fork", clone: true, onclose: () => {} });
    await settle();

    const idField = byTestId<HTMLInputElement>("editor-id");
    expect(idField.value).toBe("review-fork-copy");
    expect(idField.disabled).toBe(false);

    byTestId<HTMLButtonElement>("editor-save").click();
    await settleUntil(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    const put = calls.find((c) => c.method === "PUT");
    expect(put?.path).toBe("/api/admin/workflows/definitions/review-fork-copy");
    expect(put?.headers["if-match"]).toBeUndefined();

    // The editor re-anchors on the copy: its posture toggles speak the
    // new definition now.
    await settleUntil(() => expect(byTestId("editor-enable")).toBeTruthy());
  });

  it("the palette offers every kind; not-yet-executable ones are disabled and labelled", async () => {
    signIn(["framework-admin"]);
    stubEditor();
    mountView(FlowEditor, { defId: "new", onclose: () => {} });
    await settle();

    expect(byTestId<HTMLButtonElement>("add-timer").disabled).toBe(false);
    expect(byTestId<HTMLButtonElement>("add-invoke").disabled).toBe(false);
    const join = byTestId<HTMLButtonElement>("add-join");
    expect(join.disabled).toBe(true);
    expect(join.textContent).toContain("not yet executable");
  });

  it("adding a timer step draws it in the picture and saves", async () => {
    signIn(["framework-admin"]);
    stubEditor();
    mountView(FlowEditor, { defId: "new", onclose: () => {} });
    await settle();

    const idField = byTestId<HTMLInputElement>("editor-id");
    idField.value = "t-new-flow";
    idField.dispatchEvent(new Event("input"));
    flushSync();

    byTestId<HTMLButtonElement>("add-timer").click();
    flushSync();
    const svg = byTestId("flow-graph").querySelector("svg");
    const labels = [...(svg?.querySelectorAll("[role='button']") ?? [])].map((g) =>
      g.getAttribute("aria-label"),
    );
    expect(labels.some((l) => l?.startsWith("step-1:"))).toBe(true);

    byTestId<HTMLButtonElement>("editor-save").click();
    await settleUntil(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    const body = JSON.parse(calls.find((c) => c.method === "PUT")?.body ?? "{}") as {
      steps: { timer: { for: string } }[];
    };
    expect(body.steps[0]?.timer?.for).toBe("PT30M");
  });

  it("a read-only principal gets the form without the save", async () => {
    signIn(["reader"]);
    stubEditor();
    mountView(FlowEditor, { defId: "review-fork", onclose: () => {} });
    await settle();
    expect(byTestId<HTMLInputElement>("editor-title").value).toBe("Review fork");
    expect(container?.querySelector("[data-testid='editor-save']")).toBeNull();
  });
});

const instanceDetail: WorkflowInstanceDetail = {
  instanceId: "11111111-1111-4111-8111-111111111111",
  definitionId: "review-fork",
  definitionVersion: 3,
  status: "failed",
  currentStep: null,
  depth: 0,
  primaryWorkId: null,
  endReason: "step-failed",
  startedAt: "2026-11-14T09:00:00Z",
  updatedAt: "2026-11-14T09:05:00Z",
  invocationPath: "$",
  output: null,
  ancestors: [],
  children: [],
};

const stepLog: WorkflowStepLogRow[] = [
  {
    seq: 1,
    stepId: "guard",
    status: "done",
    effectCommandId: null,
    output: '{"branch":"fork"}',
    startedAt: "2026-11-14T09:00:00Z",
    updatedAt: "2026-11-14T09:00:01Z",
  },
  {
    seq: 2,
    stepId: "fork",
    status: "failed",
    effectCommandId: null,
    output: '{"error":"endpoint went away"}',
    startedAt: "2026-11-14T09:00:01Z",
    updatedAt: "2026-11-14T09:05:00Z",
  },
];

describe("FlowDetail's graph", () => {
  it("wears the run: a done node, a failed node, and click-to-inspect", async () => {
    signIn(["framework-admin"]);
    stubEditor();
    mountView(FlowDetail, {
      instanceId: instanceDetail.instanceId,
      onclose: () => {},
    });
    await settle();

    const svg = byTestId("flow-graph").querySelector("svg");
    expect(svg).not.toBeNull();
    const done = [...(svg?.querySelectorAll("rect") ?? [])].filter((r) =>
      r.classList.contains("fg-done"),
    );
    const failed = [...(svg?.querySelectorAll("rect") ?? [])].filter((r) =>
      r.classList.contains("fg-failed"),
    );
    expect(done).toHaveLength(1); // guard landed
    expect(failed).toHaveLength(1); // fork is why the run failed

    // click the failed node: its leg's output surfaces
    const node = [...(svg?.querySelectorAll("[role='button']") ?? [])].find((g) =>
      g.getAttribute("aria-label")?.startsWith("fork:"),
    );
    node?.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    flushSync();
    expect(byTestId("graph-inspect").textContent).toContain("failed");
    expect(byTestId("graph-inspect").textContent).toContain("endpoint went away");

    // a failed run offers retry on the last failed step
    expect(byTestId("flow-retry").textContent).toContain("retry 'fork'");
  });
});
