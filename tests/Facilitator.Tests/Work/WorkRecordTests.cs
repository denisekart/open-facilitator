using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;



namespace Facilitator.Tests.Work;

/// <summary>
/// Work as the durable record: merge keeps the loser's history and redirects
/// reads (303) to the survivor; links are first-class rows; a completion with
/// nothing to point at is refused; a partial completion must name a successor;
/// cost round-trips verbatim; steering a terminal work stays illegal (the
/// successor path is provider ingress, covered in <see cref="WorkSuccessorTests"/>).
/// </summary>
[Collection("postgres:work")]
public class WorkRecordTests(PostgresFixture fixture)
{
    private const string ProblemBase = "https://errors.openfacilitator.org/v1/";

    [Fact]
    public async Task Merge_KeepsLoserHistory_RedirectsToSurvivor_ExposesLink()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var (loserId, _) = await KernelOps.CreateWorkAsync(client, "duplicate ask");
        var (survivorId, _) = await KernelOps.CreateWorkAsync(client, "the real ask");

        var merged = await KernelOps.PostJsonAsync(client, $"/api/works/{loserId}/merge",
            JsonSerializer.Serialize(new { intoWorkId = survivorId, reason = "same ask, opened twice" }),
            HttpStatusCode.OK);
        Assert.Equal(survivorId, merged.GetProperty("workId").GetGuid());

        // The loser keeps its history (created row stands) and answers 303 to the
        // survivor. A redirect-following client cannot observe this, so send it
        // through a no-redirect client.
        using var noRedirect = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        noRedirect.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        using var response = await noRedirect.GetAsync($"/api/works/{loserId}");
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal($"/api/works/{survivorId}", response.Headers.Location?.ToString());

        var timeline = await KernelOps.GetJsonAsync(client, $"/api/works/{loserId}/timeline", HttpStatusCode.OK);
        Assert.Contains("work.linked", timeline.EnumerateArray()
            .Select(r => r.GetProperty("kind").GetString()));

        var links = await KernelOps.GetJsonAsync(client, $"/api/works/{loserId}/links", HttpStatusCode.OK);
        var link = Assert.Single(links.EnumerateArray());
        Assert.Equal("merged-duplicate", link.GetProperty("kind").GetString());
        Assert.Equal(survivorId, link.GetProperty("refWorkId").GetGuid());
        Assert.Equal("same ask, opened twice", link.GetProperty("note").GetString());

        // The survivor is untouched by the merge.
        var survivor = await KernelOps.GetJsonAsync(client, $"/api/works/{survivorId}", HttpStatusCode.OK);
        Assert.False(survivor.TryGetProperty("mergedInto", out var m) && m.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Merge_IntoSelf_IsRefused()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "loop candidate");

