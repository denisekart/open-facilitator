import { flushSync, mount, unmount } from "svelte";
import type { User } from "oidc-client-ts";
import { afterEach, describe, expect, it, vi } from "vitest";

import { session } from "../lib/auth.svelte.js";
import type { WorkflowRegistry } from "../lib/types.js";
import FlowEditor from "./FlowEditor.svelte";

// The YAML-first editor: metadata fields, the CodeMirror host, the server
// validate face, and the save law (If-Match when stored, none when fresh).
// The graph's own laws live in flow-layout.test.ts (pure), and the run
// detail in FlowDetail.test.ts.

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

const storedYaml = [
  "id: review-fork",
  "title: Review fork",
  "on:",
  "  event: org.facilitator.event.provider.review.v1",
  "steps:",
  "  - id: guard",
  "    title: terminal only",
  "    decide:",
  "      guard: { field: work.state, in: [completed] }",
  "      fallback: end",
  "  - id: fork",
  "    title: fork",
  "    on: guard:completed",
  "    fork-work: { noteRef: a note }",
  "",
].join("\n");

const storedDoc = {
  id: "review-fork",
  version: 3,
  title: "Review fork",
  description: "fork a review",
  enabled: false,
  on: { event: "org.facilitator.event.provider.review.v1" },
  steps: [
    { id: "guard", title: "terminal only", decide: { fallback: "end" } },
    { id: "fork", title: "fork", on: "guard:completed", "fork-work": { noteRef: "a note" } },
  ],
};

const summary = {
  id: "review-fork",
  version: 3,
  digest: "sha256:beef",
  title: "Review fork",
  description: "fork a review",
  enabled: false,
  shadow: false,
  team: null,
};

const schemaDoc = {
  $id: "https://openfacilitator.org/protocol/workflow-definition.v1.json",
  type: "object",
  properties: {
    id: { type: "string" },
    title: { type: "string" },
    on: { type: "object" },
    steps: { type: "array", items: { $ref: "#/$defs/step" } },
  },
  $defs: {
    step: {
      type: "object",
      required: ["id", "title"],
      properties: {
        id: { type: "string" },
        title: { type: "string" },
        on: { type: "string" },
        emit: { type: "object" },
      },
    },
  },
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

let pending = 0;

async function settle(): Promise<void> {
  await vi.waitFor(() => {
    flushSync();
    const boot = calls.filter((c) => c.path.includes("/registry") || c.path.includes("/schema"));
    if (boot.length < 2 || pending > 0) {
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
      pending += 1;
      try {
        return await respond(path, init, opts);
      } finally {
        pending -= 1;
      }
    }),
  );
}

async function respond(
  path: string,
  init: RequestInit | undefined,
  opts: { putStatus?: number },
): Promise<Response> {
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
  if (url.includes("/schema")) return json({ definition: schemaDoc, common: {} });
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
    return json({ summary, document: storedDoc, yaml: storedYaml });
  }
  throw new Error(`unstubbed ${method} ${url}`);
}

describe("FlowEditor (YAML face)", () => {
  it("loads a definition: metadata fields, the CodeMirror host, the graph container", async () => {
    signIn(["framework-admin"]);
    stubEditor();
    mountView(FlowEditor, { defId: "review-fork", onclose: () => {} });
    await settle();

    expect(byTestId<HTMLInputElement>("editor-title").value).toBe("Review fork");
    expect(byTestId<HTMLInputElement>("editor-id").value).toBe("review-fork");
    // The YAML body reached the editor (CodeMirror renders it, and the
    // testid root exists regardless of viewport measurement in jsdom).
    expect(byTestId("editor-yaml")).toBeTruthy();
    await settleUntil(() =>
      expect(
        (container?.querySelector(".cm-content")?.textContent ?? "").includes("Review fork"),
      ).toBe(true),
    );
    expect(byTestId("flow-graph")).toBeTruthy();
  });

  it("edits the title, shows the diff, and saves a new version with If-Match", async () => {
    signIn(["framework-admin"]);
    stubEditor();
    mountView(FlowEditor, { defId: "review-fork", onclose: () => {} });
    await settle();

    const titleField = byTestId<HTMLInputElement>("editor-title");
    titleField.value = "Review fork v2";
    titleField.dispatchEvent(new Event("input"));
    flushSync();

    await settleUntil(() => expect(byTestId("editor-diff").textContent).toContain("title"));

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

    const titleField = byTestId<HTMLInputElement>("editor-title");
    titleField.value = "racing edit";
    titleField.dispatchEvent(new Event("input"));
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

    expect(byTestId<HTMLInputElement>("editor-id").value).toBe("review-fork-copy");
    expect(byTestId<HTMLInputElement>("editor-title").value).toContain("(copy)");

    byTestId<HTMLButtonElement>("editor-save").click();
    await settleUntil(() => expect(calls.some((c) => c.method === "PUT")).toBe(true));
    const put = calls.find((c) => c.method === "PUT");
    expect(put?.path).toBe("/api/admin/workflows/definitions/review-fork-copy");
    expect(put?.headers["if-match"]).toBeUndefined();
    // The YAML body itself carries the copy's identity, not just the field.
    const body = JSON.parse(put?.body ?? "{}") as Record<string, unknown>;
    expect(body["id"]).toBe("review-fork-copy");

    // The editor re-anchors on the copy: its posture toggles speak the
    // new definition now.
    await settleUntil(() => expect(byTestId("editor-enable")).toBeTruthy());
  });

  it("the server validate face reports through yaml-validation", async () => {
    signIn(["framework-admin"]);
    stubEditor();
    mountView(FlowEditor, { defId: "review-fork", onclose: () => {} });
    await settle();

    byTestId<HTMLButtonElement>("yaml-validate").click();
    await settleUntil(() => expect(byTestId("yaml-validation").textContent).toContain("valid"));
  });
});
