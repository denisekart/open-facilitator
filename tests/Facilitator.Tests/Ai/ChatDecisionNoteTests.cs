using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Ai;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Ai;

/// <summary>
/// Decision ergonomics on the chat action surface: a confirm/cancel may carry
/// a note, the note lands on the row AND in the <c>chat.action.executed</c>
/// audit detail with the action id as correlation key (the join the console
/// links through), and a proposal past its <c>expires_at</c> is parked in the
/// visible <c>expired</c> terminal state by the gauge sweeper.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class ChatDecisionNoteTests(DispatchDbFixture fixture)
{
    [Fact]
    public async Task ConfirmCarriesTheNoteIntoRowAndAuditCorrelation()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, useKestrel: true);
        factory.CreateClient().Dispose(); // force host start => migrations
        using var minty = await UserAsync(factory, "minty");
        var mintyActor = await KeycloakFixture.ActorOfAsync("minty", "minty");

        // A read minty's own slice admits: an unbound route answers 200 for
        // any authenticated principal (the gate's own law).
        var (_, _, actionId) = await fixture.Db.SeedChatAction(
            mintyActor, "GET", "/api/works", "pending");

        var confirmed = await KernelOps.PostJsonAsync(
            minty, $"/api/chat/actions/{actionId}/confirm", """{"note":"cleared it by hand"}""",
            HttpStatusCode.OK, "chat decision");
        Assert.Equal("executed", confirmed.GetProperty("status").GetString());
        Assert.Equal("cleared it by hand", confirmed.GetProperty("note").GetString());

        // The row keeps the note; the audit row carries decisionNote + the
        // action id as its correlation id (the console's deep link).
        var detail = await fixture.Db.AuditDetailByCorrelation("chat.action.executed", actionId.ToString("N"));
        Assert.NotNull(detail);
        using var document = JsonDocument.Parse(detail!);
        Assert.Equal("cleared it by hand", document.RootElement.GetProperty("decisionNote").GetString());
        Assert.True(document.RootElement.GetProperty("aiInitiated").GetBoolean());
    }

    [Fact]
    public async Task BlankNoteIsRejectedButNoNoteIsFine()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, useKestrel: true);
        factory.CreateClient().Dispose(); // force host start => migrations
        using var minty = await UserAsync(factory, "minty");
        var mintyActor = await KeycloakFixture.ActorOfAsync("minty", "minty");

        var (blankSession, _, blankAction) = await fixture.Db.SeedChatAction(
            mintyActor, "GET", "/api/works", "pending");
        using var blank = await minty.PostAsync(
            $"/api/chat/actions/{blankAction}/cancel", new StringContent("""{"note":""}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("invalid-decision-note", await blank.Content.ReadAsStringAsync());

        // No body at all still cancels (the note stays optional).
        using var headless = new HttpRequestMessage(HttpMethod.Post, $"/api/chat/actions/{blankAction}/cancel");
        headless.Headers.Authorization = minty.DefaultRequestHeaders.Authorization;
        using var cancelled = await minty.SendAsync(headless);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("cancelled", await fixture.Db.ChatActionStatus(blankAction));
        _ = blankSession;
    }

    [Fact]
    public async Task SweeperParksDueProposalsInExpired()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        factory.CreateClient().Dispose(); // force host start => migrations
        var mintyActor = await KeycloakFixture.ActorOfAsync("minty", "minty");
        var (_, _, due) = await fixture.Db.SeedChatAction(
            mintyActor, "GET", "/api/admin/summary", "pending", expiresAt: DateTime.UtcNow.AddMinutes(-1));
        var (_, _, fresh) = await fixture.Db.SeedChatAction(
            mintyActor, "GET", "/api/admin/summary", "pending", expiresAt: DateTime.UtcNow.AddMinutes(30));

        await factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<AiActionGaugeSweeper>().Single().SweepOnceAsync(CancellationToken.None);

        Assert.Equal("expired", await fixture.Db.ChatActionStatus(due));
        Assert.Equal("pending", await fixture.Db.ChatActionStatus(fresh));
    }

    private static async Task<HttpClient> UserAsync(KernelTestFactory factory, string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await KeycloakFixture.TokenForUserAsync(user, user));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
