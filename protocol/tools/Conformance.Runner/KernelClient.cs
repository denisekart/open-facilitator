// Thin HTTP face of the kernel for the runner: bearer-token minting
// (client_credentials for the operator plane, password grant for the human
// plane), link enrollment, and JSON calls that surface the typed
// ProblemDetails code so scenarios can assert on the locked error contract.
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

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

/// <summary>A minted link session credential (HTTP fallback plane).</summary>
/// <param name="EndpointId">The enrolled endpoint identity.</param>
/// <param name="LinkToken">Bearer token for /api/link/v1/*.</param>
public sealed record LinkSession(string EndpointId, string LinkToken);

/// <summary>Authenticated face of one kernel deployment.</summary>
public sealed class KernelClient(RunnerConfig config) : IDisposable
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>Bearer for the operator plane (kernel-operator role).</summary>
    public string OperatorToken { get; private set; } = string.Empty;

    /// <summary>Bearer for the human plane (framework-admin user).</summary>
    public string UserToken { get; private set; } = string.Empty;

    private string TokenUrl => $"{config.IdpUrl}/realms/{config.Realm}/protocol/openid-connect/token";

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

    /// <summary>Enroll a throwaway link endpoint (mode (a)); returns its bearer.</summary>
    public async Task<LinkSession> EnrollAsync(string endpointId, CancellationToken ct)
    {
        var payload = new
        {
            token = config.EnrollmentToken,
            endpointId,
            instanceId = $"runner-{Guid.NewGuid():N}",
            trustMode = "separate-process-same-host",
            team = config.Team,
            role = "child",
        };
        var response = await CallAsync(HttpMethod.Post, "/api/link/v1/enroll", payload, bearer: null, ct);
        if (!response.Ok)
        {
            throw new InvalidOperationException($"enroll {endpointId} failed: {response.Status} {response.Body}");
        }

        return new LinkSession(endpointId, response.Body!["linkToken"]!.GetValue<string>());
    }

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
        if (body is JsonObject problem
            && problem["type"] is JsonValue { } typeValue && typeValue.TryGetValue<string>(out var typeUri))
        {
            var slash = typeUri.LastIndexOf('/');
            errorType = slash >= 0 ? typeUri[(slash + 1)..] : typeUri;
        }

        return new KernelResponse((int)response.StatusCode, body, errorType);
    }

    public void Dispose() => _http.Dispose();
}
