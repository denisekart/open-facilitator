// Golden synth snapshots: the whole tree from envs/example.json, so any
// structural drift in any stack fails loudly.
import { Template } from "aws-cdk-lib/assertions";
import { describe, expect, it } from "vitest";
import { stackOf, synthApp } from "./helpers.js";

const { stacks } = synthApp();

describe("golden snapshots", () => {
  it.each([
    "network",
    "data",
    "identity",
    "kernel",
    "observability",
    "team-team-alpha-network",
    "team-team-alpha-plane",
  ])("ofac-example-%s", (suffix) => {
    expect(Template.fromStack(stackOf(stacks, suffix))).toMatchSnapshot();
  });

  it("instantiates exactly the seven example stacks (fleet off)", () => {
    expect([...stacks.keys()].sort()).toEqual([
      "ofac-example-data",
      "ofac-example-identity",
      "ofac-example-kernel",
      "ofac-example-network",
      "ofac-example-observability",
      "ofac-example-team-team-alpha-network",
      "ofac-example-team-team-alpha-plane",
    ]);
  });
});
