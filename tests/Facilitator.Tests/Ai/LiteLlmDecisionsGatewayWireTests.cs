using System.Net;
using System.Diagnostics;
using System.Text.Json;

using Facilitator.Kernel.Ai;

namespace Facilitator.Tests.Ai;

/// <summary>
/// The decisions-API wire path against a stubbed <see cref="HttpMessageHandler"/>
/// (the injected-handler seam: no factory, no network, no AI env vars — base URL
/// and key are in-test literals). Covers request shaping (URL, bearer, input
/// message, typed questions), answer parsing (predicate threshold, choice +
/// confidence, refusal/missing-name ⇒ all-no-preference rows in question order),
/// and the degradation law: non-2xx / connect error / deadline / unreadable body /
/// unconfigured endpoint ⇒ <see cref="AiUnavailableException"/>; caller
/// cancellation stays itself. The 1..32 question bound is the caller's contract.
/// </summary>
public sealed class LiteLlmDecisionsGatewayWireTests
{
    private const string TwoQuestionRequest = "state being judged";

    private static AiOptions Options => LlmTestOptions.Single("decision");

    /// <summary>The gateway over the public handler-injection seam — the same
    /// seam the chat gateway's wire tests use (no factory, no network).</summary>
    private static LiteLlmDecisionsGateway Gateway(AiOptions options, LlmStubHandler handler) =>
        new(options, handler);

    private static DecisionRequest Request(params DecisionQuestion[] questions) =>
        new("decision", TwoQuestionRequest, questions);

    private static readonly PredicateQuestion Smoke = new("smoke", "Is the build on fire?");

    private static readonly ChoiceQuestion Owner = new("owner", "Who should take it?", ["team-alpha", "team-beta"]);

