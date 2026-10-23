namespace Facilitator.Kernel.Domain;

/// <summary>Canonical Work states (spec §4.1).</summary>
public enum WorkState
{
    Open,
    Planning,
    Active,
    Review,
    Completed,
    Blocked,
    Cancelled,
    Failed,
}

/// <summary>Canonical Assignment states (spec §4.1).</summary>
public enum AssignmentState
{
    Proposed,
    Routed,
    Accepted,
    Running,
    InputRequired,
    Completed,
    Failed,
    Cancelled,
    Expired,
}

/// <summary>
/// Single authority for legal state edges (spec §4.1). No implicit jumps:
/// every transition the kernel performs is looked up here first, so the
/// "do not invent shortcuts" rule is enforced by exactly one table.
/// </summary>
public static class StateGuard
{
    private static readonly IReadOnlyDictionary<WorkState, WorkState[]> WorkEdges = new Dictionary<WorkState, WorkState[]>
    {
        [WorkState.Open] = [WorkState.Planning, WorkState.Blocked, WorkState.Cancelled, WorkState.Failed],
        [WorkState.Planning] = [WorkState.Active, WorkState.Blocked, WorkState.Cancelled, WorkState.Failed],
        [WorkState.Active] = [WorkState.Review, WorkState.Blocked, WorkState.Cancelled, WorkState.Failed],
        [WorkState.Review] = [WorkState.Completed, WorkState.Cancelled],
        // Resume targets: the caller resolves which non-blocked predecessor to
        // resume to via work_events; the guard only lists the legal edges.
        [WorkState.Blocked] = [WorkState.Planning, WorkState.Active, WorkState.Cancelled, WorkState.Failed],
        [WorkState.Completed] = [],
        [WorkState.Cancelled] = [],
        [WorkState.Failed] = [],
    };

    private static readonly IReadOnlyDictionary<AssignmentState, AssignmentState[]> AssignmentEdges = new Dictionary<AssignmentState, AssignmentState[]>
    {
        [AssignmentState.Proposed] = [AssignmentState.Routed, AssignmentState.Cancelled, AssignmentState.Expired],
        [AssignmentState.Routed] = [AssignmentState.Accepted, AssignmentState.Cancelled, AssignmentState.Expired],
        [AssignmentState.Accepted] = [AssignmentState.Running, AssignmentState.Cancelled, AssignmentState.Expired],
        // running→running is illegal: a restart is a generation bump.
        [AssignmentState.Running] = [AssignmentState.InputRequired, AssignmentState.Completed, AssignmentState.Failed, AssignmentState.Cancelled, AssignmentState.Expired],
        [AssignmentState.InputRequired] = [AssignmentState.Running, AssignmentState.Cancelled, AssignmentState.Expired],
        [AssignmentState.Completed] = [],
        [AssignmentState.Failed] = [],
        [AssignmentState.Cancelled] = [],
        [AssignmentState.Expired] = [],
    };

    /// <summary>Wire-name of a Work state (lowercase, as stored + exposed).</summary>
    public static string Wire(this WorkState state) => state switch
    {
        WorkState.Open => "open",
        WorkState.Planning => "planning",
        WorkState.Active => "active",
        WorkState.Review => "review",
        WorkState.Completed => "completed",
        WorkState.Blocked => "blocked",
        WorkState.Cancelled => "cancelled",
        WorkState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>Parse a wire Work state; false for anything unknown.</summary>
    public static bool TryParseWork(string? wire, out WorkState state)
    {
        state = wire switch
        {
            "open" => WorkState.Open,
            "planning" => WorkState.Planning,
            "active" => WorkState.Active,
            "review" => WorkState.Review,
            "completed" => WorkState.Completed,
            "blocked" => WorkState.Blocked,
            "cancelled" => WorkState.Cancelled,
            "failed" => WorkState.Failed,
            _ => (WorkState)(-1),
        };
        return state != (WorkState)(-1);
    }

    /// <summary>Wire-name of an Assignment state.</summary>
    public static string Wire(this AssignmentState state) => state switch
    {
        AssignmentState.Proposed => "proposed",
        AssignmentState.Routed => "routed",
        AssignmentState.Accepted => "accepted",
        AssignmentState.Running => "running",
        AssignmentState.InputRequired => "input_required",
        AssignmentState.Completed => "completed",
        AssignmentState.Failed => "failed",
        AssignmentState.Cancelled => "cancelled",
        AssignmentState.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>All wire names of the Work states (CHECK constraint source).</summary>
    public static IReadOnlyList<string> AllWorkWires { get; } =
        Enum.GetValues<WorkState>().Select(Wire).ToArray();

    /// <summary>All wire names of the Assignment states (CHECK constraint source).</summary>
    public static IReadOnlyList<string> AllAssignmentWires { get; } =
        Enum.GetValues<AssignmentState>().Select(Wire).ToArray();

    /// <summary>True when <paramref name="from"/> → <paramref name="to"/> is a legal edge.</summary>
    public static bool CanTransition(WorkState from, WorkState to) =>
        WorkEdges.TryGetValue(from, out var edges) && Array.IndexOf(edges, to) >= 0;

    /// <summary>True when <paramref name="from"/> → <paramref name="to"/> is a legal edge.</summary>
    public static bool CanTransition(AssignmentState from, AssignmentState to) =>
        AssignmentEdges.TryGetValue(from, out var edges) && Array.IndexOf(edges, to) >= 0;

    /// <summary>True when no outgoing edge exists (Work).</summary>
    public static bool IsTerminal(WorkState state) => WorkEdges[state].Length == 0;
}
