using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace Facilitator.Kernel.Ai;

/// <summary>
/// The chat-completions gateway: <c>POST {BaseUrl}/v1/chat/completions</c>
/// (OpenAI wire format), bearer auth, deterministic sampling
/// (<c>temperature: 0</c>). Non-2xx / connect error / timeout ⇒
/// <see cref="AiUnavailableException"/> — never a garbage completion.
/// Per-endpoint deadline via CTS (10 s connect floor, endpoint
/// <c>TimeoutSeconds</c> ceiling).
/// </summary>
public sealed class OpenAiChatCompletionsGateway : IAiGateway
{
    private readonly AiOptions options;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly HttpMessageHandler? injectedHandler;

    public OpenAiChatCompletionsGateway(AiOptions options, IHttpClientFactory httpClientFactory)
    {
        this.options = options;
        this.httpClientFactory = httpClientFactory;
    }

    /// <summary>Test/E2E seam: run the REAL wire path against an injected handler
    /// (no factory, no network) — mirrors the S3ArtifactStore seam law.</summary>
    public OpenAiChatCompletionsGateway(AiOptions options, HttpMessageHandler handler)
    {
        this.options = options;
        httpClientFactory = null!;
        injectedHandler = handler;
    }

    /// <inheritdoc />
    public async Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken ct)
    {
        var endpoint = options.Endpoint(request.EndpointName)
            ?? throw new AiUnavailableException($"AI endpoint '{request.EndpointName}' is not configured.");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = endpoint.Model,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = request.SystemPrompt },
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = request.UserPrompt },
            },
            ["max_tokens"] = request.MaxOutputTokens,
            ["temperature"] = 0,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(endpoint.TimeoutSeconds, 10, 600)));
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri(new Uri(endpoint.BaseUrl!.TrimEnd('/') + "/"), "v1/chat/completions"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(endpoint.ApiKey))
        {
            httpRequest.Headers.Add("Authorization", $"Bearer {endpoint.ApiKey}");
        }

        using var response = await SendAsync(httpRequest, timeout.Token);
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException and not OperationCanceledException { CancellationToken: { IsCancellationRequested: false } })
        {
            throw new AiUnavailableException($"ai completion read failed ({request.EndpointName}/{request.TemplateId})", e);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new AiUnavailableException(
                $"ai gateway answered {(int)response.StatusCode} ({request.EndpointName}/{request.TemplateId})");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement
                .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                ?? throw new JsonException("null content");
            var promptTokens = 0;
            var completionTokens = 0;
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv))
                {
                    promptTokens = ptv;
                }

                if (usage.TryGetProperty("completion_tokens", out var ctk) && ctk.TryGetInt32(out var ctkv))
                {
                    completionTokens = ctkv;
                }
            }

            return new AiCompletion(text, promptTokens, completionTokens);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new AiUnavailableException($"ai gateway returned an unreadable completion ({request.EndpointName}/{request.TemplateId})", e);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            if (injectedHandler is not null)
            {
                using var owned = new HttpClient(injectedHandler, disposeHandler: false);
                return await owned.SendAsync(request, ct);
            }

            return await httpClientFactory.CreateClient("ai").SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested && e is OperationCanceledException oce && oce.CancellationToken == ct)
            {
                throw; // caller-level cancellation stays itself
            }

            throw new AiUnavailableException($"ai gateway unreachable ({request.RequestUri?.AbsolutePath})", e);
        }
    }
}
