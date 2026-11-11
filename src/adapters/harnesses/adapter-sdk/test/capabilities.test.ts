// The harness functionality list law: the list is complete against the
// protocol (every org.facilitator.command.* a harness may meet, every
// org.facilitator.event.* a harness may author), the descriptors ride the
// dataschema law, and an omission is honest — recorded with a reason and
// answered `unsupported`, never silent.
import { describe, expect, it } from "vitest";
import { SCHEMA_IDS } from "@ofac/protocol";
import {
  HARNESS_COMMANDS,
  HARNESS_EVENTS,
  HARNESS_OUTBOUND_COMMANDS,
  fullHarnessCapabilities,
  harnessCapabilityDescriptors,
  unsupportedFor,
} from "../src/capabilities.js";

describe("the harness functionality list", () => {
  it("every listed envelope type exists in the frozen protocol", () => {
    for (const type of [...HARNESS_COMMANDS, ...HARNESS_EVENTS, ...HARNESS_OUTBOUND_COMMANDS]) {
      const file = `https://schemas.openfacilitator.org/v1/${type.replace(/^org\.facilitator\./, "")}.json`;
      expect(SCHEMA_IDS, type).toContain(file);
    }
  });

  it("the full capability set is the full list", () => {
    const caps = fullHarnessCapabilities();
    expect(caps.commands).toEqual(HARNESS_COMMANDS);
    expect(caps.events).toEqual(HARNESS_EVENTS);
    expect(caps.outboundCommands).toEqual(HARNESS_OUTBOUND_COMMANDS);
    expect(caps.omittedCommands).toEqual([]);
  });

  it("an omission drops the command, records the reason, and answers unsupported", () => {
    const nudge = "org.facilitator.command.work.nudge.v1";
    const caps = fullHarnessCapabilities([{ command: nudge, reason: "no side channel" }]);
    expect(caps.commands).not.toContain(nudge);
    const answer = unsupportedFor({ type: nudge } as Parameters<typeof unsupportedFor>[0], caps);
    expect(answer).toEqual({ kind: "unsupported", reason: "no side channel" });
    expect(
      unsupportedFor({ type: "org.facilitator.command.work.assign.v1" } as never, caps),
    ).toBeUndefined();
  });

  it("descriptors carry the dataschema of each command", () => {
    const caps = fullHarnessCapabilities();
    const byId = new Map(harnessCapabilityDescriptors(caps).map((d) => [d.id, d]));
    for (const id of caps.commands) {
      expect(byId.get(id)?.inputSchema).toBe(
        `https://schemas.openfacilitator.org/v1/${id.replace(/^org\.facilitator\./, "")}.json`,
      );
    }
  });
});
