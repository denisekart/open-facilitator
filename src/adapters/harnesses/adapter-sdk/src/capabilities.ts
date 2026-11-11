// The harness-facing functionality list: the COMPLETE set of what a
// HarnessAdapter may be asked to do, what it may answer with, and what it
// sends the kernel — one law (mirroring the comms plane: provider-core
// owns the per-provider command family, the SDK owns this).
//
// The split is deliberate, by DIRECTION:
//   - HARNESS_COMMANDS: the envelope `type`s that may ARRIVE at the child
//     (inside a `start`/`steer` frame or a plain `command` frame). An
//     adapter that cannot serve one answers `unsupported` — never silence.
//   - HARNESS_EVENTS: the envelope `type`s a harness may PUBLISH upstream.
//     The kernel arms these classes for executors; anything else is a
//     schema-violation on arrival.
//   - HARNESS_OUTBOUND_COMMANDS: the commands a harness SENDS the kernel
//     (the SDK's artifact/memory clients post these on the link plane).
//   - HARNESS_FRAMES: the adapter.v1 lifecycle frames the wire itself
//     carries — lifecycle mechanics, not capability.
//
// Truthfulness law: a manifest is the FULL intended list minus what the
// native runtime genuinely lacks, and an omission must be honest (the
// adapter answers `unsupported` with the recorded reason) — a silent gap
// is a defect.
import type { EnvelopeCommand } from "@ofac/protocol";

// ── inbound: what may ARRIVE at a harness ──────────────────────────────────

/** The run begins (the envelope is the work.assign verbatim; `start`). */
export const HARNESS_COMMAND_WORK_ASSIGN =
  "org.facilitator.command.work.assign.v1";
/** Steer a running execution (`steer` frame). */
export const HARNESS_COMMAND_WORK_STEER =
  "org.facilitator.command.work.steer.v1";
/** Cancel a running execution (`stop` frame). */
export const HARNESS_COMMAND_WORK_CANCEL =
  "org.facilitator.command.work.cancel.v1";
/** The human's answer to an open `assignment.input.required` (`respond`). */
export const HARNESS_COMMAND_ASSIGNMENT_INPUT =
  "org.facilitator.command.assignment.input.v1";
/** A nudge for progress on a running execution (capability
 *  `supportsNudge`; the NudgeSweeper posts it to the assigned endpoint;
 *  an adapter without one answers `unsupported`). */
export const HARNESS_COMMAND_WORK_NUDGE =
  "org.facilitator.command.work.nudge.v1";
/** The human asks where a run stands (contract-declared; no kernel
 *  producer yet — a harness still answers, never hangs). */
export const HARNESS_COMMAND_WORK_STATUS_REQUEST =
  "org.facilitator.command.work.status.request.v1";

/** Every command class that may arrive at a harness. An adapter's manifest
 *  law: this full list, minus honest omissions. */
export const HARNESS_COMMANDS: readonly string[] = Object.freeze([
  HARNESS_COMMAND_WORK_ASSIGN,
  HARNESS_COMMAND_WORK_STEER,
  HARNESS_COMMAND_WORK_CANCEL,
  HARNESS_COMMAND_ASSIGNMENT_INPUT,
  HARNESS_COMMAND_WORK_NUDGE,
  HARNESS_COMMAND_WORK_STATUS_REQUEST,
] as const);

// ── emitted: what a harness PUBLISHES ───────────────────────────────────────

/** Lifecycle progress of a run. */
export const HARNESS_EVENT_WORK_PROGRESS =
  "org.facilitator.event.work.progress.v1";
/** The run needs the human (the respond frame settles it). */
export const HARNESS_EVENT_ASSIGNMENT_INPUT_REQUIRED =
  "org.facilitator.event.assignment.input.required.v1";
/** The run finished, with its result manifest. */
export const HARNESS_EVENT_ASSIGNMENT_COMPLETED =
  "org.facilitator.event.assignment.completed.v1";
/** The run failed, with its reason. */
export const HARNESS_EVENT_ASSIGNMENT_FAILED =
  "org.facilitator.event.assignment.failed.v1";
/** The run stored an artifact. */
export const HARNESS_EVENT_ARTIFACT_AVAILABLE =
  "org.facilitator.event.artifact.available.v1";
/** The run proposes a memory (kernel policy decides retention). */
export const HARNESS_EVENT_MEMORY_CANDIDATE =
  "org.facilitator.event.memory.candidate.v1";
/** The run consumed memory (provenance of what it read). */
export const HARNESS_EVENT_MEMORY_USED =
  "org.facilitator.event.memory.used.v1";

