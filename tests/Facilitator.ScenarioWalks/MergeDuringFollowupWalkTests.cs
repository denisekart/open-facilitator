using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// A11 merge during a follow-up: a duplicate ask is merged into the real
/// one WHILE a follow-up steer is about to land. The steer goes to the
/// survivor (the merge response hands out the survivor's fresh version),
/// the survivor records it, and the loser keeps its trail: its history
/// stands, reads 303 to the survivor, and the <c>merged-duplicate</c>
/// link row names the survivor.
/// Segment: WorkRecordTests.Merge_KeepsLoserHistory_RedirectsToSurvivor_ExposesLink.
/// </summary>
[Collection("postgres:walks")]
public sealed class MergeDuringFollowupWalkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SteerAfterMergeLandsOnSurvivor_LoserTrailIntact()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var (loserId, _) = await KernelOps.CreateWorkAsync(client, "duplicate ask");
        var (survivorId, survivorVersion) = await KernelOps.CreateWorkAsync(client, "the real ask");

        // The follow-up steer is in flight; the merge lands first and hands
        // the caller the survivor's current fence.
        var merged = await KernelOps.PostJsonAsync(client, $"/api/works/{loserId}/merge",
            JsonSerializer.Serialize(new { intoWorkId = survivorId, reason = "same ask, opened twice" }),
            HttpStatusCode.OK);
        Assert.Equal(survivorId, merged.GetProperty("workId").GetGuid());
        var version = merged.GetProperty("version").GetInt64();
        Assert.Equal(survivorVersion, version);

        // The steer lands on the survivor.
        var steer = await KernelOps.PostJsonAsync(client, $"/api/works/{survivorId}/steer",
            JsonSerializer.Serialize(new
            {
                directive = "guard the retry path",
                commandId = Guid.NewGuid().ToString("N"),
                expectedVersion = version,
            }),
            HttpStatusCode.OK);
        Assert.True(steer.GetProperty("version").GetInt64() > version);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(survivorId, "steer"));

        // The loser's trail: created row stands, reads redirect (303) to
        // the survivor, and the link names it with the note.
        Assert.Equal(1, await fixture.Db.TimelineEventCount(loserId, "created"));
        using var noRedirect = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        noRedirect.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        using var response = await noRedirect.GetAsync($"/api/works/{loserId}");
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal($"/api/works/{survivorId}", response.Headers.Location?.ToString());
        var links = await KernelOps.GetJsonAsync(client, $"/api/works/{loserId}/links", HttpStatusCode.OK);
        var link = Assert.Single(links.EnumerateArray());
        Assert.Equal("merged-duplicate", link.GetProperty("kind").GetString());
        Assert.Equal(survivorId, link.GetProperty("refWorkId").GetGuid());
        Assert.Equal(1, await fixture.Db.WorkLinkCount(loserId, survivorId, "merged-duplicate"));
    }
}
