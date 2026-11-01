using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Dispatch;

/// <summary>
/// Durable-queue trace law (extract-or-fresh): the dispatcher parents the
/// <c>outbox.dispatch</c> span on the stored envelope's W3C traceparent when
/// present, and when absent it stamps a freshly started trace into the link
/// envelope AND persists it, so retry/restart replays re-extract the same
/// trace and worker-emitted events are causally linked to the facilitator.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class TracePropagationTests(DispatchDbFixture fixture)
{
    private static readonly Regex W3C = new("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", RegexOptions.Compiled);

    /// <summary>Link-channel sink: accepts everything, remembers the delivered envelope.</summary>
    private sealed class CapturingLinkSink : ISink
    {
        public string Channel => "link";

        public List<string> Delivered { get; } = [];

        public Task<bool> DeliverAsync(OutboxRow row, CancellationToken ct)
        {
            Delivered.Add(row.Envelope.GetRawText());
            return Task.FromResult(true);
        }
    }

    /// <summary>Scoped collector for every kernel activity started while alive.</summary>
    private sealed class SpanCollector : IDisposable
    {
        private readonly ActivityListener _listener;

        public List<Activity> Spans { get; } = [];

        public SpanCollector()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == KernelActivity.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            };
            _listener.ActivityStopped += Spans.Add;
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task UntracedAssign_StampedIntoStoredEnvelopeAndSpanTrace()
    {
        var sink = new CapturingLinkSink();
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, _ => sink);
        using var client = factory.CreateAuthedClient();
        var endpoint = "gw-tp-" + Guid.NewGuid().ToString("N")[..8];
        var envelope = LinkTestHarness.AssignEnvelopeFor(endpoint);
        var commandId = JsonDocument.Parse(envelope).RootElement.GetProperty("data").GetProperty("commandId").GetString()!;
        await LinkTestHarness.QueueLinkOutboxAsync(client, endpoint, envelope);
        await fixture.Db.ExpireLinkLeases();

        using var collector = new SpanCollector();
        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();
        await dispatcher.DrainOnceAsync(CancellationToken.None);

        var storedText = await fixture.Db.OutboxEnvelopeByCommand(commandId);
        Assert.NotNull(storedText);
        var stored = JsonDocument.Parse(storedText);
        var stamped = stored.RootElement.GetProperty("traceparent").GetString()!;
        Assert.Matches(W3C, stamped);
        // The stamped envelope stays schema-conformant (envelope.json permits traceparent).
        KernelOps.AssertEnvelopesValid([stored.RootElement.GetRawText()]);
        // The sink received the stamped copy (in-memory, not the pre-stamp read).
        // Only OUR row: parallel collections can land foreign link-channel
        // outbox rows inside this manual drain window (shared container DB).
        var delivered = JsonDocument.Parse(Assert.Single(sink.Delivered, d => d.Contains(commandId)));
        Assert.Equal(stamped, delivered.RootElement.GetProperty("traceparent").GetString());
        // Delivered, and the delivery's own span carries the stamped trace.
        Assert.NotNull(await fixture.Db.OutboxIdByCommand(commandId));
        Assert.Contains(collector.Spans, s => s.OperationName == "outbox.dispatch" && s.TraceId.ToHexString() == stamped.Split('-')[1]);
    }

    [Fact]
    public async Task TracedAssign_SpanIsChildAndEnvelopeUnchanged()
    {
        var sink = new CapturingLinkSink();
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, _ => sink);
        using var client = factory.CreateAuthedClient();
        var endpoint = "gw-tp-" + Guid.NewGuid().ToString("N")[..8];
        var root = System.Text.Json.Nodes.JsonNode.Parse(LinkTestHarness.AssignEnvelopeFor(endpoint))!.AsObject();
        var traceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var spanId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var seeded = $"00-{traceId}-{spanId}-01";
        root["traceparent"] = seeded;
        var envelope = root.ToJsonString();
        var commandId = root["data"]!.AsObject()["commandId"]!.GetValue<string>();
        await LinkTestHarness.QueueLinkOutboxAsync(client, endpoint, envelope);
        await fixture.Db.ExpireLinkLeases();

        using var collector = new SpanCollector();
        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();
        await dispatcher.DrainOnceAsync(CancellationToken.None);

        // Extract law: the dispatch span re-parents onto the stored carrier.
        Assert.Contains(collector.Spans, s => s.OperationName == "outbox.dispatch" && s.ParentId == seeded);
        // No re-stamp: the envelope the sink saw and the one stored both keep
        // the original carrier verbatim (replay stability).
        var storedText2 = await fixture.Db.OutboxEnvelopeByCommand(commandId);
        Assert.NotNull(storedText2);
        var stored = JsonDocument.Parse(storedText2);
        Assert.Equal(seeded, stored.RootElement.GetProperty("traceparent").GetString());
        Assert.Equal(seeded, JsonDocument.Parse(Assert.Single(sink.Delivered, d => d.Contains(commandId))).RootElement.GetProperty("traceparent").GetString());
    }

    [Fact]
    public async Task WorkCreate_CarriesTraceparentResponseHeader()
    {
        using var factory = new KernelTestFactory(fixture);
        using var client = factory.CreateAuthedClient();
        using var response = await client.PostAsync("/api/works", new StringContent("""{"objective":"trace header"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode);
        Assert.Matches(W3C, Assert.Single(response.Headers.TryGetValues("traceparent", out var values) ? values : []));
    }
}
