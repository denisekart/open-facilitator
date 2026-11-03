using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel;
using Facilitator.Kernel.Ai;
using Facilitator.Kernel.Shared;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Ai;

/// <summary>
/// The three-level decision ladder, host-backed for policy reads (the
/// <c>llm</c> row through the admin surface) with the fake ports — zero
/// network. Ladder law: decisions API → LLM classifier → deterministic
/// (null = "no preference", the caller's order stands).
/// </summary>
[Collection("postgres:work")]
public sealed class ModelDecisionProviderTests(PostgresFixture fixture)
{
    private static readonly IReadOnlyList<RoutingCandidate> Candidates =
    [
        new("gw-a", 2, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        new("gw-b", 1, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
    ];

    [Fact]
    public async Task DecisionsApiChoiceInSetIsAdopted()
    {
        using var host = new KernelTestFactory(fixture, noDispatcher: true);
        await PutPolicyAsync(host, decisionEndpoint: "decision", decisionMode: "auto");
        var chat = new FakeAiGateway();
        var decisions = new FakeDecisionGateway { Peer = chat };
        decisions.EnqueueDecision("preferred_endpoint", new DecisionAnswer("preferred_endpoint", null, "gw-b", 0.9));
        var provider = Build(host, decisions, chat, decision: true, decoder: true);

        var outcome = await provider.EvaluateAsync(
            DecisionKind.Routing,
            new Dictionary<string, string> { ["capabilityId"] = "ci.run", ["workId"] = Guid.NewGuid().ToString() },
            Candidates,
            CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal("gw-b", outcome!.PreferredCandidate);
        Assert.Single(decisions.DecisionRequests);
        Assert.Empty(chat.Requests);
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("off-set")]
    public async Task InformedNoPreferenceKeepsDeterministicOrder(string shape)
    {
        using var host = new KernelTestFactory(fixture, noDispatcher: true);
        await PutPolicyAsync(host, decisionEndpoint: "decision", decisionMode: "auto");
        var chat = new FakeAiGateway();
        var decisions = new FakeDecisionGateway { Peer = chat };
        decisions.EnqueueDecision(
            "preferred_endpoint",
            shape == "refusal"
                ? new DecisionAnswer("preferred_endpoint", null, null, 0d)
                : new DecisionAnswer("preferred_endpoint", null, "gw-off-set", 0.9));
        var provider = Build(host, decisions, chat, decision: true, decoder: true);

        var outcome = await provider.EvaluateAsync(
            DecisionKind.Routing,
            new Dictionary<string, string> { ["capabilityId"] = "ci.run" },
            Candidates,
            CancellationToken.None);

        Assert.Null(outcome);
    }

    [Fact]
    public async Task ForceLlmClassifierDrivesTheSameAnswer()
    {
        using var host = new KernelTestFactory(fixture, noDispatcher: true);
        await PutPolicyAsync(host, decisionEndpoint: null, decisionMode: "llm");
        var chat = new FakeAiGateway();
        chat.Enqueue(
            "decision-routing",
            """{"preferred":"gw-b","rationale":"fresher session","confidence":0.8}""");
        var provider = Build(host, new FakeDecisionGateway { Peer = chat }, chat, decision: false, decoder: true);

        var outcome = await provider.EvaluateAsync(
            DecisionKind.Routing,
            new Dictionary<string, string> { ["capabilityId"] = "ci.run" },
            Candidates,
            CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal("gw-b", outcome!.PreferredCandidate);
        Assert.Single(chat.Requests);
    }

    [Fact]
    public async Task NoAiAtAllReturnsNullWithZeroCalls()
    {
        using var host = new KernelTestFactory(fixture, noDispatcher: true);
        await PutPolicyAsync(host, decisionEndpoint: null, decisionMode: "auto");
        var chat = new FakeAiGateway();
        var decisions = new FakeDecisionGateway { Peer = chat };
        var provider = Build(host, decisions, chat, decision: false, decoder: false);

        var outcome = await provider.EvaluateAsync(
            DecisionKind.Routing,
            new Dictionary<string, string> { ["capabilityId"] = "ci.run" },
            Candidates,
            CancellationToken.None);

        Assert.Null(outcome);
        Assert.Empty(chat.Requests);
        Assert.Empty(decisions.DecisionRequests);
    }

    [Fact]
    public void ModelProviderIsRegisteredAlongsideDeterministic()
    {
        using var host = new KernelTestFactory(fixture, noDispatcher: true);
        var names = host.Services.GetServices<IDecisionProvider>().Select(p => p.Name).ToList();
        Assert.Contains("deterministic", names);
        Assert.Contains("model", names);
    }

    // ---- plumbing ----------------------------------------------------------

    private static ModelDecisionProvider Build(
        KernelTestFactory host, FakeDecisionGateway decisions, FakeAiGateway chat, bool decision, bool decoder)
    {
        var options = new AiOptions();
        var endpoints = new Dictionary<string, AiEndpointOptions>(StringComparer.OrdinalIgnoreCase);
        if (decision)
        {
            endpoints["decision"] = new AiEndpointOptions { BaseUrl = "http://litellm.test:4000", Model = "jev", ApiKey = "k" };
        }

        if (decoder)
        {
            endpoints["decoder"] = new AiEndpointOptions { BaseUrl = "http://litellm.test:4000", Model = "llm", ApiKey = "k" };
        }

        options.Endpoints.Clear();
        foreach (var (name, value) in endpoints)
        {
            options.Endpoints[name] = value;
        }

        return new ModelDecisionProvider(
            options,
            chat,
            decisions,
            host.Services.GetRequiredService<AiPolicyStore>(),
            host.Services.GetRequiredService<PromptRenderer>());
    }

    /// <summary>Write the hot <c>llm</c> policy row through the admin config
    /// surface (the sanctioned writer). Null decisionEndpoint ⇒ field absent.</summary>
    private static async Task PutPolicyAsync(KernelTestFactory host, string? decisionEndpoint, string decisionMode)
    {
        using var client = host.CreateAuthedClient();
        client.DefaultRequestHeaders.Add("X-Change-Reason", "ladder test");
        client.DefaultRequestHeaders.IfMatch.Add(EntityTagHeaderValue.Any);
        var endpoint = decisionEndpoint is null ? string.Empty : $"\"decisionEndpoint\":\"{decisionEndpoint}\",";
        var response = await client.PatchAsync(
            "/api/admin/config/llm",
            new StringContent(
                $$"""{"enabled":true,"verbosityDefault":"balanced","replyMode":"template",{{endpoint}}"decisionMode":"{{decisionMode}}","maxOutputTokens":600}""",
                Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();
    }
}
