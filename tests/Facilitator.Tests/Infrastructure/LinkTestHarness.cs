using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.AspNetCore.SignalR.Client;

namespace Facilitator.Tests.Infrastructure;

/// <summary>
/// Shared plumbing for the link suites: enroll/hello/receipt payload
/// builders over the frozen fixtures, a SignalR fake link client (WebSockets,
/// bearer header — no secrets in query strings) over a
/// Kestrel-bound factory, and a controllable <see cref="TimeProvider"/> so
/// lease math never sleeps on wall clock.
/// </summary>
internal static class LinkTestHarness
{
    internal static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Enroll an endpoint (Development bootstrap secret) and return its link token.</summary>
    internal static async Task<string> EnrollAsync(HttpClient client, string endpointId, string trustMode = "isolated-executor", string team = "team-alpha")
    {
        var body = JsonSerializer.Serialize(new { token = "enroll-dev", endpointId, instanceId = "inst-1", trustMode, team });
        var json = await KernelOps.PostJsonAsync(client, "/api/link/v1/enroll", body, System.Net.HttpStatusCode.Created);
        return json.GetProperty("linkToken").GetString()!;
    }

    /// <summary>A client carrying a link token (not the kernel bearer).</summary>
    internal static HttpClient CreateLinkClient(this KernelTestFactory factory, string linkToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", linkToken);
        return client;
    }

