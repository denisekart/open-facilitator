using System.Text.Json;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Facilitator.Tests.Infrastructure;

/// <summary>
/// One Keycloak 26 container for the WHOLE suite (same static-Lazy pattern as
/// <see cref="PostgresFixture"/>): started once with
/// <c>start-dev --import-realm</c> importing the reference realm (the
/// <c>reference/keycloak</c> directory bind-mounted as the import dir — a raw generic container, no
/// vendor module dependency). Tokens are minted through plain
/// <c>application/x-www-form-urlencoded</c> POSTs (client_credentials for the
/// <c>facilitator-cli</c> service account, password grants for realm users) —
/// no admin client. Tests authenticate with these tokens; the kernel only
/// ever sees issuer + JWKS.
/// </summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    private const ushort KeycloakPort = 8080;

    // The lazy starts the container itself, so ANY static access (test
    // factories, spawned server processes minting tokens) gets a live
    // container regardless of xUnit fixture instantiation order.
    private static readonly Task<IContainer> Shared =
        new Lazy<Task<IContainer>>(StartSharedAsync).Value;

    private static readonly HttpClient Http = new();
    private static readonly SemaphoreSlim MintGate = new(1, 1);
    private static string? _cliToken;
    private static DateTime _cliTokenExpiresUtc;

    /// <inheritdoc />
    public Task InitializeAsync() => Shared;

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Public base URL of the container (valid after initialization).</summary>
    public static string BaseUrl =>
        $"http://{Shared.Result.Hostname}:{Shared.Result.GetMappedPublicPort(KeycloakPort)}";

    /// <summary>The issuer the kernel must trust (realm URL).</summary>
    public static string Issuer => $"{BaseUrl}/realms/facilitator";

    /// <summary>
    /// Cached <c>facilitator-cli</c> service-account token, re-minted 30 s
    /// before expiry — synchronous for test call sites while honoring the
    /// realm's 300 s access-token TTL.
    /// </summary>
    public static string CliToken
    {
        get
        {
            if (_cliToken is null || DateTime.UtcNow >= _cliTokenExpiresUtc)
            {
                MintGate.Wait();
                try
                {
                    if (_cliToken is null || DateTime.UtcNow >= _cliTokenExpiresUtc)
                    {
                        (_cliToken, _cliTokenExpiresUtc) = MintAsync(new Dictionary<string, string>
                        {
                            ["grant_type"] = "client_credentials",
                            ["client_id"] = "facilitator-cli",
                            ["client_secret"] = "e2e-cli-secret",
                        }).GetAwaiter().GetResult();
                    }
                }
                finally
                {
                    MintGate.Release();
                }
            }

            return _cliToken;
        }
    }

    /// <summary>Access token for the <c>facilitator-cli</c> service account (holds the operator realm roles).</summary>
    public static Task<string> TokenForClientAsync() => Task.FromResult(CliToken);

    /// <summary>Access token via the direct grant (e.g. alice / bob / carol).</summary>
    public static async Task<string> TokenForUserAsync(string user, string password) =>
        (await MintAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "facilitator-api",
            ["username"] = user,
            ["password"] = password,
        })).Token;

    private static async Task<IContainer> StartSharedAsync()
    {
        var container = new ContainerBuilder("quay.io/keycloak/keycloak:26.7.5")
            .WithCleanUp(true)
            .WithBindMount(Path.Combine(RealmDirectory(), "realm.json"),
                "/opt/keycloak/data/import/realm.json")
            .WithCommand("start-dev", "--import-realm")
            .WithPortBinding(KeycloakPort, true)
            // Keycloak binds 8080 before the realm import finishes; only the
            // realm's own discovery document proves the fixture is servable.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(KeycloakPort)
                .ForPath("/realms/facilitator/.well-known/openid-configuration")))
            .Build();
        await container.StartAsync();
        return container;
    }

    private static async Task<(string Token, DateTime ExpiresUtc)> MintAsync(Dictionary<string, string> fields)
    {
        // Keycloak 26.x intermittently answers the first password grant of a
        // boot with LOGIN_ERROR resolve_required_actions ("Account is not
        // fully set up") while the required-action provider cache warms; two
        // retries cover it. Client credentials never hit that path.
        for (var attempt = 0; ; attempt++)
        {
            using var response = await Http.PostAsync($"{Issuer}/protocol/openid-connect/token",
                new FormUrlEncodedContent(fields));
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var token = root.GetProperty("access_token").GetString()!;
                var lifetime = root.GetProperty("expires_in").GetInt32();
                return (token, DateTime.UtcNow.AddSeconds(lifetime - 30));
            }

            if (attempt >= 2 || !body.Contains("not fully set up", StringComparison.Ordinal))
            {
                response.EnsureSuccessStatusCode();
                throw new InvalidOperationException($"token mint failed: {body}");
            }

            await Task.Delay(250);
        }
    }

    /// <summary>
    /// Absolute path of <c>reference/keycloak</c>, found by walking up from
    /// the test assembly location (repo-root-relative, build-dir safe).
    /// </summary>
    private static string RealmDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "reference", "keycloak");
            if (File.Exists(Path.Combine(candidate, "realm.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("reference/keycloak/realm.json not found above " + AppContext.BaseDirectory);
    }
}
