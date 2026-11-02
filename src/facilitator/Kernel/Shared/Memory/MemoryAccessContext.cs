using Facilitator.Kernel.Shared.Evidence;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Storage;

namespace Facilitator.Kernel.Shared.Memory;

/// <summary>
/// The server-side authority view of whoever asked: built ONLY
/// from a verified principal — the OIDC REST principal or the link plane's
/// <see cref="EffectivePrincipal"/> — never from agent JSON. The broker
/// resolves every scope against this context; a caller-supplied selector is a
/// hint, never an authority (a <c>callerPrincipal</c> field inside a payload
/// is schema-rejected outright).
/// </summary>
/// <param name="Actor">Canonical actor string (audit/usage identity): <c>oidc:iss|sub</c>, a delegated subject, or <c>link:&lt;endpointId&gt;</c>.</param>
/// <param name="TeamSlug">The caller's team (null when the principal carries no team — such a caller retains nowhere).</param>
/// <param name="Roles">Granted roles (framework-admin sees metadata, never another team's content).</param>
/// <param name="IsWorkload">True for link-plane callers: retain lands <c>candidate</c> and curation ops are refused (D4).</param>
public sealed record MemoryAccessContext(
    string Actor,
    string? TeamSlug,
    IReadOnlySet<string> Roles,
    bool IsWorkload)
{
    /// <summary>A REST human: actor/team from the OIDC principal (the <c>OriginOf</c> team idiom).</summary>
    public static MemoryAccessContext From(FacilitatorPrincipal principal) =>
        new(principal.Actor, ArtifactLedger.TeamOfGroups(principal.Groups), principal.Roles, IsWorkload: false);

    /// <summary>A link-plane caller: effective authority (delegated human when validated, else the workload endpoint).</summary>
    public static MemoryAccessContext From(EffectivePrincipal principal) =>
        new(
            principal.Delegated?.Subject ?? $"link:{principal.Workload.EndpointId}",
            principal.Workload.TeamSlug,
            new HashSet<string>(StringComparer.Ordinal),
            IsWorkload: true);
}
