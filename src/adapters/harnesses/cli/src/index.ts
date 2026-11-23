// @ofac/harness-cli — the playbook-driven HarnessAdapter: a plain
// command-line program becomes a team worker by declaring its runs in a
// YAML playbook (`kind: Playbook`), no TypeScript fork required. Config
// loader + playbook grammar + step executor + `ofac-cli-worker`.
export { CLI_CAPABILITIES } from "./capabilities.js";
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
  PlaybookError,
  loadPlaybookFile,
  parsePlaybook,
  type ParsePlaybookOptions,
} from "./playbook/load.js";
export type {
  AskUserInject,
  Playbook,
  PlaybookCapability,
  PlaybookStep,
  PromptMarker,
  StepAsk,
  StepCapture,
  StepExpect,
  StepRetry,
  StepRun,
} from "./playbook/schema.js";
export { createCliHooks, type CliHooks, type CliRunOptions } from "./harness.js";
export { deansify, DeansifyWindow } from "./playbook/ansi.js";
export { startAskUserBridge, writeMcpConfig } from "./mcp/bridge.js";
