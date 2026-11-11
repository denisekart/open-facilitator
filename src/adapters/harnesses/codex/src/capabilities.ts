// The honest functionality picture of the codex harness: like omp, minus
// live steering — OpenAI `codex exec` is fire-per-turn, a steer mid-run has
// nowhere to land (the SDK answers `unsupported`, never a lie).
import {
  fullHarnessCapabilities,
  HARNESS_COMMAND_WORK_NUDGE,
  HARNESS_COMMAND_WORK_STATUS_REQUEST,
  HARNESS_COMMAND_WORK_STEER,
  type HarnessCapabilities,
} from "@ofac/adapter";

export const CODEX_CAPABILITIES: HarnessCapabilities = fullHarnessCapabilities([
  {
    command: HARNESS_COMMAND_WORK_STEER,
    reason: "codex exec is fire-per-turn: no mid-run steering channel",
  },
  {
    command: HARNESS_COMMAND_WORK_NUDGE,
    reason:
      "no nudge frame on the adapter.v1 child wire (the Gateway answers on the child's behalf)",
  },
  {
    command: HARNESS_COMMAND_WORK_STATUS_REQUEST,
    reason: "no kernel producer yet, and no side channel to inject a status answer mid-run",
  },
]);
