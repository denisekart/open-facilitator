using System.Text.Json.Nodes;

using Npgsql;

using Testcontainers.PostgreSql;

namespace Facilitator.Tests;

/// <summary>
/// One Postgres 17 container for the whole <c>postgres</c> xunit collection
/// (plan 02 §tests): tests isolate via distinct works, not fresh databases.
/// Exposes the connection string plus small Npgsql helpers so assertions can
/// inspect <c>facilitator.*</c> tables directly.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("docker.io/library/postgres:17-alpine")
        .Build();

    /// <summary>Container connection string (valid after initialization).</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        Environment.SetEnvironmentVariable("ConnectionStrings__Facilitator", ConnectionString);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Run a non-SELECT statement.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Execute a scalar integer query (counts).</summary>
    public async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Execute a query returning zero or more single-text-column rows.</summary>
    public async Task<List<string>> TextListAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>
    /// Absolute path of <c>protocol/conformance-fixtures</c>, found by walking
    /// up from the test assembly location (repo-root-relative, build-dir safe).
    /// </summary>
    public static string FixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "protocol", "conformance-fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("protocol/conformance-fixtures not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Raw fixture text.</summary>
    public static string ReadFixture(string relativePath) =>
        File.ReadAllText(Path.Combine(FixtureRoot(), relativePath));

    /// <summary>
    /// <c>valid/command.work.steer.json</c> rewritten for a live work: fresh
    /// envelope id + commandId, subject <c>work/{id}</c>, and
    /// <c>expectedContextVersion</c> equal to the work's current version.
    /// </summary>
    public static string SteerFixtureFor(string relativePath, Guid workId, long expectedContextVersion)
    {
        var root = JsonNode.Parse(ReadFixture(relativePath))!.AsObject();
        var commandId = "cmd-" + Guid.NewGuid().ToString("N");
        root["id"] = "evt-" + Guid.NewGuid().ToString("N");
        root["subject"] = $"work/{workId}";
        var data = root["data"]!.AsObject();
        data["commandId"] = commandId;
        data["workId"] = workId.ToString();
        data["assignmentId"] = "kernel";
        data["targetId"] = "kernel";
        data["actorEvidenceRef"] = "kernel:test";
        data["expectedContextVersion"] = expectedContextVersion;
        return root.ToJsonString();
    }
}
