using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Identity;

using Microsoft.Extensions.Options;

namespace Facilitator.Tests.Identity;

/// <summary>
/// OidcJwks signing-key freshness: an unfamiliar token `kid` must trigger a
/// forced discovery refresh + one retry before the `unknown-key` rejection
/// (Keycloak creates the realm key lazily on first issuance, so a kernel
/// that cached the JWKS before that moment must recover without a restart).
/// </summary>
public sealed class OidcJwksKeyRotationTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _issuer;
    private readonly Task _loop;
    private volatile string _jwksJson;

    public OidcJwksKeyRotationTests()
    {
        var port = FreePort();
        _issuer = $"http://127.0.0.1:{port}/realms/facilitator";
        _listener = new HttpListener { Prefixes = { _issuer + "/" } };
        _listener.Start();
        _jwksJson = KeysJson();
        _loop = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            /* listener teardown races are expected */
        }
    }

    [Fact]
    public async Task UnfamiliarKidForcesRefreshAndVerifies()
    {
        var jwks = new OidcJwks(Options.Create(new IdentityOptions
        {
            Oidc = { Authority = _issuer, RequireHttps = false },
        }));

        // Prime the per-issuer cache while key 1 is the only published key.
        var first = Sign(_key, _kid);
        Assert.True((await jwks.ValidateAsync(first, "facilitator-kernel", CancellationToken.None)).Valid);

        // The IdP rotates to key 2 (the manager's own refresh interval is
        // 30s, so only the unknown-kid retry can see this inside the test).
        _key = MintRsa();
        _kid = "key-2";
        _jwksJson = KeysJson();

        var rotated = Sign(_key, _kid);
        var result = await jwks.ValidateAsync(rotated, "facilitator-kernel", CancellationToken.None);
        Assert.True(result.Valid, $"rotated-key token rejected: {result.Reason}");
    }

    [Fact]
    public async Task PermanentlyUnknownKidStillRejectsWithoutKey()
    {
        var jwks = new OidcJwks(Options.Create(new IdentityOptions
        {
            Oidc = { Authority = _issuer, RequireHttps = false },
        }));
        var alien = Sign(MintRsa(), "key-alien");
        var result = await jwks.ValidateAsync(alien, "facilitator-kernel", CancellationToken.None);
        Assert.False(result.Valid);
        Assert.Equal("unknown-key", result.Reason);
    }

    // ── fake IdP ───────────────────────────────────────────────────────────

    private RSA _key = MintRsa();
    private string _kid = "key-1";

    private string KeysJson() =>
        JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = _kid,
                    n = Base64Url(_key.ExportParameters(false).Modulus!),
                    e = Base64Url(_key.ExportParameters(false).Exponent!),
                },
            },
        });

    private async Task ServeAsync()
    {
        try
        {
            while (_listener.IsListening)
            {
                var ctx = await _listener.GetContextAsync();
                var body = ctx.Request.Url!.AbsolutePath.EndsWith("openid-configuration")
                    ? JsonSerializer.Serialize(new
                    {
                        issuer = _issuer,
                        jwks_uri = _issuer + "/certs",
                    })
                    : _jwksJson;
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.Close();
            }
        }
        catch (HttpListenerException)
        {
            /* stopped */
        }
    }

    private string Sign(RSA key, string kid)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT", kid })));
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = _issuer,
            aud = "facilitator-kernel",
            iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        })));
        var sig = key.SignData(
            Encoding.UTF8.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(sig)}";
    }

    private static RSA MintRsa()
    {
        var rsa = RSA.Create(2048);
        return rsa;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
