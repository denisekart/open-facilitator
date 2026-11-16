using System.Text.Json;
using System.Text.Json.Serialization;

namespace Facilitator.Kernel.Link;

/// <summary>
/// One durable delivery — the SAME JSON object on the SignalR <c>Deliver</c>
/// client method and the HTTP <c>GET /deliveries</c> items (OpenAPI
/// <c>Delivery</c> component / link-delivery.v1.json). Receipts reference the
/// durable deliveryId, never the socket.
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
/// <param name="PublicKey">Mode (a): raw Ed25519 public key (base64url) — the
/// endpoint's identity; mode (b) ignores it (identity stays the issuer trust).</param>
/// <param name="KeyAlgorithm">Mode (a): must be <c>ed25519</c>.</param>
/// <param name="Contributor">Bind the enrollment to this contributor handle (K9):
/// the workload then speaks AS the named non-human actor; several enrollments
/// may share one contributor. The contributor must exist on the bound team.</param>
public sealed record EnrollRequestDto(string Token, string EndpointId, string InstanceId, string TrustMode, string? PublicKey, string? KeyAlgorithm, string? Role, string? Team, string? Contributor);

/// <summary>
/// Wire shape of the 201 enroll response (enroll-response.v1.json): identity,
/// not secrets — the key fingerprint and the replay fence, nothing to steal.
/// </summary>
/// <param name="EnrollmentId">Enrollment record identity.</param>
/// <param name="TrustMode">Echoed trust mode.</param>
/// <param name="EndpointKeyId">sha256: fingerprint of the registered identity.</param>
/// <param name="Seq">Replay fence the endpoint starts from.</param>
public sealed record EnrollResponseDto(
    string EnrollmentId,
    string TrustMode,
    [property: JsonPropertyName("endpointKeyId")] string EndpointKeyId,
    [property: JsonPropertyName("seq")] long Seq);

/// <summary>Server→client <c>welcome</c> frame (control.v1.json, exact fields).</summary>
public sealed record WelcomeFrame(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("leaseEpoch")] long LeaseEpoch,
    [property: JsonPropertyName("maxInFlight")] int MaxInFlight,
    [property: JsonPropertyName("heartbeatSeconds")] int HeartbeatSeconds,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion,
    [property: JsonPropertyName("sessionKeyId")] string? SessionKeyId = null,
    [property: JsonPropertyName("seq")] long? Seq = null);

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
/// <param name="Data">In-band command payload (e.g. an upload ticket), additive/optional; carried on accepted AND duplicate acks when the command produced one.</param>
public sealed record EnvelopeAck(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("deliveryId")] string? DeliveryId = null,
    [property: JsonPropertyName("problem")] Microsoft.AspNetCore.Mvc.ProblemDetails? Problem = null,
    [property: JsonPropertyName("data")] object? Data = null);
