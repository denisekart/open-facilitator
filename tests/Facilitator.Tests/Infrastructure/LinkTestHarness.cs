using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Facilitator.Kernel.Link;

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

    /// <summary>
    /// Enroll an endpoint (Development bootstrap CODE) with a freshly generated
    /// Ed25519 identity and hand back a session-token provider: the credential
    /// is the key, so a test's "who is this client" answer is a keypair plus the
    /// challenge/proof exchange, never a shared bearer.
    /// </summary>
    internal static Task<LinkEndpointCredential> EnrollAsync(
        HttpClient client, string endpointId, string trustMode = "isolated-executor", string team = "team-alpha",
        string? role = null, FakeTimeProvider? clock = null) =>
        EnrollWithTokenAsync(client, "enroll-dev", endpointId, team, role, trustMode, clock: clock);

    /// <summary>
    /// Enroll with a specific bootstrap credential (a minted enrollment token as
    /// much as the static dev code) under a freshly generated key.
    /// </summary>
    internal static async Task<LinkEndpointCredential> EnrollWithTokenAsync(
        HttpClient client, string bootstrapToken, string endpointId, string team = "team-alpha", string? role = null,
        string trustMode = "isolated-executor", string instanceId = "inst-1", FakeTimeProvider? clock = null,
        (byte[] Seed, string PublicKey)? key = null)
    {
        var (seed, publicKey) = key ?? NewEd25519();
        var body = JsonSerializer.Serialize(new
        {
            token = bootstrapToken,
            endpointId,
            instanceId,
            trustMode,
            team,
            role,
            publicKey,
            keyAlgorithm = "ed25519",
        });
        var json = await KernelOps.PostJsonAsync(client, "/api/link/v1/enroll", body, System.Net.HttpStatusCode.Created);
        return new LinkEndpointCredential(
            client,
            endpointId,
            seed,
            publicKey,
            json.GetProperty("endpointKeyId").GetString()!,
            json.GetProperty("seq").GetInt64(),
            clock ?? new FakeTimeProvider(DateTimeOffset.UtcNow),
            json);
    }

    /// <summary>A client carrying a live link session (not the kernel bearer).</summary>
    internal static HttpClient CreateLinkClient(this KernelTestFactory factory, LinkEndpointCredential session)
    {
        var client = factory.CreateClient();
        var token = session.SessionTokenAsync().GetAwaiter().GetResult();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// A client carrying a session JWT the test minted itself — the class-oidc
    /// path, where identity is the issuer and there is no key to prove.
    /// </summary>
    internal static HttpClient CreateLinkClient(this KernelTestFactory factory, string sessionToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);
        return client;
    }

    /// <summary>A fresh Ed25519 keypair: (32-byte seed, base64url raw public key).</summary>
    internal static (byte[] Seed, string PublicKey) NewEd25519()
    {
        var seed = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(seed);
        return (seed, LinkKeys.Base64Url(LinkKeys.PublicKeyFromSeed(seed)));
    }

    /// <summary>
    /// A test endpoint's identity + session machinery: enroll gave it a key, and
    /// every later call needs a session JWT, which this mints by
    /// challenge -> proof (the wire a real endpoint rides).
    /// </summary>
    internal sealed class LinkEndpointCredential
    {
        private readonly HttpClient _client;
        private readonly FakeTimeProvider _clock;
        private string? _sessionToken;
        private DateTime _sessionExpiresAt;

        internal LinkEndpointCredential(
            HttpClient client, string endpointId, byte[] seed, string publicKey, string keyId, long seq,
            FakeTimeProvider clock, JsonElement enrollment)
        {
            Enrollment = enrollment;
            _client = client;
            EndpointId = endpointId;
            Seed = seed;
            PublicKey = publicKey;
            KeyId = keyId;
            Seq = seq;
            _clock = clock;
        }

        public string EndpointId { get; }

        /// <summary>The fingerprint the kernel answered with (`sha256:` + hex).</summary>
        public string KeyId { get; private set; }

        /// <summary>The 201 body, for tests that assert on the wire shape itself.</summary>
        public JsonElement Enrollment { get; }

        public string PublicKey { get; private set; }

        /// <summary>The replay fence this endpoint last asserted.</summary>
        public long Seq { get; private set; }

        /// <summary>Drop the cached session so the next call re-proves.</summary>
        public void ForgetSession() => _sessionToken = null;

        /// <summary>A live session JWT, minted (or re-minted) through challenge+proof.</summary>
        public async Task<string> SessionTokenAsync()
        {
            if (_sessionToken is not null && _sessionExpiresAt > _clock.GetUtcNow().UtcDateTime)
            {
                return _sessionToken;
            }

            var proof = await ProofForAsync("POST", "/api/link/v1/proof");
            var json = await KernelOps.PostJsonAsync(
                _client, "/api/link/v1/proof", JsonSerializer.Serialize(new { proof }), System.Net.HttpStatusCode.OK);
            _sessionToken = json.GetProperty("sessionToken").GetString()!;
            _sessionExpiresAt = DateTime.Parse(json.GetProperty("expiresAt").GetString()!, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            Seq = json.GetProperty("seq").GetInt64();
            return _sessionToken;
        }

        /// <summary>A proof bound to one channel (`htu` as the client sees it), fresh nonce/fence.</summary>
        public async Task<string> ProofForAsync(string htm, string htuPath, long? seq = null, long? epoch = null, string? baseUrlOverride = null)
        {
            var baseUrl = baseUrlOverride ?? _client.BaseAddress?.ToString().TrimEnd('/') ?? "";
            var (nonce, claimedEpoch, fence) = await ChallengeAsync();
            var claims = new LinkProofClaims(
                EndpointId, KeyId, "jti-" + Guid.NewGuid().ToString("N"), htm,
                baseUrl + htuPath, nonce, seq ?? fence, epoch ?? claimedEpoch, string.Empty,
                _clock.GetUtcNow().UtcDateTime.AddSeconds(60), _clock.GetUtcNow().UtcDateTime);
            return LinkProof.Build(Seed, LinkKeys.PublicKeyFromSeed(Seed), claims);
        }

        /// <summary>
        /// A proof over EXPLICIT claims (defaults: this identity, this endpoint's
        /// latest challenge state). A test that expects a refusal — wrong key,
        /// replayed jti, stale fence — needs to sign something the kernel will not
        /// accept, so it names the claims itself.
        /// </summary>
        public string ProofWith(
            string nonce, long epoch, long seq, byte[]? seed = null, string? sub = null, string? jti = null,
            string htm = "POST", string htuPath = "/api/link/v1/proof", string baseUrlOverride = "")
        {
            var key = seed ?? Seed;
            var claims = new LinkProofClaims(
                EndpointId, sub ?? KeyId, jti ?? ("jti-" + Guid.NewGuid().ToString("N")), htm,
                (baseUrlOverride.Length > 0 ? baseUrlOverride : _client.BaseAddress?.ToString().TrimEnd('/') ?? "") + htuPath,
                nonce, seq, epoch, string.Empty,
                _clock.GetUtcNow().UtcDateTime.AddSeconds(60), _clock.GetUtcNow().UtcDateTime);
            return LinkProof.Build(key, LinkKeys.PublicKeyFromSeed(key), claims);
        }

        /// <summary>POST one hand-built proof and hand back the raw response.</summary>
        public async Task<HttpResponseMessage> ProveWithAsync(string proof) =>
            await _client.PostAsync(
                "/api/link/v1/proof",
                new StringContent(JsonSerializer.Serialize(new { proof }), System.Text.Encoding.UTF8, "application/json"));

        /// <summary>
        /// The raw proof exchange (challenge, then POST the proof) returning the
        /// RESPONSE rather than a token: a test that expects a refusal needs the
        /// status and the problem body, not a thrown exception.
        /// </summary>
        public async Task<HttpResponseMessage> ProveRawAsync(string htm = "POST", string htuPath = "/api/link/v1/proof")
        {
            var proof = await ProofForAsync(htm, htuPath);
            return await _client.PostAsync(
                "/api/link/v1/proof",
                new StringContent(JsonSerializer.Serialize(new { proof }), System.Text.Encoding.UTF8, "application/json"));
        }

        /// <summary>The 32-byte seed of this endpoint's identity key (rotation proofs and adoption follow it).</summary>
        public byte[] Seed { get; private set; }

        /// <summary>Adopt a new identity key after a successful rotate route call.</summary>
        public void AdoptKey(byte[] seed, string publicKey, string keyId)
        {
            Seed = seed;
            PublicKey = publicKey;
            KeyId = keyId;
            _sessionToken = null;
        }

        /// <summary>
        /// Two-phase rotation: POST the rotate route with two proofs over the SAME
        /// payload — one signed by the current (old) key, one by the new key. The
        /// call itself rides a session JWT, so a rotation needs a live session and a
        /// challenge: no bearer-less takeover.
        /// </summary>
        public async Task<HttpResponseMessage> RotateAsync(
            (byte[] Seed, string PublicKey) next, byte[]? retiredSeed = null, string? retiredKeyId = null,
            long? seq = null, bool adoptOnSuccess = true)
        {
            var (nonce, _, fence) = await ChallengeAsync();
            fence = seq ?? fence;
            var oldSeed = retiredSeed ?? Seed;
            var oldKid = retiredKeyId ?? KeyId;
            var newKeyId = KeyIdOf(next.PublicKey);
            var proofs = new[]
            {
                RotationProof(oldSeed, oldKid, newKeyId, nonce, fence),
                RotationProof(next.Seed, oldKid, newKeyId, nonce, fence),
            };

            var session = await SessionTokenAsync();
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/link/v1/enroll/{EndpointId}/rotate")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { oldKeyId = oldKid, newPublicKey = next.PublicKey, proofs }),
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session);
            var response = await _client.SendAsync(request);
            if (adoptOnSuccess && response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                AdoptKey(next.Seed, next.PublicKey, newKeyId);
            }

            return response;
        }

        /// <summary>A rotation proof: same payload law, either key's hand.</summary>
        public static string RotationProof(byte[] seed, string oldKid, string newKid, string nonce, long seq)
        {
            var header = LinkKeys.Base64Url(LinkKeys.Utf8(JsonSerializer.Serialize(new { alg = "EdDSA", typ = LinkProof.HeaderType })));
            var body = LinkKeys.Base64Url(LinkKeys.Utf8(JsonSerializer.Serialize(new { oldKid, newKid, nonce, seq })));
            return header + "." + body + "." + LinkKeys.Base64Url(LinkKeys.Sign(seed, System.Text.Encoding.ASCII.GetBytes(header + "." + body)));
        }

        /// <summary>`sha256:` + hex fingerprint of a raw base64url public key.</summary>
        public static string KeyIdOf(string publicKeyBase64Url) =>
            LinkKeys.KeyId(ParsePublicKey(publicKeyBase64Url));

        /// <summary>Decode a raw base64url public key (throws when it is not one).</summary>
        public static byte[] ParsePublicKey(string publicKeyBase64Url) =>
            LinkKeys.TryParsePublicKey(publicKeyBase64Url, out var raw)
                ? raw
                : throw new ArgumentException("not a raw Ed25519 public key", nameof(publicKeyBase64Url));

        /// <summary>The kernel's challenge for this endpoint: (nonce, epoch, seq it carries).</summary>
        public async Task<(string Nonce, long Epoch, long Seq)> ChallengeAsync()
        {
            var json = await KernelOps.PostJsonAsync(
                _client, "/api/link/v1/challenge",
                JsonSerializer.Serialize(new { endpointId = EndpointId }), System.Net.HttpStatusCode.OK);
            var nonce = json.GetProperty("nonce").GetString()!;
            var parts = nonce.Split('.', 3);
            return (nonce, long.Parse(parts[0]), long.Parse(parts[1]));
        }
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

        return JsonSerializer.SerializeToElement(frame, LinkTestHarness.Web);
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

    private FakeLinkClient(HubConnection connection, LinkTestHarness.LinkEndpointCredential session, string baseUrl)
    {
        Connection = connection;
        Session = session;
        BaseUrl = baseUrl.TrimEnd('/');
        HubUrl = BaseUrl + "/hubs/link";
        Connection.On<JsonElement>("Deliver", f => _deliver.Writer.TryWrite(f));
        Connection.On<JsonElement>("FlowControl", f => _flow.Writer.TryWrite(f));
        Connection.On<JsonElement>("Disconnect", f => _disconnect.Writer.TryWrite(f));
    }

    public HubConnection Connection { get; }

    /// <summary>The endpoint this fake speaks as, and the channel URI it dialed.</summary>
    public LinkTestHarness.LinkEndpointCredential Session { get; }

    /// <summary>The hub URL as this client sees it — the `htu` its proof binds to.</summary>
    public string HubUrl { get; }

    /// <summary>The kernel's base address, as this client dialed it.</summary>
    public string BaseUrl { get; }

    public string ConnectionId => Connection.ConnectionId ?? string.Empty;

    /// <summary>
    /// Dial the hub and keep the endpoint's credential handy, because the hello
    /// proof binds to the channel URI as the CLIENT sees it: the address the
    /// socket dials is the address the proof signs.
    /// </summary>
    public static async Task<FakeLinkClient> ConnectAsync(string baseAddress, LinkTestHarness.LinkEndpointCredential session)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(baseAddress.TrimEnd('/') + "/hubs/link", options =>
            {
                // The hub handshake needs a live session, so the bearer is minted
                // before the socket opens (the token rides the upgrade request).
                options.Headers["Authorization"] = "Bearer " + session.SessionTokenAsync().GetAwaiter().GetResult();
                options.SkipNegotiation = true;
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
            })
            .Build();
        var client = new FakeLinkClient(connection, session, baseAddress);
        await connection.StartAsync();
        return client;
    }

    /// <summary>
    /// Send a hello, adding this endpoint's handshake proof (htm WS against the
    /// hub URL). The key is the credential, so a hello without a proof is what a
    /// first-boot enrollment looks like: the hub refuses it.
    /// </summary>
    public async Task<JsonElement> HelloAsync(JsonElement hello) =>
        await Connection.InvokeAsync<JsonElement>("Hello", await ProveAsync(hello));

    /// <summary>Send a hello UNPROVEN — the shape of a client that presents a key
    /// it never proved, which the hub must refuse with a `proof-failed` goodbye.</summary>
    public Task<JsonElement> HelloUnprovenAsync(JsonElement hello) =>
        Connection.InvokeAsync<JsonElement>("Hello", hello);

    /// <summary>A hello frame with this endpoint's handshake proof attached (no send).</summary>
    public async Task<JsonElement> ProveAsync(JsonElement hello)
    {
        var frame = new Dictionary<string, object?>();
        foreach (var property in hello.EnumerateObject())
        {
            frame[property.Name] = property.Value.Clone();
        }

        frame["proof"] = await Session.ProofForAsync("WS", "/hubs/link", baseUrlOverride: BaseUrl);
        return JsonSerializer.SerializeToElement(frame, LinkTestHarness.Web);
    }

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
