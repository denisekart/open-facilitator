// @ofac/gitlab-connector: the team-side privileged git arm.
// A Gateway fleet child that executes git OPS (open MRs, comment, label,
// review, reply/resolve discussions, push branches, fire commit statuses)
// under the Gateway's policy/approval umbrella; the gitlab token never
// leaves this process. The inbound/event half lives in @ofac/gitlab-adapter;
// this package is outbound mutations on the @ofac/provider-core connector
// face (config law, connector_actions ledger, flag-filtered manifest, SDK
// hooks — read-before-reconcile law). The bin entry (./bin/connector.js) is
// deliberately NOT re-exported here: a tsup multi-entry build hoists the
// program guard into a shared chunk whose import.meta.url never equals
// argv[1], so the binary would stop executing when invoked.
export {
  ConnectorConfigError,
  FLAG_DOMAINS,
  connectorManifest,
  loadConnectorConfigFile,
  parseConnectorConfig,
  type ConnectorConfig,
  type ConnectorFlags,
} from "./config.js";
export {
  ActionLedger,
  ActionLedgerConflictError,
  type ActionRow,
  type ActionState,
} from "@ofac/provider-core";
export {
  CMD_MARKER,
  createOps,
  OpError,
  payloadSha256,
  type OpDeps,
  type OpResult,
  type OpRunner,
  type OpTable,
} from "./ops.js";
export { createConnectorHooks, type ConnectorHooks } from "@ofac/provider-core";
export type { ConnectorTransport } from "@ofac/provider-core";
