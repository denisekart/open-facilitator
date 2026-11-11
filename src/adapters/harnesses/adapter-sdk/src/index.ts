// @ofac/adapter — the HarnessAdapter lifecycle contract as a library
// (wire profile: protocol/json-schema/adapter.v1.json). Thin by
// law: the native harness stays authoritative; this package owns
// the child-connection lifecycle (register, reply law, restart policy,
// reconcile-honest registry) and NOTHING of the harness's behavior.
//
// The `./testing` export ships the lifecycle conformance kit a third-party
// adapter runs against its own implementation — see README.
export const ADAPTER_PACKAGE_NAME = "@ofac/adapter";

export { ADAPTER_PACKAGE, DEFAULT_RESTART_BACKOFF_MS, createAdapter } from "./adapter.js";
export {
  fullHarnessCapabilities,
  harnessCapabilityDescriptors,
  HARNESS_COMMANDS,
  HARNESS_COMMAND_ARTIFACT_CONFIRM_UPLOAD,
  HARNESS_COMMAND_ARTIFACT_REQUEST_UPLOAD,
  HARNESS_COMMAND_ASSIGNMENT_INPUT,
  HARNESS_COMMAND_CONTEXT_FETCH,
  HARNESS_COMMAND_MEMORY_RECALL,
  HARNESS_COMMAND_MEMORY_RETAIN,
  HARNESS_COMMAND_WORK_ASSIGN,
  HARNESS_COMMAND_WORK_CANCEL,
  HARNESS_COMMAND_WORK_NUDGE,
  HARNESS_COMMAND_WORK_STATUS_REQUEST,
  HARNESS_COMMAND_WORK_STEER,
  HARNESS_EVENTS,
  HARNESS_EVENT_ARTIFACT_AVAILABLE,
  HARNESS_EVENT_ASSIGNMENT_COMPLETED,
  HARNESS_EVENT_ASSIGNMENT_FAILED,
  HARNESS_EVENT_ASSIGNMENT_INPUT_REQUIRED,
  HARNESS_EVENT_MEMORY_CANDIDATE,
  HARNESS_EVENT_MEMORY_USED,
  HARNESS_EVENT_WORK_PROGRESS,
  HARNESS_OUTBOUND_COMMANDS,
  HARNESS_FRAMES,
  unsupportedFor,
  type HarnessCapabilities,
} from "./capabilities.js";
export {
  artifactUri,
  buildCompletedEvent,
  buildResultManifest,
  createArtifactClient,
  type ArtifactClient,
  type CompletedEventContext,
  type KernelHttpOptions,
  type ResultManifest,
  type ResultManifestInput,
  type ResultManifestOutput,
  type UploadOptions,
} from "./artifacts.js";
export {
  createMemoryClient,
  type CandidateProvenance,
  type MemoryCandidateInput,
  type MemoryClient,
  type MemoryPolicyLabel,
  type MemoryProvenance,
  type MemoryType,
  type RecallEntry,
  type RecallPurpose,
  type RecallRequest,
  type RecallResponse,
  type RetainReceipt,
  type RetainRequest,
  type UsageAttestation,
} from "./memory.js";
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
