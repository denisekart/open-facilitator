using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Facilitator.Kernel.Shared;

using Npgsql;

namespace Facilitator.Tests.Work;

/// <summary>
/// The fail-closed replica identity gate and the
/// per-replica fleet view. A configured <c>Kernel:ReplicaId</c> is the lease
/// identity, so the gate must refuse to start beside a twin (a session row
/// stamped with my id whose lease ADVANCES — only a live process can advance
/// a lease) and must revoke a leftover row from my dead predecessor.
/// </summary>
[Collection("postgres:work")]
public sealed class ReplicaIdentityTests(PostgresFixture fixture)
{
    private static readonly IReadOnlyDictionary<string, string?> FastProbe =
        new Dictionary<string, string?>
        {
            // Minimum cadence (the 5..600 floor): the gate's probe window
            // is heartbeat + 2 = 7 s of wall time, the fastest honest probe.
            ["Link:HeartbeatSeconds"] = "5",
            ["Kernel:ReplicaId"] = "twin-probe-replica",
            ["Nudge:SweepIntervalSeconds"] = "3600",
        };

    /// <summary>Migrate the schema once with a plain host (the gate is a
    /// no-op without a configured id), so probe seeding has tables under a
    /// host whose gate then sees the seeded rows.</summary>
    private async Task WarmSchemaAsync()
    {
        using var warm = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = warm.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task LeftoverLiveLease_RevokedAndKernelStarts()
    {
        await WarmSchemaAsync();
        var endpoint = "gw-twin-" + Guid.NewGuid().ToString("N")[..8];
        // A row my dead predecessor stamped, lease still "live": nobody can
        // advance it, so the probe window ends with delete-and-serve.
        await fixture.Db.SeedLinkSession(endpoint, 1, DateTime.UtcNow.AddMinutes(10), replicaId: "twin-probe-replica");
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: FastProbe);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(0, await fixture.Db.SessionCount(endpoint));
    }

    [Fact]
    public async Task AdvancingLeaseBesideMe_RefusesStartup()
    {
        await WarmSchemaAsync();
        var endpoint = "gw-twin-" + Guid.NewGuid().ToString("N")[..8];
        await fixture.Db.SeedLinkSession(endpoint, 1, DateTime.UtcNow.AddMinutes(10), replicaId: "twin-probe-replica");
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: FastProbe);

        // The twin runs BEFORE the host builds: WebApplicationFactory builds
        // the host (and runs the gate's probe window) lazily inside the
        // first CreateClient, so the peer must already be advancing when
        // the gate reads.
        using var cts = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            using var conn = new NpgsqlConnection(fixture.ConnectionString);
            await conn.OpenAsync();
            while (!cts.IsCancellationRequested)
            {
                await using var cmd = new NpgsqlCommand(
                    "UPDATE facilitator.link_sessions SET lease_until = now() + interval '10 minutes', last_heartbeat = now() "
                    + "WHERE logical_endpoint_id = $1", conn);
                cmd.Parameters.AddWithValue(endpoint);
                await cmd.ExecuteNonQueryAsync();
                await Task.Delay(400, CancellationToken.None);
            }
        });
        await Task.Delay(800); // advances land before the probe's first read

        string failure;
        try
        {
            using var client = factory.CreateClient();
            var response = await client.GetAsync("/health");
            failure = response.IsSuccessStatusCode ? "started-clean" : $"http-{(int)response.StatusCode}";
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
        }
        finally
        {
            cts.Cancel();
            await heartbeat;
            await fixture.Db.ExpireSessionLease(endpoint);
        }

        Assert.Contains("Kernel:ReplicaId", failure, StringComparison.Ordinal);
        Assert.Contains("collides with a live peer", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplicasView_AttributesLeasesPerReplica()
    {
        await WarmSchemaAsync();
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: FastProbe);
        using var client = factory.CreateAuthedClient();
        // Seed AFTER startup: the gate (no matching rows at boot) stays a
        // no-op, and the view reads the fleet ledger live.
        var mine = "gw-view-mine-" + Guid.NewGuid().ToString("N")[..8];
        var peer = "gw-view-peer-" + Guid.NewGuid().ToString("N")[..8];
        await fixture.Db.SeedLinkSession(mine, 1, DateTime.UtcNow.AddMinutes(10), replicaId: "twin-probe-replica");
        await fixture.Db.SeedLinkSession(peer, 1, DateTime.UtcNow.AddMinutes(10), replicaId: "other-replica");

        var view = await KernelOps.GetJsonAsync(client, "/api/admin/replicas", HttpStatusCode.OK);
        Assert.Equal("twin-probe-replica", view.GetProperty("answeringReplicaId").GetString());
        var rows = view.GetProperty("replicas").EnumerateArray()
            .ToDictionary(r => r.GetProperty("replicaId").GetString()!, r => r);
        // My lease counts against me, the peer's against the peer; no hub
        // socket exists in the test host, so nothing is held "here".
        Assert.Equal(1, rows["twin-probe-replica"].GetProperty("liveSessions").GetInt32());
        Assert.Equal(0, rows["twin-probe-replica"].GetProperty("socketsHere").GetInt32());
        Assert.Equal(1, rows["other-replica"].GetProperty("liveSessions").GetInt32());
        Assert.True(view.TryGetProperty("outboxPending", out _));

        await fixture.Db.ExpireSessionLease(mine);
        await fixture.Db.ExpireSessionLease(peer);
    }
}
