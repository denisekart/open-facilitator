// Deny-by-default policy port. Plan 12 replaces the guts of DefaultPolicy at
// the call-sites with a team-policy engine (spec §12); the seam in runtime.ts
// stays a single injection point (same precedent as Plan 03's
// ILinkTokenValidator).
import type { CapabilityDescriptor } from "@ofac/protocol";

import type { PolicyFile, PolicyRow } from "./policy-file.js";
import type { ActorClaims } from "./claims.js";

export type PolicyAction = "command.forward" | "event.forward";
export type PolicyVerdict = "allow" | "deny" | "confirm";

export interface PolicyContext {
  commandType: string;
  capabilityId?: string | undefined;
  childWorkspaceId: string;
  /** Derived resource id (see resourceOf in runtime.ts — the whole law). */
  resource: string;
  /** Actor claims the evidence vouches for (workload-only when unverified). */
  claims: ActorClaims;
  /** The calling workload's canonical actor (e.g. `workload:<endpointId>`). */
  workloadActor: string;
}

export interface PolicyEvaluator {
  evaluate(action: PolicyAction, ctx: PolicyContext): PolicyVerdict;
  /**
   * The row backing a `confirm` verdict (Plan 12: its `actorAnyOf` becomes
   * the approval's eligibleActors). Absent/null ⇒ no row to cite (the
   * DefaultPolicy fallback, or a test-injected engine).
   */
  matchRow?(action: PolicyAction, ctx: PolicyContext): PolicyRow | null;
}

const STEER = "org.facilitator.command.work.steer.v1";
const CANCEL = "org.facilitator.command.work.cancel.v1";

/**
 * Allow `command.forward` iff the target capability is advertised by the
 * child AND (for steer/cancel) the descriptor supports the interaction;
 * `event.forward` always allowed — the child owns the facts it publishes.
 * The no-policy-file fallback (Plan 05 behavior, capability-advertence only).
 */
export class DefaultPolicy implements PolicyEvaluator {
  constructor(private readonly childCapabilities: ReadonlyMap<string, CapabilityDescriptor>) {}

  evaluate(action: PolicyAction, ctx: PolicyContext): PolicyVerdict {
    if (action === "event.forward") return "allow";
    if (ctx.capabilityId === undefined) return "deny";
    const descriptor = this.childCapabilities.get(ctx.capabilityId);
    if (descriptor === undefined) return "deny";
    if (ctx.commandType === STEER && descriptor.supportsSteering !== true) return "deny";
    if (ctx.commandType === CANCEL && descriptor.supportsCancellation !== true) return "deny";
    return "allow";
  }
}

/** Fleet-honesty pre-pass shared by both evaluators (advertence, flags). */
function structuralVerdict(
  action: PolicyAction,
  ctx: PolicyContext,
  childCapabilities: ReadonlyMap<string, CapabilityDescriptor>,
): PolicyVerdict | null {
  if (action === "event.forward") return "allow";
  if (ctx.capabilityId === undefined) return "deny";
  const descriptor = childCapabilities.get(ctx.capabilityId);
  if (descriptor === undefined) return "deny";
  if (ctx.commandType === STEER && descriptor.supportsSteering !== true) return "deny";
  if (ctx.commandType === CANCEL && descriptor.supportsCancellation !== true) return "deny";
  return null;
}

/** `*` matches anything; a single trailing `*` is a prefix-glob; else exact. */
export function resourceMatches(term: string, resource: string): boolean {
  if (term === "*") return true;
  if (term.endsWith("*")) return resource.startsWith(term.slice(0, -1));
  return term === resource;
}

/** Kernel D4 spelling parity: raw group path or `/teams/`-stripped both match. */
function groupMatches(term: string, groups: readonly string[]): boolean {
  const path = term.slice("group:".length);
  const stripped = path.startsWith("/teams/") ? path.slice("/teams/".length) : path;
  return groups.some(
    (g) =>
      g === path ||
      g === stripped ||
      (g.startsWith("/teams/") && g.slice("/teams/".length) === path),
  );
}
function actorMatches(row: PolicyRow, ctx: PolicyContext): boolean {
  if (row.actorAnyOf.length === 0) return true;
  for (const term of row.actorAnyOf) {
    if (term.startsWith("group:") && ctx.claims.verified && groupMatches(term, ctx.claims.groups)) {
      return true;
    }
    if (
      term.startsWith("user:") &&
      (ctx.claims.subject === term ||
        (ctx.claims.verified && ctx.claims.preferredUsername === term.slice("user:".length)))
    ) {
      return true;
    }
    // `role:` needs an OIDC role mapper that minted roles into the evidence;
    // the realm today carries none, so role rows deny (documented, Plan 12).
    if (term.startsWith("workload:") && ctx.claims.subject === term) return true;
  }
  return false;
}

/**
 * The spec §12 team engine: fleet-honesty pre-pass, then first-match-wins
 * over the ordered rows; an implicit final deny closes the list. `conditions`
 * are advisory for connectors — the engine never evaluates them.
 */
export class TeamPolicy implements PolicyEvaluator {
  constructor(
    private readonly policy: { file: PolicyFile; policyId: string },
    private readonly childCapabilities: ReadonlyMap<string, CapabilityDescriptor>,
  ) {}

  /** The audit citation for decisions made by this engine (§12.2). */
  get policyId(): string {
    return this.policy.policyId;
  }

  evaluate(action: PolicyAction, ctx: PolicyContext): PolicyVerdict {
    const structural = structuralVerdict(action, ctx, this.childCapabilities);
    if (structural !== null) return structural;
    const row = this.match(ctx);
    return row === null ? "deny" : row.effect;
  }

  matchRow(action: PolicyAction, ctx: PolicyContext): PolicyRow | null {
    if (structuralVerdict(action, ctx, this.childCapabilities) !== null) return null;
    return this.match(ctx);
  }

  private match(ctx: PolicyContext): PolicyRow | null {
    for (const row of this.policy.file.policies) {
      if (row.capability !== ctx.capabilityId) continue;
      if (!row.resources.some((term) => resourceMatches(term, ctx.resource))) continue;
      if (!actorMatches(row, ctx)) continue;
      return row;
    }
    return null;
  }
}
