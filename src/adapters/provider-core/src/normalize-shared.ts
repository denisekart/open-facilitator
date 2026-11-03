// Shared normalization scaffolding (provider-agnostic): the echo-filter/bot-
// user law, the @-mention trigger, the label→workTrigger policy hook, and the
// payload-reading primitives every vendor normalizer repeats. Vendor→domain
// mapping itself stays provider-local.
import type {
  ProviderChangeRequestEventV1Data,
  ProviderIssueEventV1Data,
  ProviderMentionEventV1Data,
  ProviderMessageEventV1Data,
  ProviderPipelineEventV1Data,
  ProviderReactionEventV1Data,
} from "@ofac/protocol";

/** Forge-domain vocabulary: the type names the PURPOSE (identical for every
 *  provider — the provider token rides `data.provider`, never the type). */
export const ISSUE_EVENT_TYPE = "org.facilitator.event.provider.issue.v1";
export const CHANGE_REQUEST_EVENT_TYPE = "org.facilitator.event.provider.change_request.v1";
export const PIPELINE_EVENT_TYPE = "org.facilitator.event.provider.pipeline.v1";
export const MENTION_EVENT_TYPE = "org.facilitator.event.provider.mention.v1";

export type Payload = Record<string, unknown>;

/** Every provider event the fan-out can publish: a plain union of the frozen
 *  wire data types — all share the reference/provider/providerDeliveryId/
 *  actorProviderAccountId spine, so consumers read the spine without a
 *  vendor cast. */
export type NormalizedEventData =
  | ProviderIssueEventV1Data
  | ProviderChangeRequestEventV1Data
  | ProviderPipelineEventV1Data
  | ProviderMentionEventV1Data
  | ProviderMessageEventV1Data
  | ProviderReactionEventV1Data;

export interface NormalizedEvent {
  type: string;
  data: NormalizedEventData;
}

// ---- payload primitives -----------------------------------------------------

export function str(value: unknown): string | undefined {
  return typeof value === "string" && value !== "" ? value : undefined;
}

/** Wire ids arrive as strings or JSON numbers depending on the event kind. */
export function id(value: unknown): string | undefined {
  if (typeof value === "string" && value !== "") return value;
  return typeof value === "number" && Number.isFinite(value) ? String(value) : undefined;
}

/** Wire numbers arrive as JSON numbers. */
export function num(value: unknown): string | undefined {
  return typeof value === "number" && Number.isFinite(value) ? String(value) : undefined;
}

export function obj(value: unknown): Payload | undefined {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? (value as Payload)
    : undefined;
}

// ---- echo / mention / label laws ---------------------------------------------

/** Echo law (exact-match): an event authored by the bot is dropped — no
 *  feedback loops — EXCEPT provider state changes the resolver must see
 *  (opened/reopened change requests: the bot mints those, and a state-change
 *  event cannot loop). Providers reuse this to filter `senderLogin`. */
export function isBotAuthored(
  actorProviderAccountId: string | undefined,
  botUser: string,
): boolean {
  return actorProviderAccountId === botUser;
}

/** The @-bot mention inbound trigger: a human comment body that names the
 *  bot (`@bot` at a word boundary). Feeds `event.provider.mention.v1`. */
export function mentionsBot(body: string | undefined, botUser: string): boolean {
  if (body === undefined || body === "") return false;
  const escaped = botUser.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return new RegExp(`(^|[^\\w/])@${escaped}(?![\\w-])`, "i").test(body);
}

/** Label policy hook (the adapter decides, the kernel only reads the flag):
 *  workTrigger = labels ∩ configured trigger set (case-insensitive). */
export function workTriggerFor(
  labels: readonly string[],
  workLabels: ReadonlySet<string>,
): boolean {
  return labels.some((label) => workLabels.has(label.toLowerCase()));
}

/** Normalize a vendor label array of either shape — GitHub `[{name}]`,
 *  GitLab `["str"]`, mixed — to the flat lowercase-name list the
 *  normalized-event `labels` field carries. */
export function normalizeLabels(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  const names: string[] = [];
  for (const entry of value) {
    if (typeof entry === "string" && entry !== "") names.push(entry.toLowerCase());
    else if (typeof entry === "object" && entry !== null) {
      const name = (entry as Payload)["name"];
      if (typeof name === "string" && name !== "") names.push(name.toLowerCase());
    }
  }

  return names;
}
