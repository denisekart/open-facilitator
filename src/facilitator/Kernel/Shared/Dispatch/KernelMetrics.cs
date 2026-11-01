using System.Diagnostics.Metrics;

namespace Facilitator.Kernel.Shared.Dispatch;

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
    private readonly Counter<long> _identityDecisions;
    private readonly Counter<long> _linkReceived;
    private readonly Counter<long> _linkEvidence;
    private readonly Counter<long> _artifactAllocated;
    private readonly Counter<long> _artifactStored;
    private readonly Counter<long> _artifactRegistered;
    private readonly Counter<long> _artifactRejected;
    private readonly Counter<long> _artifactConfirmed;
    private readonly Counter<long> _artifactPresigned;
    private readonly Counter<long> _artifactGcCollected;
    private readonly Counter<long> _artifactStoreErrors;
    private readonly Counter<long> _manifestRecorded;
    private readonly Counter<long> _manifestSuperseded;
    private readonly Counter<long> _manifestDigestMismatch;
    private readonly Counter<long> _memoryRetained;
    private readonly Counter<long> _memoryRecalled;
    private readonly Counter<long> _memoryUsed;
    private readonly Counter<long> _memoryPromoted;
    private readonly Counter<long> _memoryDenied;
    private readonly Histogram<double> _memoryRecallLatency;
    private readonly Counter<long> _provisioningOps;
    private readonly Counter<long> _sessionLoss;
    private readonly Histogram<double> _deliveryLatency;
    private readonly Histogram<double> _ingressLatency;
    private readonly Histogram<double> _artifactStoreLatency;
    private long _outboxDepth;
    private long _inboxDepth;
    private long _deadLetters;
    private long _linkSessions;
    private long _gcLastPassAgeSeconds;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _counterTotals = new(StringComparer.Ordinal);
    private readonly System.Diagnostics.Metrics.MeterListener _counterListener;

    /// <summary>Create the meter and all instruments.</summary>
    public KernelMetrics()
    {
        var meter = new Meter(MeterName);
        // Admin metrics snapshot (Plan 20, §17): an in-process listener
        // accumulates every long counter of this meter, so the roll-up needs
        // no scrape backend (§17: no time-series query).
        _counterListener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, MeterName, StringComparison.Ordinal)
                    && instrument is System.Diagnostics.Metrics.Counter<long>)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _counterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            _counterTotals.AddOrUpdate(instrument.Name, measurement, (_, existing) => existing + measurement));
        _counterListener.Start();
        _deliveryAttempts = meter.CreateCounter<long>("facilitator.delivery.attempts", description: "Outbox delivery attempts");
        _inboxProcessed = meter.CreateCounter<long>("facilitator.inbox.processed", description: "Inbox rows processed");
        _deliveryLatency = meter.CreateHistogram<double>("facilitator.delivery.latency", unit: "ms", description: "Sink call latency");
        _ingressLatency = meter.CreateHistogram<double>("facilitator.ingress.processing.latency", unit: "ms", description: "Inline ingress processing latency");
        meter.CreateObservableGauge("facilitator.outbox.queue_depth", () => Volatile.Read(ref _outboxDepth), description: "Pending outbox rows");
        meter.CreateObservableGauge("facilitator.inbox.queue_depth", () => Volatile.Read(ref _inboxDepth), description: "Pending inbox rows");
        meter.CreateObservableGauge("facilitator.delivery.dead_letter_count", () => Volatile.Read(ref _deadLetters), description: "Dead-lettered rows");
        meter.CreateObservableGauge("facilitator.link.sessions", () => Volatile.Read(ref _linkSessions), description: "Live link sessions (appendix §9.1)");
        _linkReceived = meter.CreateCounter<long>("facilitator.link.received", description: "Envelopes admitted over the link surface");
        _identityDecisions = meter.CreateCounter<long>("facilitator.identity.decisions", description: "Authorization-gate decisions (tag: decision)");
        _linkEvidence = meter.CreateCounter<long>("facilitator.link.evidence", description: "actorEvidenceRef evaluations on link admission (tags: scheme, outcome)");
        _artifactAllocated = meter.CreateCounter<long>("facilitator.artifact.allocated", description: "Upload slots allocated");
        _artifactStored = meter.CreateCounter<long>("facilitator.artifact.stored", description: "Artifacts stored with verified digests");
        _artifactRegistered = meter.CreateCounter<long>("facilitator.artifact.registered", description: "Externally-declared artifacts registered (unverified)");
        _artifactRejected = meter.CreateCounter<long>("facilitator.artifact.rejected", description: "Upload ingest rejections (tag: reason)");
        _artifactConfirmed = meter.CreateCounter<long>("facilitator.artifact.confirmed", description: "Direct-to-store uploads confirmed by kernel re-hash (tag: origin)");
        _artifactPresigned = meter.CreateCounter<long>("facilitator.artifact.presigned", description: "Presigned URLs minted (tag: op)");
        _artifactGcCollected = meter.CreateCounter<long>("facilitator.artifact.gc.collected", description: "Retention-GC collections (tag: reason)");
        _artifactStoreErrors = meter.CreateCounter<long>("facilitator.artifact.store.errors", description: "Backend store errors by operation (tag: op)");
        meter.CreateObservableGauge("facilitator.artifact.gc.last_pass_age", () => Volatile.Read(ref _gcLastPassAgeSeconds), unit: "s", description: "Seconds since the last retention-GC pass");
        _artifactStoreLatency = meter.CreateHistogram<double>("facilitator.artifact.store.latency", unit: "ms", description: "Content put latency");
        _manifestRecorded = meter.CreateCounter<long>("facilitator.manifest.recorded", description: "Result manifests recorded into the ledger (tag: origin)");
        _manifestSuperseded = meter.CreateCounter<long>("facilitator.manifest.superseded", description: "Result-manifest chain heads superseded by a correction");
        _manifestDigestMismatch = meter.CreateCounter<long>("facilitator.manifest.digest_mismatch", description: "Manifest recompute-vs-declared digest rejections");
        _memoryRetained = meter.CreateCounter<long>("facilitator.memory.retained", description: "Entries retained (tags: status, origin)");
        _memoryRecalled = meter.CreateCounter<long>("facilitator.memory.recalled", description: "Recall queries served (tag: truncated)");
        _memoryUsed = meter.CreateCounter<long>("facilitator.memory.used", description: "Usage rows written (auto-recall + attestations)");
        _memoryPromoted = meter.CreateCounter<long>("facilitator.memory.promoted", description: "Candidates promoted to active");
        _memoryDenied = meter.CreateCounter<long>("facilitator.memory.denied", description: "Broker denials (tag: reason)");
        _provisioningOps = meter.CreateCounter<long>("facilitator.provisioning.ops", description: "Provisioning ops appended by the scale-law sweep (tags: operation, reason)");
        _sessionLoss = meter.CreateCounter<long>("facilitator.link.session_loss", description: "Executions failed by the session-loss law (tag: reason)");
        _memoryRecallLatency = meter.CreateHistogram<double>("facilitator.memory.recall.latency", unit: "ms", description: "Recall query latency");
    }

    /// <summary>Record one charged delivery attempt.</summary>
    public void DeliveryAttempt() => _deliveryAttempts.Add(1);

    /// <summary>Record one processed inbox row.</summary>
    public void InboxProcessed() => _inboxProcessed.Add(1);

    /// <summary>Record sink-call latency in milliseconds.</summary>
    public void DeliveryLatency(double ms) => _deliveryLatency.Record(ms);

    /// <summary>Record one envelope admitted over the link surface (appendix §9.1).</summary>
    public void LinkReceived() => _linkReceived.Add(1);

    /// <summary>Record one evidence evaluation on the link plane (tags per appendix §9.1).</summary>
    /// <param name="scheme">Evidence scheme ("" for absent/malformed).</param>
    /// <param name="outcome">workload-only | delegation-validated | evidence-unverified:* | evidence-malformed | delegation-refused | evidence-invalid:*.</param>
    public void LinkEvidence(string scheme, string outcome) => _linkEvidence.Add(1,
        new KeyValuePair<string, object?>("scheme", scheme), new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Record one identity-gate decision (appendix §3.2 step 5).</summary>
    /// <param name="decision">allow | deny.</param>
    public void IdentityDecision(string decision) => _identityDecisions.Add(1, new KeyValuePair<string, object?>("decision", decision));

    /// <summary>Refresh the cached live-link-session gauge (served without hitting the DB).</summary>
    public void SetLinkSessions(long live) => Volatile.Write(ref _linkSessions, live);

    /// <summary>Record inline ingress processing latency in milliseconds.</summary>
    public void IngressLatency(double ms) => _ingressLatency.Record(ms);

    /// <summary>Record one upload-slot allocation.</summary>
    public void ArtifactAllocated() => _artifactAllocated.Add(1);

    /// <summary>Record one digest-verified store.</summary>
    public void ArtifactStored() => _artifactStored.Add(1);

    /// <summary>Record one external (unverified) registration.</summary>
    public void ArtifactRegistered() => _artifactRegistered.Add(1);

    /// <summary>Record one ingest rejection.</summary>
    /// <param name="reason">digest | size | external.</param>
    public void ArtifactRejected(string reason) => _artifactRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Record content put latency in milliseconds.</summary>
    public void ArtifactStoreLatency(double ms) => _artifactStoreLatency.Record(ms);

    /// <summary>Record one direct-to-store upload confirmed by kernel re-hash.</summary>
    /// <param name="origin">link | rest.</param>
    public void ArtifactConfirmed(string origin) => _artifactConfirmed.Add(1, new KeyValuePair<string, object?>("origin", origin));

    /// <summary>Record one minted presigned URL.</summary>
    /// <param name="op">upload | download.</param>
    public void ArtifactPresigned(string op) => _artifactPresigned.Add(1, new KeyValuePair<string, object?>("op", op));

    /// <summary>Record one retention-GC collection.</summary>
    /// <param name="reason">retention | expired-slot.</param>
    public void ArtifactGcCollected(string reason) => _artifactGcCollected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Record a backend store error.</summary>
    /// <param name="op">The failing operation (gc | put | get).</param>
    public void ArtifactStoreErrors(string op) => _artifactStoreErrors.Add(1, new KeyValuePair<string, object?>("op", op));

    /// <summary>Record one manifest recorded into the ledger.</summary>
    /// <param name="origin">link | rest.</param>
    public void ManifestRecorded(string origin) => _manifestRecorded.Add(1, new KeyValuePair<string, object?>("origin", origin));

    /// <summary>Record one chain head superseded by a correction revision.</summary>
    public void ManifestSuperseded() => _manifestSuperseded.Add(1);

    /// <summary>Record one manifest digest-recompute rejection.</summary>
    public void ManifestDigestMismatch() => _manifestDigestMismatch.Add(1);

    /// <summary>Record one retained entry (or dedupe hit).</summary>
    /// <param name="status">candidate | active (the landed status).</param>
    /// <param name="origin">link | rest.</param>
    public void MemoryRetained(string status, string origin) => _memoryRetained.Add(1,
        new KeyValuePair<string, object?>("status", status), new KeyValuePair<string, object?>("origin", origin));

    /// <summary>Record one served recall query.</summary>
    /// <param name="truncated">true | false.</param>
    public void MemoryRecalled(string truncated) => _memoryRecalled.Add(1, new KeyValuePair<string, object?>("truncated", truncated));

    /// <summary>Record one memory_usage row written.</summary>
    public void MemoryUsed() => _memoryUsed.Add(1);

    /// <summary>Record one candidate promotion.</summary>
    public void MemoryPromoted() => _memoryPromoted.Add(1);

    /// <summary>Record one broker denial.</summary>
    /// <param name="reason">Machine reason tag (quota, selector-unresolvable, ...).</param>
    public void MemoryDenied(string reason) => _memoryDenied.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Record recall latency in milliseconds.</summary>
    public void MemoryRecallLatency(double ms) => _memoryRecallLatency.Record(ms);

    /// <summary>Record one provisioning op appended by the sweep (Plan 35 C).</summary>
    /// <param name="operation">start | stop.</param>
    /// <param name="reason">worker-lost | stalled-scaleup | no-capacity | min-workers | idle-shutdown.</param>
    public void ProvisioningOp(string operation, string reason) => _provisioningOps.Add(1,
        new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Record one execution failed by the session-loss law (Plan 35 C).</summary>
    public void SessionLoss(string reason) => _sessionLoss.Add(1,
        new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Refresh the GC last-pass-age gauge (written by the GC loop each pass).</summary>
    public void SetGcLastPassAge(long seconds) => Volatile.Write(ref _gcLastPassAgeSeconds, seconds);

    /// <summary>Refresh the cached queue-depth/dead-letter gauges.</summary>
    public void SetGauges(long outboxDepth, long inboxDepth, long deadLetters)
    {
        Volatile.Write(ref _outboxDepth, outboxDepth);
        Volatile.Write(ref _inboxDepth, inboxDepth);
        Volatile.Write(ref _deadLetters, deadLetters);
    }

    /// <summary>
    /// Process-lifetime counter totals (the admin metrics snapshot, Plan 20),
    /// served from the in-process listener installed in the constructor.
    /// </summary>
    public IReadOnlyDictionary<string, long> CounterTotals() =>
        new System.Collections.Generic.Dictionary<string, long>(_counterTotals, StringComparer.Ordinal);
}
