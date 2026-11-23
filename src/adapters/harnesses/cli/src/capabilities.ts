// The honest functionality picture of the CLI harness: like codex, minus
// live steering — a playbook step is fire-per-step, a steer mid-run has
// nowhere to land (the SDK answers `unsupported`, never a lie).
import {
  fullHarnessCapabilities,
  HARNESS_COMMAND_WORK_NUDGE,
  HARNESS_COMMAND_WORK_STATUS_REQUEST,
  HARNESS_COMMAND_WORK_STEER,
  type HarnessCapabilities,
} from "@ofac/adapter";

export const CLI_CAPABILITIES: HarnessCapabilities = fullHarnessCapabilities([
  {
    command: HARNESS_COMMAND_WORK_STEER,
    reason: "fire-per-step: no mid-run channel",
  },
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
