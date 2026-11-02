using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.SignalR;

namespace Facilitator.Kernel.Link;

/// <summary>
/// Materializes link-addressed outbox rows into the ledger and pushes
/// <c>Deliver</c> frames to this replica's live sockets. Runs: after every
/// Hello / receipt (inline <see cref="PumpEndpointAsync"/>), on every
/// <c>NOTIFY facilitator_outbox</c>, on a 2 s safety poll (NOTIFY is a hint).
/// The claim phase only runs with a tracked socket; the HTTP fallback
/// principal claims through GET /deliveries after <see cref="MaterializeForClaimAsync"/>.
/// Also the lease sweeper: sessions whose <c>lease_until</c> passed get their
/// tracked connection closed with a
/// <c>Disconnect</c> (lease expiry, not TCP, ends a session) — every
/// lease decision reads the clock via <see cref="TimeProvider"/> inside
/// <see cref="LinkService"/>, so fake clocks drive the sweeper in tests.
/// </summary>
public sealed class LinkDeliveryPump(
    LinkLedger ledger,
    LinkService sessions,
    LinkConnectionRegistry registry,
    IHubContext<LinkHub> hub,
    PgWakeListener wakeListener,
    KernelMetrics metrics,
    LinkOptions options,
    ILogger<LinkDeliveryPump> logger) : BackgroundService
{
    private static readonly TimeSpan SafetyPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GaugeCache = TimeSpan.FromSeconds(5);

    // Mutated concurrently from hub Hello/Receipt, HTTP receipt/claim, the
    // pump loop, and LinkSink.DeliverAsync — hence a concurrent dictionary.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _paused = new(StringComparer.Ordinal);
    private long _lastGaugeRefreshTicks = -1;

    /// <summary>
    /// One pump pass for an endpoint: materialize, and — only when a socket
    /// is tracked on THIS replica — claim within the window
    /// (maxInFlight − inFlight), push Deliver frames, and emit the advisory
    /// FlowControl pause when the window is now full (resume on the next pass
    /// that finds capacity). Without a tracked socket the pass is
    /// materialize-only: a claim would strand rows in_flight with nobody to
    /// deliver them, and an HTTP-only principal claims through
    /// GET /deliveries, which performs the claim itself after this inline
    /// materialization.
    /// </summary>
    public async Task PumpEndpointAsync(string endpointId, int maxInFlight, CancellationToken ct)
    {
        var session = await sessions.TryGetLiveAsync(endpointId, ct);
        if (session is null)
        {
            return;
        }

        await ledger.MaterializeAsync(endpointId, ct);
        var connections = registry.ForEndpoint(endpointId);
        if (connections.Count == 0)
        {
            await RefreshSessionGaugeAsync(ct);
            return;
        }

        var inFlight = await ledger.CountInFlightAsync(endpointId, ct);
        var capacity = maxInFlight - inFlight;
        if (capacity > 0 && _paused.TryRemove(endpointId, out _))
        {
            await FlowControlAsync(endpointId, maxInFlight, connections, ct); // resume hint
        }

        if (capacity > 0)
        {
            var claimed = await ledger.ClaimAsync(endpointId, session.SessionEpoch, capacity, maxInFlight, ct);
            foreach (var delivery in claimed)
            {
                foreach (var connection in connections)
                {
                    await hub.Clients.Client(connection.ConnectionId).SendAsync("Deliver", delivery.Delivery, ct);
                }
            }

            inFlight = await ledger.CountInFlightAsync(endpointId, ct);
        }

        if (inFlight >= maxInFlight && connections.Count > 0 && _paused.TryAdd(endpointId, 0))
        {
            await FlowControlAsync(endpointId, 0, connections, ct); // pause hint (frozen schema allows 0)
        }

        await RefreshSessionGaugeAsync(ct);
    }

    /// <summary>
    /// Inline materialization for the HTTP pre-claim path: link-addressed
    /// outbox rows for this endpoint enter the ledger; the calling endpoint
    /// performs the claim itself (an HTTP principal has no tracked socket,
    /// and the pump never claims without one).
    /// </summary>
    public Task MaterializeForClaimAsync(string endpointId, CancellationToken ct) =>
        ledger.MaterializeAsync(endpointId, ct);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var wake = new SemaphoreSlim(0, 1);
        var listenerTask = wakeListener.ListenAsync(wake, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
                await PumpAllAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "link pump pass failed; continuing to poll");
            }

            try
            {
                await wake.WaitAsync(SafetyPoll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        try
        {
            await listenerTask;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "link pump LISTEN loop ended");
        }
    }

    private async Task FlowControlAsync(string endpointId, int maxInFlight, IReadOnlyList<LinkConnection> connections, CancellationToken ct)
    {
        foreach (var connection in connections)
        {
            await hub.Clients.Client(connection.ConnectionId).SendAsync(
                "FlowControl", new FlowControlFrame("flow_control", connection.ConnectionId, maxInFlight), ct);
        }
    }

    private async Task PumpAllAsync(CancellationToken ct)
    {
        await ledger.MaterializeAsync(ct);
        foreach (var endpointId in registry.ForAllEndpoints())
        {
            await PumpEndpointAsync(endpointId, options.MaxInFlight, ct);
        }

        await RefreshSessionGaugeAsync(ct);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        if (!options.LeaseSweeperEnabled)
        {
            return;
        }

        var expired = await sessions.SweepExpiredAsync(ct);
        foreach (var session in expired)
        {
            foreach (var connection in registry.ForEndpoint(session.EndpointId))
            {
                await hub.Clients.Client(connection.ConnectionId).SendAsync("Disconnect",
                    new GoodbyeFrame("goodbye", connection.ConnectionId, "lease-expired"), CancellationToken.None);
                registry.Unregister(connection.ConnectionId);
            }

            _paused.TryRemove(session.EndpointId, out _);
        }
    }

    /// <summary>
    /// Refresh the live-session gauge from a 5 s cache so a pump pass never
    /// pays a COUNT per endpoint (same pattern as the dispatcher's gauges).
    /// </summary>
    private async Task RefreshSessionGaugeAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var last = Interlocked.Read(ref _lastGaugeRefreshTicks);
        if (last >= 0 && now - new DateTime(last, DateTimeKind.Utc) < GaugeCache)
        {
            return;
        }

        Interlocked.Exchange(ref _lastGaugeRefreshTicks, now.Ticks);
        metrics.SetLinkSessions(await sessions.CountLiveAsync(ct));
    }
}
