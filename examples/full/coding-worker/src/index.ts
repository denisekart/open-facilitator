// @ofac/example-coding-worker — the full-profile coding example: config
// loader + the deterministic scripted-coder harness + `ofac-coding-worker`.
// Third-party implementers copy this package as the shape of a harness that
// mutates a repo over git-http and asks the gateway for the PR via the
// result manifest's structuredResult (never a direct API call).
export {
  DEFAULT_RESTART_BACKOFF_MS,
  WorkerConfigError,
  loadWorkerConfigFile,
  parseWorkerConfig,
  type WorkerConfig,
  type WorkerTransport,
} from "./config.js";
export {
  createCodingHooks,
  shortId,
  type CodingHooks,
  type CodingRunOptions,
  type CodingStructuredResult,
  type CommentRequest,
  type PrRequest,
} from "./harness.js";
export {
  checkoutBranch,
  commitAll,
  gitClone,
  injectToken,
  pushLease,
  runTool,
  scrubSecrets,
  GitError,
  type GitRunOptions,
  type GitRunResult,
} from "./git.js";
