// Schema registry for @ofac/protocol. The normative JSON Schemas (draft
// 2020-12, protocol/json-schema + protocol/cloudevents-schemas) ship as plain
// .json files, loaded via import attributes; codegen mirrors them into
// schemas/ — never edit the copies here.
// Validators resolve a CloudEvents `dataschema` value against this registry by
// $id — never by HTTP fetch (spec §5.2: `dataschema` identifies an advertised
// immutable schema).
import commonV1 from "../schemas/common.v1.json" with { type: "json" };
import envelope from "../schemas/envelope.json" with { type: "json" };
import envelopeCommand from "../schemas/envelope-command.json" with { type: "json" };
import envelopeEvent from "../schemas/envelope-event.json" with { type: "json" };
import commandWorkAssign from "../schemas/command.work.assign.v1.json" with { type: "json" };
import commandWorkSteer from "../schemas/command.work.steer.v1.json" with { type: "json" };
import commandWorkCancel from "../schemas/command.work.cancel.v1.json" with { type: "json" };
import commandWorkStatusRequest from "../schemas/command.work.status.request.v1.json" with { type: "json" };
import commandWorkNudge from "../schemas/command.work.nudge.v1.json" with { type: "json" };
import commandContextFetch from "../schemas/command.context.fetch.v1.json" with { type: "json" };
import commandApprovalConsume from "../schemas/command.approval.consume.v1.json" with { type: "json" };
import commandApprovalRespond from "../schemas/command.approval.respond.v1.json" with { type: "json" };
import commandArtifactRequestUpload from "../schemas/command.artifact.request_upload.v1.json" with { type: "json" };
import commandArtifactConfirmUpload from "../schemas/command.artifact.confirm_upload.v1.json" with { type: "json" };
import commandMemoryRetain from "../schemas/command.memory.retain.v1.json" with { type: "json" };
import commandMemoryRecall from "../schemas/command.memory.recall.v1.json" with { type: "json" };
import commandCommunicationReply from "../schemas/command.communication.reply.v1.json" with { type: "json" };
import commandCommunicationReact from "../schemas/command.communication.react.v1.json" with { type: "json" };
import commandCommunicationUpdate from "../schemas/command.communication.update.v1.json" with { type: "json" };
import commandCommunicationAcknowledge from "../schemas/command.communication.acknowledge.v1.json" with { type: "json" };
import eventWorkAccepted from "../schemas/event.work.accepted.v1.json" with { type: "json" };
import eventWorkProgress from "../schemas/event.work.progress.v1.json" with { type: "json" };
import eventWorkInputRequired from "../schemas/event.work.input_required.v1.json" with { type: "json" };
import eventAssignmentCompleted from "../schemas/event.assignment.completed.v1.json" with { type: "json" };
import eventAssignmentFailed from "../schemas/event.assignment.failed.v1.json" with { type: "json" };
import eventWorkspaceHeartbeat from "../schemas/event.workspace.heartbeat.v1.json" with { type: "json" };
import eventArtifactAvailable from "../schemas/event.artifact.available.v1.json" with { type: "json" };
import eventMemoryCandidate from "../schemas/event.memory.candidate.v1.json" with { type: "json" };
import eventApprovalDecided from "../schemas/event.approval.decided.v1.json" with { type: "json" };
import eventApprovalRequired from "../schemas/event.approval.required.v1.json" with { type: "json" };
import eventApprovalRevoked from "../schemas/event.approval.revoked.v1.json" with { type: "json" };
import eventMemoryUsed from "../schemas/event.memory.used.v1.json" with { type: "json" };
import eventProviderMattermostMention from "../schemas/event.provider.mattermost.mention.v1.json" with { type: "json" };
import eventProviderMattermostReaction from "../schemas/event.provider.mattermost.reaction.v1.json" with { type: "json" };
import eventProviderMattermostMessage from "../schemas/event.provider.mattermost.message.v1.json" with { type: "json" };
import eventProviderForgejoIssue from "../schemas/event.provider.forgejo.issue.v1.json" with { type: "json" };
import eventProviderForgejoPullRequest from "../schemas/event.provider.forgejo.pull_request.v1.json" with { type: "json" };
import eventProviderForgejoCi from "../schemas/event.provider.forgejo.ci.v1.json" with { type: "json" };
import receiptV1 from "../schemas/receipt.v1.json" with { type: "json" };
import controlV1 from "../schemas/control.v1.json" with { type: "json" };
import resultManifestV1 from "../schemas/result-manifest.v1.json" with { type: "json" };
import capabilityManifestV1 from "../schemas/capability-manifest.v1.json" with { type: "json" };
import enrollRequestV1 from "../schemas/enroll-request.v1.json" with { type: "json" };
import enrollResponseV1 from "../schemas/enroll-response.v1.json" with { type: "json" };
import linkDeliveryV1 from "../schemas/link-delivery.v1.json" with { type: "json" };
import adapterV1 from "../schemas/adapter.v1.json" with { type: "json" };
import policyFileV1 from "../schemas/policy-file.v1.json" with { type: "json" };

