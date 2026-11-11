// The honest functionality picture of the omp harness (the SDK law: a
// manifest is the full list minus what the plane/runtime genuinely lacks,
// each omission with its reason — see @ofac/adapter capabilities.ts).
// omp steers live (rpc mode), cancels, asks, uploads, remembers.
import {
  fullHarnessCapabilities,
  HARNESS_COMMAND_WORK_NUDGE,
  HARNESS_COMMAND_WORK_STATUS_REQUEST,
  type HarnessCapabilities,
} from "@ofac/adapter";

export const OMP_CAPABILITIES: HarnessCapabilities = fullHarnessCapabilities([
  {
    command: HARNESS_COMMAND_WORK_NUDGE,
    reason: "no nudge frame on the adapter.v1 child wire (the Gateway answers on the child's behalf)",
  },
  {
    command: HARNESS_COMMAND_WORK_STATUS_REQUEST,
    reason: "no kernel producer yet, and no side channel to inject a status answer mid-run",
  },
]);