/** Every event class a harness may publish. */
export const HARNESS_EVENTS: readonly string[] = Object.freeze([
  HARNESS_EVENT_WORK_PROGRESS,
  HARNESS_EVENT_ASSIGNMENT_INPUT_REQUIRED,
  HARNESS_EVENT_ASSIGNMENT_COMPLETED,
  HARNESS_EVENT_ASSIGNMENT_FAILED,
  HARNESS_EVENT_ARTIFACT_AVAILABLE,
  HARNESS_EVENT_MEMORY_CANDIDATE,
  HARNESS_EVENT_MEMORY_USED,
] as const);

// ── outbound: what a harness SENDS the kernel ────────────────────────────────

/** The artifact-upload handshake — slot allocation (the SDK artifact
 *  client posts it; see artifacts.ts). */
export const HARNESS_COMMAND_ARTIFACT_REQUEST_UPLOAD =
  "org.facilitator.command.artifact.request_upload.v1";
/** The artifact-upload handshake — settle confirmation. */
export const HARNESS_COMMAND_ARTIFACT_CONFIRM_UPLOAD =
  "org.facilitator.command.artifact.confirm_upload.v1";
/** Retain a memory the run earned (kernel policy decides). */
export const HARNESS_COMMAND_MEMORY_RETAIN =
  "org.facilitator.command.memory.retain.v1";
/** A memory recall the run wants. */
export const HARNESS_COMMAND_MEMORY_RECALL =
  "org.facilitator.command.memory.recall.v1";
/** Fetch context the kernel holds (artifacts, memory, work state). */
export const HARNESS_COMMAND_CONTEXT_FETCH =
  "org.facilitator.command.context.fetch.v1";

/** Every command class a harness may send. */
export const HARNESS_OUTBOUND_COMMANDS: readonly string[] = Object.freeze([
  HARNESS_COMMAND_ARTIFACT_REQUEST_UPLOAD,
  HARNESS_COMMAND_ARTIFACT_CONFIRM_UPLOAD,
  HARNESS_COMMAND_MEMORY_RETAIN,
  HARNESS_COMMAND_MEMORY_RECALL,
  HARNESS_COMMAND_CONTEXT_FETCH,
] as const);

/** The adapter.v1 lifecycle frames (wire mechanics, not capability). */
export const HARNESS_FRAMES: readonly string[] = Object.freeze([
  "hello",
  "registerAck",
  "command",
  "reply",
  "event",
  "heartbeat",
  "goaway",
] as const);

/** The honest capability picture of ONE adapter: the full intended list
 *  plus the omissions it lives with. omp/codex/deterministic each derive
 *  theirs; the descriptor's `supports` block is DERIVED from it, never a
 *  second opinion. */
export interface HarnessCapabilities {
  /** What this adapter serves (HARNESS_COMMANDS minus `omittedCommands`). */
  readonly commands: readonly string[];
  /** What this adapter publishes. */
  readonly events: readonly string[];
  /** The kernel-facing commands this adapter uses (the SDK clients cover
   *  the full set; a hand-rolled adapter may carry a subset). */
  readonly outboundCommands: readonly string[];
  /** Commands from the full list it will NOT serve, each with the reason
   *  the native runtime says so (the truthfulness law: an omission is
   *  recorded, never silent). */
  readonly omittedCommands: readonly { command: string; reason: string }[];
}

/** The full-list capability set: every inbound command, every event, every
 *  outbound command, no omissions — the shape an SDK-based adapter with a
 *  capable runtime advertises. */
export function fullHarnessCapabilities(
  omissions: readonly { command: string; reason: string }[] = [],
): HarnessCapabilities {
  const omitted = new Set(omissions.map((o) => o.command));
  return {
    commands: HARNESS_COMMANDS.filter((c) => !omitted.has(c)),
    events: HARNESS_EVENTS,
    outboundCommands: HARNESS_OUTBOUND_COMMANDS,
    omittedCommands: omissions,
  };
}

/** The manifest face: descriptors for the kernel's capability registry,
 *  derived from ONE source — this list. Each inbound command becomes a
 *  synchronous descriptor (the reply law answers per frame); the run
 *  itself is the async capability the deployment manifest names. */
export function harnessCapabilityDescriptors(
  caps: HarnessCapabilities,
): {
  id: string;
  version: number;
  execution: "synchronous";
  inputSchema: string;
}[] {
  return caps.commands.map((id) => ({
    id,
    version: 1,
    execution: "synchronous" as const,
    // org.facilitator.command.X.v1 -> .../v1/command.X.v1.json
    inputSchema: `https://schemas.openfacilitator.org/v1/${id.replace(/^org\.facilitator\./, "")}.json`,
  }));
}

/** The `unsupported` answer an adapter owes for anything outside its list
 *  (never a timeout, never silence). */
export function unsupportedFor(
  cmd: EnvelopeCommand,
  caps: HarnessCapabilities,
): { kind: "unsupported"; reason?: string } | undefined {
  return caps.commands.includes(cmd.type)
    ? undefined
    : {
        kind: "unsupported" as const,
        reason: caps.omittedCommands.find((o) => o.command === cmd.type)?.reason,
      };
}
