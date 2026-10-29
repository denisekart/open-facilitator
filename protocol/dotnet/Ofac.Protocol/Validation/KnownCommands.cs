namespace Ofac.Protocol.Validation;

/// <summary>
/// Fail-closed command table: the exact sixteen <c>org.facilitator.command.*.v1</c>
/// types the v1 contract accepts. Mirrors KNOWN_COMMAND_TYPES in
/// <c>src/protocol/src/validate.ts</c> and the schema-derived set in
/// <c>protocol/tools/Validate.Fixtures</c>; a command type outside this set is
/// rejected <c>unknown-command</c> (spec §5.5).
/// </summary>
public static class KnownCommands
{
    /// <summary>All command envelope <c>type</c> values the contract knows.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "org.facilitator.command.approval.consume.v1",
        "org.facilitator.command.approval.respond.v1",
        "org.facilitator.command.artifact.request_upload.v1",
        "org.facilitator.command.artifact.confirm_upload.v1",
        "org.facilitator.command.context.fetch.v1",
        "org.facilitator.command.work.assign.v1",
        "org.facilitator.command.work.cancel.v1",
        "org.facilitator.command.work.nudge.v1",
        "org.facilitator.command.work.status.request.v1",
        "org.facilitator.command.work.steer.v1",
        "org.facilitator.command.memory.retain.v1",
        "org.facilitator.command.memory.recall.v1",
        "org.facilitator.command.communication.reply.v1",
        "org.facilitator.command.communication.react.v1",
        "org.facilitator.command.communication.update.v1",
        "org.facilitator.command.communication.acknowledge.v1",
    };
}
