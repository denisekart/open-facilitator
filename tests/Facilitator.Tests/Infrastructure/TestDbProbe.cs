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

    /// <summary>A work's assignment ledger rows (status, capability, generation).</summary>
    public async Task<List<(string Status, string Capability, int Generation)>> AssignmentRowsForWork(Guid workId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT status, coalesce(capability_id, ''), current_execution_generation "
            + "FROM facilitator.assignments WHERE work_id = $1 ORDER BY created_at", connection);
        cmd.Parameters.AddWithValue(workId);
        var rows = new List<(string, string, int)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return rows;
    }

    /// <summary>Crash-replay seeding: force a command's outbox row back to
    /// pending (the sink committed but the mark did not land).</summary>
    public Task RequeueOutboxByCommand(string commandId) =>
        NonQuery(
            "UPDATE facilitator.outbox SET status = 'pending', attempts = 0, available_at = now() - interval '1 hour', "
            + "lease_owner = NULL, leased_until = NULL WHERE command_id = $1",
            cmd => cmd.Parameters.AddWithValue(commandId));

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

    /// <summary>Detail payloads of timeline rows of one kind for a work.</summary>
    public async Task<List<string>> TimelineDetail(Guid workId, string kind)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT detail FROM facilitator.work_events WHERE work_id = $1 AND kind = $2 ORDER BY created_at", connection);
        cmd.Parameters.AddWithValue(workId);
        cmd.Parameters.AddWithValue(kind);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

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

    /// <summary>True when the delivery carries a receipt with the given status
    /// in the given ledger state (default: terminally acked).</summary>
    public async Task<bool> DeliveryReceiptStatus(Guid deliveryId, string receiptStatus, string state = "acked") =>
        await ScalarBool(
            "SELECT count(*) FROM facilitator.link_deliveries WHERE delivery_id = $1 AND state = $3 AND receipt->>'status' = $2",
            cmd =>
            {
                cmd.Parameters.AddWithValue(deliveryId);
                cmd.Parameters.AddWithValue(receiptStatus);
                cmd.Parameters.AddWithValue(state);
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

    /// <summary>Hand-mint a facilitator.evidence row (the approvals ledger owns minting; tests seed validation paths).</summary>
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
    /// (one link-addressed row per flip): recipient channel + envelope jsonb text, oldest first.</summary>
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

    /// <summary>Idempotently ensure a roster team row exists (fleet/work team
    /// isolation: routing tests mint per-test unique teams so a leaked
    /// enrollment from another suite can never share their candidate slot).</summary>
    public Task SeedTeam(string slug) =>
        NonQuery(
            "INSERT INTO facilitator.teams (team_id, slug, created_at) "
            + "VALUES (gen_random_uuid(), $1, now()) ON CONFLICT (slug) DO NOTHING",
            cmd => cmd.Parameters.AddWithValue(slug));

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

    /// <summary>Hold a transactional advisory lock on a dedicated connection
    /// (simulates a peer replica's sweep pass while asserting a second
    /// sweep skips); disposing releases it.</summary>
    public async Task<IAsyncDisposable> HoldAdvisoryLockAsync(int key)
    {
        var connection = await _dataSource.OpenConnectionAsync();
        try
        {
            var tx = await connection.BeginTransactionAsync();
            await using (var cmd = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({key})", connection, (NpgsqlTransaction)tx))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            return new LockHolder(connection, tx);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private sealed class LockHolder(NpgsqlConnection connection, NpgsqlTransaction tx) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await tx.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>One result-manifest ledger assertion row (typed window, no raw SQL in tests).</summary>
    public sealed record ManifestRow(
        string ManifestId, string WorkId, string AssignmentId, string ExecutionId, int Revision,
        string RevisionOf, string Digest, string StructuredDigest, string ArtifactId,
        string ResultState, string State, DateTime RecordedAt);

    /// <summary>Ledger rows recorded under one manifest digest.</summary>
    public Task<long> ManifestRowCountByDigest(string digest) =>
        ScalarLong("SELECT count(*) FROM facilitator.result_manifests WHERE digest = $1",
            cmd => cmd.Parameters.AddWithValue(digest));

    /// <summary>conversation root ids bound for one provider channel.</summary>
    public async Task<List<string>> BindingRoots(string provider, string channel)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT root_message_id FROM facilitator.communication_bindings WHERE provider = $1 AND channel = $2",
            connection);
        cmd.Parameters.AddWithValue(provider);
        cmd.Parameters.AddWithValue(channel);
        return await ReadStrings(cmd);
    }

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

    /// <summary>Usage rows recorded for one entry (the usage-event law), optionally one purpose.</summary>
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

    /// <summary>Hand-mint a live link session row for an endpoint (optional
    /// opened_at for deterministic oldest-online ordering; optional
    /// replicaId to stamp a chosen lease owner).</summary>
    public Task SeedLinkSession(string endpointId, long epoch, DateTime leaseUntil, DateTime? openedAt = null, string replicaId = "replica-seed") =>
        NonQuery(
            "INSERT INTO facilitator.link_sessions (logical_endpoint_id, connection_instance_id, replica_id, "
            + "session_epoch, lease_until, last_heartbeat, opened_at) "
            + "VALUES ($1, 'inst-seed', $5, $2, $3, now(), coalesce($4, now()))",
            cmd =>
            {
                cmd.Parameters.AddWithValue(endpointId);
                cmd.Parameters.AddWithValue(epoch);
                cmd.Parameters.AddWithValue(leaseUntil);
                cmd.Parameters.AddWithValue((object?)openedAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue(replicaId);
            });

    /// <summary>Re-lease an existing session row (a reconnect: new epoch,
    /// fresh lease window) — the link_sessions PK is per-endpoint, so a
    /// second SeedLinkSession for the same endpoint would collide.</summary>
    public Task RefreshLinkLease(string endpointId, long epoch, DateTime leaseUntil) =>
        NonQuery(
            "UPDATE facilitator.link_sessions SET session_epoch = $2, lease_until = $3, last_heartbeat = now() "
            + "WHERE logical_endpoint_id = $1",
            cmd =>
            {
                cmd.Parameters.AddWithValue(endpointId);
                cmd.Parameters.AddWithValue(epoch);
                cmd.Parameters.AddWithValue(leaseUntil);
            });

    /// <summary>Close an open attempt row (lane cleanup: a terminal attempt
    /// is inert for every sweeper's candidate law).</summary>
    public Task EndAttempt(Guid executionId) =>
        NonQuery(
            "UPDATE facilitator.execution_attempts SET status = 'lost', ended_at = now() WHERE execution_id = $1",
            cmd => cmd.Parameters.AddWithValue(executionId));

    /// <summary>Drop an endpoint's link-session row (lane cleanup).</summary>
    public Task DeleteLinkSession(string endpointId) =>
        NonQuery(
            "DELETE FROM facilitator.link_sessions WHERE logical_endpoint_id = $1",
            cmd => cmd.Parameters.AddWithValue(endpointId));

    /// <summary>Drop test fleets by endpoint prefix (per-test isolation: a
    /// leaked live session would join a later test's candidate set).</summary>
    public async Task PurgeFleets(string endpointPrefix)
    {
        await NonQuery(
            "DELETE FROM facilitator.link_sessions WHERE logical_endpoint_id LIKE $1",
            cmd => cmd.Parameters.AddWithValue(endpointPrefix + "%"));
        await NonQuery(
            "DELETE FROM facilitator.link_enrollments WHERE endpoint_id LIKE $1",
            cmd => cmd.Parameters.AddWithValue(endpointPrefix + "%"));
    }

    /// <summary>Store a capability manifest on an enrollment (the funnel's
    /// candidate source; the POST route writes the same column).</summary>
    public Task SeedCapabilityManifest(string endpointId, string manifestJson) =>
        NonQuery(
            "UPDATE facilitator.link_enrollments SET capability_manifest = $2::jsonb WHERE endpoint_id = $1",
            cmd =>
            {
                cmd.Parameters.AddWithValue(endpointId);
                cmd.Parameters.AddWithValue(manifestJson);
            });

    /// <summary>The routing funnel's audit row for one intent command id.</summary>
    public async Task<RoutingDecisionRow?> RoutingDecision(string intentCommandId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT allowed_candidates::text, coalesce(chosen_endpoint_id, ''), status, basis, "
            + "coalesce(provider_ref, ''), attempt_count FROM facilitator.routing_decisions "
            + "WHERE intent_command_id = $1", connection);
        cmd.Parameters.AddWithValue(intentCommandId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new RoutingDecisionRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt32(5));
    }


    /// <summary>One routing-decision audit row (typed window, no raw SQL in tests).</summary>
    public sealed record RoutingDecisionRow(
        string AllowedCandidates, string ChosenEndpointId, string Status, string Basis,
        string ProviderRef, int AttemptCount);

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

    /// <summary>Hand-mint an assignment ledger row (optionally with a pinned
    /// identity/generation — the DAG tests pin the deterministic UUID the
    /// funnel will land); returns the assignment id. Probe seeding for the
    /// nudge/fence lanes where the full funnel is not the subject.</summary>
    public async Task<Guid> SeedAssignmentRow(
        Guid workId, string status, int generation = 1, Guid? assignmentId = null, string? capabilityId = null)
    {
        var id = assignmentId ?? Guid.NewGuid();
        await NonQuery(
            "INSERT INTO facilitator.assignments (assignment_id, work_id, team_id, capability_id, status, "
            + "current_execution_generation, created_at, updated_at) "
            + "VALUES ($1, $2, NULL, $3, $4, $5, now(), now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue((object?)capabilityId ?? DBNull.Value);
                cmd.Parameters.AddWithValue(status);
                cmd.Parameters.AddWithValue(generation);
            });
        return id;
    }

    /// <summary>Hand-mint an execution-attempt row with a pinned start time
    /// (the nudge staleness clock).</summary>
    public Task SeedAttemptRow(
        Guid executionId, Guid assignmentId, int generation, string status, DateTime startedAt) =>
        NonQuery(
            "INSERT INTO facilitator.execution_attempts (execution_id, assignment_id, generation, status, started_at, ended_at) "
            + "VALUES ($1, $2, $3, $4, $5, NULL)",
            cmd =>
            {
                cmd.Parameters.AddWithValue(executionId);
                cmd.Parameters.AddWithValue(assignmentId);
                cmd.Parameters.AddWithValue(generation);
                cmd.Parameters.AddWithValue(status);
                cmd.Parameters.AddWithValue(startedAt);
            });

    /// <summary>Hand-mint the outbox row that carried an assign to an endpoint
    /// — the ownership ledger the nudge sweeper reads (its owner join matches
    /// the N-format assignmentId inside `data`). Stable command id
    /// `work-assign:&lt;assignmentId&gt;-seed`; returns the outbox id.</summary>
    public async Task<Guid> SeedAssignLedgerRow(Guid workId, Guid assignmentId, string endpoint, int generation = 1)
    {
        var outboxId = Guid.NewGuid();
        await NonQuery(
            "INSERT INTO facilitator.outbox (outbox_id, recipient_endpoint_id, aggregate_type, aggregate_id, "
            + "event_type, command_id, envelope, work_id, status, available_at, attempts, created_at, processed_at) "
            + "VALUES ($1, 'link.' || $2, 'work', $3, 'org.facilitator.command.work.assign.v1', "
            + "'work-assign:' || $4 || '-seed', "
            + "jsonb_build_object('data', jsonb_build_object('assignmentId', $5, 'executionGeneration', $6)), "
            + "$3, 'delivered', now(), 1, now(), now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(outboxId);
                cmd.Parameters.AddWithValue(endpoint);
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue(assignmentId.ToString("N"));
                cmd.Parameters.AddWithValue(assignmentId.ToString("N"));
                cmd.Parameters.AddWithValue(generation);
            });
        return outboxId;
    }

    /// <summary>Hand-mint a work timeline row (the sweeper's progress signal).</summary>
    public Task SeedTimelineRow(Guid workId, string kind, string detailJson = "{}") =>
        NonQuery(
            "INSERT INTO facilitator.work_events (event_id, work_id, work_version, kind, actor, detail, created_at) "
            + "VALUES (gen_random_uuid(), $1, 1, $2, 'seed', $3::jsonb, now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue(kind);
                cmd.Parameters.AddWithValue(detailJson);
            });

    /// <summary>Hand-mint (or replace) an admin_config row for a test that
    /// wants configuration state without the admin API roundtrip.</summary>
    public Task SeedAdminConfig(string resource, string payloadJson) =>
        NonQuery(
            "INSERT INTO facilitator.admin_config (resource, row_version, payload, author, reason, updated_at) "
            + "VALUES ($1, 1, $2::jsonb, 'seed', 'test seed', now()) "
            + "ON CONFLICT (resource) DO UPDATE SET payload = excluded.payload, "
            + "row_version = facilitator.admin_config.row_version + 1, updated_at = now()",
            cmd =>
            {
                cmd.Parameters.AddWithValue(resource);
                cmd.Parameters.AddWithValue(payloadJson);
            });

    /// <summary>Acked link delivery for an outbox row — the sweeper counts it
    /// as work activity (the freshness signal on a live lane).</summary>
    public Task SeedAckedDelivery(Guid outboxId, string endpointId, DateTime ackedAt) =>
        NonQuery(
            "INSERT INTO facilitator.link_deliveries (delivery_id, endpoint_id, session_epoch, state, "
            + "delivered_at, acked_at, attempts) "
            + "VALUES ($1, $2, 1, 'acked', $3, $3, 1)",
            cmd =>
            {
                cmd.Parameters.AddWithValue(outboxId);
                cmd.Parameters.AddWithValue(endpointId);
                cmd.Parameters.AddWithValue(ackedAt);
            });

    /// <summary>Hand-mint an approvals ledger row against a work (admin detail seeding).</summary>
    public Task SeedApproval(Guid approvalId, Guid workId, string eligibleActorsJson = "[]") =>
        NonQuery(
            "INSERT INTO facilitator.approvals (approval_id, work_id, action_digest, state, nonce, eligible_actors, "
            + "expires_at, created_at) VALUES ($1, $2, 'sha256:' || repeat('c', 64), 'pending', $3, $4::jsonb, "
            + "now() + interval '1 hour', now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(approvalId);
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue($"nonce-{approvalId:N}");
                cmd.Parameters.AddWithValue(eligibleActorsJson);
            });

    /// <summary>Simulate a second writer on a config resource: bump the row, stamp attribution, history row (E2E 412 path).</summary>
    public async Task AdminConfigConcurrentWrite(string resource, string payloadJson, string author, string reason)
    {
        await NonQuery(
            "UPDATE facilitator.admin_config SET row_version = row_version + 1, payload = $2::jsonb, author = $3, "
            + "reason = $4, updated_at = now() WHERE resource = $1",
            cmd =>
            {
                cmd.Parameters.AddWithValue(resource);
                cmd.Parameters.AddWithValue(payloadJson);
                cmd.Parameters.AddWithValue(author);
                cmd.Parameters.AddWithValue(reason);
            });
        await NonQuery(
            "INSERT INTO facilitator.admin_config_history (resource, version, payload, author, reason, updated_at) "
            + "SELECT $1, row_version, payload, $3, $4, now() FROM facilitator.admin_config WHERE resource = $1",
            cmd =>
            {
                cmd.Parameters.AddWithValue(resource);
                cmd.Parameters.AddWithValue(payloadJson);
                cmd.Parameters.AddWithValue(author);
                cmd.Parameters.AddWithValue(reason);
            });
    }

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

    // ---- Provider plane --------------------------------------------------

    /// <summary>provider_events rows for one delivery id (typed window).</summary>
    public Task<long> ProviderEventCount(string provider, string deliveryId) =>
        ScalarLong(
            "SELECT count(*) FROM facilitator.provider_events WHERE provider = $1 AND provider_delivery_id = $2",
            cmd =>
            {
                cmd.Parameters.AddWithValue(provider);
                cmd.Parameters.AddWithValue(deliveryId);
            });

    /// <summary>The verified_reference judgment stored for one delivery id.</summary>
    public async Task<string?> ProviderEventJudgment(string deliveryId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT coalesce(verified_reference, '') FROM facilitator.provider_events WHERE provider_delivery_id = $1",
            connection);
        cmd.Parameters.AddWithValue(deliveryId);
        var value = await cmd.ExecuteScalarAsync();
        return value as string is "" or null ? null : (string)value;
    }

    /// <summary>communication_bindings row for a conversation (Work id + parent ledger).</summary>
    public Task<(Guid? WorkId, string? ParentMessageId)> BindingFor(string channel, string root) =>
        BindingFor("mattermost", channel, root);

    /// <summary>communication_bindings row for one provider's conversation.</summary>
    public async Task<(Guid? WorkId, string? ParentMessageId)> BindingFor(string provider, string channel, string root)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT coalesce(work_id::text, ''), coalesce(parent_message_id, '') "
            + "FROM facilitator.communication_bindings WHERE provider = $3 AND channel = $1 AND root_message_id = $2",
            connection);
        cmd.Parameters.AddWithValue(channel);
        cmd.Parameters.AddWithValue(root);
        cmd.Parameters.AddWithValue(provider);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.GetString(0) is "" ? null : Guid.Parse(reader.GetString(0)), reader.GetString(1) is "" ? null : reader.GetString(1))
            : (null, null);
    }

    /// <summary>The binding's Work id for a conversation (null row or null work ⇒ null).</summary>
    public Task<Guid?> BindingWorkId(string channel, string root) => BindingWorkId("mattermost", channel, root);

    /// <summary>The binding's Work id for one provider's conversation.</summary>
    public async Task<Guid?> BindingWorkId(string provider, string channel, string root) => (await BindingFor(provider, channel, root)).WorkId;

    /// <summary>provider_identities row for a (provider, tenant, account) triple.</summary>
    public async Task<bool> ProviderIdentityExists(string provider, string tenant, string accountId) =>
        await ScalarBool(
            "SELECT count(*) FROM facilitator.provider_identities WHERE provider = $1 AND tenant = $2 AND account_id = $3",
            cmd =>
            {
                cmd.Parameters.AddWithValue(provider);
                cmd.Parameters.AddWithValue(tenant);
                cmd.Parameters.AddWithValue(accountId);
            });

    /// <summary>provider_link_challenges rows for one account (challenge provenance).</summary>
    public Task<long> ProviderChallengeCount(string accountId) =>
        ScalarLong("SELECT count(*) FROM facilitator.provider_link_challenges WHERE account_id = $1",
            cmd => cmd.Parameters.AddWithValue(accountId));

    /// <summary>The most recent challenge id for one account (confirmation-row assertion).</summary>
    public async Task<string?> ProviderChallengeId(string accountId)
    {
        var rows = await TextList(
            "SELECT challenge_id::text FROM facilitator.provider_link_challenges WHERE account_id = $1 ORDER BY created_at DESC LIMIT 1",
            accountId);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Fence a challenge's expiry (expired-redemption seeding).</summary>
    public Task SetChallengeExpiry(string accountId, bool expired) =>
        NonQuery(
            $"UPDATE facilitator.provider_link_challenges SET expires_at = {(expired ? "now() - interval '1 minute'" : "now() + interval '10 minutes'")} WHERE account_id = $1",
            cmd => cmd.Parameters.AddWithValue(accountId));

    /// <summary>Seed a linked provider identity directly (linked-mention tests without a full redeem flow).</summary>
    public Task SeedProviderIdentity(string provider, string tenant, string accountId, string subject) =>
        NonQuery(
            "INSERT INTO facilitator.provider_identities "
            + "(identity_id, provider, tenant, account_id, oidc_issuer, oidc_subject, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), $1, $2, $3, 'seed-issuer', $4, now(), now()) "
            + "ON CONFLICT (provider, tenant, account_id) DO UPDATE SET oidc_subject = EXCLUDED.oidc_subject, updated_at = now()",
            cmd =>
            {
                cmd.Parameters.AddWithValue(provider);
                cmd.Parameters.AddWithValue(tenant);
                cmd.Parameters.AddWithValue(accountId);
                cmd.Parameters.AddWithValue(subject);
            });

    /// <summary>Expire one endpoint's live session lease (per-lane nudge
    /// isolation: the sweeper skips offline owners, so a finished test's
    /// lane stops joining later sweeps without touching foreign leases).</summary>
    public Task ExpireSessionLease(string endpointId) =>
        NonQuery(
            "UPDATE facilitator.link_sessions SET lease_until = now() - interval '1 second' "
            + "WHERE logical_endpoint_id = $1 AND lease_until > now()",
            cmd => cmd.Parameters.AddWithValue(endpointId));

    /// <summary>The nudge ledger rows for an assignment (episode, count,
    /// generation), ordered by episode — the bounded-nudge window.</summary>
    public async Task<List<(int Episode, int NudgeCount, int Generation)>> NudgeRowsForAssignment(Guid assignmentId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT stall_episode, nudge_count, generation FROM facilitator.nudges "
            + "WHERE assignment_id = $1 ORDER BY stall_episode", connection);
        cmd.Parameters.AddWithValue(assignmentId);
        var rows = new List<(int, int, int)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
        }

        return rows;
    }

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

    /// <summary>Hand-mint a result-manifest ledger row pinned to a specific
    /// assignment (the DAG gate's <c>result_validated</c> edge reads the row
    /// by <c>assignment_id</c>, which <c>SeedResultManifest</c> cannot target),
    /// in the given <c>result_state</c>.</summary>
    public Task SeedAssignmentManifest(string manifestId, Guid workId, Guid assignmentId, string resultState) =>
        NonQuery(
            "INSERT INTO facilitator.result_manifests (manifest_id, work_id, assignment_id, execution_id, "
            + "revision, digest, artifact_id, output_artifact_ids, result_state, state, recorded_at) "
            + "VALUES ($1, $2, $3, 'exec-dag', 1, 'sha256:' || repeat('b', 64), gen_random_uuid(), "
            + "'{}'::text[], $4, 'recorded', now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(manifestId);
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue(assignmentId);
                cmd.Parameters.AddWithValue(resultState);
            });

    /// <summary>The inbox settle pair (state, error) for one envelope id,
    /// when a row exists — the fence law's refused-audit window.</summary>
    public async Task<(string State, string? Error)?> InboxStateError(string eventId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT state, error FROM facilitator.inbox WHERE event_id = $1", connection);
        cmd.Parameters.AddWithValue(eventId);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    /// <summary>The stored envelope payload for one event id (the session-loss
    /// law asserts the synthesized event's data block on the wire).</summary>
    public async Task<string?> InboxPayload(string eventId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT payload::text FROM facilitator.inbox WHERE event_id = $1", connection);
        cmd.Parameters.AddWithValue(eventId);
        var v = await cmd.ExecuteScalarAsync();
        return v as string;
    }

    /// <summary>The attempt row's terminal vocabulary state for an execution
    /// (the session-loss law's effect on <c>execution_attempts</c>).</summary>
    public async Task<string?> AttemptState(Guid executionId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT status FROM facilitator.execution_attempts WHERE execution_id = $1", connection);
        cmd.Parameters.AddWithValue(executionId);
        var v = await cmd.ExecuteScalarAsync();
        return v as string;
    }

    /// <summary>Move an assignment's generation line forward (the re-dispatch
    /// effect the session-loss candidate law fences straggers against).</summary>
    public Task BumpAssignmentGeneration(Guid assignmentId) =>
        NonQuery(
            "UPDATE facilitator.assignments SET current_execution_generation = current_execution_generation + 1 "
            + "WHERE assignment_id = $1",
            cmd => cmd.Parameters.AddWithValue(assignmentId));

    /// <summary>Drop an admin_config row (manual-mode state: no policy).</summary>
    public Task DeleteAdminConfig(string resource) =>
        NonQuery(
            "DELETE FROM facilitator.admin_config WHERE resource = $1",
            cmd => cmd.Parameters.AddWithValue(resource));

    // ---- Provisioning plane ----------------------------------------------

    /// <summary>Hand-mint a nudges ledger row at a generation (the stalled-arm
    /// trigger: a row whose nudge_count sits at the cap).</summary>
    public Task SeedNudgeRow(Guid assignmentId, int generation, int episode, int count) =>
        NonQuery(
            "INSERT INTO facilitator.nudges (assignment_id, generation, stall_episode, nudge_count, "
            + "last_nudge_at, last_progress_at) VALUES ($1, $2, $3, $4, now(), now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(assignmentId);
                cmd.Parameters.AddWithValue(generation);
                cmd.Parameters.AddWithValue(episode);
                cmd.Parameters.AddWithValue(count);
            });

    /// <summary>Hand-mint a pending routing_decisions row with a basis (the
    /// no-capacity arm's trigger; the funnel's own still-un-routed marker).</summary>
    public Task SeedRoutingDecision(string intentCommandId, Guid workId, string teamSlug, string basis, string capabilityId = "cap.seed") =>
        NonQuery(
            "INSERT INTO facilitator.routing_decisions (decision_id, work_id, intent_command_id, capability_id, "
            + "team_slug, allowed_candidates, chosen_endpoint_id, status, basis, provider_ref, "
            + "last_routing_attempt_at, attempt_count, created_at, updated_at) "
            + "VALUES (gen_random_uuid(), $2, $1, $5, $3, '[]'::jsonb, NULL, 'pending', $4, NULL, now(), 1, now(), now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(intentCommandId);
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue(teamSlug);
                cmd.Parameters.AddWithValue(basis);
                cmd.Parameters.AddWithValue(capabilityId);
            });

    /// <summary>Hand-mint the outbox row that carried an assign WITH the full
    /// data block (capability + inputs) the worker-lost arm rebuilds a fresh
    /// routing intent from; returns the outbox id.</summary>
    public async Task<Guid> SeedAssignLedgerRowWithContext(Guid workId, Guid assignmentId, string endpoint, string capabilityId, string objective, int generation = 1)
    {
        var outboxId = Guid.NewGuid();
        await NonQuery(
            "INSERT INTO facilitator.outbox (outbox_id, recipient_endpoint_id, aggregate_type, aggregate_id, "
            + "event_type, command_id, envelope, work_id, status, available_at, attempts, created_at, processed_at) "
            + "VALUES ($1, 'link.' || $2, 'work', $3, 'org.facilitator.command.work.assign.v1', "
            + "'work-assign:' || $4 || '-seed', "
            + "jsonb_build_object('data', jsonb_build_object('assignmentId', $4, 'executionGeneration', $6, "
            + "'capabilityId', $5, 'targetId', 'kernel', 'inputs', "
            + "jsonb_build_object('objective', $7))), "
            + "$3, 'delivered', now(), 1, now(), now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(outboxId);
                cmd.Parameters.AddWithValue(endpoint);
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue(assignmentId.ToString("N"));
                cmd.Parameters.AddWithValue(capabilityId);
                cmd.Parameters.AddWithValue(generation);
                cmd.Parameters.AddWithValue(objective);
            });
        return outboxId;
    }

    /// <summary>Hand-mint a provisioning_operations row in a given state (the
    /// cap tests seed ledger states the sweep cannot reach without a launcher).</summary>
    public Task SeedProvisionOp(string operationId, string workloadId, string team, string operation, string state, string reason, int? generation = null) =>
        NonQuery(
            "INSERT INTO facilitator.provisioning_operations (operation_id, workload_id, team, operation, "
            + "state, work_id, assignment_id, generation, reason, created_at, updated_at) "
            + "VALUES ($1, $2, $3, $4, $5, NULL, NULL, $6, $7, now(), now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(operationId);
                cmd.Parameters.AddWithValue(workloadId);
                cmd.Parameters.AddWithValue(team);
                cmd.Parameters.AddWithValue(operation);
                cmd.Parameters.AddWithValue(state);
                cmd.Parameters.AddWithValue((object?)generation ?? DBNull.Value);
                cmd.Parameters.AddWithValue(reason);
            });

    /// <summary>One provisioning ledger row (typed window, no raw SQL in tests).</summary>
    public sealed record ProvisionOpRow(
        string OperationId, string Operation, string State, string Reason, string? WorkId, string? AssignmentId, int? Generation);

    /// <summary>The provisioning ledger rows for one workload (by op identity).</summary>
    public async Task<List<ProvisionOpRow>> ProvisionOpsFor(string workloadId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT operation_id, operation, state, reason, work_id::text, assignment_id::text, generation "
            + "FROM facilitator.provisioning_operations WHERE workload_id = $1 ORDER BY created_at", connection);
        cmd.Parameters.AddWithValue(workloadId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<ProvisionOpRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new ProvisionOpRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6)));
        }

        return rows;
    }

    /// <summary>Outbox rows addressed to one recipient channel (the launcher's
    /// inbox depth: one per emitted envelope, never more).</summary>
    public Task<long> OutboxCountForRecipient(string recipientEndpointId) =>
        ScalarLong("SELECT count(*) FROM facilitator.outbox WHERE recipient_endpoint_id = $1",
            cmd => cmd.Parameters.AddWithValue(recipientEndpointId));

    /// <summary>The status stored for a command's outbox row (null when absent).</summary>
    public async Task<string?> OutboxStatusByCommand(string commandId)
    {
        var rows = await TextList("SELECT status FROM facilitator.outbox WHERE command_id = $1", commandId);
        return rows.Count == 0 ? null : Assert.Single(rows);
    }

    /// <summary>Outbox rows database-wide of one event type (the offline-
    /// launcher law: ops land with ZERO provision envelopes).</summary>
    public Task<long> OutboxCountByEventType(string eventType) =>
        ScalarLong("SELECT count(*) FROM facilitator.outbox WHERE event_type = $1",
            cmd => cmd.Parameters.AddWithValue(eventType));

    /// <summary>Settle a command's outbox row as delivered (the dispatcher's
    /// own settle: status + processed stamp).</summary>
    public Task MarkOutboxDeliveredByCommand(string commandId) =>
        NonQuery(
            "UPDATE facilitator.outbox SET status = 'delivered', processed_at = now() WHERE command_id = $1",
            cmd => cmd.Parameters.AddWithValue(commandId));

    /// <summary>Hand-mint a delivered launcher command row (the mirror's
    /// "delivered" observation without running a dispatcher; no receipt row,
    /// so the rejection join stays empty).</summary>
    public Task SeedLauncherDeliveryRow(string commandId, string launcherEndpoint, string eventType) =>
        NonQuery(
            "INSERT INTO facilitator.outbox (outbox_id, recipient_endpoint_id, aggregate_type, aggregate_id, "
            + "event_type, command_id, envelope, work_id, status, available_at, attempts, created_at, processed_at) "
            + "VALUES (gen_random_uuid(), 'link.' || $2, 'work', NULL, $3, $1, '{}', NULL, 'delivered', "
            + "now(), 1, now(), now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(commandId);
                cmd.Parameters.AddWithValue(launcherEndpoint);
                cmd.Parameters.AddWithValue(eventType);
            });

    /// <summary>facilitator.ai_turns rows for one conversation key with a
    /// given basis (the slash/model/fallback ledger law).</summary>
    public Task<long> AiTurnCount(string conversationKey, string basis) =>
        ScalarLong(
            "SELECT count(*) FROM facilitator.ai_turns WHERE conversation_key = $1 AND basis = $2",
            cmd =>
            {
                cmd.Parameters.AddWithValue(conversationKey);
                cmd.Parameters.AddWithValue(basis);
            });

    /// <summary>facilitator.ai_turns count for one conversation key, any basis.</summary>
    public Task<long> AiTurnCountTotal(string conversationKey) =>
        ScalarLong("SELECT count(*) FROM facilitator.ai_turns WHERE conversation_key = $1",
            cmd => cmd.Parameters.AddWithValue(conversationKey));

    /// <summary>Ledger texts of a conversation's turns of one basis (chronological).</summary>
    public async Task<List<string>> AiTurnTexts(string conversationKey, string basis)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT text FROM facilitator.ai_turns WHERE conversation_key = $1 AND basis = $2 ORDER BY occurred_at",
            connection);
        cmd.Parameters.AddWithValue(conversationKey);
        cmd.Parameters.AddWithValue(basis);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    /// <summary>decoded jsonb of the newest ledger 'in' row of a conversation
    /// (null when no model pass was ledgered).</summary>
    public async Task<string?> AiTurnDecoded(string conversationKey)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT decoded::text FROM facilitator.ai_turns "
            + "WHERE conversation_key = $1 AND direction = 'in' AND decoded IS NOT NULL "
            + "ORDER BY occurred_at DESC LIMIT 1",
            connection);
        cmd.Parameters.AddWithValue(conversationKey);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>Status + charged attempts of a command's outbox row (null when absent).</summary>
    public async Task<(string Status, int Attempts)?> OutboxStatusAttemptsByCommand(string commandId)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT status, attempts FROM facilitator.outbox WHERE command_id = $1", connection);
        cmd.Parameters.AddWithValue(commandId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return (reader.GetString(0), reader.GetInt32(1));
    }

    /// <summary>Count of outbox rows carrying a command id (single-row law checks).</summary>
    public Task<long> OutboxCountByCommand(string commandId) =>
        ScalarLong("SELECT count(*) FROM facilitator.outbox WHERE command_id = $1",
            cmd => cmd.Parameters.AddWithValue(commandId));

    /// <summary>Age a command's row past its backoff so the dispatcher may claim it now.</summary>
    public Task ExpireOutboxBackoffByCommand(string commandId) =>
        NonQuery("UPDATE facilitator.outbox SET available_at = now() - interval '1 hour' WHERE command_id = $1",
            cmd => cmd.Parameters.AddWithValue(commandId));

    /// <summary>Settle every pending outbox row (family hygiene: the claim
    /// is global and cross-class debris can starve a fresh row out of the
    /// batch or get re-delivered against the wrong host's fakes).</summary>
    public Task SettlePendingOutboxRows() =>
        NonQuery("UPDATE facilitator.outbox SET status = 'delivered', processed_at = now() WHERE status = 'pending'",
            _ => { });

    /// <summary>Hand a command's row back to the head of the queue (the crash-replay
    /// simulation: a committed pass whose outbox row never settled).</summary>
    public Task ReplayOutboxRowByCommand(string commandId) =>
        NonQuery(
            "UPDATE facilitator.outbox SET status = 'pending', attempts = 0, available_at = now(), "
            + "lease_owner = NULL, leased_until = NULL WHERE command_id = $1",
            cmd => cmd.Parameters.AddWithValue(commandId));

    /// <summary>Hand-mint an actor verbosity preference row (precedence tests).</summary>
    public Task SeedActorPreference(string actor, string verbosity) =>
        NonQuery(
            "INSERT INTO facilitator.actor_preferences (actor, verbosity, extras, updated_at) "
            + "VALUES ($1, $2, '{}'::jsonb, now()) ON CONFLICT (actor) DO UPDATE SET verbosity = $2",
            cmd =>
            {
                cmd.Parameters.AddWithValue(actor);
                cmd.Parameters.AddWithValue(verbosity);
            });
}
