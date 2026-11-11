using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Notifications;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Notifications;

/// <summary>
/// The notification ledger (A6, B4, B5, D1, D5, F5): a notice is a row before
/// it is a message; an unlinked DM target falls back to the team's public
/// channel anonymized, or records its abandonment reason (never silence); a
/// vanished conversation retires the ADDRESS (obsolete + a timeline row) and
/// the item endures; a name matching two team members produces the question,
/// not a pick; cadence resolves channel → team → system.
/// </summary>
[Collection("postgres:work")]
public sealed class NotificationLedgerTests(PostgresFixture fixture)
{
    private const string ChatIssuer = "https://chat.test";

    private static Dictionary<string, string?> ChatProvider(string endpoint) => new()
    {
        ["Providers:Mattermost:Enabled"] = "true",
        ["Providers:Mattermost:EndpointId"] = endpoint,
        ["Providers:Mattermost:Tenant"] = "chat.localhost",
    };

    [Fact]
    public async Task EmissionWritesLedgerRowAndOutboxRowAtomically()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ChatProvider("mm-s4"));
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "ledger row");
        var ledger = factory.Services.GetRequiredService<NotificationLedger>();

        var target = new Facilitator.Kernel.Integrations.ConversationTarget(
            "mattermost", null, "town-square", "post-1", null, null, "thread", null);
        var (notificationId, state) = await ledger.EmitAsync(new NotificationRequest(
            Audience: "team",
            Text: "The build needs a decision.",
            WorkId: workId,
            TeamSlug: "team-alpha",
            Target: target), CancellationToken.None);

        Assert.Equal("sent", state);
        var row = await fixture.Db.Notification(notificationId);
        Assert.NotNull(row);
        Assert.Equal("sent", row.Value.State);
        var outbox = await fixture.Db.OutboxEnvelopeByCommand($"notify:{notificationId:N}");
        Assert.NotNull(outbox); // the promise and the attempt land together
        Assert.Contains("town-square", outbox);
    }

    [Fact]
    public async Task UnlinkedDmTargetFallsBackToTheTeamChannelAnonymized()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ChatProvider("mm-s4"));
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "dm fallback");
        await fixture.Db.SeedAdminConfig("notifications",
            """{"replyVsReaction":"reply","teamChannel":{"provider":"mattermost","channel":"town-square"}}""");
        var ledger = factory.Services.GetRequiredService<NotificationLedger>();

        // The DM address names a person who never linked (P4: never guessed).
        var dm = new Facilitator.Kernel.Integrations.ConversationTarget(
            "mattermost", null, "dmdir-ghost", null, null, null, "dm", "ghost-account");
        var (notificationId, state) = await ledger.EmitAsync(new NotificationRequest(
            Audience: "actor",
            Text: "Your request has an outcome.",
            WorkId: workId,
            TeamSlug: "team-alpha",
            Actor: "oidc:" + ChatIssuer + "|ghost-subject",
            Target: dm), CancellationToken.None);

        // P5: public fallback, not silence — and the fallback is recorded.
        Assert.Equal("fell-back", state);
        var row = await fixture.Db.Notification(notificationId);
        Assert.NotNull(row);
        Assert.Equal("town-square", JsonDocument.Parse(
                (await fixture.Db.NotificationsFor(workId)).Single().Target)
            .RootElement.GetProperty("channelOrResourceId").GetString());
        Assert.Contains("team channel", row.Value.Reason);
    }

    [Fact]
    public async Task NoLadderLeftUndeliverableNamesTheReason()
    {
        // No provider section at all: nothing can deliver, and the ledger says so.
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "undeliverable");
        var ledger = factory.Services.GetRequiredService<NotificationLedger>();

        var dm = new Facilitator.Kernel.Integrations.ConversationTarget(
            "mattermost", null, "dmdir-x", null, null, null, "dm", "x");
        var (notificationId, state) = await ledger.EmitAsync(new NotificationRequest(
            Audience: "actor",
            Text: "Outcome.",
            WorkId: workId,
            Actor: "oidc:" + ChatIssuer + "|x",
            Target: dm), CancellationToken.None);

        Assert.Equal("undeliverable", state);
        var row = await fixture.Db.Notification(notificationId);
        Assert.NotNull(row);
        Assert.False(string.IsNullOrEmpty(row.Value.Reason)); // P5: a visible reason
        // No attempt was made (the work's own creation row is not a notice).
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand($"notify:{notificationId:N}"));
    }

    [Fact]
    public async Task VanishedChannelMarksTheAddressObsoleteAndTheItemEndures()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ChatProvider("mm-s4"));
        var kernel = factory.CreateAuthedClient();
        var endpoint = "mm-s4";
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "vanished channel");
        var ledger = factory.Services.GetRequiredService<NotificationLedger>();
        var target = new Facilitator.Kernel.Integrations.ConversationTarget(
            "mattermost", null, "deleted-channel", "post-1", null, null, "thread", null);
        var (notificationId, _) = await ledger.EmitAsync(new NotificationRequest(
            Audience: "team",
            Text: "Outcome of the vanished-channel work.",
            WorkId: workId,
            TeamSlug: "team-alpha",
            Target: target), CancellationToken.None);

        // The adapter reports the address is gone (permanent rejection).
        var deliveryId = await fixture.Db.OutboxIdByCommand($"notify:{notificationId:N}");
        Assert.NotNull(deliveryId);
        var sessions = factory.Services.GetRequiredService<Facilitator.Kernel.Shared.Persistence.LinkService>();
        await sessions.OpenSessionAsync(endpoint, "http-1", null, 300, CancellationToken.None);
        await fixture.Db.MaterializeLinkDeliveries();
        using var receipt = await link.PostAsync(
            $"/api/link/v1/deliveries/{deliveryId}/receipt",
            new StringContent(
                """{"status":"rejected","reason":"target-missing-channel","processedAt":"2026-01-01T00:00:00Z"}""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, receipt.StatusCode);

        // The address retires, named on the timeline; the item stands.
        Assert.Equal("obsolete", (await fixture.Db.Notification(notificationId))!.Value.State);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "address.obsolete"));
        var detail = Assert.Single(await fixture.Db.TimelineDetail(workId, "address.obsolete"));
        Assert.Contains("deleted-channel", detail);
        Assert.Contains("address-obsolete", detail);
        var work = await fixture.Db.WorkStateRow(workId);
        Assert.NotNull(work); // I7: the work and its history are untouched
    }

    [Fact]
    public async Task TransientRejectionNeverTouchestheLedgerRow()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ChatProvider("mm-s4"));
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "transient rejection");
        var ledger = factory.Services.GetRequiredService<NotificationLedger>();
        var target = new Facilitator.Kernel.Integrations.ConversationTarget(
            "mattermost", null, "town-square", "post-1", null, null, "thread", null);
        var (notificationId, _) = await ledger.EmitAsync(new NotificationRequest(
            Audience: "team",
            Text: "Retryable.",
            WorkId: workId,
            TeamSlug: "team-alpha",
            Target: target), CancellationToken.None);

        var changed = await ledger.MarkObsoleteAsync(
            $"notify:{notificationId:N}", "address-obsolete: unknown-post", CancellationToken.None);
        Assert.True(changed);
        Assert.Equal("obsolete", (await fixture.Db.Notification(notificationId))!.Value.State);
    }

    [Fact]
    public async Task AmbiguousNameAsksInsteadOfPicking()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ChatProvider("mm-s4"));
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "name collision");
        // Two "Alex"s: one linked human, one contributor handle.
        await fixture.Db.SeedProviderIdentity("mattermost", "chat.localhost", "alex.one",
            ChatIssuer, "sub-alex-one");
        await fixture.Db.SeedProviderIdentity("mattermost", "chat.localhost", "alex.two",
            ChatIssuer, "sub-alex-two");
        await fixture.Db.SeedRosterMember("team-alpha", $"oidc:{ChatIssuer}|sub-alex-one", "human", "member");
        await fixture.Db.SeedRosterMember("team-alpha", $"oidc:{ChatIssuer}|sub-alex-two", "human", "member");
        var ledger = factory.Services.GetRequiredService<NotificationLedger>();

        var target = new Facilitator.Kernel.Integrations.ConversationTarget(
            "mattermost", null, "town-square", "post-1", null, null, "thread", null);
        var (notificationId, state) = await ledger.EmitAsync(new NotificationRequest(
            Audience: "actor",
            Text: "Tell Alex the outcome.",
            WorkId: workId,
            TeamSlug: "team-alpha",
            Actor: "alex", // a name, not a canonical actor
            Target: target), CancellationToken.None);

        // B5: the question is delivered; nobody was silently chosen.
        Assert.Equal("sent", state);
        var row = await fixture.Db.Notification(notificationId);
        Assert.NotNull(row);
        Assert.Equal("team", row.Value.Audience);
        Assert.Contains("matches more than one", row.Value.Text);
        Assert.Contains("alex", row.Value.Text);
    }

    [Fact]
    public async Task UniqueNameResolvesThroughTheLinkedIdentity()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ChatProvider("mm-s4"));
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "unique name");
        await fixture.Db.SeedProviderIdentity("mattermost", "chat.localhost", "bobonly",
            ChatIssuer, "sub-bobonly");
        await fixture.Db.SeedRosterMember("team-alpha", $"oidc:{ChatIssuer}|sub-bobonly", "human", "member");
        var ledger = factory.Services.GetRequiredService<NotificationLedger>();

        var dm = new Facilitator.Kernel.Integrations.ConversationTarget(
            "mattermost", null, "dm-bob", null, null, null, "dm", "bobonly");
        var (_, state) = await ledger.EmitAsync(new NotificationRequest(
            Audience: "actor",
            Text: "You asked, here it is.",
            WorkId: workId,
            TeamSlug: "team-alpha",
            Actor: "bobonly",
            Target: dm), CancellationToken.None);

        Assert.Equal("sent", state); // resolved + linked: the DM rung stands
    }

    [Fact]
    public async Task CadenceResolvesChannelOverTeamOverSystem()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        await KernelOps.CreateWorkAsync(kernel, "cadence");
        await fixture.Db.SeedAdminConfig("notifications", """{"replyVsReaction":"reaction","progressVerbosity":"minimal"}""");
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>();
        await using var db = await contextFactory.CreateDbContextAsync();

        // System level: the admin_config row is the answer at every scope.
        Assert.Equal("reaction", await NotificationLedger.CadenceAsync(
            db, "team-alpha", "mattermost/town-square", "reply", CancellationToken.None));

        // Team layer overrides via team_settings.
        await fixture.Db.SeedTeamSetting("team-alpha", "team", "team-alpha", "notifications",
            """{"default":{"replyVsReaction":"reply"},"teamChannel":{"provider":"mattermost","channel":"town-square"}}""");
        Assert.Equal("reply", await NotificationLedger.CadenceAsync(
            db, "team-alpha", "mattermost/town-square", "reaction", CancellationToken.None));

        // Channel layer wins over both.
        await fixture.Db.SeedTeamSetting("team-alpha", "channel", "mattermost/town-square", "notifications",
            """{"default":{"replyVsReaction":"reaction"}}""");
        Assert.Equal("reaction", await NotificationLedger.CadenceAsync(
            db, "team-alpha", "mattermost/town-square", "reply", CancellationToken.None));
    }

    [Fact]
    public async Task ProviderDegradedDisclosesPerStatus()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ChatProvider("mm-s4"));
        var kernel = factory.CreateAuthedClient();
        var endpoint = "mm-s4";
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        await fixture.Db.SeedAdminConfig("notifications",
            """{"teamChannel":{"provider":"mattermost","channel":"town-square"}}""");

        // 403: a team-visible permission notice (retryable false), addressed
        // at the provider's team so the team-channel rung can carry it.
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            DegradedEnvelope("mattermost", 403, "permission-denied", endpoint, retryable: false, team: "team-alpha"),
            HttpStatusCode.Accepted);
        // 429: a system-operators notice; only that endpoint stalls.
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            DegradedEnvelope("mattermost", 429, "rate-limited", endpoint, retryable: true),
            HttpStatusCode.Accepted);

        Assert.True(await fixture.Db.NotificationWithReason("refused permission"));
        Assert.True(await fixture.Db.NotificationWithReason("rate limited"));
    }

    private static string DegradedEnvelope(
        string provider, int status, string reason, string endpointId, bool retryable, string? team = null)
    {
        var doc = new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = "evt-" + Guid.NewGuid().ToString("N"),
            ["source"] = $"urn:workspace:{endpointId}",
            ["type"] = "org.facilitator.event.provider.degraded.v1",
            ["subject"] = $"provider/{provider}",
            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/event.provider.degraded.v1.json",
            ["data"] = new JsonObject
            {
                ["provider"] = provider,
                ["reason"] = reason,
                ["status"] = status,
                ["endpointId"] = endpointId,
                ["observedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["retryable"] = retryable,
            },
        };
        if (team is not null)
        {
            doc["data"]!["team"] = team;
        }

        return doc.ToJsonString();
    }
}

