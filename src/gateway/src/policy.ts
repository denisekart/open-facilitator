// Deny-by-default policy port. Plan 10-era OIDC + policy engine replaces the
// guts of DefaultPolicy; the call-sites in runtime.ts stay untouched (single
// seam, same precedent as Plan 03's ILinkTokenValidator).
import type { CapabilityDescriptor } from "@ofac/protocol";

export type PolicyAction = "command.forward" | "event.forward";

export interface PolicyContext {
  commandType: string;
  capabilityId?: string | undefined;
  childWorkspaceId: string;
}

export interface PolicyEvaluator {
  evaluate(action: PolicyAction, ctx: PolicyContext): "allow" | "deny";
}

const STEER = "org.facilitator.command.work.steer.v1";
const CANCEL = "org.facilitator.command.work.cancel.v1";

/**
 * Allow `command.forward` iff the target capability is advertised by the
 * child AND (for steer/cancel) the descriptor supports the interaction;
 * `event.forward` always allowed — the child owns the facts it publishes.
 */
export class DefaultPolicy implements PolicyEvaluator {
  constructor(private readonly childCapabilities: ReadonlyMap<string, CapabilityDescriptor>) {}

  evaluate(action: PolicyAction, ctx: PolicyContext): "allow" | "deny" {
    if (action === "event.forward") return "allow";
    if (ctx.capabilityId === undefined) return "deny";
    const descriptor = this.childCapabilities.get(ctx.capabilityId);
    if (descriptor === undefined) return "deny";
    if (ctx.commandType === STEER && descriptor.supportsSteering !== true) return "deny";
    if (ctx.commandType === CANCEL && descriptor.supportsCancellation !== true) return "deny";
    return "allow";
  }
}
