// @ofac/workspace-link — the official TypeScript Workspace Link client SDK.
// Transport-neutral core: state machine, delivery pipeline law, enrollment,
// envelope/receipt builders, fault-injection test mode. Node-only pieces
// (SignalR + HTTP-poll transports, SQLite ledger) sit behind "./node".
export const LINK_PACKAGE = "@ofac/workspace-link";

export * from "./core/types.js";
export * from "./core/errors.js";
export { LinkClient, createLinkClient, type LinkClientOptions } from "./core/client.js";
export { parseArgv, ConfigError, type LinkdConfig } from "./core/config.js";
export {
  loadOrCreateIdentity,
  verifyWithPublicKey,
  keyIdFromPublicKey,
  type EndpointIdentity,
} from "./core/keystore.js";
export {
  buildProof,
  buildRotationProof,
  signProofForGateway,
  parseProof,
  verifyProof,
  type ProofClaims,
  type ProofFields,
  type ProofRejection,
  type VerifyProofOptions,
} from "./core/proof.js";
export { SessionProvider, type SessionProviderOptions } from "./core/session.js";
export {
  createEndpointCredential,
  identityDirFor,
  isNotEnrolled,
  type EndpointCredential,
  type EndpointCredentialOptions,
} from "./core/credential.js";
export {
  dedupeKeyOf,
  type InsertResult,
  type Ledger,
  type LedgerRow,
  type LedgerState,
} from "./core/ledger.js";
export { MemoryLedger } from "./core/memory-ledger.js";
export {
  enroll,
  parseProblem,
  trimSlash,
  ENROLL_SCHEMA_ID,
  ENROLL_RESPONSE_SCHEMA_ID,
  type EnrollOptions,
} from "./core/enroll.js";
export {
  oidcClientCredentialsTokenProvider,
  type OidcClientCredentialsOptions,
} from "./core/oidc-token-provider.js";
export { buildEvent, dataschemaFor, SCHEMA_BASE, type BuildEventOptions } from "./core/envelope.js";
export { FaultInjector, type FaultPolicy } from "./core/fault-injector.js";
export { createEchoHandler, createStubHandler, type EchoHandlerOptions } from "./core/handlers.js";
export {
  createConsoleLogger,
  createMemoryLogger,
  type ConsoleLoggerOptions,
} from "./core/logging.js";