    /// <summary>
    /// The frozen hello fixture rewritten for a test endpoint: fresh instance,
    /// offered protocol versions, junk lastKnownAck (a hint the server MUST
    /// ignore).
    /// </summary>
    internal static JsonElement HelloFrame(string endpointId, params string[] protocolVersions)
    {
        var versions = (protocolVersions.Length == 0 ? new[] { "v1" } : protocolVersions)
            .Select(v => JsonSerializer.Serialize(v));
        var text = PostgresFixture.ReadFixture("valid/control.hello.json")
            .Replace("\"gw-team-alpha\"", JsonSerializer.Serialize(endpointId))
            .Replace("\"v1\"", string.Join(", ", versions));
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A control frame with the protocolVersions list replaced wholesale (e.g. ["v9"]).</summary>
    internal static JsonElement HelloFrameVersions(string endpointId, string versionsJson)
    {
        var text = PostgresFixture.ReadFixture("valid/control.hello.json")
            .Replace("\"gw-team-alpha\"", JsonSerializer.Serialize(endpointId))
            .Replace("[\n    \"v1\"\n  ]", versionsJson);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>heartbeat fixture rewritten for a session id + lease epoch.</summary>
    internal static JsonElement HeartbeatFrame(string sessionId, long leaseEpoch)
    {
        var text = PostgresFixture.ReadFixture("valid/control.heartbeat.json")
            .Replace("\"sig-01\"", JsonSerializer.Serialize(sessionId));
        text = System.Text.RegularExpressions.Regex.Replace(text, "\"leaseEpoch\":\\s*\\d+", $"\"leaseEpoch\": {leaseEpoch}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>goodbye fixture rewritten for a session id.</summary>
    internal static JsonElement GoodbyeFrame(string sessionId)
    {
        var text = PostgresFixture.ReadFixture("valid/control.goodbye.json")
            .Replace("\"sess-1\"", JsonSerializer.Serialize(sessionId));
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Receipt frame carrying deliveryId (hub path: full frame).</summary>
    internal static JsonElement ReceiptFrame(Guid deliveryId, string status = "accepted")
    {
        var frame = new Dictionary<string, object>
        {
            ["deliveryId"] = deliveryId,
            ["status"] = status,
            ["processedAt"] = DateTime.UtcNow,
        };
        if (status is "rejected" or "unsupported")
        {
            frame["reason"] = "test-" + status;
        }

        return JsonSerializer.SerializeToElement(frame, Web);
    }

    /// <summary>Receipt WITHOUT deliveryId (HTTP path: identity from the route).</summary>
    internal static string ReceiptBody(string status = "accepted") =>
        status is "rejected" or "unsupported"
            ? JsonSerializer.Serialize(new { status, reason = "test-" + status, processedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") })
            : JsonSerializer.Serialize(new { status, processedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") });

    /// <summary>
    /// The frozen work.assign fixture made unique (fresh envelope id +
    /// commandId — command_id is unique across the shared container) and
    /// targeted at the test endpoint.
    /// </summary>
    internal static string AssignEnvelopeFor(string endpointId) =>
        PostgresFixture.ReadFixture("valid/command.work.assign.json")
            .Replace("\"01JCOMMAND01\"", JsonSerializer.Serialize("01J" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant()))
            .Replace("\"cmd-123\"", JsonSerializer.Serialize("cmd-" + Guid.NewGuid().ToString("N")[..8]))
            .Replace("\"gateway-team-alpha\"", JsonSerializer.Serialize(endpointId));

    /// <summary>Queue one link-addressed outbox row (bootstrap-auth'd test affordance).</summary>
    internal static async Task QueueLinkOutboxAsync(HttpClient kernelClient, string endpointId, string envelopeJson)
    {
        var body = JsonSerializer.Serialize(new { endpointId, envelope = JsonDocument.Parse(envelopeJson).RootElement.Clone() });
        await KernelOps.PostJsonAsync(kernelClient, "/api/dev/link-outbox", body, System.Net.HttpStatusCode.Accepted);
    }

    /// <summary>A schema-valid capability manifest owned by a team.</summary>
    internal static string Manifest(string ownerTeam) =>
        JsonSerializer.Serialize(new
        {
            apiVersion = "facilitator.framework/v1",
            kind = "CapabilityManifest",
            ownerTeam,
            capabilities = new[] { new { id = "research.analyze", version = 1, execution = "asynchronous" } },
        });
}

/// <summary>
/// SignalR fake link client over raw WebSockets (skip negotiation): bearer
/// token in the Authorization header, buffered client-method pushes
/// (Deliver/FlowControl/Disconnect) for ordered assertions.
/// </summary>
internal sealed class FakeLinkClient : IAsyncDisposable
{
    private readonly Channel<JsonElement> _deliver = Channel.CreateUnbounded<JsonElement>();
    private readonly Channel<JsonElement> _flow = Channel.CreateUnbounded<JsonElement>();
    private readonly Channel<JsonElement> _disconnect = Channel.CreateUnbounded<JsonElement>();

    private FakeLinkClient(HubConnection connection)
    {
        Connection = connection;
        Connection.On<JsonElement>("Deliver", f => _deliver.Writer.TryWrite(f));
        Connection.On<JsonElement>("FlowControl", f => _flow.Writer.TryWrite(f));
        Connection.On<JsonElement>("Disconnect", f => _disconnect.Writer.TryWrite(f));
    }

    public HubConnection Connection { get; }

    public string ConnectionId => Connection.ConnectionId ?? string.Empty;

    public static async Task<FakeLinkClient> ConnectAsync(string baseAddress, string linkToken)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(baseAddress.TrimEnd('/') + "/hubs/link", options =>
            {
                options.Headers["Authorization"] = "Bearer " + linkToken;
                options.SkipNegotiation = true;
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
            })
            .Build();
        var client = new FakeLinkClient(connection);
        await connection.StartAsync();
        return client;
    }

    public Task<JsonElement> HelloAsync(JsonElement hello) => Connection.InvokeAsync<JsonElement>("Hello", hello);

    public Task<JsonElement> EnvelopeAsync(JsonElement envelope) => Connection.InvokeAsync<JsonElement>("Envelope", envelope);

    public Task ReceiptAsync(JsonElement receipt) => Connection.InvokeAsync("Receipt", receipt);

    public Task<bool> HeartbeatAsync(JsonElement heartbeat) => Connection.InvokeAsync<bool>("Heartbeat", heartbeat);

    public Task GoodbyeAsync(JsonElement goodbye) => Connection.InvokeAsync("Goodbye", goodbye);

    /// <summary>Next buffered Deliver frame (deliveryId + envelope + deliveredAt).</summary>
    public Task<JsonElement> NextDeliverAsync(int timeoutMs = 10_000) => ReadAsync(_deliver, "Deliver", timeoutMs);

    /// <summary>Next buffered FlowControl frame.</summary>
    public Task<JsonElement> NextFlowAsync(int timeoutMs = 10_000) => ReadAsync(_flow, "FlowControl", timeoutMs);

    /// <summary>Next buffered Disconnect push (goodbye frame).</summary>
    public Task<JsonElement> NextDisconnectAsync(int timeoutMs = 10_000) => ReadAsync(_disconnect, "Disconnect", timeoutMs);

    /// <summary>True when a Disconnect arrives within the window (no throw on absence).</summary>
    public async Task<bool> TryDisconnectAsync(int timeoutMs = 5_000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            await _disconnect.Reader.ReadAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Assert the channel drains nothing within the window (negative proofs).</summary>
    public async Task AssertNoFrameAsync(Channel<JsonElement> channel, string what, int timeoutMs = 2_000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            var frame = await channel.Reader.ReadAsync(timeout.Token);
            Assert.Fail($"unexpected {what} frame: {frame.GetRawText()}");
        }
        catch (OperationCanceledException)
        {
            // the absence we came to prove
        }
    }

    public Task AssertNoDeliverAsync(int timeoutMs = 2_000) => AssertNoFrameAsync(_deliver, "Deliver", timeoutMs);

    private static async Task<JsonElement> ReadAsync(Channel<JsonElement> channel, string what, int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            return await channel.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"no {what} frame within {timeoutMs} ms");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Connection.DisposeAsync();
        }
        catch (Exception)
        {
            // socket teardown races are irrelevant to assertions
        }
    }
}

/// <summary>
/// Hand-rolled fake clock (no extra package): every lease decision in the
/// kernel reads TimeProvider, so advancing this moves session leases,
/// in-flight deadlines, and the pump sweeper without wall-clock sleeps.
/// </summary>
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _offsetTicks) + start.UtcTicks, TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);
}
