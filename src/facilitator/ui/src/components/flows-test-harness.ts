import { flushSync, mount, unmount } from "svelte";
import type { User } from "oidc-client-ts";
import { vi } from "vitest";

import { session } from "../lib/auth.svelte.js";
import type {
  WorkflowDefinitionRow,
  WorkflowInstanceRow,
  WorkflowInstanceDetail,
  WorkflowStepLogRow,
} from "../lib/types.js";

// Shared fixture plumbing for the flows-family vitest files (FlowsView,
// MonitorView, FlowDetail): the same fake kernel they all rode before the
// plane split into screens.

export function jwt(roles: string[]): string {
  const b64 = (input: string) =>
    btoa(input).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return `${b64('{"alg":"RS256"}')}.${b64(JSON.stringify({ realm_access: { roles } }))}.sig`;
}

export function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

export const live: WorkflowDefinitionRow = {
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

export const shadow: WorkflowDefinitionRow = {
  ...live,
  id: "assign-rule-forgejo-1",
  title: "Forgejo issue #1",
  source: "import",
  shadow: true,
  instancesRunning: 0,
};

export const off: WorkflowDefinitionRow = {
  ...live,
  id: "pr-on-manifest",
  title: "PR on manifest",
  enabled: false,
  instancesRunning: 0,
};

export const runningRow: WorkflowInstanceRow = {
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

export const doneRow: WorkflowInstanceRow = {
  ...runningRow,
  instanceId: "22222222-2222-4222-8222-222222222222",
  definitionId: "assign-rule-forgejo-1",
  definitionVersion: 1,
  status: "completed",
  currentStep: "ack",
  endReason: "steps-exhausted",
};

export function detailOf(overrides: Partial<WorkflowInstanceDetail> = {}): WorkflowInstanceDetail {
  return {
    ...runningRow,
    invocationPath: "$",
    output: null,
    ancestors: [],
    children: [],
    ...overrides,
  };
}

export const stepLog: { items: WorkflowStepLogRow[] } = {
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

export interface Call {
  path: string;
  method: string;
  headers: Record<string, string>;
}

export function signIn(roles: string[]): void {
  session.user = { access_token: jwt(roles) } as User;
  flushSync();
}

export function mountView(
  component: object,
  props?: object,
): { container: HTMLDivElement; app: Record<string, unknown> } {
  const container = document.createElement("div");
  document.body.append(container);
  const app = mount(component as never, {
    target: container,
    props: props as never,
  }) as unknown as Record<string, unknown>;
  flushSync();
  return { container, app };
}

export function cleanup(
  app: Record<string, unknown> | null,
  container: HTMLDivElement | null,
): void {
  if (app !== null) {
    unmount(app as never);
  }
  container?.remove();
  session.user = null;
  location.hash = "";
  vi.unstubAllGlobals();
}

export function byTestIdIn<T extends HTMLElement>(container: HTMLDivElement, id: string): T {
  const el = container.querySelector(`[data-testid="${id}"]`) as T | undefined;
  if (!el) {
    throw new Error(`no element with data-testid="${id}"`);
  }
  return el;
}

export function stubFlows(
  calls: Call[],
  opts: {
    detail?: WorkflowInstanceDetail;
    cancelStatus?: number;
    items?: WorkflowInstanceRow[];
  } = {},
): void {
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
        return json({ items: opts.items ?? [runningRow, doneRow] });
      }
      return json(null, 404);
    }),
  );
}
