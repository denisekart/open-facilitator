using Npgsql;

namespace Facilitator.Tests.Infrastructure;

/// <summary>
/// The typed window onto <c>facilitator.*</c> for assertions: every query is
/// parameterized and named for the feature it proves, so no test file
/// interleaves SQL strings with GUIDs. One instance per
/// <see cref="PostgresFixture"/>, over a pooled <see cref="NpgsqlDataSource"/>.
/// </summary>
public sealed class TestDbProbe : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Open a pooled data source over the shared container.</summary>
    public TestDbProbe(string connectionString) => _dataSource = NpgsqlDataSource.Create(connectionString);

    /// <summary>Outbox rows for a work, optionally fenced by status/attempts/lease-absence.</summary>
    public Task<long> OutboxCountBy(Guid workId, string? state = null, int? attempts = null, bool noLeaseOwner = false) =>
        ScalarLong(
            "SELECT count(*) FROM facilitator.outbox WHERE work_id = $1"
            + (state is null ? string.Empty : " AND status = $2")
            + (attempts is null ? string.Empty : $" AND attempts = {(state is null ? "$2" : "$3")}")
            + (noLeaseOwner ? " AND lease_owner IS NULL" : string.Empty),
            cmd =>
            {
                cmd.Parameters.AddWithValue(workId);
                if (state is not null)
                {
                    cmd.Parameters.AddWithValue(state);
                }

                if (attempts is not null)
                {
                    cmd.Parameters.AddWithValue(attempts.Value);
                }
            });

    /// <summary>Outbox rows database-wide in one status.</summary>
    public Task<long> OutboxTotal(string state) =>
        ScalarLong("SELECT count(*) FROM facilitator.outbox WHERE status = $1", cmd => cmd.Parameters.AddWithValue(state));

    /// <summary>True when the row is delivered with its settle timestamp written.</summary>
    public async Task<bool> OutboxDelivered(Guid outboxId) =>
        await ScalarBool(
            "SELECT count(*) FROM facilitator.outbox WHERE outbox_id = $1 AND status = 'delivered' AND processed_at IS NOT NULL",
            cmd => cmd.Parameters.AddWithValue(outboxId));

    /// <summary>Outbox row ids for a work (stable text form, for restart-identity checks).</summary>
    public Task<List<string>> OutboxIdTexts(Guid workId) =>
        TextList("SELECT outbox_id::text FROM facilitator.outbox WHERE work_id = $1", workId);

    /// <summary>The outbox row id created for a command, when any.</summary>
    public async Task<Guid?> OutboxIdByCommand(string commandId)
    {
        var rows = await TextList("SELECT outbox_id::text FROM facilitator.outbox WHERE command_id = $1", commandId);
        return rows.Count == 0 ? null : Guid.Parse(Assert.Single(rows));
    }

    /// <summary>The stored envelope jsonb text for a command's outbox row (post-dispatch, to observe the dispatcher's trace stamp).</summary>
    public async Task<string?> OutboxEnvelopeByCommand(string commandId)
    {
        var rows = await TextList("SELECT envelope::text FROM facilitator.outbox WHERE command_id = $1", commandId);
        return rows.Count == 0 ? null : Assert.Single(rows);
    }

    /// <summary>Envelope jsonb snapshots for a work's outbox rows.</summary>
    public Task<List<string>> OutboxEnvelopes(Guid workId) =>
        TextList("SELECT envelope::text FROM facilitator.outbox WHERE work_id = $1", workId);

    /// <summary>Distinct recipient channels a work's outbox rows address.</summary>
    public Task<List<string>> OutboxRecipients(Guid workId) =>
        TextList("SELECT DISTINCT recipient_endpoint_id FROM facilitator.outbox WHERE work_id = $1", workId);

    /// <summary>Age a work's pending rows past their backoff so the dispatcher may claim them now.</summary>
    public Task ExpireOutboxBackoff(Guid workId) =>
        NonQuery("UPDATE facilitator.outbox SET available_at = now() - interval '1 hour' WHERE work_id = $1 AND status = 'pending'",
            cmd => cmd.Parameters.AddWithValue(workId));

    /// <summary>Simulate a crashed replica holding the work's rows (expired lease, attempts burned).</summary>
    public Task MarkOutboxLeaseExpired(Guid workId) =>
        NonQuery(
            "UPDATE facilitator.outbox SET status = 'leased', lease_owner = 'dead-replica', "
            + "leased_until = now() - interval '1 second', attempts = 1 WHERE work_id = $1",
            cmd => cmd.Parameters.AddWithValue(workId));

    /// <summary>Release every expired link-session lease (idempotent: only live rows are touched).</summary>
    public Task ExpireLinkLeases() =>
        NonQuery("UPDATE facilitator.link_sessions SET lease_until = now() - interval '1 second' WHERE lease_until > now()",
            _ => { });

    /// <summary>Timeline rows for a work.</summary>
    public Task<long> TimelineCount(Guid workId) =>
        ScalarLong("SELECT count(*) FROM facilitator.work_events WHERE work_id = $1", cmd => cmd.Parameters.AddWithValue(workId));

    /// <summary>Timeline rows of one kind for a work.</summary>
    public Task<long> TimelineEventCount(Guid workId, string kind) =>
        ScalarLong("SELECT count(*) FROM facilitator.work_events WHERE work_id = $1 AND kind = $2",
            cmd =>
            {
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue(kind);
            });

    /// <summary>State of the inbox row for an envelope id, when one exists.</summary>
    public async Task<string?> InboxState(string eventId)
    {
        var rows = await TextList("SELECT state FROM facilitator.inbox WHERE event_id = $1", eventId);
        return rows.Count == 0 ? null : Assert.Single(rows);
    }

    /// <summary>True when the envelope was admitted fail-closed (failed audit row by envelope id).</summary>
    public async Task<bool> InboxFailedFor(string eventId) =>
        await ScalarBool("SELECT count(*) FROM facilitator.inbox WHERE state = 'failed' AND payload->>'id' = $1",
            cmd => cmd.Parameters.AddWithValue(eventId));

    /// <summary>True when a failed audit row records the given envelope type for the event id.</summary>
    public async Task<bool> InboxFailedAudit(string eventId, string envelopeType) =>
        await ScalarBool(
            "SELECT count(*) FROM facilitator.inbox WHERE state = 'failed' AND event_id = $1 AND payload->>'type' = $2",
            cmd =>
            {
                cmd.Parameters.AddWithValue(eventId);
                cmd.Parameters.AddWithValue(envelopeType);
            });

    /// <summary>Session rows for a logical endpoint (any lease state).</summary>
    public Task<long> SessionCount(string endpointId) =>
        ScalarLong("SELECT count(*) FROM facilitator.link_sessions WHERE logical_endpoint_id = $1",
            cmd => cmd.Parameters.AddWithValue(endpointId));

    /// <summary>The endpoint's session epoch, or null when it has no session row.</summary>
    public async Task<long?> SessionEpoch(string endpointId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT session_epoch FROM facilitator.link_sessions WHERE logical_endpoint_id = $1", connection);
        command.Parameters.AddWithValue(endpointId);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    /// <summary>True when the endpoint's lease extends past the instant.</summary>
    public async Task<bool> SessionLeaseBeyond(string endpointId, DateTimeOffset instant) =>
        await ScalarBool(
            "SELECT count(*) FROM facilitator.link_sessions WHERE logical_endpoint_id = $1 AND lease_until > $2",
            cmd =>
            {
                cmd.Parameters.AddWithValue(endpointId);
                cmd.Parameters.AddWithValue(instant.UtcDateTime);
            });

    /// <summary>True when the delivery's ledger row matches the state (and attempt count, when given).</summary>
    public async Task<bool> DeliveryState(Guid deliveryId, string? state = null, int? attempts = null) =>
        await ScalarBool(
            "SELECT count(*) FROM facilitator.link_deliveries WHERE delivery_id = $1"
            + (state is null ? string.Empty : " AND state = $2")
            + (attempts is null ? string.Empty : $" AND attempts = {(state is null ? "$2" : "$3")}"),
            cmd =>
            {
                cmd.Parameters.AddWithValue(deliveryId);
                if (state is not null)
                {
                    cmd.Parameters.AddWithValue(state);
                }

                if (attempts is not null)
                {
                    cmd.Parameters.AddWithValue(attempts.Value);
                }
            });

    /// <summary>Force a delivery's ledger state (lease-loss simulation).</summary>
    public Task MarkDeliveryState(Guid deliveryId, string state) =>
        NonQuery("UPDATE facilitator.link_deliveries SET state = $2 WHERE delivery_id = $1",
            cmd =>
            {
                cmd.Parameters.AddWithValue(deliveryId);
                cmd.Parameters.AddWithValue(state);
            });

    /// <summary>True when the delivery is acked with a receipt carrying the given status.</summary>
    public async Task<bool> DeliveryReceiptStatus(Guid deliveryId, string receiptStatus) =>
        await ScalarBool(
            "SELECT count(*) FROM facilitator.link_deliveries WHERE delivery_id = $1 AND state = 'acked' AND receipt->>'status' = $2",
            cmd =>
            {
                cmd.Parameters.AddWithValue(deliveryId);
                cmd.Parameters.AddWithValue(receiptStatus);
            });

    /// <summary>True when the delivery is fully acked (state + settle time + structured receipt).</summary>
    public async Task<bool> DeliveryFullyAcked(Guid deliveryId) =>
        await ScalarBool(
            "SELECT count(*) FROM facilitator.link_deliveries WHERE delivery_id = $1 "
            + "AND state = 'acked' AND acked_at IS NOT NULL AND receipt ? 'processedAt' AND receipt->>'status' = 'accepted'",
            cmd => cmd.Parameters.AddWithValue(deliveryId));

    /// <summary>Undelivered (pending) ledger rows for an endpoint.</summary>
    public Task<long> PendingDeliveryCount(string endpointId) =>
        ScalarLong("SELECT count(*) FROM facilitator.link_deliveries WHERE endpoint_id = $1 AND state = 'pending'",
            cmd => cmd.Parameters.AddWithValue(endpointId));

    /// <summary>Dead-letter rows for a work's outbox rows, matching reason and envelope snapshot.</summary>
    public Task<long> DeadLetterCount(Guid workId, string reason) =>
        ScalarLong(
            "SELECT count(*) FROM facilitator.dead_letters dl "
            + "JOIN facilitator.outbox o ON o.outbox_id = dl.outbox_id WHERE o.work_id = $1 "
            + "AND dl.reason = $2 AND dl.envelope ? 'specversion'",
            cmd =>
            {
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue(reason);
            });

    /// <summary>Every table in the facilitator schema.</summary>
    public Task<List<string>> SchemaTables() =>
        TextListRaw("SELECT table_name FROM information_schema.tables WHERE table_schema = 'facilitator'");

    /// <summary>True when the facilitator schema contains the table.</summary>
    public async Task<bool> SchemaHasTable(string name) =>
        (await SchemaTables()).Contains(name, StringComparer.Ordinal);

    /// <summary>Every check-constraint name in the facilitator schema.</summary>
    public Task<List<string>> CheckConstraintNames() =>
        TextListRaw("SELECT constraint_name FROM information_schema.check_constraints WHERE constraint_schema = 'facilitator'");

    /// <summary>True when the seed/migration created the default team.</summary>
    public async Task<bool> TeamExists(string slug) =>
        await ScalarBool("SELECT count(*) FROM facilitator.teams WHERE slug = $1",
            cmd => cmd.Parameters.AddWithValue(slug));

    /// <summary>Hand-mint a facilitator.evidence row (Plan 11 owns minting; tests seed validation paths).</summary>
    public Task SeedEvidence(string evidenceId, string subject, DateTime? expiresAt, DateTime? consumedAt) =>
        NonQuery(
            "INSERT INTO facilitator.evidence (evidence_id, subject, claims, issued_at, expires_at, consumed_at) "
            + "VALUES ($1, $2, '{\"groups\": [\"/teams/team-alpha\"]}', now(), $3, $4)",
            cmd =>
            {
                cmd.Parameters.AddWithValue(evidenceId);
                cmd.Parameters.AddWithValue(subject);
                cmd.Parameters.AddWithValue((object?)expiresAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue((object?)consumedAt ?? DBNull.Value);
            });

    /// <summary>Hand-mint a facilitator.artifacts row (GC tests seed ledger states the API cannot reach without time travel).</summary>
    public Task SeedArtifact(
        Guid artifactId, string state, string retentionPolicy, DateTime? storedAt, Guid? workId,
        string? storageUri = null, DateTime? expiresAt = null) =>
        NonQuery(
            "INSERT INTO facilitator.artifacts (artifact_id, work_id, media_type, size, digest, storage_uri, "
            + "state, verification, origin, created_by, created_at, expires_at, stored_at, retention_policy) "
            + "VALUES ($1, $2, 'application/octet-stream', 11, 'sha256:' || repeat('a', 64), $3, "
            + "$4, 'verified', 'rest', 'seed', now(), coalesce($7, now()), $5, $6)",
            cmd =>
            {
                cmd.Parameters.AddWithValue(artifactId);
                cmd.Parameters.AddWithValue((object?)workId ?? DBNull.Value);
                cmd.Parameters.AddWithValue(storageUri ?? $"kernel-fs:aa/bb/sha256/{new string('a', 64)}");
                cmd.Parameters.AddWithValue(state);
                cmd.Parameters.AddWithValue((object?)storedAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue(retentionPolicy);
                cmd.Parameters.AddWithValue((object?)expiresAt ?? DBNull.Value);
            });

    /// <summary>Force a row's state (crash-recovery seeding: pretend the
    /// backend delete landed but the tombstone transaction did not).</summary>
    public Task RevertArtifactState(Guid artifactId, string state) =>
        NonQuery(
            "UPDATE facilitator.artifacts SET state = $2 WHERE artifact_id = $1",
            cmd =>
            {
                cmd.Parameters.AddWithValue(artifactId);
                cmd.Parameters.AddWithValue(state);
            });

    /// <summary>The approvals ledger row for one approval (null when never recorded).</summary>
    public async Task<ApprovalRow?> Approval(Guid approvalId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT state, action_digest, digest_verification, coalesce(work_id::text, ''), coalesce(decided_by, ''), "
            + "expires_at, created_at, coalesce(requested_by_endpoint_id, '') FROM facilitator.approvals WHERE approval_id = $1", connection);
        cmd.Parameters.AddWithValue(approvalId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new ApprovalRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetFieldValue<DateTime>(5), reader.GetFieldValue<DateTime>(6),
                reader.GetString(7))
            : null;
    }

    /// <summary>Decided-push outbox rows enqueued for one approval's terminal transitions
    /// (Plan 12): recipient channel + envelope jsonb text, oldest first.</summary>
    public async Task<List<(string Recipient, string Envelope)>> DecidedPushRows(Guid approvalId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT recipient_endpoint_id, envelope::text FROM facilitator.outbox "
            + "WHERE event_type = 'org.facilitator.event.approval.decided.v1' AND command_id = ANY($1) ORDER BY created_at", connection);
        cmd.Parameters.AddWithValue(new[]
        {
            $"approval-decided:{approvalId}:approved", $"approval-decided:{approvalId}:denied",
            $"approval-decided:{approvalId}:expired", $"approval-decided:{approvalId}:revoked",
        });
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    /// <summary>True when the facilitator.evidence row exists and is already consumed.</summary>
    public Task<bool> EvidenceConsumed(string evidenceId) =>
        ScalarBool(
            "SELECT count(*) FROM facilitator.evidence WHERE evidence_id = $1 AND consumed_at IS NOT NULL",
            cmd => cmd.Parameters.AddWithValue(evidenceId));

    /// <summary>One approvals ledger assertion row (typed window, no raw SQL in tests).</summary>
    public sealed record ApprovalRow(
        string State, string ActionDigest, string DigestVerification, string WorkId,
        string DecidedBy, DateTime ExpiresAt, DateTime CreatedAt, string RequestedByEndpointId);

    /// <summary>The artifacts ledger row for one artifact (null when never allocated/registered).</summary>
    public async Task<ArtifactRow?> Artifact(Guid artifactId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT state, digest, storage_uri, verification, origin, size, coalesce(work_id::text, ''), "
            + "coalesce(team_slug, ''), coalesce(command_id, '') FROM facilitator.artifacts WHERE artifact_id = $1", connection);
        cmd.Parameters.AddWithValue(artifactId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new ArtifactRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8))
            : null;
    }

    /// <summary>One artifacts ledger assertion row (typed window, no raw SQL in tests).</summary>
    public sealed record ArtifactRow(
        string State, string Digest, string StorageUri, string Verification, string Origin,
        long Size, string WorkId, string TeamSlug, string CommandId);

    /// <summary>Artifacts rows allocated under one commandId (re-allocation must never fork rows).</summary>
    public Task<long> ArtifactCountForCommand(string commandId) =>
        ScalarLong("SELECT count(*) FROM facilitator.artifacts WHERE command_id = $1",
            cmd => cmd.Parameters.AddWithValue(commandId));

    /// <summary>Hand-mint a facilitator.projects row on a roster team (work team-scoping tests).</summary>
    public async Task<Guid> SeedProject(string teamSlug)
    {
        var projectId = Guid.NewGuid();
        await NonQuery(
            "INSERT INTO facilitator.projects (project_id, team_id, name, config, created_at) "
            + "VALUES ($1, (SELECT team_id FROM facilitator.teams WHERE slug = $2), 'proj-' || $1, '{}', now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(projectId);
                cmd.Parameters.AddWithValue(teamSlug);
            });
        return projectId;
    }

    /// <summary>Hand-mint a facilitator.works row in a given state (GC tests pin/reference scenarios need a work without the full API lifecycle).</summary>
    public async Task<Guid> SeedWork(string state)
    {
        var workId = Guid.NewGuid();
        await NonQuery(
            "INSERT INTO facilitator.works (work_id, project_id, objective, state, version, created_at, updated_at) "
            + "VALUES ($1, NULL, 'gc-seed', $2, 1, now(), now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue(state);
            });
        return workId;
    }

    /// <summary>Hand-mint a facilitator.result_manifests row (GC tests seed ledger states the API cannot reach without a full upload flow).</summary>
    public Task SeedResultManifest(
        string manifestId, Guid workId, Guid artifactId, string[] outputArtifactIds, string state, string resultState) =>
        NonQuery(
            "INSERT INTO facilitator.result_manifests (manifest_id, work_id, assignment_id, execution_id, "
            + "revision, digest, artifact_id, output_artifact_ids, result_state, state, recorded_at) "
            + "VALUES ($1, $6, gen_random_uuid(), 'exec-seed', 1, 'sha256:' || repeat('b', 64), "
            + "$2, $3::text[], $5, $4, now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(manifestId);
                cmd.Parameters.AddWithValue(artifactId);
                cmd.Parameters.AddWithValue(outputArtifactIds);
                cmd.Parameters.AddWithValue(state);
                cmd.Parameters.AddWithValue(resultState);
                cmd.Parameters.AddWithValue(workId);
            });

    /// <summary>The result-manifest ledger rows for one assignment chain (null-valued optionals as empty strings).</summary>
    public async Task<List<ManifestRow>> ManifestRowsForAssignment(Guid assignmentId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT manifest_id, work_id::text, assignment_id::text, execution_id, revision, "
            + "coalesce(revision_of, ''), digest, coalesce(structured_digest, ''), artifact_id::text, "
            + "result_state, state, recorded_at FROM facilitator.result_manifests WHERE assignment_id = $1 ORDER BY revision",
            connection);
        cmd.Parameters.AddWithValue(assignmentId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<ManifestRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new ManifestRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
                reader.GetDateTime(11)));
        }

        return rows;
    }

    /// <summary>One result-manifest ledger assertion row (typed window, no raw SQL in tests).</summary>
    public sealed record ManifestRow(
        string ManifestId, string WorkId, string AssignmentId, string ExecutionId, int Revision,
        string RevisionOf, string Digest, string StructuredDigest, string ArtifactId,
        string ResultState, string State, DateTime RecordedAt);

    /// <summary>The broker row for one entry (null when absent), typed window for memory assertions.</summary>
    public async Task<MemoryEntryRow?> MemoryEntry(Guid entryId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT team_slug, status, content, content_hash, revision, "
            + "coalesce(supersedes_entry_id::text, ''), coalesce(expires_at::text, ''), (search <> ''::tsvector) "
            + "FROM facilitator.memory_entries WHERE entry_id = $1", connection);
        cmd.Parameters.AddWithValue(entryId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new MemoryEntryRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetString(5), reader.GetString(6),
                !reader.IsDBNull(7))
            : null;
    }

    /// <summary>One memory-entry assertion row (typed window, no raw SQL in tests).</summary>
    public sealed record MemoryEntryRow(
        string TeamSlug, string Status, string Content, string ContentHash, int Revision,
        string SupersedesEntryId, string ExpiresAt, bool Indexed);

    /// <summary>Usage rows recorded for one entry (the §6.2 usage-event law), optionally one purpose.</summary>
    public Task<long> MemoryUsageCount(Guid entryId, string? purpose = null) =>
        ScalarLong(
            "SELECT count(*) FROM facilitator.memory_usage WHERE entry_id = $1"
            + (purpose is null ? string.Empty : " AND purpose = $2"),
            cmd =>
            {
                cmd.Parameters.AddWithValue(entryId);
                if (purpose is not null)
                {
                    cmd.Parameters.AddWithValue(purpose);
                }
            });

    /// <summary>Live (unrevoked) shares from one entry to one grantee team.</summary>
    public Task<long> MemoryShareCount(Guid entryId, string granteeTeam) =>
        ScalarLong(
            "SELECT count(*) FROM facilitator.memory_shares WHERE entry_id = $1 AND grantee_team_slug = $2 AND revoked_at IS NULL",
            cmd =>
            {
                cmd.Parameters.AddWithValue(entryId);
                cmd.Parameters.AddWithValue(granteeTeam);
            });

    /// <summary>Hand-age a candidate's expiry (the GC tests pin scenarios the API cannot reach without waiting days).</summary>
    public Task ExpireMemoryEntry(Guid entryId) =>
        NonQuery("UPDATE facilitator.memory_entries SET expires_at = now() - interval '1 second' WHERE entry_id = $1",
            cmd => cmd.Parameters.AddWithValue(entryId));

    /// <summary>Artifacts rows registered under one wire artifactId (redelivery dedupe proof).</summary>
    public async Task<List<(Guid ArtifactId, string State, string Verification, string Origin, string StorageUri)>> ArtifactByWireId(string wireId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT artifact_id::text, state, verification, origin, storage_uri FROM facilitator.artifacts WHERE wire_artifact_id = $1", connection);
        cmd.Parameters.AddWithValue(wireId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid, string, string, string, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return rows;
    }

    /// <summary>facilitator.audit rows, newest first, optionally fenced by actor/decision.</summary>
    public async Task<List<AuditRow>> AuditRows(string? actor = null, string? decision = null)
    {
        var clauses = new List<string>();
        var parameters = new List<object>();
        if (actor is not null)
        {
            clauses.Add($"actor = ${parameters.Count + 1}");
            parameters.Add(actor);
        }

        if (decision is not null)
        {
            clauses.Add($"decision = ${parameters.Count + 1}");
            parameters.Add(decision);
        }

        var sql = "SELECT actor, action, decision, coalesce(reason, ''), detail::text, coalesce(policy_version, ''), coalesce(evidence_ref, ''), coalesce(resource, '') "
            + "FROM facilitator.audit"
            + (clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses))
            + " ORDER BY occurred_at DESC";
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters)
        {
            cmd.Parameters.AddWithValue(p);
        }

        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        return rows;
    }

    /// <summary>True when an outbox row with the command id exists in the given status (admin replay probes).</summary>
    public Task<bool> OutboxRowByCommand(string commandId, string? state = null) =>
        ScalarBool(
            "SELECT count(*) FROM facilitator.outbox WHERE command_id = $1"
            + (state is null ? string.Empty : " AND status = $2"),
            cmd =>
            {
                cmd.Parameters.AddWithValue(commandId);
                if (state is not null)
                {
                    cmd.Parameters.AddWithValue(state);
                }
            });

    /// <summary>The dead-letter ids sourced from a work's outbox rows (replay seeding).</summary>
    public async Task<List<string>> DeadLetterIdsForWork(Guid workId) =>
        await TextList(
            "SELECT d.dead_letter_id::text FROM facilitator.dead_letters d "
            + "JOIN facilitator.outbox o ON o.outbox_id = d.outbox_id WHERE o.work_id = $1",
            workId);

    /// <summary>The dead-letter snapshot envelope by dead-letter id (replay byte-identity probe).</summary>
    public async Task<string> DeadLetterSnapshot(Guid deadLetterId) =>
        Assert.Single(await TextList("SELECT envelope::text FROM facilitator.dead_letters WHERE dead_letter_id = $1", deadLetterId));

    /// <summary>Remove a config resource's rows (suite isolation on the shared DB).</summary>
    public async Task AdminConfigPurge(string resource)
    {
        await NonQuery("DELETE FROM facilitator.admin_config_history WHERE resource = $1", cmd => cmd.Parameters.AddWithValue(resource));
        await NonQuery("DELETE FROM facilitator.admin_config WHERE resource = $1", cmd => cmd.Parameters.AddWithValue(resource));
    }

    /// <summary>The result_state of a manifest row (null when absent).</summary>
    public async Task<string?> ManifestResultState(string manifestId)
    {
        var rows = await TextList("SELECT result_state FROM facilitator.result_manifests WHERE manifest_id = $1", manifestId);
        return rows.Count == 0 ? null : Assert.Single(rows);
    }

    /// <summary>The stored admin_config payload for a resource (null when never written).</summary>
    public async Task<(long RowVersion, string Payload)?> AdminConfigRow(string resource)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT row_version, payload::text FROM facilitator.admin_config WHERE resource = $1", connection);
        cmd.Parameters.AddWithValue(resource);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt64(0), reader.GetString(1)) : null;
    }

    /// <summary>Versions stored in the bounded admin_config history for a resource (ascending).</summary>
    public async Task<List<long>> AdminConfigHistoryVersions(string resource)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT version FROM facilitator.admin_config_history WHERE resource = $1 ORDER BY version", connection);
        cmd.Parameters.AddWithValue(resource);
        await using var reader = await cmd.ExecuteReaderAsync();
        var versions = new List<long>();
        while (await reader.ReadAsync())
        {
            versions.Add(reader.GetInt64(0));
        }

        return versions;
    }

    /// <summary>Hand-mint a gateway enrollment row (link views seed enrollments without a live hub session).</summary>
    public Task SeedGatewayEnrollment(string endpointId, string teamSlug, string trustMode) =>
        NonQuery(
            "INSERT INTO facilitator.link_enrollments (enrollment_id, endpoint_id, team_id, token_sha256, trust_mode, "
            + "enrolled_at, expires_at, role, accepts_delegated_claims) "
            + "VALUES (gen_random_uuid(), $1, (SELECT team_id FROM facilitator.teams WHERE slug = $2), 'seed', $3, "
            + "now(), now() + interval '1 hour', 'gateway', false)",
            cmd =>
            {
                cmd.Parameters.AddWithValue(endpointId);
                cmd.Parameters.AddWithValue(teamSlug);
                cmd.Parameters.AddWithValue(trustMode);
            });

    /// <summary>Hand-mint a live link session row for an endpoint.</summary>
    public Task SeedLinkSession(string endpointId, long epoch, DateTime leaseUntil) =>
        NonQuery(
            "INSERT INTO facilitator.link_sessions (logical_endpoint_id, connection_instance_id, replica_id, "
            + "session_epoch, lease_until, last_heartbeat) "
            + "VALUES ($1, 'inst-seed', 'replica-seed', $2, $3, now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(endpointId);
                cmd.Parameters.AddWithValue(epoch);
                cmd.Parameters.AddWithValue(leaseUntil);
            });

    /// <summary>Hand-mint an assignment row for a team in a given status (link views seed affected assignments).</summary>
    public Task SeedAssignmentForTeam(Guid workId, string teamSlug, string status) =>
        NonQuery(
            "INSERT INTO facilitator.assignments (assignment_id, work_id, team_id, status, "
            + "current_execution_generation, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), $1, (SELECT team_id FROM facilitator.teams WHERE slug = $2), $3, 1, now(), now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue(teamSlug);
                cmd.Parameters.AddWithValue(status);
            });

    /// <summary>Hand-mint an approvals ledger row against a work (admin detail seeding).</summary>
    public Task SeedApproval(Guid approvalId, Guid workId) =>
        NonQuery(
            "INSERT INTO facilitator.approvals (approval_id, work_id, action_digest, state, nonce, eligible_actors, "
            + "expires_at, created_at) VALUES ($1, $2, 'sha256:' || repeat('c', 64), 'pending', $3, '[]', "
            + "now() + interval '1 hour', now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(approvalId);
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue($"nonce-{approvalId:N}");
            });

    /// <summary>The assignments minted for a work (admin detail seeding chains).</summary>
    public Task<List<string>> AssignmentIdsForWork(Guid workId) =>
        TextList("SELECT assignment_id::text FROM facilitator.assignments WHERE work_id = $1", workId);

    /// <summary>Hand-mint an execution attempt for an assignment (admin detail seeding).</summary>
    public Task SeedExecutionAttempt(Guid assignmentId, string status) =>
        NonQuery(
            "INSERT INTO facilitator.execution_attempts (execution_id, assignment_id, generation, status, started_at) "
            + "VALUES (gen_random_uuid(), $1, 1, $2, now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(assignmentId);
                cmd.Parameters.AddWithValue(status);
            });

    /// <summary>Rewrite a dead-letter snapshot envelope (replay fail-closed seeding).</summary>
    public Task SetDeadLetterEnvelope(Guid deadLetterId, string envelopeJson) =>
        NonQuery(
            "UPDATE facilitator.dead_letters SET envelope = $2::jsonb WHERE dead_letter_id = $1",
            cmd =>
            {
                cmd.Parameters.AddWithValue(deadLetterId);
                cmd.Parameters.AddWithValue(envelopeJson);
            });

    /// <summary>Role names present in facilitator.identity_roles.</summary>
    public Task<List<string>> IdentityRoleNames() =>
        TextListRaw("SELECT role_name FROM facilitator.identity_roles ORDER BY role_name");

    /// <summary>One audit assertion row (typed window, no raw SQL in tests).</summary>
    public sealed record AuditRow(string? Actor, string Action, string Decision, string Reason, string Detail, string PolicyVersion, string EvidenceRef, string Resource);

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();

    private async Task<bool> ScalarBool(string sql, Action<NpgsqlCommand> bind) =>
        await ScalarLong(sql, bind) == 1;

    private async Task<long> ScalarLong(string sql, Action<NpgsqlCommand> bind)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        bind(command);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task NonQuery(string sql, Action<NpgsqlCommand> bind)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        bind(command);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> TextList(string sql, object parameter)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(parameter);
        return await ReadStrings(command);
    }

    private async Task<List<string>> TextListRaw(string sql)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await ReadStrings(command);
    }

    private static async Task<List<string>> ReadStrings(NpgsqlCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