        using var response = await client.PostAsync(
            $"/api/works/{workId}/merge",
            new StringContent(
                JsonSerializer.Serialize(new { intoWorkId = workId }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BareCompletion_IsRefused_ReasonMakesItARecord()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var (workId, version) = await KernelOps.CreateWorkAsync(client, "evidence gate");

        await KernelOps.TransitionAsync(client, workId, "planning", 1);
        await KernelOps.TransitionAsync(client, workId, "active", 2);
        await KernelOps.TransitionAsync(client, workId, "review", 3);

        // review→completed with no artifact, manifest, link or reason: refused.
        using var refused = await client.PostAsync(
            $"/api/works/{workId}/transitions",
            new StringContent(
                JsonSerializer.Serialize(new { to = "completed", expectedVersion = 4 }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await refused.Content.ReadJsonAsync();
        Assert.Equal(ProblemBase + "completion-unevidenced", problem.GetProperty("type").GetString());

        // The work stands in review, unevidenced completion wrote nothing.
        var after = await KernelOps.GetJsonAsync(client, $"/api/works/{workId}", HttpStatusCode.OK);
        Assert.Equal("review", after.GetProperty("state").GetString());
        Assert.Equal(4, after.GetProperty("version").GetInt64());
        version = 4;

        // The same transition with a stated basis lands, and the basis is a
        // first-class field on the read model.
        var done = await KernelOps.TransitionAsync(client, workId, "completed", version,
            reason: "answered inline in the thread");
        Assert.Equal("completed", done.GetProperty("state").GetString());
        Assert.Equal("answered inline in the thread", done.GetProperty("closeReason").GetString());
    }

    [Fact]
    public async Task PartialCompletion_RequiresSuccessor_RecordsLinkAtomically()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "half the ask done");
        var (successorId, _) = await KernelOps.CreateWorkAsync(client, "the remainder");

        await KernelOps.TransitionAsync(client, workId, "planning", 1);
        await KernelOps.TransitionAsync(client, workId, "active", 2);
        await KernelOps.TransitionAsync(client, workId, "review", 3);

        // Partial basis WITHOUT a successor: refused with its own code.
        var payload = JsonSerializer.Serialize(new
        {
            to = "completed",
            expectedVersion = 4,
            basis = "partially-complete",
            note = "second half continues as the follow-up",
        });
        using var refused = await client.PostAsync(
            $"/api/works/{workId}/transitions",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await refused.Content.ReadJsonAsync();
        Assert.Equal(ProblemBase + "successor-required", problem.GetProperty("type").GetString());

        var accepted = await KernelOps.PostJsonAsync(client, $"/api/works/{workId}/transitions",
            JsonSerializer.Serialize(new
            {
                to = "completed",
                expectedVersion = 4,
                basis = "partially-complete",
                successorWorkId = successorId,
                note = "second half continues as the follow-up",
            }),
            HttpStatusCode.OK);
        Assert.Equal("completed", accepted.GetProperty("state").GetString());

        var links = await KernelOps.GetJsonAsync(client, $"/api/works/{workId}/links", HttpStatusCode.OK);
        var link = Assert.Single(links.EnumerateArray());
        Assert.Equal("successor", link.GetProperty("kind").GetString());
        Assert.Equal(successorId, link.GetProperty("refWorkId").GetGuid());
    }

    [Fact]
    public async Task Cost_RoundTripsVerbatim()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "costed work");

        var cost = new
        {
            tokens = 1234,
            units = "USD",
            currency = "USD",
            source = "codex",
            at = "2026-11-11T00:00:00Z",
        };
        using (var stored = await client.PostAsync($"/api/works/{workId}/cost",
                   new StringContent(JsonSerializer.Serialize(cost), Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.NoContent, stored.StatusCode);
        }

        var work = await KernelOps.GetJsonAsync(client, $"/api/works/{workId}", HttpStatusCode.OK);
        var served = work.GetProperty("cost");
        Assert.Equal(1234, served.GetProperty("tokens").GetInt32());
        Assert.Equal("codex", served.GetProperty("source").GetString());
    }

    [Fact]
    public async Task NewWork_CarriesNullRecordFields_SupersetShape()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "plain work");

        var work = await KernelOps.GetJsonAsync(client, $"/api/works/{workId}", HttpStatusCode.OK);
        // Old-shape consumers keep working: the original fields stand...
        Assert.Equal("open", work.GetProperty("state").GetString());
        // ...and the new ones are present as null on a row nothing delivered.
        foreach (var field in new[] { "teamSlug", "ownerActor", "origin", "activeWhere", "cost", "mergedInto", "closeReason" })
        {
            Assert.True(work.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Null,
                $"{field} present as null");
        }
    }

    [Fact]
    public async Task SteerOfTerminalWork_StaysIllegal_ReopenNeverHappens()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "closed shop");
        await KernelOps.WalkToCompletedAsync(client, workId, 1);

        using var response = await client.PostAsync(
            $"/api/works/{workId}/steer",
            new StringContent(
                JsonSerializer.Serialize(new { directive = "one more thing", commandId = Guid.NewGuid().ToString("N"), expectedVersion = 5 }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadJsonAsync();
        Assert.Equal(ProblemBase + "invalid-state-transition", problem.GetProperty("type").GetString());

        var after = await KernelOps.GetJsonAsync(client, $"/api/works/{workId}", HttpStatusCode.OK);
        Assert.Equal("completed", after.GetProperty("state").GetString());
    }
}
