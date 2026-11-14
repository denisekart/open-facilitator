// Hash routing, no router dependency: the whole navigation surface is one
// string, so a deep link is copy-pasteable and a reload restores the view.
// Grammar (the only one the console speaks):
//   #/works?state=<csv>&team=<slug>   the queue, filtered
//   #/work/<id>                       one work, inspector open
//   #/audit?decision=&actor=&action=&correlation=&since=
//   #/links  #/deliveries  #/config  #/chat[/<id>]  #/runtime
//   #/flows  #/flow/<instanceId>           the workflow plane, read-only
//   #/flowdef/<id|new>                    one definition under edit
//   #/hit/<kind>/<id>                 one non-work search hit
// Anything else is #/ — the shell with no view open.

import { SEARCH_SECTIONS, type SearchSection } from "./types.js";

export type RouteView =
  | "works"
  | "work"
  | "audit"
  | "links"
  | "deliveries"
  | "config"
  | "chat"
  | "runtime"
  | "flows"
  | "flow"
  | "flowdef"
  | "hit"
  | "none";

export interface Route {
  view: RouteView;
  id: string | null;
  kind: string | null;
  params: URLSearchParams;
}

const VIEWS = [
  "works",
  "audit",
  "links",
  "deliveries",
  "config",
  "chat",
  "runtime",
  "flows",
] as const;

/** Param order each view writes back out, so href(parse(h)) === h. */
const PARAM_ORDER: Partial<Record<RouteView, string[]>> = {
  works: ["state", "team"],
  audit: ["decision", "actor", "action", "correlation", "since"],
};

function isSection(value: string): value is SearchSection {
  return (SEARCH_SECTIONS as readonly string[]).includes(value);
}

export function parse(hash: string): Route {
  const none: Route = { view: "none", id: null, kind: null, params: new URLSearchParams() };
  if (!hash.startsWith("#/")) {
    return none;
  }
  const raw = hash.slice(2);
  const [path = "", ...rest] = raw.split("?");
  const params = new URLSearchParams(rest.join("?"));
  const segments = path.split("/").filter((s) => s.length > 0);

  if (segments.length === 1 && (VIEWS as readonly string[]).includes(segments[0])) {
    const view = segments[0] as RouteView;
    return { view, id: null, kind: null, params };
  }
  if (segments.length === 2 && segments[0] === "work") {
    return { view: "work", id: segments[1], kind: null, params };
  }
  if (segments.length === 2 && segments[0] === "chat") {
    return { view: "chat", id: segments[1], kind: null, params };
  }
  if (segments.length === 2 && segments[0] === "flow") {
    return { view: "flow", id: segments[1], kind: null, params };
  }
  if (segments.length === 2 && segments[0] === "flowdef") {
    return { view: "flowdef", id: segments[1], kind: null, params };
  }
  if (segments.length === 3 && segments[0] === "hit" && isSection(segments[1])) {
    return { view: "hit", id: segments[2], kind: segments[1], params };
  }
  return none;
}

/** Canonical hash for a route: known params first, in the view's own order. */
export function href(r: Route): string {
  const base =
    r.view === "work" && r.id
      ? `#/work/${r.id}`
      : r.view === "chat" && r.id
        ? `#/chat/${r.id}`
        : r.view === "flow" && r.id
          ? `#/flow/${r.id}`
          : r.view === "flowdef" && r.id
            ? `#/flowdef/${r.id}`
            : r.view === "hit" && r.kind && r.id
              ? `#/hit/${r.kind}/${r.id}`
              : r.view === "none"
                ? "#/"
                : `#/${r.view}`;
  // Param order: the view's canonical keys first, then anything else that
  // rode along. Commas and colons stay literal so the canonical forms
  // (#/works?state=blocked,review, #/audit?since=2026-11-12T00:00:00Z)
  // survive parse → href unchanged.
  const order = PARAM_ORDER[r.view] ?? [];
  const keys = [...new Set([...order, ...r.params.keys()])];
  const parts = keys
    .map((key) => [key, r.params.get(key) ?? ""] as const)
    .filter(([, value]) => value !== "")
    .map(
      ([key, value]) =>
        `${key}=${encodeURIComponent(value).replace(/%2C/gi, ",").replace(/%3A/gi, ":")}`,
    );
  return parts.length > 0 ? `${base}?${parts.join("&")}` : base;
}

export class RouteStore {
  route = $state(parse(location.hash));
}

export const route = new RouteStore();

/**
 * Start following the address bar. Called from main.ts BEFORE mount — the
 * first state write must not happen inside a component $effect (theme law).
 */
export function startRouting(): void {
  route.route = parse(location.hash);
  window.addEventListener("hashchange", () => {
    route.route = parse(location.hash);
  });
}

/** Navigate: assigning location.hash fires the listener above. */
export function go(hash: string): void {
  location.hash = hash;
}
