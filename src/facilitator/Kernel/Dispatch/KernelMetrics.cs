using System.Diagnostics.Metrics;

namespace Facilitator.Kernel.Dispatch;

/// <summary>
/// The <c>facilitator</c> meter's instruments (appendix §9.1). Queue-depth
/// gauges serve from a 5-second cache refreshed by the dispatcher loop, so
/// observable callbacks never hit the database synchronously.
/// </summary>
public sealed class KernelMetrics
{
    /// <summary>Meter name (appendix §9.1).</summary>
    public const string MeterName = "facilitator";

    private readonly Counter<long> _deliveryAttempts;
    private readonly Counter<long> _inboxProcessed;
    private readonly Counter<long> _linkReceived;
    private readonly Histogram<double> _deliveryLatency;
    private readonly Histogram<double> _ingressLatency;
    private long _outboxDepth;
    private long _inboxDepth;
    private long _deadLetters;
    private long _linkSessions;

    /// <summary>Create the meter and all instruments.</summary>
    public KernelMetrics()
    {
        var meter = new Meter(MeterName);
        _deliveryAttempts = meter.CreateCounter<long>("facilitator.delivery.attempts", description: "Outbox delivery attempts");
        _inboxProcessed = meter.CreateCounter<long>("facilitator.inbox.processed", description: "Inbox rows processed");
        _deliveryLatency = meter.CreateHistogram<double>("facilitator.delivery.latency", unit: "ms", description: "Sink call latency");
        _ingressLatency = meter.CreateHistogram<double>("facilitator.ingress.processing.latency", unit: "ms", description: "Inline ingress processing latency");
        meter.CreateObservableGauge("facilitator.outbox.queue_depth", () => Volatile.Read(ref _outboxDepth), description: "Pending outbox rows");
        meter.CreateObservableGauge("facilitator.inbox.queue_depth", () => Volatile.Read(ref _inboxDepth), description: "Pending inbox rows");
        meter.CreateObservableGauge("facilitator.delivery.dead_letter_count", () => Volatile.Read(ref _deadLetters), description: "Dead-lettered rows");
        meter.CreateObservableGauge("facilitator.link.sessions", () => Volatile.Read(ref _linkSessions), description: "Live link sessions (appendix §9.1)");
        _linkReceived = meter.CreateCounter<long>("facilitator.link.received", description: "Envelopes admitted over the link surface");
    }

    /// <summary>Record one charged delivery attempt.</summary>
    public void DeliveryAttempt() => _deliveryAttempts.Add(1);

    /// <summary>Record one processed inbox row.</summary>
    public void InboxProcessed() => _inboxProcessed.Add(1);

    /// <summary>Record sink-call latency in milliseconds.</summary>
    public void DeliveryLatency(double ms) => _deliveryLatency.Record(ms);

    /// <summary>Record one envelope admitted over the link surface (appendix §9.1).</summary>
    public void LinkReceived() => _linkReceived.Add(1);

    /// <summary>Refresh the cached live-link-session gauge (served without hitting the DB).</summary>
    public void SetLinkSessions(long live) => Volatile.Write(ref _linkSessions, live);

    /// <summary>Record inline ingress processing latency in milliseconds.</summary>
    public void IngressLatency(double ms) => _ingressLatency.Record(ms);

    /// <summary>Refresh the cached queue-depth/dead-letter gauges.</summary>
    public void SetGauges(long outboxDepth, long inboxDepth, long deadLetters)
    {
        Volatile.Write(ref _outboxDepth, outboxDepth);
        Volatile.Write(ref _inboxDepth, inboxDepth);
        Volatile.Write(ref _deadLetters, deadLetters);
    }
}
