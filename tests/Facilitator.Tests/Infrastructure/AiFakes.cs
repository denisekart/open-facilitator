using System.Collections.Concurrent;
using System.Net;
using System.Text;

using Facilitator.Kernel.Ai;

namespace Facilitator.Tests.Infrastructure;

/// <summary>The test host's paired fake gateways (shared unavailability
/// toggle). One instance per factory; tests that need to seed a decode
/// fixture construct it first and pass it to <c>KernelTestFactory</c>.</summary>
public sealed class AiFixtures
{
    public FakeAiGateway Chat { get; } = new();

    public FakeDecisionGateway Decisions { get; } = new();

    public AiFixtures() => Decisions.Peer = Chat;
}

/// <summary>
/// THE simulation rule: deterministic tests never call a real AI. Both AI
/// ports are swapped for these fakes unconditionally under the test host.
/// Chat calls: FIFO canned responses per template id (never throws
/// unpredictably — an unmatched call is recorded and answers the
/// unknown-intent decode shape). Decision calls: FIFO canned answers per
/// question name; unmatched ⇒ refusal-shaped all-null rows, so tests that
/// don't care about decisions stay deterministic for free (provider null ⇒
/// deterministic order). <see cref="Unavailable"/> drives degradation tests.
/// </summary>
public sealed class FakeAiGateway : IAiGateway
{
    private readonly ConcurrentQueue<(string TemplateId, string Response)> _chat = new();
    private readonly ConcurrentQueue<(string TemplateId, string Response)> _unmatched = new();

    /// <summary>Every completion call observed (prompt assertions).</summary>
    public List<AiRequest> Requests { get; } = [];

    /// <summary>When true, both ports throw <see cref="AiUnavailableException"/>.</summary>
    public bool Unavailable { get; set; }

    /// <summary>Queue a canned assistant response for the next call rendering
    /// <paramref name="templateId"/> (FIFO per template).</summary>
    public void Enqueue(string templateId, string responseJson) => _chat.Enqueue((templateId, responseJson));

    /// <inheritdoc />
    public Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        if (Unavailable)
        {
            throw new AiUnavailableException("fake gateway unavailable");
        }

        // FIFO per template: drain matches; non-matches park aside and get
        // re-queued so per-template ordering holds.
        var matched = false;
        string response = """{"intent":"unknown","command":null,"args":{},"reply":null,"confidence":0.0}""";
        while (_chat.TryDequeue(out var item))
        {
            if (!matched && item.TemplateId == request.TemplateId)
            {
                matched = true;
                response = item.Response;
            }
            else
            {
                _unmatched.Enqueue(item);
            }
        }

        while (_unmatched.TryDequeue(out var park))
        {
            _chat.Enqueue(park);
        }

        return Task.FromResult(new AiCompletion(response, 10, 20));
    }
}

/// <summary>Fake decisions port (see <see cref="FakeAiGateway"/> law).</summary>
public sealed class FakeDecisionGateway : IDecisionGateway
{
    private readonly ConcurrentQueue<(string QuestionName, DecisionAnswer Answer)> _answers = new();
    private readonly ConcurrentQueue<(string QuestionName, DecisionAnswer Answer)> _parked = new();

    /// <summary>Every decision call observed (question-set assertions).</summary>
    public List<DecisionRequest> DecisionRequests { get; } = [];

    /// <summary>Shared unavailability toggle (both ports degrade together).</summary>
    public FakeAiGateway? Peer { get; set; }

    /// <summary>Queue a canned answer for the next call carrying a question
    /// named <paramref name="questionName"/> (FIFO per name).</summary>
    public void EnqueueDecision(string questionName, DecisionAnswer answer) => _answers.Enqueue((questionName, answer));

    /// <inheritdoc />
    public Task<IReadOnlyList<DecisionAnswer>> DecideAsync(DecisionRequest request, CancellationToken ct)
    {
        lock (DecisionRequests)
        {
            DecisionRequests.Add(request);
        }

        if (Peer?.Unavailable == true)
        {
            throw new AiUnavailableException("fake decision gateway unavailable");
        }

        var answers = new List<DecisionAnswer>();
        foreach (var question in request.Questions)
        {
            DecisionAnswer? canned = null;
            var matched = false;
            while (_answers.TryDequeue(out var item))
            {
                if (!matched && item.QuestionName == question.Name)
                {
                    matched = true;
                    canned = item.Answer;
                }
                else
                {
                    _parked.Enqueue(item);
                }
            }

            while (_parked.TryDequeue(out var park))
            {
                _answers.Enqueue(park);
            }

            // Unmatched ⇒ refusal-shaped no-preference row.
            answers.Add(canned ?? new DecisionAnswer(question.Name, null, null, 0d));
        }

        return Task.FromResult<IReadOnlyList<DecisionAnswer>>(answers);
    }
}

/// <summary>
/// Wire-path proof without network: canned responses for the REAL
/// <c>OpenAiChatCompletionsGateway</c> / <c>LiteLlmDecisionsGateway</c>
/// (request body shape, auth header, refusal mapping, 503→fallback) via the
/// internal handler-injection seam. FIFO by path prefix.
/// </summary>
public sealed class ScriptedLlmHandler : HttpMessageHandler
{
    private readonly List<(string PathContains, string ResponseJson, HttpStatusCode Status)> _script = [];

    /// <summary>Captured outbound requests (body + headers) for assertions.</summary>
    public List<(string Path, string Body, string? Auth)> Captured { get; } = [];

    /// <summary>Queue a canned response for the next request whose path
    /// contains <paramref name="pathContains"/>.</summary>
    public void Enqueue(string pathContains, string responseJson, HttpStatusCode status = HttpStatusCode.OK) =>
        _script.Add((pathContains, responseJson, status));

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        Captured.Add((request.RequestUri?.AbsolutePath ?? string.Empty, body, request.Headers.Authorization?.ToString()));

        for (var i = 0; i < _script.Count; i++)
        {
            if (_script[i].PathContains.Contains(request.RequestUri?.AbsolutePath ?? string.Empty, StringComparison.Ordinal)
                || request.RequestUri?.AbsolutePath.Contains(_script[i].PathContains, StringComparison.Ordinal) == true)
            {
                var entry = _script[i];
                _script.RemoveAt(i);
                return new HttpResponseMessage(entry.Status)
                {
                    Content = new StringContent(entry.ResponseJson, Encoding.UTF8, "application/json"),
                };
            }
        }

        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"error":"script exhausted"}"""),
        };
    }
}
