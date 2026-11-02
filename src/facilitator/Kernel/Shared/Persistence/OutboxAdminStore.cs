namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>
/// Persistence seam behind the admin <c>/api</c> endpoints (dead-letter
/// visibility + the dev link-outbox affordance), so no endpoint lambda ever
/// touches a DbContext directly.
/// </summary>
public sealed class OutboxAdminStore(OutboxRepository outbox)
{
    /// <summary>Newest dead letters, newest first.</summary>
    public Task<IReadOnlyList<DeadLetterRow>> ListDeadLettersAsync(int limit, CancellationToken ct) =>
        outbox.ListDeadLettersAsync(limit, ct);

    /// <summary>Dev/test affordance: append one outbox row + wake the dispatchers.</summary>
    public Task<Guid> AppendDevWorkAsync(string recipientEndpointId, string eventType, string commandId, string envelopeJson, CancellationToken ct) =>
        outbox.AppendAsync(recipientEndpointId, eventType, commandId, envelopeJson, ct);

    /// <summary>Admin replay: re-enqueue a dead-lettered envelope verbatim.</summary>
    public Task<OutboxRepository.ReplayOutcome> ReplayAsync(Guid deadLetterId, string? actor, CancellationToken ct) =>
        outbox.ReplayAsync(deadLetterId, actor, ct);
}
