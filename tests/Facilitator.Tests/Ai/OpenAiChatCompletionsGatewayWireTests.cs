using System.Net;
using System.Diagnostics;
using System.Text.Json;

using Facilitator.Kernel.Ai;

namespace Facilitator.Tests.Ai;

/// <summary>
/// The chat-completions wire path against a stubbed <see cref="HttpMessageHandler"/>
/// (the injected-handler seam: no factory, no network, no AI env vars — base URL
/// and key are in-test literals). Covers request shaping (URL, bearer, model,
/// messages, budget, temperature 0), response parsing (text + usage), and the
/// degradation law: every provider trouble (non-2xx, connect error, per-endpoint
/// deadline, unreadable body, unconfigured endpoint) surfaces as
/// <see cref="AiUnavailableException"/>, while caller cancellation stays itself.
/// The gateway sends exactly one request per call — retry policy is the caller's.
/// </summary>
public sealed class OpenAiChatCompletionsGatewayWireTests
{
    private const string CompletionFixture =
        """{"choices":[{"message":{"role":"assistant","content":"hello"}}],"usage":{"prompt_tokens":11,"completion_tokens":7}}""";

    private static AiOptions Options => LlmTestOptions.Single("decoder");

    [Fact]
    public async Task PostsTheDeterministicRequestAndReadsTextAndUsage()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, CompletionFixture));
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        var completion = await gateway.CompleteAsync(
            new AiRequest("decoder", "decode", "system law", "the message", 640), CancellationToken.None);

        Assert.Equal("hello", completion.Text);
        Assert.Equal(11, completion.PromptTokens);
        Assert.Equal(7, completion.CompletionTokens);

        var request = handler.SingleRequest();
        Assert.Equal("POST", request.Method);
        Assert.Equal("http://litellm.test:4000/v1/chat/completions", request.Uri);
        Assert.Equal("Bearer test-key", request.Auth);
        Assert.StartsWith("application/json", request.ContentType);

        using var payload = JsonDocument.Parse(request.Body);
        var root = payload.RootElement;
        Assert.Equal("model-x", root.GetProperty("model").GetString());
        Assert.Equal(640, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system law", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("the message", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TrailingSlashInBaseUrlStillAddressesTheCompletionsPathOnce()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, CompletionFixture));
        var gateway = new OpenAiChatCompletionsGateway(
            LlmTestOptions.Single("decoder", baseUrl: "http://litellm.test:4000/"), handler);

        await gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None);

        Assert.Equal("http://litellm.test:4000/v1/chat/completions", handler.SingleRequest().Uri);
    }

    [Fact]
    public async Task EmptyApiKeySendsNoAuthorizationHeader()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, CompletionFixture));
        var gateway = new OpenAiChatCompletionsGateway(
            LlmTestOptions.Single("decoder", apiKey: null), handler);

        await gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None);

        Assert.Null(handler.SingleRequest().Auth);
    }

    [Fact]
    public async Task UsageBlockIsOptionalAndDefaultsToZero()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"ok"}}]}"""));
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        var completion = await gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None);

        Assert.Equal("ok", completion.Text);
        Assert.Equal(0, completion.PromptTokens);
        Assert.Equal(0, completion.CompletionTokens);
    }

    [Fact]
    public async Task PartialUsageBlockReadsWhatItCan()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":5}}"""));
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        var completion = await gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None);

        Assert.Equal(5, completion.PromptTokens);
        Assert.Equal(0, completion.CompletionTokens);
    }

    [Fact]
    public async Task NonNumericUsageCountDegradesTheWholeCompletion()
    {
        // The reading is strict: a usage value that is not a number is an
        // unreadable response, not a zero — a garbled body is provider trouble.
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":"n/a"}}"""));
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None));

        Assert.Contains("unreadable completion (decoder/chat)", error.Message);
    }

    [Fact]
    public async Task OneCallSendsExactlyOneRequestEvenOnServerError()
    {
        // No hidden retry inside the gateway: the attempt budget is the
        // caller's (the decode sink counts its own attempts).
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.ServiceUnavailable, """{"error":"busy"}"""));
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task NonOkStatusMapsToUnavailableNamingStatusAndEndpoint(HttpStatusCode status)
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(status, """{"error":"boom"}"""));
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.CompleteAsync(new AiRequest("decoder", "classify", "s", "u", 100), CancellationToken.None));

        Assert.Contains($"{(int)status}", error.Message);
        Assert.Contains("decoder/classify", error.Message);
    }

    [Fact]
    public async Task ConnectErrorMapsToUnavailableNamingThePath()
    {
        var handler = new LlmStubHandler(_ => throw new HttpRequestException("connection refused"));
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None));

        Assert.Contains("ai gateway unreachable (/v1/chat/completions)", error.Message);
    }

    [Fact]
    public async Task BodyThatDiesMidReadDegradesToUnavailable()
    {
        // Provider died after the headers arrived: HttpClient surfaces the
        // broken response stream through the send path, so the gateway's
        // read-failure mapping and its unreachable mapping converge — either
        // way the caller gets AiUnavailableException, never a half-read body.
        var handler = new LlmStubHandler(_ => LlmStubHandler.UnreadableBody());
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None));

        Assert.Contains("ai gateway", error.Message);
    }

