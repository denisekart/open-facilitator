using System.Text.Json.Nodes;

namespace Facilitator.Kernel.Work;

/// <summary>Wire DTO for a Work (camelCase via serializer options).</summary>
/// <param name="WorkId">Work identity.</param>
/// <param name="ProjectId">Owning project when bound.</param>
/// <param name="Objective">Objective text.</param>
/// <param name="State">Wire state name.</param>
/// <param name="Version">Current fencing version.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="UpdatedAt">Last mutation time (UTC).</param>
public sealed record WorkDto(
    Guid WorkId,
    Guid? ProjectId,
    string Objective,
    string State,
    long Version,
    string? TeamSlug,
    string? OwnerActor,
    string? Origin,
    JsonNode? ActiveWhere,
    JsonNode? Cost,
    Guid? MergedInto,
    string? CloseReason,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Wire mapping for Work views.</summary>
public static class WorkDtoMapper
{
    /// <summary>Map a WorkView to its wire DTO.</summary>
    public static WorkDto ToDto(WorkView work) =>
        new(work.WorkId, work.ProjectId, work.Objective, work.State, work.Version, work.TeamSlug,
            work.OwnerActor, work.Origin, JsonOrNull(work.ActiveWhere), JsonOrNull(work.Cost),
            work.MergedInto, work.CloseReason, work.CreatedAt, work.UpdatedAt);

    private static JsonNode? JsonOrNull(string? jsonb) =>
        jsonb is { Length: > 0 } ? JsonNode.Parse(jsonb) : null;
}
