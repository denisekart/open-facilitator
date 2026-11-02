using Facilitator.Kernel.Work;

namespace Facilitator.Tests.Work;

/// <summary>
/// Single authority: the guard table matches the design's legal edge set
/// EXACTLY — no implicit jumps, no self-loops, nothing leaves a terminal
/// state. Exhaustive over all 8x8 WorkState pairs; no database needed.
/// </summary>
public class StateGuardTests
{
    private static readonly Dictionary<WorkState, WorkState[]> Legal = new()
    {
        [WorkState.Open] = [WorkState.Planning, WorkState.Blocked, WorkState.Cancelled, WorkState.Failed],
        [WorkState.Planning] = [WorkState.Active, WorkState.Blocked, WorkState.Cancelled, WorkState.Failed],
        [WorkState.Active] = [WorkState.Review, WorkState.Blocked, WorkState.Cancelled, WorkState.Failed],
        [WorkState.Review] = [WorkState.Completed, WorkState.Cancelled],
        [WorkState.Blocked] = [WorkState.Planning, WorkState.Active, WorkState.Cancelled, WorkState.Failed],
        [WorkState.Completed] = [],
        [WorkState.Cancelled] = [],
        [WorkState.Failed] = [],
    };

    public static TheoryData<WorkState, WorkState, bool> AllPairs
    {
        get
        {
            var data = new TheoryData<WorkState, WorkState, bool>();
            foreach (var fromState in Enum.GetValues<WorkState>())
            {
                foreach (var toState in Enum.GetValues<WorkState>())
                {
                    data.Add(fromState, toState, Legal[fromState].Contains(toState));
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void GuardTable_MatchesPlanEdgeSetExactly(WorkState from, WorkState to, bool expected) =>
        Assert.Equal(expected, StateGuard.CanTransition(from, to));

    [Fact]
    public void TerminalStates_HaveNoOutgoingEdges()
    {
        foreach (var state in new[] { WorkState.Completed, WorkState.Cancelled, WorkState.Failed })
        {
            Assert.True(StateGuard.IsTerminal(state), $"{state} must be terminal");
            Assert.Equal(0, Enum.GetValues<WorkState>().Count(to => StateGuard.CanTransition(state, to)));
        }

        Assert.False(StateGuard.IsTerminal(WorkState.Open));
        Assert.False(StateGuard.IsTerminal(WorkState.Blocked));
    }

    [Theory]
    [InlineData("open", WorkState.Open)]
    [InlineData("input_required", (WorkState)(-1))]
    [InlineData("Open", (WorkState)(-1))]
    [InlineData("", (WorkState)(-1))]
    public void WireNames_RoundTrip_AndUnknownFailsClosed(string wire, WorkState expected)
    {
        Assert.Equal(StateGuard.TryParseWork(wire, out var parsed), expected != (WorkState)(-1));
        if (expected != (WorkState)(-1))
        {
            Assert.Equal(expected, parsed);
            Assert.Equal(wire, expected.Wire());
        }
    }
}
