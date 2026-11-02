using System.Text.Json;

using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using LinkOptions = Facilitator.Kernel.Link.LinkOptions;

namespace Facilitator.Kernel.Shared.Evidence;

/// <summary>Validation state of an <c>actorEvidenceRef</c>.</summary>
public enum EvidenceState
{
    /// <summary>No reference presented — workload authority only.</summary>
    Absent,

    /// <summary>Recognized-free scheme (e.g. <c>identity-evidence:</c>) — recorded, never authoritative.</summary>
    UnverifiedScheme,

    /// <summary>Not <c>scheme:value</c> shaped.</summary>
    Malformed,

    /// <summary>Kernel-checked and passed — extends the principal when the enrollment accepts delegation.</summary>
    Valid,

    /// <summary>Kernel-checked and FAILED — admission denies (failed evidence never silently downgrades).</summary>
    Invalid,
}

/// <summary>One evidence-validation outcome.</summary>
/// <param name="Scheme">Scheme prefix before the first colon ("" when absent).</param>
/// <param name="State">Validation state.</param>
/// <param name="DelegatedSubject">Canonical human subject (<c>oidc:iss|sub</c>) when Valid.</param>
/// <param name="Groups">Group claims carried by the evidence (Valid only).</param>
/// <param name="InvalidReason">Machine reason when Invalid (feeds the deny reason + problem detail).</param>
public sealed record EvidenceOutcome(string Scheme, EvidenceState State, string? DelegatedSubject, IReadOnlyList<string> Groups, string? InvalidReason)
{
    /// <summary>The no-evidence outcome.</summary>
    public static readonly EvidenceOutcome None = new(string.Empty, EvidenceState.Absent, null, [], null);
}

/// <summary>
/// Scheme-split evidence validator: <c>oidc:&lt;jwt&gt;</c>
/// verifies signature/iss/aud/exp/nbf + the required <c>delegation: true</c>
/// claim against the realm JWKS; <c>kernel:&lt;id&gt;</c> resolves through
/// <see cref="IEvidenceStore"/> (unknown/expired/consumed ⇒ Invalid). Every
/// other scheme is recorded non-authoritative — an opaque pointer the kernel
/// has no oracle for must neither deny nor silently authorize beyond workload.
/// </summary>
public sealed class EvidenceValidator(OidcJwks jwks, IEvidenceStore evidence, LinkOptions options, TimeProvider timeProvider)
{
    /// <summary>Validate one raw <c>actorEvidenceRef</c> (null/empty ⇒ Absent).</summary>
    public async Task<EvidenceOutcome> EvaluateAsync(string? actorEvidenceRef, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(actorEvidenceRef))
        {
            return EvidenceOutcome.None;
        }

        var colon = actorEvidenceRef.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == actorEvidenceRef.Length - 1)
        {
            return new EvidenceOutcome(string.Empty, EvidenceState.Malformed, null, [], "no-scheme");
        }

        var (scheme, value) = (actorEvidenceRef[..colon], actorEvidenceRef[(colon + 1)..]);
        return scheme switch
        {
            "oidc" => await OidcAsync(value, ct),
            "kernel" => await KernelAsync(value, ct),
            _ => new EvidenceOutcome(scheme, EvidenceState.UnverifiedScheme, null, [], null),
        };
    }

    private async Task<EvidenceOutcome> OidcAsync(string jwt, CancellationToken ct)
    {
        var verified = await jwks.ValidateAsync(jwt, options.EvidenceAudience, ct, options.EvidenceClockSkewSeconds);
        if (!verified.Valid)
        {
            return new EvidenceOutcome("oidc", EvidenceState.Invalid, null, [], verified.Reason);
        }

        var payload = verified.Payload!.Value;
        if (!OidcClaims.IsTrue(payload, "delegation"))
        {
            return new EvidenceOutcome("oidc", EvidenceState.Invalid, null, [], "missing-delegation");
        }

        var subject = OidcClaims.String(payload, "sub");
        if (subject is null)
        {
            return new EvidenceOutcome("oidc", EvidenceState.Invalid, null, [], "missing-subject");
        }

        // Authoritative claim set: sub + groups + scope (scope rides to
        // the policy engine; groups first).
        var groups = OidcClaims.ArrayOrSpaced(payload, "groups");
        return new EvidenceOutcome("oidc", EvidenceState.Valid, $"oidc:{verified.Token!.Issuer}|{subject}", groups, null);
    }

    private async Task<EvidenceOutcome> KernelAsync(string id, CancellationToken ct)
    {
        var row = await evidence.FindAsync(id, ct);
        if (row is null)
        {
            return new EvidenceOutcome("kernel", EvidenceState.Invalid, null, [], "unknown-id");
        }

        if (row.ExpiresAt is { } expiresAt && expiresAt <= timeProvider.GetUtcNow().UtcDateTime)
        {
            return new EvidenceOutcome("kernel", EvidenceState.Invalid, null, [], "expired");
        }

        if (row.ConsumedAt is not null)
        {
            return new EvidenceOutcome("kernel", EvidenceState.Invalid, null, [], "consumed");
        }

        return new EvidenceOutcome("kernel", EvidenceState.Valid, row.Subject,
            OidcClaims.ArrayOrSpaced(JsonDocument.Parse(row.Claims).RootElement, "groups"), null);
    }
}
