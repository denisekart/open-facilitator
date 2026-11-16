// Thin HTTP face of the kernel for the runner: bearer-token minting
// (client_credentials for the operator plane, password grant for the human
// plane), link enrollment + proof-of-possession session minting, and JSON
// calls that surface the typed ProblemDetails code so scenarios can assert on
// the locked error contract.
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Link;

namespace Ofac.Conformance;

/// <summary>One kernel call, reduced to what scenarios assert on.</summary>
/// <param name="Status">HTTP status code.</param>
/// <param name="Body">Parsed JSON body (null when the response carried none or none parsed).</param>
/// <param name="ErrorType">ProblemDetails <c>type</c> URI's last segment (the locked error code), null when not a problem.</param>
public sealed record KernelResponse(int Status, JsonNode? Body, string? ErrorType)
{
    public bool Ok => Status is >= 200 and <= 299;

    public string? Text(string pointer) =>
        Body?[pointer]?.GetValue<string>();
}

/// <summary>An enrolled link endpoint (mode (a), keyed class): the identity it
/// enrolled is a public key, so every call authenticates with a freshly proved
/// session JWT rather than a cached bearer.</summary>
/// <param name="EndpointId">The enrolled endpoint identity.</param>
/// <param name="EndpointKeyId">The <c>sha256:</c> fingerprint the kernel echoed for the enrolled key.</param>
/// <param name="Seq">The replay fence the enrollment started from.</param>
/// <param name="Client">The face that signs proof-of-possession calls for this endpoint.</param>
public sealed record LinkSession(string EndpointId, string EndpointKeyId, long Seq, KernelClient Client)
{
    /// <summary>Run a link-plane call as this endpoint (challenge → proof → session JWT per call, re-proved once on 401).</summary>
    public Task<KernelResponse> GetAsync(string path, CancellationToken ct) =>
        Client.LinkGetAsAsync(EndpointId, path, ct);

    /// <summary>Run a link-plane POST as this endpoint.</summary>
    public Task<KernelResponse> PostAsync(string path, string? rawJson, CancellationToken ct) =>
        Client.LinkPostAsAsync(EndpointId, path, rawJson, ct);

    /// <summary>A hello-carried handshake proof for this endpoint's socket channel (htm <c>WS</c>).</summary>
    public Task<string> ChannelProofAsync(string hubUrl, CancellationToken ct) =>
        Client.ChannelProofAsync(EndpointId, "WS", hubUrl, ct);
}

