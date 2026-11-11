// The zero-randomness worked example steers (directives fold into the
// report), so its only omissions are the two the plane cannot deliver.
import {
  fullHarnessCapabilities,
  HARNESS_COMMAND_WORK_NUDGE,
  HARNESS_COMMAND_WORK_STATUS_REQUEST,
  type HarnessCapabilities,
} from "@ofac/adapter";

export const DETERMINISTIC_CAPABILITIES: HarnessCapabilities = fullHarnessCapabilities([
  {
    command: HARNESS_COMMAND_WORK_NUDGE,
    reason:
      "no nudge frame on the adapter.v1 child wire (the Gateway answers on the child's behalf)",
  },
  {
    command: HARNESS_COMMAND_WORK_STATUS_REQUEST,
    reason: "no kernel producer yet; the run answers input.required instead",
  },
]);
