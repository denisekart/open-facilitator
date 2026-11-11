using System.Linq.Expressions;

using Facilitator.Kernel.Work;

namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>The single <c>works</c>-row to <see cref="WorkView"/> projection,
/// shared by every read surface so the record shape cannot drift between the
/// store, the inbox-side list, and the API.</summary>
public static class WorkProjection
{
    /// <summary>EF-translatable projection: team resolves to the work's own
    /// <c>team_id</c> when set, else its project's team.</summary>
    public static readonly Expression<Func<WorkEntity, WorkView>> View = w => new WorkView(
        w.WorkId,
        w.ProjectId,
        w.Objective,
        w.State,
        w.Version,
        w.Team != null ? w.Team.Slug : (w.Project == null ? null : w.Project.Team!.Slug),
        w.OwnerActor,
        w.Origin,
        w.ActiveWhere,
        w.Cost,
        w.MergedInto,
        w.CloseReason,
        w.CreatedAt,
        w.UpdatedAt);
}
