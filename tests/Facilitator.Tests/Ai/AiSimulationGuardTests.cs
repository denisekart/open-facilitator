using Facilitator.Kernel.Ai;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Ai;

/// <summary>
/// The simulation rule, enforced: deterministic tests NEVER call a real AI.
/// Both AI ports resolve to the fakes under the test host, no real-gateway
/// instance exists in the container, and no test host configures an
/// <c>Ai:Endpoints</c> section (a leaked real gateway would throw at
/// composition — the loud failure this guard documents). Rule text lives in
/// tests/Facilitator.Tests/AGENTS.md.
/// </summary>
[Collection("postgres:ingress")]
public sealed class AiSimulationGuardTests(PostgresFixture fixture)
{
    [Fact]
    public void BothAiPortsResolveToTheFakes()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        Assert.IsType<FakeAiGateway>(factory.Services.GetRequiredService<IAiGateway>());
        Assert.IsType<FakeDecisionGateway>(factory.Services.GetRequiredService<IDecisionGateway>());
    }

    [Fact]
    public void NoRealGatewayInstanceExistsInAnyRegisteredService()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var real = factory.Services.GetServices<IAiGateway>()
            .OfType<object>()
            .Concat(factory.Services.GetServices<IDecisionGateway>().OfType<object>())
            .Where(static s => s is OpenAiChatCompletionsGateway or LiteLlmDecisionsGateway)
            .ToList();
        Assert.Empty(real);
    }

    [Fact]
    public void TestHostsNeverConfigureAiEndpoints()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        // No-network law: no endpoint section is ever set, so a real gateway
        // (should one leak past the swap) fails closed instead of reaching
        // the internet.
        Assert.Empty(configuration.GetSection("Ai:Endpoints").GetChildren());
    }
}
