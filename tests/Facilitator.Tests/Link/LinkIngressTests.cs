using System.Net;
using System.Text;
using System.Text.Json;

namespace Facilitator.Tests.Link;

/// <summary>
/// §5.5 + appendix A:305-306 over the HTTP fallback: fail-closed typed
/// <c>unsupported</c> + a failed audit inbox row for unknown commands,
/// work.assign is illegal FROM a link, both accepted content types admit,
/// receipt-class envelopes are 400, and 262144 bytes is the hard 413 gate.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class LinkIngressTests(DispatchDbFixture fixture)
{
    private const string ProblemBase = "https://errors.openfacilitator.org/v1/";

    [Fact]
    public async Task UnknownCommandFailsClosedWithAuditRow()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var link = factory.CreateLinkClient(await EnrollAsync(factory));

        var id = Guid.NewGuid().ToString();
        var unknown = PostgresFixture.ReadFixture("invalid/command.unknown.v1.json").Replace("\"01JCOMMAND-NOVEL\"", "\"" + id + "\"");
        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(unknown, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadJsonAsync();
        Assert.Equal(ProblemBase + "unsupported", problem.GetProperty("type").GetString());

        // audit trail (S:305): the envelope is persisted, state failed.
        Assert.True(await fixture.Db.InboxFailedFor(id));
    }

    [Fact]
    public async Task AssignFromLinkIsIllegal()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var link = factory.CreateLinkClient(await EnrollAsync(factory));

        var assign = PostgresFixture.ReadFixture("valid/command.work.assign.json");
        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(assign, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadJsonAsync();
        Assert.Equal(ProblemBase + "unsupported", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task BothContentTypesAdmitEventEnvelopes()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var link = factory.CreateLinkClient(await EnrollAsync(factory));

        for (var i = 0; i < 2; i++)
        {
            var content = PostgresFixture.ReadFixture("valid/event.work.progress.json")
                .Replace("\"01JEVENT-work-progress\"", "\"" + Guid.NewGuid() + "\"");
            using var response = await link.PostAsync("/api/link/v1/events",
                new StringContent(content, Encoding.UTF8, i == 0 ? "application/cloudevents+json" : "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var receipt = await response.Content.ReadJsonAsync();
            Assert.Equal("accepted", receipt.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task ReceiptClassEnvelopeIs400()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var link = factory.CreateLinkClient(await EnrollAsync(factory));

        // a receipt frame sent AS an envelope: receipts travel via the receipt path.
        var envelope = JsonSerializer.Serialize(new
        {
            specversion = "1.0",
            id = Guid.NewGuid(),
            source = "urn:workspace:ws-01",
            type = "org.facilitator.receipt.accepted.v1",
            subject = "work/work-123",
            time = "2026-10-22T18:00:00Z",
            datacontenttype = "application/json",
            dataschema = "https://schemas.openfacilitator.org/v1/receipt.v1.json",
            data = new { deliveryId = "del-123", status = "accepted", processedAt = "2026-10-22T18:01:00Z" },
        });
        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(envelope, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadJsonAsync();
        Assert.Equal(ProblemBase + "envelope-violation", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task OversizedBodyIs413RegardlessOfType()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var link = factory.CreateLinkClient(await EnrollAsync(factory));

        // > 262144 bytes: the size gate fires before any parsing.
        var padding = new string('x', 300_000);
        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent("{\"id\":\"" + padding + "\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await response.Content.ReadJsonAsync();
        Assert.Equal(ProblemBase + "payload-too-large", problem.GetProperty("type").GetString());
        Assert.Contains("262144", problem.GetProperty("detail").GetString());
    }

    private static async Task<string> EnrollAsync(KernelTestFactory factory) =>
        await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), "gw-li-" + Guid.NewGuid().ToString("N")[..8]);
}
