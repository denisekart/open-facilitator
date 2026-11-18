using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Facilitator.Tests.Work;

/// <summary>
/// The dispatch-time context fold, driven straight on the service over an
/// ambient context (the routing sink's shape): the template vocabulary
/// (<c>$binding.reference</c>, <c>$work.team</c>, <c>$project.tags</c> —
/// closed: an unknown template resolves null, a literal rides verbatim),
/// the missing-work honest refusal, the envelope/work/timeline/media/memory
/// sources with their per-leg honesty (dropped media names its reason),
/// the history leg's three states (no-bound-thread, park-pending-fetch with
/// the deterministic fetchId + routed command row, deadline after a parked
/// expiry, and the audit-answered window clipped by the team's policy cap),
/// the budget law (over budget ⇒ trim the oldest NON-envelope/work source
/// and flag the document truncated), and the document's evidence tail: a
/// digest-stamped kernel artifact row + one <c>context.dispatch</c> audit.
/// No AI leg exists in this plane; the memory source rides the real
/// Postgres-FTS broker with seeded rows.
/// </summary>
[Collection("postgres:work")]
public sealed class ContextBuilderTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private readonly KernelTestFactory _factory = new(fixture, noDispatcher: true, settings:
        new Dictionary<string, string?>
        {
            // The fold routes a parked thread fetch to the SOURCE adapter:
            // a bound provider is the only way the 3.2 leg can fire.
            ["Providers:ctxbprov:Enabled"] = "true",
            ["Providers:ctxbprov:EndpointId"] = "ctxb.endpoint",
            ["Providers:ctxbprov:Tenant"] = "ctxb-tenant",
        });
    private readonly List<string> _teamSlugs = [];
    private readonly List<Guid> _workIds = [];
    private readonly List<Guid> _projectIds = [];
    private readonly List<Guid> _bindingIds = [];
    private readonly List<Guid> _artifactIds = [];
    private readonly List<Guid> _entryIds = [];
    private readonly List<Guid> _scopeIds = [];
    private readonly List<string> _outboxCommands = [];
    private readonly List<Guid> _auditIds = [];

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _ = _factory.CreateAuthedClient(); // boots migrations
        await fixture.Db.DeleteAdminConfig("media-policy");
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        foreach (var command in _outboxCommands)
        {
            await NonQueryAsync("DELETE FROM facilitator.outbox WHERE command_id = $1",
                cmd => cmd.Parameters.AddWithValue(command));
        }

        foreach (var id in _artifactIds)
        {
            await NonQueryAsync("DELETE FROM facilitator.artifacts WHERE artifact_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
        }

        foreach (var id in _entryIds)
        {
            await NonQueryAsync("DELETE FROM facilitator.memory_usage WHERE entry_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
            await NonQueryAsync("DELETE FROM facilitator.memory_entries WHERE entry_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
        }

        foreach (var id in _scopeIds)
        {
            await NonQueryAsync("DELETE FROM facilitator.memory_scopes WHERE scope_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
        }

        foreach (var id in _bindingIds)
        {
            await NonQueryAsync("DELETE FROM facilitator.communication_bindings WHERE binding_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
        }

        foreach (var id in _workIds)
        {
            await NonQueryAsync("DELETE FROM facilitator.works WHERE work_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
        }

        foreach (var id in _projectIds)
        {
            await NonQueryAsync("DELETE FROM facilitator.projects WHERE project_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
        }

        foreach (var slug in _teamSlugs)
        {
            await NonQueryAsync("DELETE FROM facilitator.teams WHERE slug = $1",
                cmd => cmd.Parameters.AddWithValue(slug));
        }

        foreach (var id in _auditIds)
        {
            await NonQueryAsync("DELETE FROM facilitator.audit WHERE audit_id = $1",
                cmd => cmd.Parameters.AddWithValue(id));
        }
        _factory.Dispose();
    }

    public void Dispose() => _factory.Dispose();

    // ---- the template vocabulary (pure) --------------------------------------

    [Theory]
    [InlineData("$binding.reference", null)] // no thread bound
    [InlineData("$event.reference", null)]
    [InlineData("$work.team", "team-x")]
    [InlineData("$project.tags", "alpha beta")] // joined string
    [InlineData("$unknown.template", null)] // closed vocabulary
    [InlineData("thread://matrix/room/1", "thread://matrix/room/1")] // literal rides verbatim
    [InlineData("", "")]
    public void TemplateVocabulary(string template, string? expected)
    {
        var config = """{"tags":["alpha","beta"]}""";
        var resolved = ContextBuilder.ResolveTemplate(template, null, "team-x", config);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ProjectTagsLegs()
    {
        Assert.Equal("a", ContextBuilder.ResolveTemplate("$project.tags", null, "t", """{"tags":["a",5]}"""));
        Assert.Null(ContextBuilder.ResolveTemplate("$project.tags", null, "t", "not json")); // malformed config
        Assert.Null(ContextBuilder.ResolveTemplate("$project.tags", null, "t", """{"noTags":1}"""));
        Assert.Null(ContextBuilder.ResolveTemplate("$project.tags", null, "t", null));
    }

    // ---- build legs ------------------------------------------------------------

    [Fact]
    public async Task MissingWorkRefusesHonestly()
    {
        var (result, _) = await BuildAsync([new ContextSource { Envelope = "required" }], Guid.NewGuid());
        Assert.False(result.Ready);
        Assert.Equal("work-not-found", result.Note);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task EnvelopeAndWorkSourcesFoldIntoADigestStampedDocument()
    {
        var (team, projectId, workId) = await SeedWorkAsync("fold objective");
        var (result, _) = await BuildAsync(
            [
                new ContextSource { Envelope = "required" },
                new ContextSource { Work = new ContextWork { Fields = ["objective", "state", "team", "nonsense"] } },
            ],
            workId, commandId: "ctxb-" + workId.ToString("N")[..8]);
        _outboxCommands.Add("context-dispatch:ctxb-" + workId.ToString("N")[..8]);

        Assert.True(result.Ready);
        Assert.Null(result.Note);
        var document = result.Document!;
        Assert.Equal(ContextBuilder.Schema, document["schema"]!.GetValue<string>());
        Assert.Equal(workId.ToString("N"), document["workId"]!.GetValue<string>());
        Assert.False(document["truncated"]!.GetValue<bool>());

        var sources = document["sources"]!.AsArray();
        Assert.Equal(2, sources.Count);
        var envelope = sources[0]!.AsObject();
        Assert.Equal("envelope", envelope["key"]!.GetValue<string>());
        Assert.True(envelope["required"]!.GetValue<bool>());
        Assert.Equal("fold objective", envelope["work"]!["objective"]!.GetValue<string>());
        var workSource = sources[1]!.AsObject();
        Assert.Equal("work", workSource["key"]!.GetValue<string>());
        Assert.Equal("fold objective", workSource["fields"]!["objective"]!.GetValue<string>());
        Assert.Equal(team, workSource["fields"]!["team"]!.GetValue<string>());
        Assert.True(workSource["fields"]!.AsObject().TryGetPropertyValue("nonsense", out var nullField)
            && nullField is null); // an unknown field rides an honest null

        // The digest is over the document WITHOUT the digest member, and it
        // is stamped back in: recompute and compare.
        var stamped = document["digest"]!.GetValue<string>();
        document.Remove("digest");
        var bytes = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString());
        var recomputed = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
        Assert.Equal(recomputed, stamped);

        // Evidence tail: the kernel artifact row + one audit naming both.
        var rows = await fixture.Db.ArtifactRowsForKind(workId, "context.dispatch");
        var artifact = Assert.Single(rows);
        _artifactIds.Add(artifact.ArtifactId);
        Assert.Equal("stored", artifact.State);
        Assert.Equal(ContextBuilder.RetentionPolicy, artifact.RetentionPolicy); // no policy row ⇒ 7d
        Assert.StartsWith("sha256:", artifact.Digest, StringComparison.Ordinal);
        var audits = await AuditRowsAsync("context.dispatch", workId);
        var audit = Assert.Single(audits);
        Assert.Equal("allow", audit.Item1);
        Assert.Equal("built", audit.Item2);
        Assert.Equal($"artifact/{artifact.ArtifactId}", audit.Item3);
    }

    [Fact]
    public async Task TimelineAndMediaAndMemoryLegsAreHonest()
    {
        var (team, projectId, workId) = await SeedWorkAsync("leg objective");
        await fixture.Db.SeedTimelineRow(workId, "created", "{\"k\":\"v\"}");
        // work_events.detail is a jsonb column: a malformed detail cannot be
        // stored at all (Postgres rejects it), so the fold's TryJson leg
        // cannot see garbage through a row — seeded rows are always valid.
        await fixture.Db.SeedTimelineRow(workId, "steer", "{\"nested\":{\"deep\":1}}");
        var kept = Guid.NewGuid();
        _artifactIds.Add(kept);
        await fixture.Db.SeedStoredArtifact(kept, "image/png", 4096, workId, team, "ctxb.ep");
        var tooBig = Guid.NewGuid();
        _artifactIds.Add(tooBig);
        await fixture.Db.SeedStoredArtifact(tooBig, "image/png", 9_000_000, workId, team, "ctxb.ep");
        var (entryId, scopeId) = await SeedMemoryAsync(team, "hull-plating lore for the fold");
        _ = scopeId;

        // The media source reads the INTENT's own refs: one stored-small,
        // one stored-but-oversized, one never-stored.
        var missing = Guid.NewGuid();
        var refsJson = JsonSerializer.Serialize(new object[]
        {
            new { artifactId = kept.ToString() },
            new { artifactId = tooBig.ToString() },
            new { artifactId = missing.ToString() },
        });
        var (result, _) = await BuildAsync(
            [
                new ContextSource
                {
                    Timeline = new ContextTimeline { Last = 30 },
                },
                new ContextSource
                {
                    Media = new ContextMedia { MaxBytes = 5_000_000 },
                },
                new ContextSource
                {
                    Memory = new ContextMemory { Recall = "hull-plating", Scopes = ["team:" + team], Limit = 4 },
                },
            ],
            workId, sourceRefsJson: refsJson);

        Assert.True(result.Ready);
        var sources = result.Document!.AsObject()["sources"]!.AsArray();

        var timeline = sources[0]!.AsObject();
        Assert.Equal("timeline", timeline["key"]!.GetValue<string>());
        var events = timeline["events"]!.AsArray();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e!["detail"]!.GetValueKind() == JsonValueKind.Object);
        Assert.Contains(events, e => e!["detail"]!.AsObject().ContainsKey("nested"));

        var media = sources[1]!.AsObject();
        Assert.Equal("$event.data.sourceRefs", media["from"]!.GetValue<string>());
        var items = media["items"]!.AsArray();
        var dropped = media["dropped"]!.AsArray();
        Assert.Contains(items, i => i!["artifactId"]!.GetValue<string>() == kept.ToString("N"));
        Assert.Equal(2, dropped.Count);
        // As-built: a drop entry echoes the DECLARED wire form verbatim
        // (only attached items normalize to N); the reason names the law.
        Assert.Contains(dropped, d => d!["artifactId"]!.GetValue<string>() == tooBig.ToString()
            && d["reason"]!.GetValue<string>() == "size-cap");
        Assert.Contains(dropped, d => d!["reason"]!.GetValue<string>() == "not-stored");

        var memory = sources[2]!.AsObject();
        Assert.Equal("memory", memory["key"]!.GetValue<string>());
        var entries = memory["entries"]!.AsArray();
        Assert.Single(entries);
        Assert.Equal(entryId.ToString("N"), entries[0]!["entryId"]!.GetValue<string>());
        // The usage attestation law: one memory_usage row naming the block path.
        Assert.Equal(1, await fixture.Db.MemoryUsageCount(entryId, "assignment.context"));
    }

    [Fact]
    public async Task BudgetOverflowTrimsTheOldestOptionalSourceAndFlagsIt()
    {
        var (_, _, workId) = await SeedWorkAsync("budget objective");
        var filler = string.Join(' ', Enumerable.Repeat("timeline-word", 60));
        await fixture.Db.SeedTimelineRow(workId, "created", $$"""{"filler":"{{filler}}","more":"{{filler}}"}""");

        var (result, _) = await BuildAsync(
            [
                new ContextSource { Envelope = "optional" },
                new ContextSource { Timeline = new ContextTimeline { Last = 5 } },
                new ContextSource { Work = new ContextWork { Fields = ["objective"] } },
                new ContextSource { Budget = new ContextBudget { TotalBytes = 400 } },
            ],
            workId);

        Assert.True(result.Ready);
        Assert.Equal("budget-truncated", result.Note);
        Assert.True(result.Document!["truncated"]!.GetValue<bool>());
        var keys = result.Document!["sources"]!.AsArray()
            .Select(s => s!["key"]!.GetValue<string>()).ToList();
        // The oldest optional source (the timeline) is trimmed; envelope and
        // work stay — they are never the sacrifice.
        Assert.DoesNotContain("timeline", keys);
        Assert.Contains("envelope", keys);
        Assert.Contains("work", keys);
        _artifactIds.AddRange((await fixture.Db.ArtifactRowsForKind(workId, "context.dispatch"))
            .Select(a => a.ArtifactId));
    }

    [Fact]
    public async Task HistoryWithoutABindingAnswersNoBoundThread()
    {
        var (_, _, workId) = await SeedWorkAsync("unbound objective");
        var (result, _) = await BuildAsync(
            [new ContextSource { History = new ContextHistory { Thread = "$binding.reference" } }], workId);
        Assert.True(result.Ready);
        var source = Assert.Single(result.Document!["sources"]!.AsArray())!.AsObject();
        Assert.Equal("history", source["key"]!.GetValue<string>());
        Assert.Equal("no-bound-thread", source["error"]!.GetValue<string>());
        _artifactIds.AddRange((await fixture.Db.ArtifactRowsForKind(workId, "context.dispatch"))
            .Select(a => a.ArtifactId));
    }

    [Fact]
    public async Task ThreadSourceParksWithDeterministicFetchIdAndRoutesTheFetch()
    {
        var (team, _, workId) = await SeedWorkAsync("parking objective");
        await SeedBindingAsync(workId, "ctxbprov", "chan-1", "root-1");

        var (result, _) = await BuildAsync(
            [new ContextSource { History = new ContextHistory { Thread = "$binding.reference" } }],
            workId, commandId: "routing:ctxb-park:" + workId.ToString("N")[..6]);

        Assert.False(result.Ready);
        Assert.Equal("pending-thread-fetch", result.Note);
        var uri = "thread://ctxbprov/chan-1/root-1";
        var fetchId = ContextBuilder.FetchIdFor($"work-assign:{workId:N}", uri);
        _outboxCommands.Add("context-fetch:" + fetchId);

        // The 3.3 record: one context.fetch audit naming the pending fetch
        // with a deadline.
        var audits = await AuditRowsAsync("context.fetch", workId);
        var audit = Assert.Single(audits);
        Assert.Equal("pending-thread", audit.Item2);
        using (var doc = JsonDocument.Parse(audit.Item4))
        {
            var pending = doc.RootElement.GetProperty("pendingFetches")[0];
            Assert.Equal(uri, pending.GetProperty("uri").GetString());
            Assert.Equal(fetchId, pending.GetProperty("fetchId").GetString());
            Assert.True(pending.TryGetProperty("deadline", out _));
        }

        // The 3.2 leg: the read rides the SOURCE adapter's endpoint outbox.
        Assert.NotNull(await fixture.Db.OutboxIdByCommand("context-fetch:" + fetchId));
        Assert.Contains("link.ctxb.endpoint", await fixture.Db.OutboxRecipients(workId));
    }

    [Fact]
    public async Task ParkedFetchPastItsDeadlineAnswersTheDeadlineError()
    {
        var (team, _, workId) = await SeedWorkAsync("deadline objective");
        await SeedBindingAsync(workId, "ctxbprov", "chan-2", "root-2");
        var uri = "thread://ctxbprov/chan-2/root-2";
        var fetchId = ContextBuilder.FetchIdFor($"work-assign:{workId:N}", uri);

        // An already-parked fetch whose deadline has passed.
        await NonQueryAsync(
            "INSERT INTO facilitator.audit (audit_id, occurred_at, actor, action, resource, decision, reason, "
            + "policy_version, detail) VALUES ($1, now() - interval '10 minutes', 'kernel', 'context.fetch', $2, "
            + "'allow', 'pending-thread', 'context-v1', $3::jsonb)",
            cmd =>
            {
                var auditId = Guid.NewGuid();
                _auditIds.Add(auditId);
                cmd.Parameters.AddWithValue(auditId);
                cmd.Parameters.AddWithValue($"work/{workId}");
                cmd.Parameters.AddWithValue("""{"pendingFetches":[{"uri":"URI","fetchId":"FID","deadline":"2020-01-01T00:00:00Z"}]}""".Replace("URI", uri).Replace("FID", fetchId));
            });

        var (result, _) = await BuildAsync(
            [new ContextSource { History = new ContextHistory { Thread = "$event.reference" } }],
            workId, team: team);
        Assert.True(result.Ready); // the second pass answers — it never parks forever
        var source = Assert.Single(result.Document!["sources"]!.AsArray())!.AsObject();
        Assert.Equal("deadline", source["error"]!.GetValue<string>());
        Assert.Equal(uri, source["uri"]!.GetValue<string>());
        _artifactIds.AddRange((await fixture.Db.ArtifactRowsForKind(workId, "context.dispatch"))
            .Select(a => a.ArtifactId));
    }

    [Fact]
    public async Task FetchedAnswerFillsHistoryAndThePolicyCapsTheWindow()
    {
        var (team, _, workId) = await SeedWorkAsync("answered objective");
        await SeedBindingAsync(workId, "ctxbprov", "chan-3", "root-3");
        var uri = "thread://ctxbprov/chan-3/root-3";
        // The adapter answer, built through JsonObject so the jsonb detail is
        // unambiguously valid JSON (the earlier hand-rolled literal tripped
        // 22P02 on an escaping accident).
        var messages = new JsonArray(
            Enumerable.Range(1, 4).Select(i =>
                (JsonNode)new JsonObject { ["seq"] = i, ["text"] = $"m{i}" }).ToArray<JsonNode>());
        var answerDetail = new JsonObject
        {
            ["items"] = new JsonArray((JsonNode)new JsonObject
            {
                ["uri"] = uri,
                ["content"] = new JsonObject { ["messages"] = messages },
                ["truncated"] = false,
            }),
        };
        await NonQueryAsync(
            "INSERT INTO facilitator.audit (audit_id, occurred_at, actor, action, resource, decision, reason, "
            + "policy_version, detail) VALUES ($1, now(), 'kernel', 'context.fetched', $2, 'allow', "
            + "'adapter-answer', 'context-v1', $3::jsonb)",
            cmd =>
            {
                var auditId = Guid.NewGuid();
                _auditIds.Add(auditId);
                cmd.Parameters.AddWithValue(auditId);
                cmd.Parameters.AddWithValue($"work/{workId}");
                cmd.Parameters.AddWithValue(answerDetail.ToJsonString());
            });
        await fixture.Db.SeedAdminConfig("media-policy",
            """{"teams":[{"team":"TEAM","history":{"toHarnessMaxMessages":2}}]}""".Replace("TEAM", team));

        try
        {
            var (result, _) = await BuildAsync(
                [new ContextSource { History = new ContextHistory { Thread = "$binding.reference", Last = 20 } }],
                workId);
            Assert.True(result.Ready);
            var source = Assert.Single(result.Document!["sources"]!.AsArray())!.AsObject();
            Assert.Equal("asc", source["order"]!.GetValue<string>()); // default order
            var answer = source["answer"]!.AsObject();
            Assert.True(answer["truncated"]!.GetValue<bool>()); // clipped ⇒ honest
            var clipped = answer["content"]!["messages"]!.AsArray();
            var clippedSeqs = clipped.Select(m => m!["seq"]!.GetValue<int>()).ToList();
            // As-built: the window is the adapter's array and the cap cuts
            // its TAIL (Take(cap)) — an asc-ordered answer keeps the oldest.
            Assert.Equal([1, 2], clippedSeqs);
            _artifactIds.AddRange((await fixture.Db.ArtifactRowsForKind(workId, "context.dispatch"))
                .Select(a => a.ArtifactId));
        }
        finally
        {
            await fixture.Db.DeleteAdminConfig("media-policy");
        }
    }

    // ---- plumbing ----------------------------------------------------------------

    private ContextBuilder Builder() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<ContextBuilder>();

    private async Task<(ContextBuildResult Result, FacilitatorDbContext Db)> BuildAsync(
        IReadOnlyList<ContextSource> block, Guid workId, string? commandId = null,
        string team = "unused", string? sourceRefsJson = null)
    {
        await using var db = await _factory.Services
            .GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync();
        var intent = new RoutingIntent(
            workId, "cap.one", team, null, "target-1", "work-assign", workId.ToString("N"),
            "objective", sourceRefsJson, null, "kernel:test");
        var result = await Builder().BuildAsync(db, block, intent, commandId, default);
        await db.SaveChangesAsync(); // the sink's ambient commit
        return (result, db);
    }

    private async Task<(string Team, Guid ProjectId, Guid WorkId)> SeedWorkAsync(string objective)
    {
        var team = "ctxb-" + Guid.NewGuid().ToString("N")[..8];
        await fixture.Db.SeedTeam(team);
        _teamSlugs.Add(team);
        var projectId = Guid.NewGuid();
        await NonQueryAsync(
            "INSERT INTO facilitator.projects (project_id, team_id, name, config, created_at) "
            + "VALUES ($1, (SELECT team_id FROM facilitator.teams WHERE slug = $2), 'p', "
            + "'{\"tags\":[\"ctxb\"]}', now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(projectId);
                cmd.Parameters.AddWithValue(team);
            });
        _projectIds.Add(projectId);
        var workId = Guid.NewGuid();
        await NonQueryAsync(
            "INSERT INTO facilitator.works (work_id, project_id, objective, state, version, team_id, origin, "
            + "created_at, updated_at) VALUES ($1, $2, $3, 'active', 1, "
            + "(SELECT team_id FROM facilitator.teams WHERE slug = $4), 'ctxb', now(), now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue(projectId);
                cmd.Parameters.AddWithValue(objective);
                cmd.Parameters.AddWithValue(team);
            });
        _workIds.Add(workId);
        return (team, projectId, workId);
    }

    private async Task SeedBindingAsync(Guid workId, string provider, string channel, string root)
    {
        var bindingId = Guid.NewGuid();
        _bindingIds.Add(bindingId);
        await NonQueryAsync(
            "INSERT INTO facilitator.communication_bindings (binding_id, provider, channel, root_message_id, "
            + "work_id, idempotency_key, created_at) VALUES ($1, $2, $3, $4, $5, $6, now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(bindingId);
                cmd.Parameters.AddWithValue(provider);
                cmd.Parameters.AddWithValue(channel);
                cmd.Parameters.AddWithValue(root);
                cmd.Parameters.AddWithValue(workId);
                cmd.Parameters.AddWithValue("ctxb-" + bindingId);
            });
    }

    private async Task<(Guid EntryId, Guid ScopeId)> SeedMemoryAsync(string teamSlug, string content)
    {
        var scopeId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        _scopeIds.Add(scopeId);
        _entryIds.Add(entryId);
        var hash = "sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        await NonQueryAsync(
            "INSERT INTO facilitator.memory_scopes (scope_id, kind, team_slug, canonical_selector, created_at) "
            + "VALUES ($1, 'team', $2, 'team:' || $2, now())",
            cmd =>
            {
                cmd.Parameters.AddWithValue(scopeId);
                cmd.Parameters.AddWithValue(teamSlug);
            });
        await NonQueryAsync(
            "INSERT INTO facilitator.memory_entries (entry_id, scope_id, team_slug, type, content, media_type, "
            + "content_summary, content_hash, provenance, observed_at, classification, policy_label, status, "
            + "revision, contradicts_entry_ids, created_at, created_by) "
            + "VALUES ($1, $2, $3, 'observed_fact', $4, 'text/plain', 'seeded', $5, "
            + "'{\"source\":\"seed\"}', now(), 'internal', 'facilitator-visible', 'active', 1, '{}', now(), 'seed')",
            cmd =>
            {
                cmd.Parameters.AddWithValue(entryId);
                cmd.Parameters.AddWithValue(scopeId);
                cmd.Parameters.AddWithValue(teamSlug);
                cmd.Parameters.AddWithValue(content);
                cmd.Parameters.AddWithValue(hash);
            });
        return (entryId, scopeId);
    }

    private async Task<List<(string Decision, string? Reason, string? Evidence, string Detail)>> AuditRowsAsync(
        string action, Guid workId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT decision, coalesce(reason,''), coalesce(evidence_ref,''), detail FROM facilitator.audit "
            + "WHERE action = $1 AND resource = $2", connection);
        cmd.Parameters.AddWithValue(action);
        cmd.Parameters.AddWithValue($"work/{workId}");
        var rows = new List<(string, string?, string?, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    private async Task NonQueryAsync(string sql, Action<NpgsqlCommand> bind)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        bind(cmd);
        await cmd.ExecuteNonQueryAsync();
    }

}
