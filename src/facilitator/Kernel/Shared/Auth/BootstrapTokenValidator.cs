using System.Security.Cryptography;
using System.Text;

namespace Facilitator.Kernel.Shared.Auth;

/// <summary>
/// Bootstrap shared-token validator. Config <c>Auth:BootstrapToken</c>; unset
/// + Development accepts the literal <c>dev-token</c>; unset + non-Development
/// fails closed (503 + startup throw, see <see cref="BootstrapAuthStartupCheck"/>).
/// </summary>
public sealed class BootstrapTokenValidator(IConfiguration configuration, IHostEnvironment environment) : IIngressTokenValidator
{
    /// <summary>Token accepted in Development when <c>Auth:BootstrapToken</c> is unset.</summary>
    public const string DevToken = "dev-token";

    private string? ConfiguredToken => configuration["Auth:BootstrapToken"];

    internal bool IsConfigured => !string.IsNullOrEmpty(ConfiguredToken) || environment.IsDevelopment();

    /// <inheritdoc />
    public TokenCheck Validate(string? authorizationHeader)
    {
        var expected = ConfiguredToken;
        if (string.IsNullOrEmpty(expected))
        {
            if (!environment.IsDevelopment())
            {
                return TokenCheck.Unavailable;
            }

            expected = DevToken;
        }

        const string prefix = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(prefix, StringComparison.Ordinal))
        {
            return TokenCheck.Unauthorized;
        }

        var provided = authorizationHeader[prefix.Length..].Trim();
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes)
            ? TokenCheck.Ok
            : TokenCheck.Unauthorized;
    }
}
