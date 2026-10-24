namespace Facilitator.Kernel.Work;

/// <summary>Wire DTO for a Work (camelCase via serializer options).</summary>
/// <param name="WorkId">Work identity.</param>
/// <param name="ProjectId">Owning project when bound.</param>
/// <param name="Objective">Objective text.</param>
/// <param name="State">Wire state name.</param>
/// <param name="Version">Current fencing version.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="UpdatedAt">Last mutation time (UTC).</param>
public sealed record WorkDto(Guid WorkId, Guid? ProjectId, string Objective, string State, long Version, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Wire mapping for Work views.</summary>
public static class WorkDtoMapper
{
    /// <summary>Map a WorkView to its wire DTO.</summary>
    public static WorkDto ToDto(WorkView work) =>
        new(work.WorkId, work.ProjectId, work.Objective, work.State, work.Version, work.CreatedAt, work.UpdatedAt);
}
