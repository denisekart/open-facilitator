import type { CapabilityDescriptor } from "@ofac/protocol";
import { describe, expect, it } from "vitest";

import { DefaultPolicy, type PolicyContext } from "../../src/policy.js";

const ANALYZE: CapabilityDescriptor = {
  id: "research.analyze",
  version: 1,
  execution: "asynchronous",
  supportsSteering: true,
  supportsCancellation: true,
};

const SYNC_NO_OPTS: CapabilityDescriptor = {
  id: "build.compile",
  version: 1,
  execution: "synchronous",
};

const POLICY = new DefaultPolicy(
  new Map<string, CapabilityDescriptor>([
    [ANALYZE.id, ANALYZE],
    [SYNC_NO_OPTS.id, SYNC_NO_OPTS],
  ]),
);

const CHILD = "urn:workspace:child-echo";

function ctx(over: Partial<PolicyContext>): PolicyContext {
  return {
    commandType: "org.facilitator.command.work.assign.v1",
    childWorkspaceId: CHILD,
    resource: "research.analyze",
    claims: { subject: "workload:gw.test", groups: [], verified: false },
    workloadActor: "workload:gw.test",
    ...over,
  };
}

describe("DefaultPolicy deny-by-default matrix", () => {
  it("allows assign for an advertised capability", () => {
    expect(POLICY.evaluate("command.forward", ctx({ capabilityId: "research.analyze" }))).toBe(
      "allow",
    );
  });

  it("denies assign for an unknown capability", () => {
    expect(POLICY.evaluate("command.forward", ctx({ capabilityId: "not.advertised" }))).toBe(
      "deny",
    );
  });

  it("denies a forward without a capabilityId", () => {
    expect(POLICY.evaluate("command.forward", ctx({}))).toBe("deny");
  });

  it("denies steer when supportsSteering is absent", () => {
    expect(
      POLICY.evaluate(
        "command.forward",
        ctx({
          commandType: "org.facilitator.command.work.steer.v1",
          capabilityId: "build.compile",
        }),
      ),
    ).toBe("deny");
  });

  it("allows steer when the descriptor supports it", () => {
    expect(
      POLICY.evaluate(
        "command.forward",
        ctx({
          commandType: "org.facilitator.command.work.steer.v1",
          capabilityId: "research.analyze",
        }),
      ),
    ).toBe("allow");
  });

  it("denies cancel when supportsCancellation is absent", () => {
    expect(
      POLICY.evaluate(
        "command.forward",
        ctx({
          commandType: "org.facilitator.command.work.cancel.v1",
          capabilityId: "build.compile",
        }),
      ),
    ).toBe("deny");
  });

  it("allows non-interaction commands for an advertised capability (pass-through)", () => {
    expect(
      POLICY.evaluate(
        "command.forward",
        ctx({
          commandType: "org.facilitator.command.work.status.request.v1",
          capabilityId: "build.compile",
        }),
      ),
    ).toBe("allow");
  });

  it("allows every event forward — the child owns its facts", () => {
    expect(
      POLICY.evaluate(
        "event.forward",
        ctx({ commandType: "org.facilitator.event.assignment.completed.v1" }),
      ),
    ).toBe("allow");
  });
});
