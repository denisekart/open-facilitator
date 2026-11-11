// Shared test scaffolding: the normalize context and type-constants +
// filtering the classification tests share.
import {
  CHANGE_REQUEST_EVENT_TYPE,
  ISSUE_EVENT_TYPE,
  MENTION_EVENT_TYPE,
  PIPELINE_EVENT_TYPE,
  REVIEW_EVENT_TYPE,
  type NormalizedEvent,
} from "@ofac/provider-core";
import type { NormalizeContext } from "../src/normalize.js";

export const ISSUE = ISSUE_EVENT_TYPE;
export const CHANGE_REQUEST = CHANGE_REQUEST_EVENT_TYPE;
export const MENTION = MENTION_EVENT_TYPE;
export const CI = PIPELINE_EVENT_TYPE;
export const REVIEW = REVIEW_EVENT_TYPE;

export const ctx: NormalizeContext = {
  providerInstanceId: "github-com",
  tenantOrOrganizationId: "api.github.com",
  workLabels: new Set(["ofac:work"]),
  botUser: "ofac-bot",
};

export function eventsOfType(events: readonly NormalizedEvent[], type: string): NormalizedEvent[] {
  return events.filter((event) => event.type === type);
}
