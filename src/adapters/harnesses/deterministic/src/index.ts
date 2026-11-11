// @ofac/harness-deterministic — the worked-example HarnessAdapter: config
// loader + deterministic two-step research script + `ofac-deterministic-worker`.
// Third-party implementers copy this package (src/harness.ts especially)
// as the reference shape of harness hooks over @ofac/adapter.
export { DETERMINISTIC_CAPABILITIES } from "./capabilities.js";
export type { HarnessCapabilities } from "@ofac/adapter";
export {
  DEFAULT_RESTART_BACKOFF_MS,
  WorkerConfigError,
  loadWorkerConfigFile,
  parseWorkerConfig,
  type WorkerConfig,
  type WorkerTransport,
} from "./config.js";
export {
  createDeterministicHooks,
  type DeterministicHooks,
  type DeterministicRunOptions,
} from "./harness.js";
