// @ofac/gateway — the team-side composition over @ofac/workspace-link:
// one Link endpoint (gw.<slug>), one embedded child behind the serializable
// ChildExecutor vocabulary, the §6.1 execution ledger, reconcile-on-restart,
// a deny-by-default policy seam, and a localhost diagnostics endpoint.
// The Gateway composes the SDK — it never forks it (spec S:471).
export const GATEWAY_PACKAGE = "@ofac/gateway";

export {
  GATEWAY_API_VERSION,
  TRUST_MODES,
  GatewayConfigError,
  loadConfigFile,
  parseConfig,
  tokenFilePath,
  type ChildMode,
  type GatewayConfig,
  type ParseConfigOptions,
  type TrustMode,
} from "./config.js";
export {
  DefaultPolicy,
  type PolicyAction,
  type PolicyContext,
  type PolicyEvaluator,
} from "./policy.js";
export {
  BindingConflictError,
  ExecutionLedger,
  type BindingRow,
  type BindingState,
  type NewBinding,
} from "./ledger.js";
export {
  MANIFEST_SCHEMA_ID,
  EmbeddedChild,
  builtinChildManifest,
  newExecutionId,
  type ChildExecutor,
  type ChildMessage,
  type ChildReply,
  type EmbeddedChildOptions,
} from "./child.js";
export {
  GatewayRuntime,
  createGatewayRuntime,
  type CreateGatewayRuntimeDeps,
  type GatewayRuntimeState,
  type GatewayStatus,
} from "./runtime.js";
export { startDiagnostics, type DiagnosticsHandle } from "./diagnostics.js";
