// @ofac/adapter — the HarnessAdapter lifecycle contract as a library
// (spec §8.3; wire profile: protocol/json-schema/adapter.v1.json). Thin by
// law: the native harness stays authoritative (S:422); this package owns
// the child-connection lifecycle (register, reply law, restart policy,
// reconcile-honest registry) and NOTHING of the harness's behavior.
//
// The `./testing` export ships the lifecycle conformance kit a third-party
// adapter runs against its own implementation — see README.
export const ADAPTER_PACKAGE_NAME = "@ofac/adapter";

export { ADAPTER_PACKAGE, DEFAULT_RESTART_BACKOFF_MS, createAdapter } from "./adapter.js";
export {
  ExecutionRegistry,
  TERMINAL_TTL_MS,
  type RegistryEntry,
  type RegistryState,
} from "./registry.js";
export type {
  AdapterDescriptor,
  AdapterHandle,
  AdapterStatus,
  CreateAdapterOptions,
  HarnessHooks,
  RunContext,
  RunOutcome,
  SessionStore,
} from "./types.js";
