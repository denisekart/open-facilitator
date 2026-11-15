import { describe, expect, it } from "vitest";

import { href, parse } from "./route.svelte.js";

describe("parse", () => {
  it("reads the flows definitions screen and one definition under edit", () => {
    expect(parse("#/flows").view).toBe("flows");
    const d = parse("#/flows/review-fork?clone=1");
    expect(d.view).toBe("flows");
    expect(d.id).toBe("review-fork");
    expect(d.params.get("clone")).toBe("1");
    expect(parse("#/flows/new").id).toBe("new");
  });

  it("reads the monitor family", () => {
    expect(parse("#/monitor/runs").view).toBe("monitor-runs");
    expect(parse("#/monitor").view).toBe("monitor-runs");
    const r = parse("#/monitor/runs/11111111-1111-4111-8111-111111111111");
    expect(r.view).toBe("monitor-run");
    expect(r.id).toBe("11111111-1111-4111-8111-111111111111");
    expect(parse("#/monitor/runs?def=nudge-escalation").params.get("def")).toBe("nudge-escalation");
    expect(parse("#/monitor/deliveries").view).toBe("monitor-deliveries");
    expect(parse("#/monitor/audit").view).toBe("monitor-audit");
    expect(parse("#/monitor/runtime").view).toBe("monitor-runtime");
    const def = parse("#/monitor/definitions/review-fork");
    expect(def.view).toBe("monitor-definition");
    expect(def.id).toBe("review-fork");
  });

  it("reads the works queue and its filters", () => {
    const r = parse("#/works?state=blocked");
    expect(r.view).toBe("works");
    expect(r.params.get("state")).toBe("blocked");
    expect(r.id).toBeNull();
    expect(r.kind).toBeNull();
  });

  it("keeps a comma list in one param value", () => {
    expect(parse("#/works?state=blocked,review&team=team-alpha").params.get("state")).toBe(
      "blocked,review",
    );
    expect(parse("#/works?state=blocked,review&team=team-alpha").params.get("team")).toBe(
      "team-alpha",
    );
  });

  it("reads one work id", () => {
    const r = parse("#/work/0194f0a0-0000-7000-8000-000000000001");
    expect(r.view).toBe("work");
    expect(r.id).toBe("0194f0a0-0000-7000-8000-000000000001");
  });

  it("reads an audit correlation filter", () => {
    expect(parse("#/monitor/audit?correlation=x").params.get("correlation")).toBe("x");
  });

  it("reads a search hit by kind and id", () => {
    const r = parse("#/hit/artifacts/0194f0a0-0000-7000-8000-000000000002");
    expect(r.view).toBe("hit");
    expect(r.kind).toBe("artifacts");
    expect(r.id).toBe("0194f0a0-0000-7000-8000-000000000002");
  });

  it("reads the plain views", () => {
    for (const view of ["team", "config", "chat"] as const) {
      expect(parse(`#/${view}`).view).toBe(view);
    }
    const session = parse("#/chat/s-1");
    expect(session.view).toBe("chat");
    expect(session.id).toBe("s-1");
  });

  it("redirects every pre-category hash onto the new grammar", () => {
    // The redirect proof: an old shared link parses to the NEW route (the
    // right screen renders) and href() writes the new canonical form.
    const cases: [string, string][] = [
      ["#/flowdef/nudge-escalation", "#/flows/nudge-escalation"],
      ["#/flowdef/nudge-escalation?clone=1", "#/flows/nudge-escalation?clone=1"],
      [
        "#/flow/0194f0a0-0000-7000-8000-000000000002",
        "#/monitor/runs/0194f0a0-0000-7000-8000-000000000002",
      ],
      ["#/audit?decision=deny", "#/monitor/audit?decision=deny"],
      ["#/deliveries", "#/monitor/deliveries"],
      ["#/runtime", "#/monitor/runtime"],
      ["#/links", "#/team"],
    ];
    for (const [old, next] of cases) {
      const r = parse(old);
      expect(href(r), old).toBe(next);
      // and the new form is stable under parse → href
      expect(href(parse(next)), next).toBe(next);
    }
  });

  it("falls back to no view for anything else", () => {
    for (const hash of [
      "#/nope",
      "",
      "#/",
      "#/hit/unknown-kind/x",
      "#/work",
      "#/works/x/y",
      "#/monitor",
      "#/monitor/nope",
      "#/monitor/runs/a/b",
    ]) {
      const r = parse(hash);
      if (hash === "#/monitor") {
        expect(r.view, hash).toBe("monitor-runs");
        continue;
      }
      expect(r.view, hash).toBe("none");
      expect(r.id).toBeNull();
      expect(r.kind).toBeNull();
    }
  });
});

describe("href", () => {
  it("round-trips every canonical form", () => {
    for (const hash of [
      "#/works",
      "#/works?state=blocked",
      "#/works?state=blocked,review",
      "#/works?state=blocked,review&team=team-alpha",
      "#/work/0194f0a0-0000-7000-8000-000000000001",
      "#/flows",
      "#/flows/review-fork",
      "#/flows/new",
      "#/flows/review-fork?clone=1",
      "#/monitor/runs",
      "#/monitor/runs?def=nudge-escalation",
      "#/monitor/runs/0194f0a0-0000-7000-8000-000000000002",
      "#/monitor/definitions/review-fork",
      "#/monitor/audit",
      "#/monitor/audit?decision=deny",
      "#/monitor/audit?correlation=x",
      "#/monitor/audit?decision=deny&actor=bob&action=work.create&correlation=x&since=2026-11-12T00:00:00Z",
      "#/monitor/deliveries",
      "#/monitor/runtime",
      "#/team",
      "#/config",
      "#/chat",
      "#/chat/s-1",
      "#/hit/artifacts/0194f0a0-0000-7000-8000-000000000002",
      "#/",
    ]) {
      expect(href(parse(hash)), hash).toBe(hash);
    }
  });
});
