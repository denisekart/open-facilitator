// @ofac/harness-omp — the omp HarnessAdapter: config loader + the
// `omp --mode rpc` TUI stand-in (agentic mode, ask_user relay, native
// session resume) + `ofac-omp-worker`. Shaped after
// @ofac/harness-deterministic; the conformance kit proves the lifecycle.
export { OMP_CAPABILITIES } from "./capabilities.js";
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
  SYSTEM_PROMPT_INJECTION,
  createOmpHooks,
  type OmpHooks,
  type OmpInvocation,
  type OmpRunOptions,
} from "./harness.js";
export { FileSessionStore } from "./session-store.js";
export { OmpRpcSession, type OmpExit } from "./rpc.js";
export {
  startAskUserBridge,
  writeMcpConfig,
  type AskUserBridge,
  type AskUserParams,
} from "./mcp/bridge.js";
