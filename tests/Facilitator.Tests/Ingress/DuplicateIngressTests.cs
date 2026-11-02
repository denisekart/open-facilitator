using System.Net;
using System.Text;

namespace Facilitator.Tests.Ingress;

/// <summary>
/// The real conformance fixture drives CloudEvents ingress —
/// accepted once, duplicate on replay, exactly one business effect — while
/// unknown commands fail closed with an audit inbox row and envelope
/// violations answer 400.
/// </summary>
[Collection("postgres:ingress")]
public class DuplicateIngressTests(PostgresFixture fixture)
{
    [Fact]
    public async Task FixtureSteer_AcceptedOnce_DuplicateReplay_InvalidFixturesFailClosed()
    {
        using var factory = new KernelTestFactory(fixture);
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "cloud event steer dedupe");

        var envelope = PostgresFixture.SteerFixtureFor("valid/command.work.steer.json", workId, expectedContextVersion: 1);

        var accepted = await KernelOps.PostJsonAsync(client, "/api/ingress/events", envelope, HttpStatusCode.OK);
        Assert.Equal("accepted", accepted.GetProperty("status").GetString());

        var replay = await KernelOps.PostJsonAsync(client, "/api/ingress/events", envelope, HttpStatusCode.OK);
        Assert.Equal("duplicate", replay.GetProperty("status").GetString());
        Assert.Equal(accepted.GetProperty("inboxId").GetGuid(), replay.GetProperty("inboxId").GetGuid());

        // Business effect exactly once: created + exactly one steer row.
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "steer"));
        var timeline = await KernelOps.GetJsonAsync(client, $"/api/works/{workId}/timeline", HttpStatusCode.OK);
        Assert.Equal(2, timeline.GetArrayLength());

        var work = await KernelOps.GetJsonAsync(client, $"/api/works/{workId}", HttpStatusCode.OK);
        Assert.Equal(2, work.GetProperty("version").GetInt64());

        // Unknown command: 422 + fail-closed audit row (state='failed').
        var unknown = PostgresFixture.ReadFixture("invalid/command.unknown.v1.json");
        using var unknownResponse = await client.PostAsync("/api/ingress/events",
            new StringContent(unknown, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownResponse.StatusCode);
        var unknownProblem = await JsonWire.ReadJsonAsync(unknownResponse.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/unknown-command", unknownProblem.GetProperty("type").GetString());
        Assert.True(await fixture.Db.InboxFailedAudit("01JCOMMAND-NOVEL", "org.facilitator.command.novel.thing.v1"));

        // Envelope violation: bad specversion ⇒ 400 envelope-violation.
        using var envelopeResponse = await client.PostAsync("/api/ingress/events",
            new StringContent(PostgresFixture.ReadFixture("invalid/envelope.specversion.json"), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, envelopeResponse.StatusCode);
        var envelopeProblem = await JsonWire.ReadJsonAsync(envelopeResponse.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/envelope-violation", envelopeProblem.GetProperty("type").GetString());
    }
}
