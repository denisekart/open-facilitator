using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Shared.Storage;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Artifacts;

/// <summary>
/// The transfer primitive (<see cref="ArtifactSignOver"/>) and its admin leg
/// (<c>POST /api/admin/artifacts/sign-over</c>): an atomic, audited, digest
/// re-verified move of <c>artifacts.team_slug</c> — a column, not a location.
/// Foreign-owner / missing / already-owned rows skip with a reason and never
/// move; a digest mismatch denies that row alone; the ledger keeps its row and
/// only gains the <c>|owner:</c> stamp; the teamless binding repoints with the
/// bytes' new owner.
/// </summary>
[Collection("postgres:work")]
public sealed class ArtifactSignOverTests(PostgresFixture fixture)
{
    private static readonly byte[] Bytes = "sign-over bytes, hashed for real\n"u8.ToArray();

    private static string DigestOf(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static IReadOnlyDictionary<string, string?> RouterRoster => new Dictionary<string, string?>
    {
        ["Link:Teams:2"] = "router",
    };

    private static string RequestUploadEnvelope(string commandId, string evidenceRef) =>
        ArtifactTestKit.RequestUploadEnvelope(commandId, Bytes, evidenceRef);

    [Fact]
    public async Task SignOverMovesRowsSkipsForeignAndUnknownAndStampsAuditLedgerBinding()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: RouterRoster);
        var kernel = factory.CreateAuthedClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var evidence = "oidc:" + await KeycloakFixture.TokenForUserAsync("alice", "alice", "ofac-evidence", "evidence-secret");

        // Two rows born on `router`, one on `team-beta`, one that will lie about
        // its digest, one that does not exist — every leg of the skip law.
        var routerClient = factory.CreateLinkClient(
            await LinkTestHarness.EnrollAsync(kernel, $"so-router-{suffix}", team: "router"));
        var idA = await AllocateAsync(routerClient, evidence);
        var idB = await AllocateAsync(routerClient, evidence);
        var betaClient = factory.CreateLinkClient(
            await LinkTestHarness.EnrollAsync(kernel, $"so-beta-{suffix}", team: "team-beta"));
        var idBeta = await AllocateAsync(betaClient, evidence);

        var lyingEnvelope = JsonNode.Parse(RequestUploadEnvelope("cmd-so-lie-" + suffix, evidence))!.AsObject();
        lyingEnvelope["data"]!["digest"] = ArtifactTestKit.FreshDigest();
        var lieAck = await KernelOps.PostJsonAsync(routerClient, "/api/link/v1/events",
            lyingEnvelope.ToJsonString(), HttpStatusCode.Accepted);
        var idLie = Guid.Parse(lieAck.GetProperty("data").GetProperty("artifactId").GetString()!);

        await StoreBytes(routerClient, idA);
        await StoreBytes(routerClient, idB);
        await StoreBytes(routerClient, idLie); // real bytes hash elsewhere than declared
        await StoreBytes(betaClient, idBeta);

