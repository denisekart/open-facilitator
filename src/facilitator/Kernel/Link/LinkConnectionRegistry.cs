using System.Threading.Channels;

using Facilitator.Kernel.Shared;

/// <summary>One live hub connection on THIS replica (§7.3: a replica owns only its own sockets).</summary>
/// <param name="ConnectionId">SignalR connection id.</param>
/// <param name="EndpointId">Authenticated logical endpoint.</param>
/// <param name="SessionEpoch">Epoch the handshake minted.</param>
/// <param name="TrustMode">Declared trust mode (logged per Hello, appendix §4).</param>
public sealed record LinkConnection(string ConnectionId, string EndpointId, long SessionEpoch, string TrustMode);

/// <summary>
/// Connection-scoped registry of live hub sessions on this replica. The
/// delivery pump pushes <c>Deliver</c>/<c>FlowControl</c>/<c>Disconnect</c>
/// through it; the HTTP long-poll waits on its per-endpoint wake channels.
/// Authoritative liveness stays in the DB lease — the sweeper closes entries
/// whose <c>link_sessions.lease_until</c> passed.
/// </summary>
public sealed class LinkConnectionRegistry(IConfiguration configuration)
{
    /// <summary>This replica's lease identity (the same value session rows
    /// carry; the admin replicas view answers "who am I").</summary>
    public string ReplicaId { get; } = KernelOptions.ReplicaId(configuration);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LinkConnection> _byConnection = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Channel<long>> _wakes = new(StringComparer.Ordinal);

    /// <summary>Register (or replace) the connection tracked for an endpoint's new session.</summary>
    public void Register(LinkConnection connection) => _byConnection[connection.ConnectionId] = connection;

    /// <summary>Forget a connection (close/disconnect path); the wake channel survives.</summary>
    public void Unregister(string connectionId) => _byConnection.TryRemove(connectionId, out _);

    /// <summary>All connections of an endpoint tracked on this replica (typically one).</summary>
    public IReadOnlyList<LinkConnection> ForEndpoint(string endpointId) =>
        _byConnection.Values.Where(c => c.EndpointId == endpointId).ToArray();

    /// <summary>Distinct endpoints with a tracked connection on this replica.</summary>
    public IReadOnlyList<string> ForAllEndpoints() =>
        _byConnection.Values.Select(c => c.EndpointId).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>Signal the HTTP long-pollers of an endpoint that deliveries were materialized.</summary>
    public void Wake(string endpointId) => WakeChannel(endpointId).Writer.TryWrite(Environment.TickCount64);

    /// <summary>
    /// Wait until <paramref name="probe"/> reports work or <paramref name="wait"/>
    /// elapses; wakes come from <see cref="Wake"/> plus a 1 s polling safety net
    /// (NOTIFY is a hint, §7.3 — correctness never depends on it).
    /// </summary>
    public async Task WaitForWakeAsync(string endpointId, TimeSpan wait, Func<Task<bool>> probe, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        var channel = WakeChannel(endpointId);

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (await probe())
            {
                return;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            var budget = remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
            if (budget <= TimeSpan.Zero)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(budget);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await channel.Reader.ReadAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                // poll tick or deadline — loop re-probes
            }

            while (channel.Reader.TryRead(out _))
            {
                // coalesce bursts
            }
        }
    }

    private Channel<long> WakeChannel(string endpointId) =>
        _wakes.GetOrAdd(endpointId, _ => Channel.CreateUnbounded<long>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        }));
}
