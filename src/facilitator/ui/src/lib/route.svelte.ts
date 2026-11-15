// Hash routing, no router dependency: the whole navigation surface is one
// string, so a deep link is copy-pasteable and a reload restores the view.
// Grammar (the only one the console speaks) — organized by the four console
// categories, so the address bar and the category rail say the same thing:
//
//   Works        #/works?state=<csv>&team=<slug>   the queue, filtered
//                #/work/<id>                       one work, inspector open
//   Flows        #/flows                           definitions, editorial
//                #/flows/<id|new>                  one definition under edit
//   Monitor      #/monitor/runs[?def=<id>]         instances, optionally one def
//                #/monitor/runs/<instanceId>       one run, read-only
//                #/monitor/deliveries              every message sent, and its fate
//                #/monitor/audit?decision=&actor=&action=&correlation=&since=
//                #/monitor/runtime                 replicas, provisioning, sweeps
//                #/monitor/definitions/<id>        one definition's history + runs
//   Team         #/team                            links, gateways, enrollment
//   Config       #/config                          the live routing policy
//   (assistant)  #/chat[/<id>]                     not a screen: opens the
//                                                AiDeck drawer over whatever is
//                                                underneath, session preserved
//                #/hit/<kind>/<id>                 one non-work search hit
//
// Old hashes still work: `flowdef/X`, `flow/X`, `audit`, `deliveries`,
// `runtime`, `links` redirect (parse-level) to the new form, and `href()`
// writes the NEW form back out — so a stale bookmark lands on the right
// screen and the address bar heals itself to the canonical route.
// Anything else is #/ — the shell with no view open.

import { SEARCH_SECTIONS, type SearchSection } from "./types.js";

export type RouteView =
  | "works"
  | "work"
  | "flows"
  | "monitor-runs"
  | "monitor-run"
  | "monitor-deliveries"
  | "monitor-audit"
  | "monitor-runtime"
  | "monitor-definition"
  | "team"
  | "config"
  | "chat"
  | "hit"
  | "none";

export interface Route {
  view: RouteView;
  id: string | null;
  kind: string | null;
  params: URLSearchParams;
}

const VIEWS = ["works", "flows", "team", "config", "chat", "monitor"] as const;

/** Param order each view writes back out, so href(parse(h)) === h. */
const PARAM_ORDER: Partial<Record<RouteView, string[]>> = {
  works: ["state", "team"],
  "monitor-audit": ["decision", "actor", "action", "correlation", "since"],
  "monitor-runs": ["def"],
};

function isSection(value: string): value is SearchSection {
  return (SEARCH_SECTIONS as readonly string[]).includes(value);
}

function makeRoute(
  view: RouteView,
  id: string | null = null,
  kind: string | null = null,
  params = new URLSearchParams(),
): Route {
  return { view, id, kind, params };
}

/**
 * The two-segment `monitor/*` views. Their params ride in the query string,
 * parsed by the caller, so this only maps the path tail.
 */
function monitor(segments: string[], params: URLSearchParams): Route | null {
  const tail = segments.slice(1);
  if (tail.length === 1 && tail[0] === "runs") return makeRoute("monitor-runs", null, null, params);
  if (tail.length === 2 && tail[0] === "runs")
    return makeRoute("monitor-run", tail[1], null, params);
  if (tail.length === 1 && tail[0] === "deliveries")
    return makeRoute("monitor-deliveries", null, null, params);
  if (tail.length === 1 && tail[0] === "audit")
    return makeRoute("monitor-audit", null, null, params);
  if (tail.length === 1 && tail[0] === "runtime")
    return makeRoute("monitor-runtime", null, null, params);
  if (tail.length === 2 && tail[0] === "definitions")
    return makeRoute("monitor-definition", tail[1], null, params);
  return null;
}

/**
 * Pre-category hashes, mapped onto the new grammar before the branch logic
 * below ever sees them. This IS the redirect: a shared old link parses to
 * the new route, so the correct screen renders and href() (which only
 * speaks the new form) rewrites the address bar to it.
 */
function legacy(segments: string[], params: URLSearchParams): Route | null {
  const [head = "", second] = segments;
  if (segments.length === 1) {
    if (head === "audit") return makeRoute("monitor-audit", null, null, params);
    if (head === "deliveries") return makeRoute("monitor-deliveries", null, null, params);
    if (head === "runtime") return makeRoute("monitor-runtime", null, null, params);
    if (head === "links") return makeRoute("team", null, null, params);
    return null;
  }
  if (segments.length === 2) {
    if (head === "flowdef") return makeRoute("flows", second, null, params);
    if (head === "flow") return makeRoute("monitor-run", second, null, params);
  }
  return null;
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

  const old = legacy(segments, params);
  if (old) return old;

  if (segments.length === 1 && (VIEWS as readonly string[]).includes(segments[0])) {
    const view = segments[0];
    if (view === "monitor") return makeRoute("monitor-runs", null, null, params);
    return makeRoute(view as RouteView, null, null, params);
  }
  if (segments.length === 2 && segments[0] === "work") {
    return makeRoute("work", segments[1], null, params);
  }
  if (segments.length === 2 && segments[0] === "chat") {
    return makeRoute("chat", segments[1], null, params);
  }
  if (segments.length === 2 && segments[0] === "flows") {
    return makeRoute("flows", segments[1], null, params);
  }
  if (segments.length >= 2 && segments[0] === "monitor") {
    const m = monitor(segments, params);
    if (m) return m;
  }
  if (segments.length === 3 && segments[0] === "hit" && isSection(segments[1])) {
    return makeRoute("hit", segments[2], segments[1], params);
  }
  return none;
}

/** Canonical hash for a route: known params first, in the view's own order. */
export function href(r: Route): string {
  const id = r.id;
  const base =
    r.view === "work" && id
      ? `#/work/${id}`
      : r.view === "chat" && id
        ? `#/chat/${id}`
        : r.view === "flows" && id
          ? `#/flows/${id}`
          : r.view === "monitor-run" && id
            ? `#/monitor/runs/${id}`
            : r.view === "monitor-definition" && id
              ? `#/monitor/definitions/${id}`
              : r.view === "monitor-runs"
                ? `#/monitor/runs`
                : r.view === "monitor-deliveries"
                  ? `#/monitor/deliveries`
                  : r.view === "monitor-audit"
                    ? `#/monitor/audit`
                    : r.view === "monitor-runtime"
                      ? `#/monitor/runtime`
                      : r.view === "hit" && r.kind && id
                        ? `#/hit/${r.kind}/${id}`
                        : r.view === "none"
                          ? "#/"
                          : `#/${r.view}`;
  // Param order: the view's canonical keys first, then anything else that
  // rode along. Commas and colons stay literal so the canonical forms
  // (#/works?state=blocked,review, #/monitor/audit?since=2026-11-12T00:00:00Z)
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
 * A legacy hash is rewritten to its canonical form, so the address bar and
 * the route state never disagree (and a copy-paste of the bar gives the new
 * link away).
 */
export function startRouting(): void {
  const adopt = (hash: string): void => {
    route.route = parse(hash);
    const canonical = href(route.route);
    if (canonical !== hash) {
      // Replace, not assign: a redirect must not stack history entries.
      history.replaceState(null, "", canonical);
    }
  };
  adopt(location.hash);
  window.addEventListener("hashchange", () => adopt(location.hash));
}

/** Navigate: assigning location.hash fires the listener above. */
export function go(hash: string): void {
  location.hash = hash;
}
