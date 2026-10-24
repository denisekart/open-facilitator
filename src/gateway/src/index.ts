// @ofac/gateway — the team-side composition over @ofac/workspace-link:
// one Link endpoint (gw.<slug>), a fleet of children (embedded builtin or
// socket-attached) behind the serializable ChildExecutor vocabulary, the
// §6.1 execution ledger + per-child fleet state, reconcile-on-restart, a
// deny-by-default policy seam, and a localhost diagnostics endpoint.
// The Gateway composes the SDK — it never forks it (spec S:471).
export const GATEWAY_PACKAGE = "@ofac/gateway";

export {
  GATEWAY_API_VERSION,
  TRUST_MODES,
  GatewayConfigError,
  loadConfigFile,
  parseConfig,
  tokenFilePath,
  type ChildEntryConfig,
  type ChildMode,
  type ChildTransportConfig,
  type GatewayConfig,
  type GatewayMode,
  type ParseConfigOptions,
  type TrustMode,
} from "./config.js";
export {
  MAX_CHILD_FRAME_BYTES,
  createFrameParser,
  encodeFrame,
  type ChildWireFrame,
  type CommandFrame,
  type FrameParseError,
  type GoawayReason,
  type HelloFrame,
  type RegisterDenyReason,
  type RegisterAckFrame,
} from "./childwire.js";
export {
  ChildFleet,
  ChildLink,
  ChildTransportError,
  EmbeddedMember,
  MAX_CHILDREN,
  digestOf,
  type ChildStatusRow,
  type FleetMember,
} from "./child-link.js";
export {
  startChildListener,
  tokenMatches,
  type ChildListener,
  type ChildListenerDeps,
} from "./listen.js";
export { connectChild, type ConnectChildOptions, type ChildConnection } from "./connect.js";
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
