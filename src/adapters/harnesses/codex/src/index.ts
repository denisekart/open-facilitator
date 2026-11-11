// @ofac/harness-codex — the Codex HarnessAdapter: config
// loader + codex-exec harness hooks + the resume ledger + the ask_user MCP
// bridge, and the ofac-codex-worker binary. Third-party implementers copy
// this package (src/harness.ts especially) as the reference shape of
// harness hooks over @ofac/adapter for a batch CLI harness with
// mid-run-interaction but NO mid-run steering.
export { CODEX_CAPABILITIES } from "./capabilities.js";
export type { HarnessCapabilities } from "@ofac/adapter";
export { DEFAULT_RESTART_BACKOFF_MS } from "./config.js";
export { WorkerConfigError, parseWorkerConfig, loadWorkerConfigFile } from "./config.js";
export type {
  WorkerConfig,
  WorkerTransport,
  ParseWorkerOptions,
  InteractivityMode,
} from "./config.js";
export { createCodexHooks } from "./harness.js";
export type { CodexHooks, CodexRunOptions } from "./harness.js";
export { FileSessionStore } from "./sessions.js";
export type { SessionEntry, ResumePointer } from "./sessions.js";
export { startAskUserBridge, writeCodexConfig } from "./mcp/bridge.js";
export type { AskUserBridge, AskUserParams } from "./mcp/bridge.js";