        // A work to hang the timeline row on, plus the binding + ledger rows.
        var work = await KernelOps.PostJsonAsync(kernel, "/api/works",
            JsonSerializer.Serialize(new { objective = "sign-over walk" }), HttpStatusCode.Created);
        var workId = work.GetProperty("workId").GetGuid();
        var ledgerId = Guid.NewGuid();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync())
        {
            await db.Artifacts.Where(a => a.ArtifactId == idA)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.WorkId, workId));
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO facilitator.communication_bindings
                    (binding_id, provider, channel, root_message_id, work_id, team, idempotency_key, created_at)
                VALUES (gen_random_uuid(), 'telegram', 'chat-42', 'thread-1', NULL, NULL, @k, now() at time zone 'utc')
                """, [new Npgsql.NpgsqlParameter("@k", "so-" + suffix)]);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO facilitator.provider_events
                    (provider_event_id, provider, provider_delivery_id, body_sha256, body, verified_reference, received_at)
                VALUES (@id, 'telegram', @pdv, 'sha256:x', '[]'::jsonb, 'work/nothing', now() at time zone 'utc')
                """, [new Npgsql.NpgsqlParameter("@id", ledgerId), new Npgsql.NpgsqlParameter("@pdv", "pdv-" + suffix)]);
        }

        using var scope = factory.Services.CreateScope();
        var signOver = scope.ServiceProvider.GetRequiredService<ArtifactSignOver>();
        var outcome = await signOver.SignOverAsync(new SignOverRequest(
            [idA, idB, idBeta, Guid.NewGuid(), idLie], "team-alpha", "router",
            "workflow:kernel", "router-ingress@1", ledgerId.ToString(),
            new ThreadRef("telegram", "chat-42", "thread-1")), CancellationToken.None);

        Assert.Equal([idA, idB], outcome.Transferred);
        Assert.Contains($"{idBeta}:foreign-owner", outcome.Skipped);
        Assert.Contains(outcome.Skipped, s => s.EndsWith(":not-found", StringComparison.Ordinal));
        Assert.Contains($"{idLie}:digest-mismatch", outcome.Skipped);

        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync())
        {
            var teams = await db.Artifacts.Where(a => a.ArtifactId == idA || a.ArtifactId == idB)
                .Select(a => a.TeamSlug).ToListAsync();
            Assert.All(teams, t => Assert.Equal("team-alpha", t));
            Assert.Equal("team-beta", (await db.Artifacts.FirstAsync(a => a.ArtifactId == idBeta)).TeamSlug);
            Assert.Equal("router", (await db.Artifacts.FirstAsync(a => a.ArtifactId == idLie)).TeamSlug);

            // Timeline: the work's row names the ownership delta.
            var timeline = await db.WorkEvents.Where(e => e.WorkId == workId && e.Kind == "artifact.signed-over").ToListAsync();
            Assert.Single(timeline);
            using (var timelineDetail = JsonDocument.Parse(timeline[0].Detail))
            {
                Assert.Equal("team-alpha", timelineDetail.RootElement.GetProperty("toTeam").GetString());
                Assert.Equal("router", timelineDetail.RootElement.GetProperty("fromTeam").GetString());
            }

            Assert.Equal(idA.ToString(), timeline[0].CorrelationId);

            // Ledger: the row never moved; the stamp rides its judgment.
            var judgment = await db.ProviderEvents.Where(e => e.ProviderEventId == ledgerId)
                .Select(e => e.VerifiedReference).FirstAsync();
            Assert.EndsWith("|owner:team-alpha", judgment);

            // Binding: the teamless conversation binding now names the owner.
            var bindingTeam = await db.CommunicationBindings
                .Where(b => b.Provider == "telegram" && b.Channel == "chat-42" && b.RootMessageId == "thread-1")
                .Select(b => b.TeamSlug).FirstAsync();
            Assert.Equal("team-alpha", bindingTeam);

            // Audit: allow row + digest-mismatch deny row, both on the outcome's correlation id.
            var audit = await db.Audit.Where(a => a.CorrelationId == outcome.AuditId.ToString()).ToListAsync();
            Assert.Contains(audit, a => a.Action == "artifact.sign-over" && a.Decision == "allow" && a.Reason == "signed-over");
            Assert.Contains(audit, a => a.Action == "artifact.sign-over" && a.Decision == "deny" && a.Reason == "digest-mismatch");
            var allow = audit.First(a => a.Decision == "allow");
            Assert.Equal("artifact-v1", allow.PolicyVersion);
            using var detail = JsonDocument.Parse(allow.Detail);
            Assert.Equal("team-alpha", detail.RootElement.GetProperty("toTeam").GetString());
            Assert.Equal(2, detail.RootElement.GetProperty("transferred").GetArrayLength());
        }

        // Double sign-over: the rows are already owned by team-alpha — all skip.
        var again = await signOver.SignOverAsync(new SignOverRequest(
            [idA, idB], "team-alpha", "router", "admin:op", "admin:twice"), CancellationToken.None);
        Assert.Empty(again.Transferred);
        Assert.All(again.Skipped, s => Assert.EndsWith(":already-owned", s));
    }

    [Fact]
    public async Task AdminSignOverMovesRowRejectsUnknownTeamAndEmptyBody()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: RouterRoster);
        var kernel = factory.CreateAuthedClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var evidence = "oidc:" + await KeycloakFixture.TokenForUserAsync("alice", "alice", "ofac-evidence", "evidence-secret");
        var routerClient = factory.CreateLinkClient(
            await LinkTestHarness.EnrollAsync(kernel, $"so-adm-{suffix}", team: "router"));
        var id = await AllocateAsync(routerClient, evidence);
        await StoreBytes(routerClient, id);

        var ok = await KernelOps.PostJsonAsync(kernel, "/api/admin/artifacts/sign-over",
            JsonSerializer.Serialize(new { artifactIds = new[] { id }, fromTeam = "router", toTeam = "team-alpha", reason = "mis-owned thread" }),
            HttpStatusCode.OK);
        Assert.Equal(1, ok.GetProperty("transferred").GetInt32());
        Assert.Equal(0, ok.GetProperty("skipped").GetArrayLength());
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync())
        {
            Assert.Equal("team-alpha", (await db.Artifacts.FirstAsync(a => a.ArtifactId == id)).TeamSlug);
            Assert.Contains(
                await db.Audit.Where(a => a.Action == "artifact.sign-over" && a.CorrelationId == ok.GetProperty("auditId").GetString()!).ToListAsync(),
                a => a.Decision == "allow" && a.Reason == "signed-over");
        }

        var unknown = await kernel.PostAsync("/api/admin/artifacts/sign-over",
            JsonContent.Create(new { artifactIds = new[] { id }, fromTeam = "router", toTeam = "team-ghost" }));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("team-not-found", await unknown.Content.ReadAsStringAsync());

        var empty = await kernel.PostAsync("/api/admin/artifacts/sign-over",
            JsonContent.Create(new { artifactIds = Array.Empty<Guid>(), fromTeam = "router", toTeam = "team-alpha" }));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("sign-over-invalid", await empty.Content.ReadAsStringAsync());

        // The moved row: a no-op transfer answers 200 with transferred=0.
        var noop = await KernelOps.PostJsonAsync(kernel, "/api/admin/artifacts/sign-over",
            JsonSerializer.Serialize(new { artifactIds = new[] { id }, fromTeam = "router", toTeam = "team-alpha" }),
            HttpStatusCode.OK);
        Assert.Equal(0, noop.GetProperty("transferred").GetInt32());
        Assert.Single(noop.GetProperty("skipped").EnumerateArray());
    }

    private static async Task<Guid> AllocateAsync(HttpClient link, string evidence)
    {
        var ack = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            RequestUploadEnvelope("cmd-so-" + Guid.NewGuid().ToString("N"), evidence), HttpStatusCode.Accepted);
        return Guid.Parse(ack.GetProperty("data").GetProperty("artifactId").GetString()!);
    }

    private static Task StoreBytes(HttpClient link, Guid id) =>
        link.PutAsync($"/api/link/v1/artifacts/{id}/content", new ByteArrayContent(Bytes));
}
