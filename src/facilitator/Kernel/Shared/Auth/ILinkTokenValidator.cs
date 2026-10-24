namespace Facilitator.Kernel.Shared.Auth;

/// <summary>Authenticated Workspace Link principal.</summary>
/// <param name="EndpointId">Logical endpoint identity (stable across reconnects).</param>
/// <param name="TeamId">Owning team.</param>
/// <param name="TrustMode">Declared trust mode (immutable for the session, appendix §4).</param>
public sealed record LinkPrincipal(string EndpointId, Guid TeamId, string TrustMode);

/// <summary>
/// The link-authentication seam (clean-cutover point for OIDC): validates
/// opaque per-endpoint link tokens from <c>link_enrollments</c>; an OIDC
/// implementation can replace it — call sites (middleware, hub policy) never
/// change.
/// </summary>
public interface ILinkTokenValidator
{
    /// <summary>Resolve a bearer token to its active principal; null when unknown/expired/revoked.</summary>
    Task<LinkPrincipal?> ValidateAsync(string token, CancellationToken ct);
}
