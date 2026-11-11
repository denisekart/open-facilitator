using Facilitator.Kernel.Teams;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>
/// The two role names the kernel declares in code — config cannot remove
/// them, only add to them. <c>operator</c> is the system-operator standing: a
/// principal the config rules already mapped to <c>kernel-operator</c> or
/// <c>framework-admin</c> holds it. <c>team-operator</c> is granted iff the
/// team roster carries a row of capacity <c>operator</c> for one of the
/// acting principal's teams, so team authority comes from a recorded fact
/// about the team, not from a claim an outsider can be given. Both are
/// evaluated by the same pass that evaluates config-mapped rules, so a
/// principal's effective set is one answer, from one place.
/// </summary>
public static class BuiltInRoles
{
    /// <summary>System operator (from the config-mapped operator/admin roles).</summary>
    public const string Operator = "operator";

    /// <summary>Team operator (from the acting team's roster capacity row).</summary>
    public const string TeamOperator = "team-operator";

    /// <summary>True when the name is code-declared (the role store refuses to delete it).</summary>
    public static bool IsBuiltIn(string roleName) =>
        string.Equals(roleName, Operator, StringComparison.Ordinal)
        || string.Equals(roleName, TeamOperator, StringComparison.Ordinal);

    /// <summary>Config roles whose holder is a system operator.</summary>
    private static readonly string[] OperatorSources = ["kernel-operator", "framework-admin"];

    /// <summary>The effective set: config-mapped roles plus the built-ins the principal earns.</summary>
    /// <param name="principal">The mapped principal (its groups name the acting teams).</param>
    /// <param name="configGranted">Roles the config rule engine granted.</param>
    /// <param name="roster">The team roster (source of team-operator standing).</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<IReadOnlySet<string>> EffectiveAsync(
        FacilitatorPrincipal principal,
        IReadOnlySet<string> configGranted,
        TeamRosterStore roster,
        CancellationToken ct)
    {
        var effective = new HashSet<string>(configGranted, StringComparer.Ordinal);
        if (OperatorSources.Any(effective.Contains))
        {
            effective.Add(Operator);
        }

        foreach (var team in TeamScope.TeamsOf(principal))
        {
            if (await roster.IsTeamOperatorAsync(team, principal.Actor, ct))
            {
                effective.Add(TeamOperator);
                break;
            }
        }

        return effective;
    }
}
