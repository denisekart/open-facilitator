// Outbound CloudEvent construction (linkd events, handler progress/completion
// publishes). `buildEvent` fills the required envelope fields, then
// validateEnvelope gates before send — the client never ships an envelope it
// would itself reject.
import { randomUUID } from "node:crypto";
import { validateEnvelope, type EnvelopeEvent } from "@ofac/protocol";
import { LinkError } from "./errors.js";

/** Event payload schemas are advertised under the frozen contract base (fixture pattern). */
export const SCHEMA_BASE = "https://schemas.openfacilitator.org/v1/";

/** `org.facilitator.event.work.progress.v1` → `…/v1/event.work.progress.v1.json`. */
export function dataschemaFor(eventType: string): string {
  const short = eventType.replace(/^org\.facilitator\./, "");
  return `${SCHEMA_BASE}${short}.json`;
}

export interface BuildEventOptions {
  source: string;
  subject: string;
  data?: Record<string, unknown>;
  /** Correlate back to the triggering delivery's envelope when present. */
  traceparent?: string;
  correlationid?: string;
  causationid?: string;
  id?: string;
  time?: string;
  dataschema?: string;
}

export function buildEvent(type: string, options: BuildEventOptions): EnvelopeEvent {
  const env: EnvelopeEvent = {
    specversion: "1.0",
    id: options.id ?? randomUUID(),
    source: options.source,
    type,
    subject: options.subject,
    time: options.time ?? new Date().toISOString(),
    datacontenttype: "application/json",
    dataschema: options.dataschema ?? dataschemaFor(type),
    ...(options.data !== undefined ? { data: options.data } : {}),
    ...(options.traceparent !== undefined ? { traceparent: options.traceparent } : {}),
    ...(options.correlationid !== undefined ? { correlationid: options.correlationid } : {}),
    ...(options.causationid !== undefined ? { causationid: options.causationid } : {}),
  };
  const verdict = validateEnvelope(env);
  if (!verdict.ok) {
    throw new LinkError(
      verdict.errorClass,
      `built envelope violates ${verdict.errorClass}: ${verdict.message}`,
    );
  }
  return env;
}
