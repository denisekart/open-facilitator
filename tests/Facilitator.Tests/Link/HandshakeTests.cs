using System.Text.Json;

namespace Facilitator.Tests.Link;

/// <summary>
/// §26.1 item 2 (server half) + appendix §10.1 (A:300-301): hello→welcome on
/// the negotiated v1, epochs monotonic across reconnects, a v9-only client
/// gets the frozen typed goodbye (fixture pair invalid/control.hello.v9-only →
/// valid/control.goodbye.incompatible) and a null welcome, and a second hello
/// supersedes the live connection with a session-superseded Disconnect.
/// Proven on the real wire: SignalR client over WebSockets against Kestrel.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class HandshakeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task HelloReturnsWelcomeV1AndEpochsGrowAcrossReconnects()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, useKestrel: true);
        var endpoint = "gw-hs-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint);

        await using var first = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        var welcome = await first.HelloAsync(LinkTestHarness.HelloFrame(endpoint));

        Assert.Equal("welcome", welcome.GetProperty("kind").GetString());
        Assert.Equal("v1", welcome.GetProperty("protocolVersion").GetString());
        Assert.Equal(1, welcome.GetProperty("leaseEpoch").GetInt64());
        Assert.Equal(16, welcome.GetProperty("maxInFlight").GetInt64());
        Assert.Equal(30, welcome.GetProperty("heartbeatSeconds").GetInt64());
        Assert.False(string.IsNullOrEmpty(welcome.GetProperty("sessionId").GetString()));
        Assert.Equal(1L, await fixture.Db.SessionEpoch(endpoint));

        // §16.2: restarts re-use the logical id, get a new session + higher epoch.
        await using var second = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        var reconnected = await second.HelloAsync(LinkTestHarness.HelloFrame(endpoint));
        Assert.Equal(2, reconnected.GetProperty("leaseEpoch").GetInt64());
    }

    [Fact]
    public async Task V9OnlyHelloGetsTypedGoodbyeAndNullWelcome()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, useKestrel: true);
        var endpoint = "gw-hs-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint);

        await using var client = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        var result = await client.HelloAsync(LinkTestHarness.HelloFrameVersions(endpoint, """["v9"]"""));

        // null-return convention: the client sees no welcome frame at all.
        Assert.True(result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null, "Hello returned null");

        var goodbye = await client.NextDisconnectAsync();
        Assert.Equal("goodbye", goodbye.GetProperty("kind").GetString());
        Assert.Equal("no-compatible-protocol-version", goodbye.GetProperty("reason").GetString());
        var expected = JsonDocument.Parse(PostgresFixture.ReadFixture("valid/control.goodbye.incompatible.json")).RootElement;
        Assert.Equal(expected.GetProperty("reason").GetString(), goodbye.GetProperty("reason").GetString());

        // a session id was minted but NO link_sessions row exists for the refused handshake
        Assert.False(string.IsNullOrEmpty(goodbye.GetProperty("sessionId").GetString()));
        Assert.Equal(0, await fixture.Db.SessionCount(endpoint));
    }

    [Fact]
    public async Task SecondHelloSupersedesTheLiveConnection()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, useKestrel: true);
        var endpoint = "gw-hs-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint);

        await using var old = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        await old.HelloAsync(LinkTestHarness.HelloFrame(endpoint));

        await using var fresh = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        await fresh.HelloAsync(LinkTestHarness.HelloFrame(endpoint));

        var goodbye = await old.NextDisconnectAsync();
        Assert.Equal("session-superseded", goodbye.GetProperty("reason").GetString());
        // the winner keeps its session
        Assert.False(await fresh.TryDisconnectAsync(500), "the superseding connection must not be disconnected");
    }
}