[Theory]
    [InlineData("""{"choices":[]}""", "no choices")]
    [InlineData("""{"choices":[{"message":{"role":"assistant"}}]}""", "no content property")]
    [InlineData("""{"choices":[{"message":{"content":null}}]}""", "explicit null content")]
    [InlineData("""{"choices":[{"message":{"content":42}}]}""", "content of the wrong kind")]
    [InlineData("not json at all", "not json")]
    [InlineData("""["array","root"]""", "array root")]
    public async Task UnreadableCompletionBodyMapsToUnavailable(string body, string _)
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, body));
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None));

        Assert.Contains("ai gateway returned an unreadable completion (decoder/chat)", error.Message);
    }

    [Fact]
    public async Task FactoryConstructionPathAsksForTheAiNamedClient()
    {
        // Both construction paths must reach the same wire: the public
        // IHttpClientFactory seam (what DI uses) names the "ai" client.
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, CompletionFixture));
        var factory = new LlmStubHttpClientFactory(handler);
        var gateway = new OpenAiChatCompletionsGateway(Options, factory);

        var completion = await gateway.CompleteAsync(
            new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None);

        Assert.Equal("hello", completion.Text);
        Assert.Equal(["ai"], factory.RequestedNames);
    }

    [Fact]
    public async Task UnconfiguredEndpointFailsClosedBeforeAnyRequest()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, CompletionFixture));
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.CompleteAsync(new AiRequest("classifier", "chat", "s", "u", 100), CancellationToken.None));

        Assert.Contains("AI endpoint 'classifier' is not configured", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CallerCancellationStaysItself()
    {
        // The framework's shape for a caller-cancelled send: the OCE carries
        // the caller's own token. That must never be rewritten into
        // "ai unavailable" — the caller asked to stop, the AI is fine.
        using var cancellation = new CancellationTokenSource();
        var handler = new LlmStubHandler(async (_, token) =>
        {
            await Task.Delay(50, CancellationToken.None);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        var thrown = await Record.ExceptionAsync(() =>
            gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.IsNotType<AiUnavailableException>(thrown);
    }

    [Fact]
    public async Task InternalDeadlineCancellationDegradesToUnavailable()
    {
        // Same exception type, foreign token (the per-endpoint deadline CTS):
        // provider trouble, so it degrades.
        using var foreign = new CancellationTokenSource();
        var handler = new LlmStubHandler(_ => throw new OperationCanceledException(foreign.Token));
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None));

        Assert.Contains("ai gateway unreachable", error.Message);
    }

    [Fact]
    public async Task HttpClientStyleDeadlineTaskCancellationDegradesToUnavailable()
    {
        var handler = new LlmStubHandler(_ => throw new TaskCanceledException("deadline", new TimeoutException()));
        var gateway = new OpenAiChatCompletionsGateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None));

        Assert.Contains("ai gateway unreachable", error.Message);
    }

    [Fact]
    public async Task HangingProviderIsCutOffByThePerEndpointDeadline()
    {
        // The real deadline path: TimeoutSeconds is clamped to the 10 s
        // connect floor, so a hung provider costs exactly that floor and the
        // caller never hangs. HttpClient stamps the cancelled request token on
        // the failure, which the gateway reads as "the caller stopped first"
        // and therefore passes cancellation through rather than rewriting it
        // into an outage — the deadline is observable as a cancellation.
        var handler = new LlmStubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return LlmStubHandler.Json(HttpStatusCode.OK, CompletionFixture);
        });
        var gateway = new OpenAiChatCompletionsGateway(
            LlmTestOptions.Single("decoder", timeoutSeconds: 0), handler); // 0 clamps up to the 10 s floor

        var started = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() =>
            gateway.CompleteAsync(new AiRequest("decoder", "chat", "s", "u", 100), CancellationToken.None));
        started.Stop();

        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.IsNotType<AiUnavailableException>(thrown);
        Assert.InRange(started.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(30));
    }
}
