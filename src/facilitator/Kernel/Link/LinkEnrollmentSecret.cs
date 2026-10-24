using System.Security.Cryptography;
using System.Text;

namespace Facilitator.Kernel.Link;

/// <summary>
/// Resolves the bootstrap enrollment secret (config <c>Link:EnrollmentToken</c>,
/// env <c>Link__EnrollmentToken</c>). Unset + Development ⇒ the literal
/// <c>enroll-dev</c>; unset + non-Development ⇒ fail closed (startup check in
/// <c>Shared/Auth/StartupChecks.cs</c>, mirroring <c>Auth:BootstrapToken</c>).
/// </summary>
public sealed class LinkEnrollmentSecret(IConfiguration configuration, IHostEnvironment environment)
{
    /// <summary>Secret accepted in Development when <c>Link:EnrollmentToken</c> is unset.</summary>
    public const string DevSecret = "enroll-dev";

    private string? ConfiguredSecret => configuration["Link:EnrollmentToken"];

    internal bool IsConfigured => !string.IsNullOrEmpty(ConfiguredSecret) || environment.IsDevelopment();

    /// <summary>Constant-time check of a candidate enrollment secret.</summary>
    public bool Matches(string? candidate)
    {
        var expected = string.IsNullOrEmpty(ConfiguredSecret) ? DevSecret : ConfiguredSecret;
        if (candidate is null)
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(candidate);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
