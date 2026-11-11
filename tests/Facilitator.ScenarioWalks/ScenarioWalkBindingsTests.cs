using System.Reflection;

using Facilitator.Tests.ScenarioGate;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// The walk-assembly half of the gate: every evidence id carrying the
/// <c>ScenarioWalks:</c> prefix must name a method on a class of THIS
/// assembly right now (DB-free: manifest + reflection only, no fixture
/// containers — a rotting walk binding goes red in milliseconds).
/// </summary>
public sealed class ScenarioWalkBindingsTests : ScenarioEvidenceResolutionTestsBase
{
    /// <inheritdoc />
    protected override Assembly Subject => typeof(ScenarioWalkBindingsTests).Assembly;

    /// <inheritdoc />
    protected override string EvidencePrefix => "ScenarioWalks:";

    public static TheoryData<string> ScenarioIds() => ScenarioIdsWith("ScenarioWalks:");

    [Theory]
    [MemberData(nameof(ScenarioIds))]
    public void EveryEvidenceIdResolves(string id) => ResolveEvidenceFor(id);
}
