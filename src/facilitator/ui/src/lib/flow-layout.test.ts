import { describe, expect, it } from "vitest";

import { buildModel, extract, layout, toneFor } from "./flow-layout.js";
import type { Doc } from "./flow-layout.js";

// The canvas's brain, tested pure: the dagre layout must produce finite
// TB coordinates, the extraction must speak the runner's edge law, and the
// run skin must flip node tones from the step log.

const twoStep: Doc = {
  id: "t",
  on: { event: "org.facilitator.event.work.progress.v1" },
  steps: [
    { id: "a", title: "first", on: "always", emit: { command: "x" } },
    { id: "b", title: "second", on: "a:completed", wait: { event: "y" } },
  ],
};

describe("extract", () => {
  it("follows the sequence and branch law", () => {
    const g = extract(twoStep);
    expect(g.steps.map((s) => s.kind)).toEqual(["emit", "wait"]);
    expect(g.edges.some((e) => e.from === "trigger" && e.to === "a")).toBe(true);
    expect(g.edges.some((e) => e.from === "a" && e.to === "b" && e.label === "")).toBe(true);
  });

  it("a completed run walks to end", () => {
    const g = extract(twoStep, "completed");
    expect(g.edges.some((e) => e.from === "b" && e.to === "end")).toBe(true);
  });

  it("decide options become labelled branch edges", () => {
    const doc: Doc = {
      on: { event: "e" },
      steps: [
        { id: "d", on: "always", decide: { options: { yes: "p", no: "q" }, fallback: "end" } },
        { id: "p", on: "d:completed", emit: {} },
        { id: "q", on: "always", emit: {} },
      ],
    };
    const g = extract(doc);
    const branches = g.edges.filter((e) => e.from === "d" && e.label !== "");
    expect(branches.map((e) => e.label).sort()).toEqual(
      [...new Set(["yes", "no", "fallback"])].sort(),
    );
    // d→p rides the ':completed' rule (unlabeled), q's own sequence edge
    // comes from its predecessor p.
    expect(g.edges.some((e) => e.from === "d" && e.to === "p" && e.label === "")).toBe(true);
    expect(g.edges.some((e) => e.from === "p" && e.to === "q" && e.label === "")).toBe(true);
  });
});

describe("layout", () => {
  it("assigns finite TB coordinates, downstream ranks lower", () => {
    const bare = [
      { id: "trigger", label: "t", sub: "", kind: "trigger", tone: "trigger" as const },
      { id: "a", label: "a", sub: "", kind: "emit", tone: "act" as const },
      { id: "b", label: "b", sub: "", kind: "wait", tone: "park" as const },
    ];
    const edges = [
      { id: "1", from: "trigger", to: "a", label: "", dashed: false },
      { id: "2", from: "a", to: "b", label: "", dashed: false },
    ];
    const laid = layout(bare, edges);
    for (const n of laid) {
      expect(Number.isFinite(n.x)).toBe(true);
      expect(Number.isFinite(n.y)).toBe(true);
    }
    const y = (id: string): number => laid.find((n) => n.id === id)!.y;
    expect(y("trigger")).toBeLessThan(y("a"));
    expect(y("a")).toBeLessThan(y("b"));
  });
});

describe("toneFor", () => {
  it("the latest leg wins", () => {
    const log = [
      { stepId: "a", status: "running", output: null },
      { stepId: "a", status: "done", output: null },
    ];
    expect(toneFor({ id: "a", kind: "emit" }, log)).toBe("ok");
    expect(toneFor({ id: "b", kind: "emit" }, log)).toBe("pending");
    expect(toneFor({ id: "b", kind: "emit" }, null)).toBe("act");
  });
});

describe("buildModel", () => {
  it("a two-step doc yields three nodes: trigger + steps", () => {
    const m = buildModel({ doc: twoStep });
    expect(m.nodes.map((n) => n.id)).toEqual(["trigger", "a", "b"]);
    expect(m.w).toBeGreaterThan(0);
    expect(m.h).toBeGreaterThan(0);
  });

  it("log legs flip the node tone", () => {
    const m = buildModel({
      doc: twoStep,
      log: [{ stepId: "a", status: "done", output: null }],
    });
    expect(m.nodes.find((n) => n.id === "a")?.tone).toBe("ok");
    expect(m.nodes.find((n) => n.id === "b")?.tone).toBe("pending");
  });

  it("the current park wears the run's skin", () => {
    const m = buildModel({
      doc: twoStep,
      log: [],
      instanceStatus: "waiting",
      currentStep: "b",
    });
    expect(m.nodes.find((n) => n.id === "b")?.tone).toBe("park-live");
  });

  it("invoke children ride as dashed child nodes", () => {
    const doc: Doc = {
      on: { event: "e" },
      steps: [{ id: "inv", on: "always", invoke: { workflow: "child-def" } }],
    };
    const m = buildModel({
      doc,
      childInstances: [{ parentId: "inv", child: [{ id: "c-1", status: "running" }] }],
    });
    expect(m.nodes.some((n) => n.id === "child:c-1")).toBe(true);
    expect(m.edges.some((e) => e.to === "child:c-1" && e.dashed)).toBe(true);
  });
});
