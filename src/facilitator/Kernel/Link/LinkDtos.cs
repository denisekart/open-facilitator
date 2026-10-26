using System.Text.Json;
using System.Text.Json.Serialization;

namespace Facilitator.Kernel.Link;

/// <summary>
/// One durable delivery — the SAME JSON object on the SignalR <c>Deliver</c>
/// client method and the HTTP <c>GET /deliveries</c> items (OpenAPI
/// <c>Delivery</c> component / link-delivery.v1.json). Receipts reference the
/// durable deliveryId, never the socket (spec §5.2).
/// </summary>
/// <param name="DeliveryId">Delivery identity = the outbox row id.</param>
/// <param name="Envelope">The full CloudEvent envelope.</param>
/// <param name="DeliveredAt">Offer time (UTC).</param>
public sealed record DeliveryDto(Guid DeliveryId, JsonElement Envelope, DateTime DeliveredAt);

/// <summary>Wire shape of POST /api/link/v1/enroll (enroll-request.v1.json).</summary>
/// <param name="Token">Mode (a): bootstrap enrollment secret. Mode (b): an OIDC client-credentials JWT.</param>
/// <param name="EndpointId">Logical endpoint identity.</param>
/// <param name="InstanceId">This process instance (changes on restart).</param>
/// <param name="TrustMode">Declared trust mode (immutable for the session).</param>
/// <param name="Role">gateway | child (mode (a) hint; must not contradict a mode-(b) binding).</param>
/// <param name="Team">Team roster slug to bind (mode (b): binding wins).</param>
public sealed record EnrollRequestDto(string Token, string EndpointId, string InstanceId, string TrustMode, string? Role, string? Team);

/// <summary>
/// Wire shape of the 201 enroll response (enroll-response.v1.json; the token
/// fields are the OpenAPI 1.0.1 additive properties — this server ALWAYS
/// returns both).
/// </summary>
/// <param name="EnrollmentId">Enrollment record identity.</param>
/// <param name="TrustMode">Echoed trust mode.</param>
/// <param name="linkToken">Opaque 256-bit base64url bearer credential.</param>
/// <param name="ExpiresAt">Token expiry (UTC).</param>
public sealed record EnrollResponseDto(
    string EnrollmentId,
    string TrustMode,
    [property: JsonPropertyName("linkToken")] string LinkToken,
    DateTime ExpiresAt);

/// <summary>Wire shape of GET /api/dead-letters (appendix §5.6 visibility).</summary>
/// <param name="DeadLetterId">Dead-letter row identity.</param>
/// <param name="OutboxId">Dead-lettered outbox row.</param>
/// <param name="Reason">Why the row dead-lettered.</param>
/// <param name="Envelope">Original CloudEvent snapshot.</param>
/// <param name="CreatedAt">Dead-letter time (UTC).</param>
public sealed record DeadLetterDto(Guid DeadLetterId, Guid OutboxId, string Reason, JsonElement Envelope, DateTime CreatedAt);

/// <summary>Server→client <c>welcome</c> frame (control.v1.json, exact fields).</summary>
public sealed record WelcomeFrame(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("leaseEpoch")] long LeaseEpoch,
    [property: JsonPropertyName("maxInFlight")] int MaxInFlight,
    [property: JsonPropertyName("heartbeatSeconds")] int HeartbeatSeconds,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion);

/// <summary>Server→client <c>goodbye</c> frame (Disconnect client-method payload).</summary>
public sealed record GoodbyeFrame(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>Server→client <c>flow_control</c> frame (pause = maxInFlight 0).</summary>
public sealed record FlowControlFrame(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("maxInFlight")] int MaxInFlight);

/// <summary>Result of the hub Envelope method (mirrors the HTTP /events codes).</summary>
/// <param name="Status">accepted | duplicate | rejected.</param>
/// <param name="DeliveryId">Inbox row id (identity the receipt would reference), when admitted.</param>
/// <param name="Problem">Typed problem document when refused (422/400/413 equivalents).</param>
public sealed record EnvelopeAck(string Status, string? DeliveryId = null, Microsoft.AspNetCore.Mvc.ProblemDetails? Problem = null);