    [Fact]
    public async Task PostsTheDecisionsRequestWithTypedQuestions()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, """{"answers":[]}"""));
        var gateway = Gateway(Options, handler);

        await gateway.DecideAsync(
            new DecisionRequest("decision", TwoQuestionRequest, [Smoke, Owner]), CancellationToken.None);

        var request = handler.SingleRequest();
        Assert.Equal("POST", request.Method);
        Assert.Equal("http://litellm.test:4000/v1/decisions", request.Uri);
        Assert.Equal("Bearer test-key", request.Auth);
        Assert.StartsWith("application/json", request.ContentType);

        using var payload = JsonDocument.Parse(request.Body);
        var root = payload.RootElement;
        Assert.Equal("model-x", root.GetProperty("model").GetString());

        var input = root.GetProperty("input").EnumerateArray().Single();
        Assert.Equal("message", input.GetProperty("type").GetString());
        Assert.Equal("user", input.GetProperty("role").GetString());
        Assert.Equal(
            TwoQuestionRequest,
            input.GetProperty("content").EnumerateArray().Single().GetProperty("text").GetString());
        Assert.Equal("input_text", input.GetProperty("content").EnumerateArray().Single().GetProperty("type").GetString());

        var questions = root.GetProperty("questions").EnumerateArray().ToList();
        Assert.Equal(2, questions.Count);
        Assert.Equal("predicate", questions[0].GetProperty("type").GetString());
        Assert.Equal("smoke", questions[0].GetProperty("name").GetString());
        Assert.Equal("Is the build on fire?", questions[0].GetProperty("instructions").GetString());
        Assert.False(questions[0].TryGetProperty("choices", out _));
        Assert.Equal("choice", questions[1].GetProperty("type").GetString());
        Assert.Equal("owner", questions[1].GetProperty("name").GetString());
        Assert.Equal(
            ["team-alpha", "team-beta"],
            questions[1].GetProperty("choices").EnumerateArray()
                .Select(c => c.GetProperty("value").GetString()).ToList());
    }

    [Fact]
    public async Task TrailingSlashInBaseUrlStillAddressesTheDecisionsPathOnce()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, """{"answers":[]}"""));
        var gateway = Gateway(LlmTestOptions.Single("decision", baseUrl: "http://litellm.test:4000/"), handler);

        await gateway.DecideAsync(Request(Smoke), CancellationToken.None);

        Assert.Equal("http://litellm.test:4000/v1/decisions", handler.SingleRequest().Uri);
    }

    [Fact]
    public async Task EmptyApiKeySendsNoAuthorizationHeader()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, """{"answers":[]}"""));
        var gateway = Gateway(LlmTestOptions.Single("decision", apiKey: null), handler);

        await gateway.DecideAsync(Request(Smoke), CancellationToken.None);

        Assert.Null(handler.SingleRequest().Auth);
    }

    [Fact]
    public async Task FactoryConstructionPathAsksForTheAiNamedClient()
    {
        // Both construction paths reach the same wire: the IHttpClientFactory
        // seam (what DI uses) names the "ai" client.
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, """{"answers":[]}"""));
        var factory = new LlmStubHttpClientFactory(handler);

        await new LiteLlmDecisionsGateway(Options, factory).DecideAsync(
            Request(Smoke), CancellationToken.None);

        Assert.Equal(["ai"], factory.RequestedNames);
    }

    [Theory]
    [InlineData(0.91, true)]
    [InlineData(0.5, true)] // the threshold itself counts as true
    [InlineData(0.4999, false)]
    [InlineData(0.0, false)]
    public async Task PredicateProbabilityThresholdsAtHalf(double probability, bool expected)
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK,
            $$"""{"answers":[{"type":"predicate","name":"smoke","probability":{{probability}}}]}"""));
        var gateway = Gateway(Options, handler);

        var answers = await gateway.DecideAsync(Request(Smoke), CancellationToken.None);

        var answer = Assert.Single(answers);
        Assert.Equal("smoke", answer.Name);
        Assert.Equal(expected, answer.Predicate);
        Assert.Null(answer.Choice);
        Assert.Equal(probability, answer.Confidence, 6);
    }

    [Fact]
    public async Task ChoiceAnswerCarriesTheValueAndConfidence()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK,
            """{"answers":[{"type":"choice","name":"owner","choice":"team-beta","confidence":0.77}]}"""));
        var gateway = Gateway(Options, handler);

        var answers = await gateway.DecideAsync(Request(Owner), CancellationToken.None);

        var answer = Assert.Single(answers);
        Assert.Equal("team-beta", answer.Choice);
        Assert.Null(answer.Predicate);
        Assert.Equal(0.77, answer.Confidence, 6);
    }

    [Fact]
    public async Task AnswersArriveInQuestionOrderEvenWhenTheProviderReorderedThem()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK,
            """
            {"answers":[
              {"type":"choice","name":"owner","choice":"team-alpha","confidence":0.6},
              {"type":"predicate","name":"smoke","probability":0.8}]}
            """));
        var gateway = Gateway(Options, handler);

        var answers = await gateway.DecideAsync(Request(Smoke, Owner), CancellationToken.None);

        Assert.Equal(["smoke", "owner"], answers.Select(a => a.Name).ToArray());
        Assert.True(answers[0].Predicate);
        Assert.Equal("team-alpha", answers[1].Choice);
    }

    [Theory]
    [InlineData("refusal", """{"answers":[{"type":"predicate","name":"smoke"}]}""")]
    [InlineData("probability-of-the-wrong-kind", """{"answers":[{"type":"predicate","name":"smoke","probability":"hot"}]}""")]
    [InlineData("choice-without-confidence", """{"answers":[{"type":"choice","name":"owner","choice":"team-alpha"}]}""")]
    [InlineData("choice-with-a-non-string-value", """{"answers":[{"type":"choice","name":"owner","choice":7,"confidence":0.9}]}""")]
    [InlineData("unknown-answer-type", """{"answers":[{"type":"guess","name":"smoke","probability":0.9}]}""")]
    [InlineData("answer-without-a-name", """{"answers":[{"type":"predicate","probability":0.9}]}""")]
    [InlineData("answer-name-not-a-string", """{"answers":[{"type":"predicate","name":5,"probability":0.9}]}""")]
    [InlineData("empty-answers", """{"answers":[]}""")]
    [InlineData("answers-key-of-the-wrong-kind", """{"answers":{"smoke":true}}""")]
    [InlineData("no-answers-key", """{"model":"model-x"}""")]
    public async Task AnythingShortOfAWellFormedAnswerIsNoPreferenceNotAnError(string _, string body)
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, body));
        var gateway = Gateway(Options, handler);

        var answers = await gateway.DecideAsync(Request(Smoke, Owner), CancellationToken.None);

        // One row per question, question order, all-null (no preference) —
        // never a throw, so the caller's deterministic order stands.
        Assert.Equal(["smoke", "owner"], answers.Select(a => a.Name).ToArray());
        Assert.All(answers, answer =>
        {
            Assert.Null(answer.Predicate);
            Assert.Null(answer.Choice);
            Assert.Equal(0d, answer.Confidence);
        });
    }

    [Fact]
    public async Task AnAnswerForAnUnknownNameIsIgnored()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK,
            """{"answers":[{"type":"predicate","name":"happened-elsewhere","probability":0.99}]}"""));
        var gateway = Gateway(Options, handler);

        var answers = await gateway.DecideAsync(Request(Smoke), CancellationToken.None);

        var answer = Assert.Single(answers);
        Assert.Equal("smoke", answer.Name);
        Assert.Null(answer.Predicate);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task NonOkStatusMapsToUnavailableNamingStatusAndEndpoint(HttpStatusCode status)
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(status, """{"error":"boom"}"""));
        var gateway = Gateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.DecideAsync(Request(Smoke), CancellationToken.None));

        Assert.Contains($"{(int)status}", error.Message);
        Assert.Contains("(decision)", error.Message);
    }

    [Fact]
    public async Task ConnectErrorMapsToUnavailableNamingTheEndpoint()
    {
        var handler = new LlmStubHandler(_ => throw new HttpRequestException("connection refused"));
        var gateway = Gateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.DecideAsync(Request(Smoke), CancellationToken.None));

        Assert.Contains("decision gateway unreachable (decision)", error.Message);
    }

    [Fact]
    public async Task HangingProviderIsCutOffByThePerEndpointDeadline()
    {
        // TimeoutSeconds is clamped to the 5 s floor, so a hung provider costs
        // exactly that floor and never hangs the caller. HttpClient stamps the
        // cancelled request token on the failure, so the gateway passes the
        // cancellation through instead of rewriting it into an outage.
        var handler = new LlmStubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return LlmStubHandler.Json(HttpStatusCode.OK, """{"answers":[]}""");
        });
        var gateway = Gateway(LlmTestOptions.Single("decision", timeoutSeconds: 0), handler);

        var started = Stopwatch.StartNew();
        var thrown = await Record.ExceptionAsync(() =>
            gateway.DecideAsync(Request(Smoke), CancellationToken.None));
        started.Stop();

        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.IsNotType<AiUnavailableException>(thrown);
        Assert.InRange(started.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task InternalDeadlineCancellationDegradesToUnavailable()
    {
        using var foreign = new CancellationTokenSource();
        var handler = new LlmStubHandler(_ => throw new OperationCanceledException(foreign.Token));
        var gateway = Gateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.DecideAsync(Request(Smoke), CancellationToken.None));

        Assert.Contains("decision gateway unreachable", error.Message);
    }

    [Fact]
    public async Task CallerCancellationStaysItself()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new LlmStubHandler(async (_, token) =>
        {
            await Task.Delay(50, CancellationToken.None);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        var gateway = Gateway(Options, handler);

        var thrown = await Record.ExceptionAsync(() =>
            gateway.DecideAsync(Request(Smoke), cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.IsNotType<AiUnavailableException>(thrown);
    }

    [Theory]
    [InlineData("not json at all", "plain text body")]
    [InlineData("", "empty body")]
    [InlineData("""{"answers":[{"name":""", "truncated json")]
    public async Task UnreadableResponseBodyMapsToUnavailable(string body, string _)
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, body));
        var gateway = Gateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.DecideAsync(Request(Smoke), CancellationToken.None));

        Assert.Contains("decision gateway returned an unreadable response (decision)", error.Message);
    }

    [Fact]
    public async Task UnconfiguredEndpointFailsClosedBeforeAnyRequest()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, """{"answers":[]}"""));
        var gateway = Gateway(Options, handler);

        var error = await Assert.ThrowsAsync<AiUnavailableException>(() =>
            gateway.DecideAsync(new DecisionRequest("decoder", "text", [Smoke]), CancellationToken.None));

        Assert.Contains("AI endpoint 'decoder' is not configured", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public async Task QuestionCountOutsideOneToThirtyTwoIsAnArgumentError(int count)
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, """{"answers":[]}"""));
        var gateway = Gateway(Options, handler);
        var questions = Enumerable.Repeat<DecisionQuestion>(Smoke, count).ToList();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            gateway.DecideAsync(new DecisionRequest("decision", "text", questions), CancellationToken.None));

        Assert.Contains("1..32 questions", error.Message);
        Assert.Empty(handler.Requests);
    }

    private sealed record OddQuestion(string Name, string Instructions) : DecisionQuestion(Name, Instructions);

    [Fact]
    public async Task AQuestionKindTheWireFormatDoesNotKnowFailsFast()
    {
        var handler = new LlmStubHandler(_ => LlmStubHandler.Json(HttpStatusCode.OK, """{"answers":[]}"""));
        var gateway = Gateway(Options, handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateway.DecideAsync(Request(new OddQuestion("odd", "?")), CancellationToken.None));

        Assert.Empty(handler.Requests);
    }
}
