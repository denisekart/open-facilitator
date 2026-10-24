namespace Facilitator.Kernel.Shared.Auth;

/// <summary>Verdict of an ingress token check.</summary>
public enum TokenCheck
{
    /// <summary>Request may proceed.</summary>
    Ok,

    /// <summary>Missing/incorrect bearer token (401).</summary>
    Unauthorized,

    /// <summary>Auth not configured in a non-development environment (503, fail-closed).</summary>
    Unavailable,
}

/// <summary>
/// The single ingress-authentication seam (clean-cutover point for OIDC):
/// Plan 07 replaces the implementation; no call sites change.
/// </summary>
public interface IIngressTokenValidator
{
    /// <summary>Check an <c>Authorization</c> header value.</summary>
    TokenCheck Validate(string? authorizationHeader);
}
