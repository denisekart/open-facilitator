using System.Net;
using System.Text;
using System.Text.Json;

namespace Facilitator.Kernel.Ai;

/// <summary>
/// The decisions-API gateway: <c>POST {BaseUrl}/v1/decisions</c> (OpenAI
/// Decisions wire format, served by LiteLLM ≥ v1.104.2). Questions are built
/// in code from the typed list — no templating, so phrasing stays stable.
/// Answers arrive in question order; a refusal or a missing name maps to an
/// all-null row (no-preference), never an error. Non-2xx / connect / timeout ⇒
/// <see cref="AiUnavailableException"/>.
/// </summary>
public sealed class LiteLlmDecisionsGateway : IDecisionGateway
{
    /// <summary>A predicate answer counts as true at or above this probability.</summary>
    public const double PredicateTrueThreshold = 0.5;

    private readonly AiOptions options;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly HttpMessageHandler? injectedHandler;

    public LiteLlmDecisionsGateway(AiOptions options, IHttpClientFactory httpClientFactory)
    {
        this.options = options;
        this.httpClientFactory = httpClientFactory;
    }

    /// <summary>Test seam: real wire path against an injected handler.</summary>
    internal LiteLlmDecisionsGateway(AiOptions options, HttpMessageHandler handler)
    {
        this.options = options;
        httpClientFactory = null!;
        injectedHandler = handler;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DecisionAnswer>> DecideAsync(DecisionRequest request, CancellationToken ct)
    {
        if (request.Questions.Count == 0 || request.Questions.Count > 32)
        {
            throw new ArgumentException("a decision request carries 1..32 questions", nameof(request));
        }

        var endpoint = options.Endpoint(request.EndpointName)
            ?? throw new AiUnavailableException($"AI endpoint '{request.EndpointName}' is not configured.");

        var questions = new List<object>(request.Questions.Count);
        foreach (var question in request.Questions)
        {
            questions.Add(question switch
            {
                PredicateQuestion p => new Dictionary<string, object?>
                {
                    ["type"] = "predicate",
                    ["name"] = p.Name,
                    ["instructions"] = p.Instructions,
                },
                ChoiceQuestion c => new Dictionary<string, object?>
                {
                    ["type"] = "choice",
                    ["name"] = c.Name,
                    ["instructions"] = c.Instructions,
                    ["choices"] = c.Choices.Select(v => (object?)new Dictionary<string, object?> { ["value"] = v }).ToArray(),
                },
                _ => throw new ArgumentException($"unknown question kind '{question.GetType().Name}'"),
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = endpoint.Model,
            ["input"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "message", ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = request.InputText },
                    },
                },
            },
            ["questions"] = questions,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(endpoint.TimeoutSeconds, 5, 600)));
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri(new Uri(endpoint.BaseUrl!.TrimEnd('/') + "/"), "v1/decisions"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(endpoint.ApiKey))
        {
            httpRequest.Headers.Add("Authorization", $"Bearer {endpoint.ApiKey}");
        }

        using var response = await SendAsync(httpRequest, timeout.Token, request.EndpointName);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new AiUnavailableException(
                $"decision gateway answered {(int)response.StatusCode} ({request.EndpointName})");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var byName = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("answers", out var answers) && answers.ValueKind == JsonValueKind.Array)
            {
                foreach (var answer in answers.EnumerateArray())
                {
                    if (answer.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                        && nameEl.GetString() is { } name)
                    {
                        byName[name] = ParseAnswer(answer);
                    }
                }
            }

            // One row per question, question order; refusal / missing name ⇒
            // all-null (no-preference), never a throw.
            return request.Questions
                .Select(q => byName.TryGetValue(q.Name, out var a)
                    ? a
                    : new DecisionAnswer(q.Name, null, null, 0d))
                .ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new AiUnavailableException($"decision gateway returned an unreadable response ({request.EndpointName})", e);
        }
    }

    private static DecisionAnswer ParseAnswer(JsonElement answer)
    {
        var name = answer.GetProperty("name").GetString() ?? string.Empty;
        var kind = answer.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (kind == "predicate"
            && answer.TryGetProperty("probability", out var p) && p.ValueKind == JsonValueKind.Number
            && p.TryGetDouble(out var probability))
        {
            return new DecisionAnswer(name, probability >= PredicateTrueThreshold, null, probability);
        }

        if (kind == "choice"
            && answer.TryGetProperty("choice", out var c) && c.ValueKind == JsonValueKind.String
            && answer.TryGetProperty("confidence", out var cf) && cf.TryGetDouble(out var confidence))
        {
            return new DecisionAnswer(name, null, c.GetString(), confidence);
        }

        return new DecisionAnswer(name, null, null, 0d);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, string endpointName)
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
                throw;
            }

            throw new AiUnavailableException($"decision gateway unreachable ({endpointName})", e);
        }
    }
}
