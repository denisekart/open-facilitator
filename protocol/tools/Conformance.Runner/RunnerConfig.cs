// Conformance reference config. Every value is overridable so the same
// binary can point at a foreign deployment: a JSON file via --config, then
// CLI flags on top. Secrets are either literal or `file:`-prefixed paths.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ofac.Conformance;

/// <summary>Where the kernel lives and who the runner talks as.</summary>
/// <param name="BaseUrl">Kernel base URL (LB or direct).</param>
/// <param name="IdpUrl">Keycloak base URL (no realm path).</param>
/// <param name="Realm">Keycloak realm name.</param>
/// <param name="OperatorClient">client_credentials client minting operator tokens.</param>
/// <param name="OperatorSecret">Secret for <paramref name="OperatorClient"/>.</param>
/// <param name="Username">Password-grant user for the human (approval) leg.</param>
/// <param name="Password">That user's password.</param>
/// <param name="EnrollmentToken">Mode (a) link enrollment secret.</param>
/// <param name="Team">Team slug the runner enrolls its endpoints into.</param>
/// <param name="ForeignClient">Command line that runs the foreign-shape link client.</param>
/// <param name="RedeliverWaitSeconds">Upper bound for the in-flight-redelivery leg.</param>
public sealed record RunnerConfig(
    string BaseUrl,
    string IdpUrl,
    string Realm,
    string OperatorClient,
    string OperatorSecret,
    string Username,
    string Password,
    string EnrollmentToken,
    string Team,
    string? ForeignClient,
    int RedeliverWaitSeconds);

public static class ConfigLoader
{
    /// <summary>Load defaults, overlay --config &lt;file&gt;, then CLI flag overrides.</summary>
    public static RunnerConfig Load(IReadOnlyDictionary<string, string> flags)
    {
        JsonNode? overlay = null;
        if (flags.TryGetValue("config", out var path))
        {
            overlay = JsonNode.Parse(File.ReadAllText(path));
        }

        string Pick(string flag, string jsonKey, string fallback) =>
            flags.TryGetValue(flag, out var direct) ? Secret(direct)
            : overlay?[jsonKey] is JsonValue { } value && value.TryGetValue<string>(out var text) ? Secret(text)
            : fallback;

        var redeliver = flags.TryGetValue("redeliver-wait-seconds", out var raw) ? int.Parse(raw) : 180;

        return new RunnerConfig(
            Pick("kernel-url", "baseUrl", "http://localhost:8088"),
            Pick("idp-url", "idpUrl", "http://localhost:8080"),
            Pick("realm", "realm", "facilitator"),
            Pick("operator-client", "operatorClient", "facilitator-cli"),
            Pick("operator-secret", "operatorSecret", "e2e-cli-secret"),
            Pick("username", "username", "alice"),
            Pick("password", "password", "alice"),
            Pick("enrollment-token", "enrollmentToken", "enroll-dev"),
            Pick("team", "team", "team-alpha"),
            flags.TryGetValue("link-client", out var client) ? client : null,
            redeliver);
    }

    /// <summary>`file:/path` reads the secret from disk (first line); anything
    /// else is taken literally.</summary>
    private static string Secret(string value) =>
        value.StartsWith("file:", StringComparison.Ordinal)
            ? File.ReadAllLines(value[5..])[0].Trim()
            : value;
}
