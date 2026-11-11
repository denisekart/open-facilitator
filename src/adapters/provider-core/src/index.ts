// @ofac/provider-core — the single way to integrate a provider. Two faces,
// no provider vocabulary inside: the ADAPTER face (link-plane run loop +
// webhook fan-out receiver) and the CONNECTOR face (config fail-closed law,
// durable action ledger, flag-filtered manifest, op-table contract, SDK
// hooks). Provider-local stays outside the core: REST clients, webhook
// signature schemes, vendor→domain mapping, init seeding.
export {
  CHANGE_REQUEST_EVENT_TYPE,
  ISSUE_EVENT_TYPE,
  MENTION_EVENT_TYPE,
  PIPELINE_EVENT_TYPE,
  id,
  isBotAuthored,
  mentionsBot,
  normalizeLabels,
  num,
  obj,
  str,
  workTriggerFor,
  type NormalizedEvent,
  type NormalizedEventData,
  type Payload,
} from "./normalize-shared.js";
export {
  ActionLedger,
  ActionLedgerConflictError,
  type ActionRow,
  type ActionState,
} from "./ledger-core.js";
export {
  CMD_MARKER,
  OpError,
  connectorManifest,
  payloadSha256,
  degradationFrom,
  replayFrom,
  required,
  requiredNumber,
  stampedBody,
  type OpDegradation,
  type OpDeps,
  type OpResult,
  type OpRunner,
  type OpTable,
} from "./ops-core.js";
export { createConnectorHooks, type ConnectorHooks } from "./hooks-core.js";
export {
  HttpError,
  httpErrorFrom,
  parseRetryAfter,
  withRetry,
  type RetryAttempt,
  type WithRetryOptions,
} from "./http-core.js";
export { createLoopBudget, withLoopBudget, type LoopBudget } from "./loop-budget.js";
export {
  ensureWebhook,
  replaceWebhook,
  type WebhookEnsureOutcome,
  type WebhookRegistrationApi,
  type WebhookSpec,
} from "./webhook-registration.js";
export {
  ConnectorConfigError,
  DEFAULT_BACKOFF,
  asRecord,
  fail,
  flagOn,
  optionalInt,
  optionalString,
  parseBackoff,
  parseSkeleton,
  parseTransport,
  readToken,
  readYamlRoot,
  rejectUnknown,
  requiredEnv,
  resolvePath,
  type ConnectorSkeleton,
  type ConnectorTransport,
  type ParseOptions,
} from "./config-core.js";
