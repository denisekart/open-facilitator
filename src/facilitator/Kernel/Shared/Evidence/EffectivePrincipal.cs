using Facilitator.Kernel.Shared.Auth;

namespace Facilitator.Kernel.Shared.Evidence;

/// <summary>A human delegation carried by validated evidence (appendix §3.2 step 3).</summary>
/// <param name="Subject">Canonical subject (<c>oidc:iss|sub</c>).</param>
/// <param name="Groups">Group claims from the evidence.</param>
public sealed record DelegatedIdentity(string Subject, IReadOnlyList<string> Groups);

/// <summary>
/// The authority an admitted command acts under: always the workload
/// principal, optionally extended by a validated human delegation. The policy
/// engine authorizes against this; the audit row records the outcome.
/// </summary>
/// <param name="Workload">The authenticated link endpoint's principal.</param>
/// <param name="Delegated">Validated human delegation, when evidence was Valid AND the enrollment accepts it.</param>
/// <param name="Outcome">Machine outcome (workload-only, delegation-validated, evidence-unverified:..., delegation-refused, ...).</param>
public sealed record EffectivePrincipal(LinkPrincipal Workload, DelegatedIdentity? Delegated, string Outcome)
{
    /// <summary>
    /// Appendix §3.2 step 3, one pure function: workload-only for every
    /// non-Valid state; <c>delegation-refused</c> (still workload-only, allowed)
    /// when the enrollment rejects delegated claims; extended only on
    /// Valid + accepts. Invalid refs never reach here — they deny at admission.
    /// </summary>
    public static EffectivePrincipal Derive(LinkPrincipal workload, EvidenceOutcome evidence, bool acceptsDelegatedClaims) => evidence.State switch
    {
        EvidenceState.Valid when !acceptsDelegatedClaims => new EffectivePrincipal(workload, null, "delegation-refused"),
        EvidenceState.Valid => new EffectivePrincipal(workload, new DelegatedIdentity(evidence.DelegatedSubject!, evidence.Groups), "delegation-validated"),
        EvidenceState.UnverifiedScheme => new EffectivePrincipal(workload, null, $"evidence-unverified:{evidence.Scheme}"),
        EvidenceState.Malformed => new EffectivePrincipal(workload, null, "evidence-malformed"),
        _ => new EffectivePrincipal(workload, null, "workload-only"),
    };
}
