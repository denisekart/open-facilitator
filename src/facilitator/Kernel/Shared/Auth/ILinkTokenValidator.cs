namespace Facilitator.Kernel.Shared.Auth;

/// <summary>Authenticated Workspace Link principal.</summary>
/// <param name="EndpointId">Logical endpoint identity (stable across reconnects).</param>
/// <param name="TeamId">Owning team.</param>
/// <param name="TeamSlug">Team slug (audit/manifest checks read it; no second query).</param>
/// <param name="TrustMode">Declared trust mode (immutable for the session).</param>
/// <param name="Role">gateway | child, bound at enrollment.</param>
/// <param name="Epoch">Revocation epoch the credential was minted at — the
/// socket's lease refresh compares it so a revoke closes a live session.</param>
/// <param name="Seq">Replay fence the credential carried (fenced without a
/// second query at the hub).</param>
/// <param name="AuthSource">key | oidc — which identity authenticated this channel.</param>
/// <param name="SessionKeyId">kid of the kernel session key that minted this
/// channel's JWT (echoed in the welcome frame; empty for non-JWT channels).</param>
public sealed record LinkPrincipal(
    string EndpointId, Guid TeamId, string TeamSlug, string TrustMode, string Role,
    long Epoch = 0, long Seq = 0, string AuthSource = "key", string SessionKeyId = "");

/// <summary>
/// The link-authentication seam (clean-cutover point for OIDC): validates the
/// short-lived kernel-signed session JWTs the link plane carries; an OIDC
/// implementation can replace it — call sites (middleware, hub policy) never
/// change.
/// </summary>
public interface ILinkTokenValidator
{
    /// <summary>Resolve a bearer token to its active principal; null when unknown/expired/revoked.</summary>
    Task<LinkPrincipal?> ValidateAsync(string token, CancellationToken ct);
}