export type JsonSchema = Record<string, unknown>;

// SAFETY: every imported document is a JSON Schema draft 2020-12 object (a
// JSON object), structurally Record<string, unknown>; TS types import-attribute
// JSON as anonymous literals and cannot express that.
const all: JsonSchema[] = [
  commonV1,
  envelope,
  envelopeCommand,
  envelopeEvent,
  commandWorkAssign,
  commandWorkSteer,
  commandWorkCancel,
  commandWorkStatusRequest,
  commandWorkNudge,
  commandContextFetch,
  commandApprovalConsume,
  commandApprovalRespond,
  commandArtifactRequestUpload,
  commandArtifactConfirmUpload,
  commandMemoryRetain,
  commandMemoryRecall,
  commandCommunicationReply,
  commandCommunicationReact,
  commandCommunicationUpdate,
  commandCommunicationAcknowledge,
  eventWorkAccepted,
  eventWorkProgress,
  eventWorkInputRequired,
  eventAssignmentCompleted,
  eventAssignmentFailed,
  eventWorkspaceHeartbeat,
  eventArtifactAvailable,
  eventMemoryCandidate,
  eventApprovalDecided,
  eventApprovalRequired,
  eventApprovalRevoked,
  eventMemoryUsed,
  eventProviderMattermostMention,
  eventProviderMattermostReaction,
  eventProviderMattermostMessage,
  eventProviderForgejoIssue,
  eventProviderForgejoPullRequest,
  eventProviderForgejoCi,
  receiptV1,
  controlV1,
  resultManifestV1,
  capabilityManifestV1,
  enrollRequestV1,
  enrollResponseV1,
  linkDeliveryV1,
  adapterV1,
  policyFileV1,
] as unknown as JsonSchema[];

const byId = new Map<string, JsonSchema>(all.map((s) => [s["$id"] as string, s]));

/** Resolve a `dataschema` (or any) schema $id to its compiled-ready document. */
export function schemaById(id: string): JsonSchema | undefined {
  return byId.get(id);
}

/** Every registered schema $id (all under https://schemas.openfacilitator.org/v1/). */
export const SCHEMA_IDS: readonly string[] = Object.freeze([...byId.keys()].sort());

/** All schema documents, for validator compilation ($ref resolution included). */
export const ALL_SCHEMAS: readonly JsonSchema[] = Object.freeze([...all]);

export const ENVELOPE_SCHEMA_ID = "https://schemas.openfacilitator.org/v1/envelope.json";
export const ENVELOPE_COMMAND_SCHEMA_ID =
  "https://schemas.openfacilitator.org/v1/envelope-command.json";
export const ENVELOPE_EVENT_SCHEMA_ID =
  "https://schemas.openfacilitator.org/v1/envelope-event.json";
export const RECEIPT_SCHEMA_ID = "https://schemas.openfacilitator.org/v1/receipt.v1.json";
export const CONTROL_SCHEMA_ID = "https://schemas.openfacilitator.org/v1/control.v1.json";
