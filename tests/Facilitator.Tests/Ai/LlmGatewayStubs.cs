using System.Net;
using System.Text;

using Facilitator.Kernel.Ai;

namespace Facilitator.Tests.Ai;

/// <summary>
/// Shared wire stubs for the two real LLM gateways: an <see cref="HttpMessageHandler"/>
/// whose answer the test supplies, plus in-test <see cref="AiOptions"/> construction.
/// Precedent: <c>AiFakes.ScriptedLlmHandler</c> (the FIFO variant used by the
/// host-backed suites); this one answers through a per-test lambda so a test can
/// throw mid-send, hang, or answer per request — the whole
/// request-shaping / parsing / error-mapping surface, zero network and zero AI
/// environment variables (base URL and key are always literals here).
/// </summary>
internal sealed class LlmStubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    /// <summary>Answer synchronously (canned bodies, or a throw for a connect error).</summary>
    public LlmStubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    /// <summary>Answer asynchronously (hangs, mid-send cancellation).</summary>
    public LlmStubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        _respond = respond;

    /// <summary>Every outbound request observed (method, absolute uri, body, auth, content type).</summary>
    public List<Captured> Requests { get; } = [];

    /// <summary>One captured outbound request.</summary>
    public sealed record Captured(string Method, string Uri, string Body, string? Auth, string? ContentType);

    /// <summary>The single request captured, failing when the call sent anything else.</summary>
    public Captured SingleRequest()
    {
        var request = Assert.Single(Requests);
        return request;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        lock (Requests)
        {
            Requests.Add(new Captured(
                request.Method.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                body,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.ToString()));
        }

        return await _respond(request, ct);
    }

    /// <summary>A canned json answer.</summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A 200 whose body blows up while being read (provider died
    /// mid-stream after the headers arrived).</summary>
    public static HttpResponseMessage UnreadableBody() =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingStream()) };

    /// <summary>A body stream that is fine until something reads it.</summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set { } }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            throw new HttpRequestException("broken pipe");

        public override int Read(byte[] buffer, int offset, int count) => throw new HttpRequestException("broken pipe");

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct) =>
            Task.FromException(new HttpRequestException("broken pipe"));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>The LiteLLM decisions gateway's production construction path: a
/// real <see cref="IHttpClientFactory"/> seam over the stub handler, so the
/// gateway runs its factory branch (the same one DI gives it) with zero
/// network. Also records which named client the gateway asked for.</summary>
internal sealed class LlmStubHttpClientFactory(LlmStubHandler handler) : IHttpClientFactory
{
    /// <summary>The client names requested, in order.</summary>
    public List<string> RequestedNames { get; } = [];

    /// <inheritdoc />
    public HttpClient CreateClient(string name)
    {
        lock (RequestedNames)
        {
            RequestedNames.Add(name);
        }

        return new HttpClient(handler, disposeHandler: false);
    }
}

/// <summary>Gateway options built in-test: one named endpoint per gateway, literal
/// base URL + key, explicit timeout — never read from the environment.</summary>
internal static class LlmTestOptions
{
    public const string BaseUrl = "http://litellm.test:4000";

    public const string ApiKey = "test-key";

    public static AiOptions Single(
        string endpointName,
        string baseUrl = BaseUrl,
        string model = "model-x",
        string? apiKey = ApiKey,
        int timeoutSeconds = 20) => new()
        {
            Endpoints =
            {
                [endpointName] = new AiEndpointOptions
                {
                    BaseUrl = baseUrl,
                    Model = model,
                    ApiKey = apiKey,
                    TimeoutSeconds = timeoutSeconds,
                },
            },
        };
}
