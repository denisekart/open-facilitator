import { describe, expect, it } from "vitest";

import { href, parse } from "./route.svelte.js";

describe("parse", () => {
  it("reads the flows console and one instance", () => {
    expect(parse("#/flows").view).toBe("flows");
    const r = parse("#/flow/11111111-1111-4111-8111-111111111111");
    expect(r.view).toBe("flow");
    expect(r.id).toBe("11111111-1111-4111-8111-111111111111");
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
    expect(parse("#/audit?correlation=x").params.get("correlation")).toBe("x");
  });

  it("reads a search hit by kind and id", () => {
    const r = parse("#/hit/artifacts/0194f0a0-0000-7000-8000-000000000002");
    expect(r.view).toBe("hit");
    expect(r.kind).toBe("artifacts");
    expect(r.id).toBe("0194f0a0-0000-7000-8000-000000000002");
  });

  it("reads the plain views", () => {
    for (const view of ["links", "deliveries", "config", "runtime", "chat"] as const) {
      expect(parse(`#/${view}`).view).toBe(view);
    }
    const session = parse("#/chat/s-1");
    expect(session.view).toBe("chat");
    expect(session.id).toBe("s-1");
  });

  it("falls back to no view for anything else", () => {
    for (const hash of ["#/nope", "", "#/", "#/hit/unknown-kind/x", "#/work", "#/works/x/y"]) {
      const r = parse(hash);
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
      "#/flow/0194f0a0-0000-7000-8000-000000000002",
      "#/audit",
      "#/audit?decision=deny",
      "#/audit?correlation=x",
      "#/audit?decision=deny&actor=bob&action=work.create&correlation=x&since=2026-11-12T00:00:00Z",
      "#/links",
      "#/deliveries",
      "#/config",
      "#/chat",
      "#/chat/s-1",
      "#/runtime",
      "#/hit/artifacts/0194f0a0-0000-7000-8000-000000000002",
      "#/",
    ]) {
      expect(href(parse(hash)), hash).toBe(hash);
    }
  });
});
