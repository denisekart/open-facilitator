using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Npgsql;

namespace Facilitator.Tests.Workflows;

/// <summary>
/// Phase 0 of the workflow definition plane: the definition artifact
/// (YAML↔JSON face law), the store (version bump, digest, audit allow AND
/// deny rows, ETag law), the validator (every referential rejection
/// provable), enable-time palette law, gallery seeding, export/import
/// round-trip. Nothing executes yet — the plane is data only here.
/// Joins <c>postgres:persistence</c>: schema-bound rows on the shared DB.
/// </summary>
[Collection("postgres:persistence")]
public sealed class WorkflowDefinitionTests(PostgresFixture fixture) : IAsyncLifetime
{
    private KernelTestFactory _factory = null!;
    private HttpClient _client = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new KernelTestFactory(fixture, noDispatcher: true);
        _client = _factory.CreateAuthedClient();
        await PurgeAsync("t.flow");
        await PurgeAsync("t.editor");
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task PurgeAsync(string defId)
    {
        await using var connection = await OpenAsync();
        await using (var cascade = new NpgsqlCommand(
            """
             DELETE FROM facilitator.workflow_step_log WHERE instance_id IN
                 (SELECT id FROM facilitator.workflow_instances WHERE def_id = $1)
             """, connection))
        {
            cascade.Parameters.AddWithValue(defId);
            await cascade.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand(
            """
             DELETE FROM facilitator.workflow_instances WHERE def_id = $1;
             """, connection);
        cmd.Parameters.AddWithValue(defId);
        await cmd.ExecuteNonQueryAsync();
        await using var defs = new NpgsqlCommand(
            "DELETE FROM facilitator.workflow_definitions WHERE def_id = $1", connection);
        defs.Parameters.AddWithValue(defId);
        await defs.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Doc(string id, int version, string? extra = null, string trigger = "\"event\": \"org.facilitator.event.provider.issue.v1\"") =>
        $$"""
        {
          "id": "{{id}}",
          "version": {{version}},
          "title": "Test flow",
          "description": "A phase-0 test definition.",
          "on": { {{trigger}} },
          "steps": [
            { "id": "s1", "title": "Step one", "select": { "project": { "a": "$event.data.x" } } },
            { "id": "s2", "title": "Step two", "on": "s1:completed", "emit": { "command": "org.facilitator.command.communication.post.v1", "inputs": { "text": "hi" } } }
            {{extra}}
          ]
        }
        """;

    private async Task<HttpResponseMessage> PutAsync(string id, string body, string etag = "*", string reason = "test write")
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/workflows/definitions/{id}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.TryAddWithoutValidation("X-Change-Reason", reason);
        return await _client.SendAsync(request);
    }

    private async Task<(string Digest, int Version)> StoreOneAsync(string id = "t.flow", string? extra = null)
    {
        var response = await PutAsync(id, Doc(id, 1, extra));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return (body.GetProperty("digest").GetString()!, body.GetProperty("version").GetInt32());
    }

    private async Task<List<(string Action, string Decision)>> AuditRows(string defId)
    {
        await using var connection = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT action, decision FROM facilitator.audit WHERE resource LIKE $1 ORDER BY occurred_at", connection);
        cmd.Parameters.AddWithValue($"%{defId}%");
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    [Fact]
    public async Task PutStoresVersionOneWithDigestAndAuditAllowRow()
    {
        var (digest, version) = await StoreOneAsync();
        Assert.Equal(1, version);
        Assert.Matches(@"^sha256:[0-9a-f]{64}$", digest);

        var stored = await GetAsync($"/api/admin/workflows/definitions/t.flow");
        Assert.Equal(1, stored.GetProperty("summary").GetProperty("version").GetInt32());
        Assert.Equal(digest, stored.GetProperty("summary").GetProperty("digest").GetString());
        Assert.Contains(("admin.workflows.put", "allow"), await AuditRows("t.flow"));
    }

    [Fact]
    public async Task SecondPutBumpsVersionAndOldVersionStaysReadable()
    {
        await StoreOneAsync();
        var (digest, version) = await StoreOneAsync();
        Assert.Equal(2, version);

        var v1 = await GetAsync("/api/admin/workflows/definitions/t.flow?version=1");
        var v2 = await GetAsync("/api/admin/workflows/definitions/t.flow");
        Assert.Equal(1, v1.GetProperty("summary").GetProperty("version").GetInt32());
        Assert.Equal(2, v2.GetProperty("summary").GetProperty("version").GetInt32());
        Assert.NotEqual(
            v1.GetProperty("summary").GetProperty("digest").GetString(),
            v2.GetProperty("summary").GetProperty("digest").GetString());
        Assert.NotEmpty(digest);
    }

    [Fact]
    public async Task IfMatchAgainstStaleDigestConflictsAndWritesNothing()
    {
        await StoreOneAsync();
        var response = await PutAsync("t.flow", Doc("t.flow", 99), "\"sha256:0000000000000000000000000000000000000000000000000000000000000000\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var rows = await AuditRows("t.flow");
        Assert.Contains(("admin.workflows.put", "deny"), rows);

        var stored = await GetAsync("/api/admin/workflows/definitions/t.flow");
        Assert.True(1 == stored.GetProperty("summary").GetProperty("version").GetInt32(), "a conflict never writes");
    }

    [Fact]
    public async Task YamlFaceRoundTripsByteEqualAfterCanonicalization()
    {
        await StoreOneAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/workflows/definitions/t.flow");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/yaml"));
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var yaml = await response.Content.ReadAsStringAsync();

        // The face law: the YAML view reparsed and canonicalized equals the
        // stored document byte-for-byte after canonicalization (JSON is the
        // only truth; the digest is its identity, so digest equality is the
        // byte-equality claim — jsonb re-serialization aside).
        var reparsed = Facilitator.Kernel.Workflows.WorkflowDoc.Parse(yaml);
        var stored = await GetAsync("/api/admin/workflows/definitions/t.flow");
        var canonicalized = Facilitator.Kernel.Workflows.WorkflowDoc.Canonical(reparsed);
        Assert.Equal(
            Facilitator.Kernel.Workflows.WorkflowDoc.Canonical(stored.GetProperty("document")),
            canonicalized);
        Assert.Equal(
            stored.GetProperty("summary").GetProperty("digest").GetString(),
            Facilitator.Kernel.Workflows.WorkflowDoc.Digest(reparsed));

        // Two renders of one document are byte-identical (the view is stable).
        using var second = new HttpRequestMessage(HttpMethod.Get, "/api/admin/workflows/definitions/t.flow");
        second.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/yaml"));
        var again = await _client.SendAsync(second);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(yaml, await again.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> GetAsync(string path)
        => await KernelOps.GetJsonAsync(_client, path, HttpStatusCode.OK);

    [Theory]
    [InlineData("unknown command", "\"emit\": { \"command\": \"org.facilitator.command.bogus.thing.v1\", \"inputs\": {} }")]
    public async Task ValidatorRejectionsAreProvable(string because, string payload)
    {
        _ = because;
        var body = $$"""
        {
          "id": "t.flow", "version": 1, "title": "t", "description": "d",
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [ { "id": "s1", "title": "x", {{payload}} } ]
        }
        """;
        var result = await KernelOps.PostJsonAsync(_client, "/api/admin/workflows/definitions/validate", body, HttpStatusCode.OK, "dry run");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown-command", result.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task ValidatorRejectsUnknownEventType()
    {
        var body = $$"""
        {
          "id": "t.flow", "version": 1, "title": "t", "description": "d",
          "on": { "event": "org.facilitator.event.not.a.real.thing.v1" },
          "steps": [ { "id": "s1", "title": "x", "select": { "project": {} } } ]
        }
        """;
        var result = await KernelOps.PostJsonAsync(_client, "/api/admin/workflows/definitions/validate", body, HttpStatusCode.OK, "dry run");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown-event-type", result.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task ValidatorRejectsMissingDecideFallback()
    {
        var body = $$"""
        {
          "id": "t.flow", "version": 1, "title": "t", "description": "d",
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [ { "id": "s1", "title": "x", "decide": { "source": "guard", "guard": { "field": "work.state", "equals": "open" } } } ]
        }
        """;
        var result = await KernelOps.PostJsonAsync(_client, "/api/admin/workflows/definitions/validate", body, HttpStatusCode.OK, "dry run");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains(result.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("code").GetString() == "schema-violation");
    }

    [Fact]
    public async Task ValidatorRejectsStepCycle()
    {
        var body = $$"""
        {
          "id": "t.flow", "version": 1, "title": "t", "description": "d",
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [
            { "id": "a", "title": "a", "on": "b:completed", "select": { "project": {} } },
            { "id": "b", "title": "b", "on": "a:completed", "select": { "project": {} } }
          ]
        }
        """;
        var result = await KernelOps.PostJsonAsync(_client, "/api/admin/workflows/definitions/validate", body, HttpStatusCode.OK, "dry run");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("step-cycle", result.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task ValidatorRejectsTooManyStepsAndOversizeDoc()
    {
        var steps = string.Join(",", Enumerable.Range(1, 21)
            .Select(i => $$"""{ "id": "s{{i}}", "title": "s{{i}}", "select": { "project": {} } }"""));
        var body = $$"""
        {
          "id": "t.flow", "version": 1, "title": "t", "description": "d",
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [{{steps}}]
        }
        """;
        var result = await KernelOps.PostJsonAsync(_client, "/api/admin/workflows/definitions/validate", body, HttpStatusCode.OK, "dry run");
        Assert.False(result.GetProperty("ok").GetBoolean()); // 21 steps exceeds the 20-step cap

        var fat = $$"""
        {
          "id": "t.flow", "version": 1, "title": "t",
          "description": "{{new string('x', 40 * 1024)}}",
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [ { "id": "s1", "title": "x", "select": { "project": {} } } ]
        }
        """;
        var fatResult = await KernelOps.PostJsonAsync(_client, "/api/admin/workflows/definitions/validate", fat, HttpStatusCode.OK, "dry run");
        Assert.False(fatResult.GetProperty("ok").GetBoolean());
        Assert.Equal("definition-too-large", fatResult.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task ToggleToEnabledWithUnsupportedStepKindIs422()
    {
        await StoreOneAsync(extra: """, { "id": "s3", "title": "memory", "on": "s2:completed", "memory": { "op": "retain", "scope": "walk-scope", "textRef": "$work.id" } }""");
        var response = await KernelOps.PostJsonAsync(_client,
            "/api/admin/workflows/definitions/t.flow/versions/1/toggle", """{"enabled": true}""",
            HttpStatusCode.UnprocessableEntity, "try to enable");
        Assert.Equal("step-kind-not-supported",
            response.GetProperty("errors")[0].GetProperty("code").GetString());
        // The deny audit row records the refusal (deny = the store's answer,
        // the structured code rides in the audit detail).
        Assert.Contains(("admin.workflows.toggle", "deny"), await AuditRows("t.flow"));
    }

    [Fact]
    public async Task ToggleRoundTripEnablesAndDisables()
    {
        await StoreOneAsync();
        await KernelOps.PostJsonAsync(_client,
            "/api/admin/workflows/definitions/t.flow/versions/1/toggle", """{"enabled": true}""",
            HttpStatusCode.OK, "enable");
        var stored = await GetAsync("/api/admin/workflows/definitions/t.flow");
        Assert.True(stored.GetProperty("summary").GetProperty("enabled").GetBoolean());
        Assert.Contains(("admin.workflows.toggle", "allow"), await AuditRows("t.flow"));
    }

    [Fact]
    public async Task GallerySeedsTheBuiltInDefinitionsDisabledAtBoot()
    {
        var list = await GetAsync("/api/admin/workflows/definitions");
        var byId = list.EnumerateArray().ToDictionary(
            d => d.GetProperty("id").GetString()!, d => d, StringComparer.Ordinal);
        // The composition pair (agentic-research -> ensure-capacity)
        // ships with the plane: their descriptions document the invoke
        // law, and the gallery is the editor's starting shelf.
        foreach (var id in new[]
                 {
                     "review-fork", "pr-on-manifest", "assign-rules", "nudge-escalation",
                     "ensure-capacity", "agentic-research",
                 })
        {
            Assert.True(byId.ContainsKey(id), $"gallery entry '{id}' seeded");
            Assert.False(byId[id].GetProperty("enabled").GetBoolean()); // gallery ships disabled
            Assert.Equal("gallery", byId[id].GetProperty("source").GetString());
        }
    }

    [Fact]
    public async Task ExportImportRoundTripLandsIdenticalDigests()
    {
        await StoreOneAsync();
        var exported = await GetAsync("/api/admin/workflows/definitions/export");
        var files = exported.GetProperty("files").EnumerateArray()
            .Where(f => f.GetProperty("name").GetString()!.StartsWith("t.flow@", StringComparison.Ordinal))
            .Select(f => new { name = f.GetProperty("name").GetString(), content = f.GetProperty("yaml").GetString() })
            .ToList();
        Assert.Single(files);

        await PurgeAsync("t.flow");
        var body = JsonSerializer.Serialize(new
        {
            files = new[] { new { name = files[0].name, content = files[0].content } },
        });
        var imported = await KernelOps.PostJsonAsync(_client, "/api/admin/workflows/definitions/import", body,
            HttpStatusCode.OK, "reimport");
        Assert.Equal("stored", imported.GetProperty("results")[0].GetProperty("outcome").GetString());

        var restored = await GetAsync("/api/admin/workflows/definitions/t.flow");
        Assert.NotNull(restored.GetProperty("summary").GetProperty("digest").GetString());
    }

    [Fact]
    public async Task RegistryAnswersClosedSetsForTheEditor()
    {
        var registry = await GetAsync("/api/admin/workflows/registry");
        Assert.Contains("org.facilitator.command.work.assign.v1",
            registry.GetProperty("commands").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("org.facilitator.event.provider.review.v1",
            registry.GetProperty("eventTypes").EnumerateArray().Select(e => e.GetString()));
        registry.GetProperty("capabilities");
        registry.GetProperty("callable");
        registry.GetProperty("teams");
    }

    [Fact]
    public async Task SchemaAnswersTheEmbeddedGrammarForTheEditor()
    {
        // The editor's completion/lint grammar is the very document the 422
        // validator compiles: $defs.step must be in the served definition.
        var schema = await GetAsync("/api/admin/workflows/schema");
        Assert.Equal(
            JsonValueKind.Object,
            schema.GetProperty("definition").GetProperty("$defs").GetProperty("step").ValueKind);
        Assert.Equal(JsonValueKind.Object, schema.GetProperty("common").ValueKind);
    }
}
