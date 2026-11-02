using System.Security.Claims;
using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Link;

/// <summary>
/// The Workspace Link hub: thin transport, no work execution. Server methods
/// <c>Hello</c>/<c>Envelope</c>/<c>Receipt</c>/<c>Heartbeat</c>/<c>Goodbye</c>;
/// client methods <c>Welcome</c> (Hello's return value), <c>Deliver</c>,
/// <c>FlowControl</c>, <c>Disconnect</c>. Authoritative liveness
/// is the <c>link_sessions</c> lease, not socket state; this replica's
/// registry only knows its own sockets. Bearer auth runs through the
/// <c>facilitator-link</c> scheme, which shares the
/// <see cref="ILinkTokenValidator"/> seam with the HTTP surface.
/// </summary>
[Authorize(AuthenticationSchemes = LinkBearerHandler.SchemeName)]
public sealed class LinkHub(
    LinkService sessions,
    LinkConnectionRegistry registry,
    LinkReceipts receipts,
    LinkIngress ingress,
    LinkDeliveryPump pump,
    ControlFrameValidator frames,
    LinkOptions options,
    ILogger<LinkHub> logger) : Hub
{
    /// <summary>Claim types minted by <see cref="LinkBearerHandler"/>.</summary>
    public const string TeamClaim = "facilitator.team";

    /// <summary>Claim type carrying the declared trust mode.</summary>
    public const string TrustModeClaim = "facilitator.trust_mode";

    /// <summary>Claim type carrying the enrollment-bound workload role.</summary>
    public const string RoleClaim = "facilitator.role";

    /// <summary>Claim type carrying the team slug (minted by the enrollment-time validator join).</summary>
    public const string TeamSlugClaim = "facilitator.team_slug";

    /// <summary>
    /// Handshake: validate the hello frame, intersect
    /// protocolVersions with the server's <c>["v1"]</c> — empty intersection
    /// mints a session id WITHOUT a link_sessions row and returns null after
    /// pushing the frozen goodbye frame — else bump the epoch (superseding +
    /// notifying any live connection), lease now + heartbeatSeconds*3, reply
    /// welcome, then immediately drain pending deliveries from the DB ledger
    /// (the client's lastKnownAck is a hint, logged, never trusted).
    /// </summary>
    public async Task<WelcomeFrame?> Hello(JsonElement hello)
    {
        var principal = RequirePrincipal();
        var verdict = frames.ValidateControlFrame(hello);
        if (!verdict.Ok)
        {
            throw new HubException($"hello frame rejected: {verdict.Message}");
        }

        var endpointId = hello.GetProperty("endpointId").GetString()!;
        if (!string.Equals(endpointId, principal.EndpointId, StringComparison.Ordinal))
        {
            throw new HubException($"hello endpointId '{endpointId}' does not match the authenticated principal");
        }

        var offered = hello.GetProperty("protocolVersions").EnumerateArray()
            .Select(v => v.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var negotiated = LinkOptions.SupportedProtocolVersions.FirstOrDefault(offered.Contains);
        var instanceId = hello.GetProperty("instanceId").GetString()!;
        var lastKnownAck = hello.TryGetProperty("lastKnownAck", out var lka) && lka.ValueKind == JsonValueKind.String
            ? lka.GetString()
            : null;

        // TrustMode MUST be logged at every reconnect.
        logger.LogInformation(
            "link hello endpoint={EndpointId} instance={InstanceId} trustMode={TrustMode} lastKnownAck={LastKnownAck} (hint only)",
            endpointId, instanceId, principal.TrustMode, lastKnownAck ?? "-");

        using var span = KernelActivity.Source.StartActivity("link.handshake");
        span?.SetTag("facilitator.endpoint_id", endpointId);

        if (negotiated is null)
        {
            // v9-only client: mint an ephemeral session id, no row, typed goodbye.
            var ephemeral = Guid.NewGuid().ToString("N");
            span?.SetTag("facilitator.protocol_version", string.Empty);
            await Clients.Caller.SendAsync("Disconnect",
                new GoodbyeFrame("goodbye", ephemeral, "no-compatible-protocol-version"), Context.ConnectionAborted);
            return null;
        }

        var digest = hello.TryGetProperty("capabilityManifestDigest", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
        var epoch = await sessions.OpenSessionAsync(endpointId, instanceId, digest, options.HeartbeatSeconds, Context.ConnectionAborted);

        // Supersede: a live connection on THIS replica for the same endpoint
        // (older epoch) gets the goodbye; the DB epoch bump fences every other
        // replica's socket regardless.
        foreach (var stale in registry.ForEndpoint(endpointId))
        {
            await Clients.Client(stale.ConnectionId).SendAsync("Disconnect",
                new GoodbyeFrame("goodbye", stale.ConnectionId, "session-superseded"), CancellationToken.None);
            registry.Unregister(stale.ConnectionId);
        }

        registry.Register(new LinkConnection(Context.ConnectionId, endpointId, epoch, principal.TrustMode));
        span?.SetTag("facilitator.session_epoch", epoch);
        span?.SetTag("facilitator.protocol_version", negotiated);

        // Pending deliveries come from the ledger, immediately.
        await pump.PumpEndpointAsync(endpointId, options.MaxInFlight, Context.ConnectionAborted);
        registry.Wake(endpointId);

        return new WelcomeFrame("welcome", Guid.NewGuid().ToString("N"), epoch, options.MaxInFlight, options.HeartbeatSeconds, negotiated);
    }

    /// <summary>Link-originated CloudEvent — the SAME admission path as POST /api/link/v1/events.</summary>
    public async Task<EnvelopeAck> Envelope(JsonElement envelope)
    {
        var principal = RequirePrincipal();
        var admission = await ingress.AdmitAsync(envelope.GetRawText(), principal, Context.ConnectionAborted);
        return admission.Problem is not null
            ? new EnvelopeAck("rejected", null, admission.Problem)
            : new EnvelopeAck(admission.Status!, admission.InboxId!.Value.ToString("N"), Data: admission.Data);
    }

    /// <summary>
    /// Receipt for a delivered envelope: the ledger is truth and
    /// <see cref="LinkReceipts.CommitAsync"/> is the single commit path shared
    /// with the HTTP receipt endpoint.
    /// </summary>
    public async Task Receipt(JsonElement receipt)
    {
        var principal = RequirePrincipal();
        var verdict = frames.ValidateReceipt(receipt);
        if (!verdict.Ok)
        {
            throw new HubException($"receipt frame rejected: {verdict.Message}");
        }

        if (!Guid.TryParse(receipt.GetProperty("deliveryId").GetString(), out var deliveryId))
        {
            throw new HubException("deliveryId must be the delivery's uuid");
        }

        if (!await receipts.CommitAsync(principal, deliveryId, receipt.GetRawText(), Context.ConnectionAborted))
        {
            throw new HubException($"delivery {deliveryId} not found for endpoint {principal.EndpointId}");
        }
    }

    /// <summary>Refresh the lease for a matching (endpoint, leaseEpoch); stale ⇒ goodbye + false.</summary>
    public async Task<bool> Heartbeat(JsonElement heartbeat)
    {
        var principal = RequirePrincipal();
        var verdict = frames.ValidateControlFrame(heartbeat);
        if (!verdict.Ok)
        {
            throw new HubException($"heartbeat frame rejected: {verdict.Message}");
        }

        var epoch = heartbeat.GetProperty("leaseEpoch").GetInt64();
        var refreshed = await sessions.TouchSessionAsync(principal.EndpointId, epoch, options.HeartbeatSeconds, Context.ConnectionAborted);
        if (!refreshed)
        {
            await Clients.Caller.SendAsync("Disconnect",
                new GoodbyeFrame("goodbye", heartbeat.GetProperty("sessionId").GetString() ?? string.Empty, "session-superseded"),
                Context.ConnectionAborted);
            return false;
        }

        return true;
    }

    /// <summary>Client-initiated goodbye: revokes the session row, acks nothing.</summary>
    public async Task Goodbye(JsonElement goodbye)
    {
        var principal = RequirePrincipal();
        var verdict = frames.ValidateControlFrame(goodbye);
        if (!verdict.Ok)
        {
            throw new HubException($"goodbye frame rejected: {verdict.Message}");
        }

        var live = await sessions.TryGetLiveAsync(principal.EndpointId, Context.ConnectionAborted);
        if (live is not null)
        {
            await sessions.CloseSessionAsync(principal.EndpointId, live.SessionEpoch, Context.ConnectionAborted);
        }

        registry.Unregister(Context.ConnectionId);
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Unregister(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private LinkPrincipal RequirePrincipal()
    {
        var user = Context.User ?? throw new HubException("unauthenticated hub connection");
        var endpointId = user.FindFirstValue(ClaimTypes.Name) ?? throw new HubException("missing endpoint identity");
        var team = Guid.Parse(user.FindFirstValue(TeamClaim) ?? throw new HubException("missing team claim"));
        var trustMode = user.FindFirstValue(TrustModeClaim) ?? throw new HubException("missing trust mode claim");
        var role = user.FindFirstValue(RoleClaim) ?? throw new HubException("missing role claim");
        return new LinkPrincipal(endpointId, team,
            user.FindFirstValue(TeamSlugClaim) ?? throw new HubException("missing team slug claim"), trustMode, role);
    }
}