/// <summary>Authenticated face of one kernel deployment.</summary>
public sealed class KernelClient(RunnerConfig config) : IDisposable
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>One endpoint's in-memory keypair plus the session minted from it (dropped on 401).</summary>
    private sealed record LinkCredential(LinkIdentity Identity, string? SessionToken, DateTime ExpiresAt, long Seq);

    private readonly Dictionary<string, LinkCredential> _link = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _linkGate = new(1, 1);

    /// <summary>Bearer for the operator plane (kernel-operator role).</summary>
    public string OperatorToken { get; private set; } = string.Empty;

    /// <summary>Bearer for the human plane (framework-admin user).</summary>
    public string UserToken { get; private set; } = string.Empty;

    private string TokenUrl => $"{config.IdpUrl}/realms/{config.Realm}/protocol/openid-connect/token";

    /// <summary>The hub URL the runner (and the client it spawns) sees the socket channel at.</summary>
    public string HubUrl => $"{config.BaseUrl}/hubs/link";

    /// <summary>Mint both human-plane and operator-plane bearer tokens.</summary>
    public async Task AuthenticateAsync(CancellationToken ct)
    {
        OperatorToken = await MintAsync(ct, new FormUrlEncodedContent(
        [
            new("grant_type", "client_credentials"),
            new("client_id", config.OperatorClient),
            new("client_secret", config.OperatorSecret),
        ]));
        UserToken = await MintAsync(ct, new FormUrlEncodedContent(
        [
            new("grant_type", "password"),
            new("client_id", "facilitator-api"),
            new("username", config.Username),
            new("password", config.Password),
        ]));
    }

    private async Task<string> MintAsync(CancellationToken ct, FormUrlEncodedContent form)
    {
        var response = await _http.PostAsync(TokenUrl, form, ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Wire, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"token mint failed ({(int)response.StatusCode}): {body.GetRawText()}");
        }

        return body.GetProperty("access_token").GetString()!;
    }

    /// <summary>Enroll a throwaway link endpoint (mode (a)): an in-memory
    /// Ed25519 keypair is generated and posted as the identity; the response
    /// carries no secret, so the returned session mints its own bearer per call
    /// via challenge → proof.</summary>
    public async Task<LinkSession> EnrollAsync(string endpointId, CancellationToken ct)
    {
        var identity = LinkIdentity.Create();
        var payload = new
        {
            token = config.EnrollmentToken,
            endpointId,
            instanceId = $"runner-{Guid.NewGuid():N}",
            trustMode = "separate-process-same-host",
            publicKey = identity.PublicKeyBase64Url,
            keyAlgorithm = "ed25519",
            team = config.Team,
            role = "child",
        };
        var response = await CallAsync(HttpMethod.Post, "/api/link/v1/enroll", payload, bearer: null, ct);
        if (!response.Ok)
        {
            throw new InvalidOperationException($"enroll {endpointId} failed: {response.Status} {response.Body}");
        }

        var keyId = response.Body!["endpointKeyId"]!.GetValue<string>();
        var seq = response.Body!["seq"]!.GetValue<long>();
        _link[endpointId] = new LinkCredential(identity, null, DateTime.MinValue, seq);
        return new LinkSession(endpointId, keyId, seq, this);
    }

    /// <summary>GET a link-plane route as the keyed endpoint: a session JWT is
    /// minted by proof of possession and re-minted (challenge + proof again)
    /// once when the call answers 401.</summary>
    public Task<KernelResponse> LinkGetAsAsync(string endpointId, string path, CancellationToken ct) =>
        LinkCallAsync(HttpMethod.Get, endpointId, path, null, ct);

    /// <summary>POST raw JSON (possibly null body) as the keyed endpoint.</summary>
    public Task<KernelResponse> LinkPostAsAsync(string endpointId, string path, string? rawJson, CancellationToken ct) =>
        LinkCallAsync(HttpMethod.Post, endpointId, path, rawJson, ct);

    private async Task<KernelResponse> LinkCallAsync(
        HttpMethod method, string endpointId, string path, string? rawJson, CancellationToken ct)
    {
        var first = await CallAsAsync(method, endpointId, path, rawJson, await SessionTokenAsync(endpointId, ct), ct);
        if (first.Status != 401)
        {
            return first;
        }

        // 401 on the link plane means the session expired or the revocation
        // epoch moved: the answer is to re-prove, then re-issue the call once —
        // never to re-enroll (that would mint a new identity per expired session).
        var reproved = await SessionTokenAsync(endpointId, ct, force: true);
        return await CallAsAsync(method, endpointId, path, rawJson, reproved, ct);
    }

    private async Task<KernelResponse> CallAsAsync(
        HttpMethod method, string endpointId, string path, string? rawJson, string bearer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"{config.BaseUrl}{path}");
        if (rawJson is not null)
        {
            request.Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new("Bearer", bearer);
        return await SendAsync(request, ct);
    }

    /// <summary>A proof for a channel that is not the HTTP proof route: the
    /// socket handshake's hello-carried proof (htm <c>WS</c>, htu the hub URL as
    /// this client sees it). Consumes its own challenge, so it never disturbs
    /// the cached HTTP session.</summary>
    public async Task<string> ChannelProofAsync(string endpointId, string htm, string htu, CancellationToken ct)
    {
        var credential = Credential(endpointId);
        var (nonce, epoch, seq) = await ChallengeAsync(endpointId, ct);
        return LinkProofWire.Build(credential.Identity, endpointId, htm, htu, nonce, seq, epoch);
    }

    /// <summary>The session JWT a socket upgrade carries while the hello frame
    /// carries the proof that authorizes it.</summary>
    public Task<string> ChannelBearerAsync(string endpointId, CancellationToken ct) =>
        SessionTokenAsync(endpointId, ct);

    /// <summary>The endpoint's cached session JWT, minted (or re-minted when
    /// <paramref name="force"/> or expired) by challenge → proof.</summary>
    private async Task<string> SessionTokenAsync(string endpointId, CancellationToken ct, bool force = false)
    {
        var credential = Credential(endpointId);
        if (!force && credential.SessionToken is { Length: > 0 } cached && credential.ExpiresAt > DateTime.UtcNow.AddSeconds(5))
        {
            return cached;
        }

        await _linkGate.WaitAsync(ct);
        try
        {
            credential = Credential(endpointId);
            if (!force && credential.SessionToken is { Length: > 0 } minted
                && credential.ExpiresAt > DateTime.UtcNow.AddSeconds(5))
            {
                return minted;
            }

            var (nonce, epoch, seq) = await ChallengeAsync(endpointId, ct);
            var proof = LinkProofWire.Build(
                credential.Identity, endpointId, "POST", $"{config.BaseUrl}/api/link/v1/proof", nonce, seq, epoch);
            var response = await CallAsync(
                HttpMethod.Post, "/api/link/v1/proof", new { proof }, bearer: null, ct);
            if (!response.Ok)
            {
                throw new InvalidOperationException($"link proof for {endpointId} refused: {response.Status} {response.Body}");
            }

            var token = response.Body!["sessionToken"]!.GetValue<string>();
            var expiresAt = response.Body!["expiresAt"]!.GetValue<DateTime>();
            _link[endpointId] = credential with { SessionToken = token, ExpiresAt = expiresAt, Seq = response.Body!["seq"]!.GetValue<long>() };
            return token;
        }
        finally
        {
            _linkGate.Release();
        }
    }

    private async Task<(string Nonce, long Epoch, long Seq)> ChallengeAsync(string endpointId, CancellationToken ct)
    {
        var response = await CallAsync(
            HttpMethod.Post, "/api/link/v1/challenge", new { endpointId }, bearer: null, ct);
        if (!response.Ok)
        {
            throw new InvalidOperationException($"link challenge for {endpointId} failed: {response.Status} {response.Body}");
        }

        // The kernel carries the state a proof may claim inside the opaque nonce:
        // `<epoch>.<seq>.<random>`.
        var nonce = response.Body!["nonce"]!.GetValue<string>();
        var parts = nonce.Split('.', 3);
        if (parts.Length != 3 || !long.TryParse(parts[0], out var epoch) || !long.TryParse(parts[1], out var seq))
        {
            throw new InvalidOperationException($"link challenge nonce is not `<epoch>.<seq>.<random>`: {nonce}");
        }

        return (nonce, epoch, seq);
    }

    private LinkCredential Credential(string endpointId) =>
        _link.TryGetValue(endpointId, out var credential)
            ? credential
            : throw new InvalidOperationException($"{endpointId} is not an endpoint this client enrolled");

    public Task<KernelResponse> GetAsync(string path, string bearer, CancellationToken ct) =>
        CallAsync(HttpMethod.Get, path, null, bearer, ct);

    public Task<KernelResponse> PostAsync(string path, object? json, string bearer, CancellationToken ct) =>
        CallAsync(HttpMethod.Post, path, json, bearer, ct);

    /// <summary>POST raw pre-serialized JSON (fixture replay needs the exact bytes).</summary>
    public async Task<KernelResponse> PostRawAsync(string path, string rawJson, string? bearer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.BaseUrl}{path}");
        request.Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json");
        if (bearer is not null)
        {
            request.Headers.Authorization = new("Bearer", bearer);
        }

        return await SendAsync(request, ct);
    }

    private async Task<KernelResponse> CallAsync(HttpMethod method, string path, object? json, string? bearer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"{config.BaseUrl}{path}");
        if (json is not null)
        {
            request.Content = JsonContent.Create(json, options: Wire);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new("Bearer", bearer);
        }

        return await SendAsync(request, ct);
    }

    private async Task<KernelResponse> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? body = null;
        try
        {
            body = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // Non-JSON body (e.g. 204): nothing parsed, no error type.
        }

        string? errorType = null;
        if (body?["type"]?.GetValue<string>() is { } type)
        {
            var slash = type.LastIndexOf('/');
            errorType = slash >= 0 ? type[(slash + 1)..] : type;
        }

        return new KernelResponse((int)response.StatusCode, body, errorType);
    }

    public void Dispose()
    {
        _http.Dispose();
        _linkGate.Dispose();
    }
}
