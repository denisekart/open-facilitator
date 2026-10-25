using System.Text.Json;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>
/// The verified human/service principal behind a kernel request (spec §11.1):
/// identity is the verified pair <c>(iss, sub)</c>; everything else is
/// operator-mapped display/authorization context. The canonical audit/timeline
/// actor string is <c>oidc:&lt;iss&gt;|&lt;sub&gt;</c>; display names never enter it.
/// </summary>
/// <param name="Issuer">Verified <c>iss</c>.</param>
/// <param name="Subject">Verified <c>sub</c> (via the claims:subject pointer).</param>
/// <param name="DisplayName">Display-only name (claims:displayName).</param>
/// <param name="Groups">Normalized groups (claims:groups pointers).</param>
/// <param name="Scopes">Normalized scopes (claims:scopes pointers).</param>
/// <param name="Permissions">Normalized permissions (claims:permissions pointers).</param>
/// <param name="Amr">Normalized authentication methods (claims:authenticationMethods pointers).</param>
/// <param name="Acr">Authentication context class (claims:authenticationContext).</param>
/// <param name="Roles">Roles granted by the role-rule engine.</param>
public sealed record FacilitatorPrincipal(
    string Issuer,
    string Subject,
    string? DisplayName,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Amr,
    string? Acr,
    IReadOnlySet<string> Roles)
{
    /// <summary>Canonical actor string for audit/timeline records.</summary>
    public string Actor => $"oidc:{Issuer}|{Subject}";
}

/// <summary>
/// Normalizes a verified token payload into a <see cref="FacilitatorPrincipal"/>
/// through the operator's <c>identity:claims</c> pointers. Missing/malformed
/// claims yield empty sequences — a misconfigured pointer can only remove
/// context, never grant it.
/// </summary>
public static class PrincipalMapper
{
    /// <summary>Map a verified payload (iss already validated) to a principal shell; roles are attached later by the gate.</summary>
    public static FacilitatorPrincipal Map(JsonElement payload, string verifiedIssuer, IdentityOptions options, IReadOnlySet<string>? roles = null)
    {
        var subject = ClaimPointers.ResolveString(payload, options.Claims.Subject);
        var displayName = options.Claims.DisplayName is null
            ? null
            : ClaimPointers.ResolveString(payload, options.Claims.DisplayName);

        return new FacilitatorPrincipal(
            verifiedIssuer,
            subject ?? string.Empty,
            displayName,
            Collect(payload, options.Claims.Groups),
            Collect(payload, options.Claims.Scopes),
            Collect(payload, options.Claims.Permissions),
            Collect(payload, options.Claims.AuthenticationMethods),
            options.Claims.AuthenticationContext is null
                ? null
                : ClaimPointers.ResolveString(payload, options.Claims.AuthenticationContext),
            roles ?? new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>Principal with the engine-granted roles swapped in.</summary>
    public static FacilitatorPrincipal WithRoles(this FacilitatorPrincipal principal, IReadOnlySet<string> roles) =>
        principal with { Roles = roles };

    private static IReadOnlyList<string> Collect(JsonElement payload, IReadOnlyList<ClaimDescriptor> descriptors)
    {
        if (descriptors.Count == 0)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var descriptor in descriptors)
        {
            foreach (var value in ClaimPointers.ResolveStrings(payload, descriptor.Path, descriptor.Shape))
            {
                values.Add(value);
            }
        }

        return values;
    }
}
