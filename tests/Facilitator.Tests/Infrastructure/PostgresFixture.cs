using System.Text.Json.Nodes;

using Testcontainers.PostgreSql;

namespace Facilitator.Tests.Infrastructure;

/// <summary>
/// One Postgres 17 container shared by every feature collection that stays
/// on the main DB: the container starts lazily once (the first fixture
/// created across any collection wins the race). Tests isolate via distinct
/// GUID works/endpoints, not fresh databases; migrations serialize behind
/// the advisory lock inside the kernel's DatabaseInitializer. DB state is
/// inspected through the typed <see cref="Db"/> probe, never raw SQL.
/// The dispatcher/link collection overrides the container
/// (<see cref="DispatchDbFixture"/>): a global outbox claim is collection-
/// scoped only when no foreign replica's dispatcher shares the DB.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    private static readonly Task<PostgreSqlContainer> Shared =
        new Lazy<Task<PostgreSqlContainer>>(() => StartAsync(setMainEnv: true)).Value;

    private readonly TestDbProbe _probe;

    /// <inheritdoc />
    public PostgresFixture() => _probe = new TestDbProbe(ConnectionString);

    /// <summary>The container this fixture family points at (main shared by default).</summary>
    protected virtual Task<PostgreSqlContainer> Container => Shared;

    /// <summary>Container connection string (valid after initialization).</summary>
    public string ConnectionString => Container.Result.GetConnectionString();

    /// <summary>Typed DB probe (parameterized, feature-named queries).</summary>
    public TestDbProbe Db => _probe;

    internal static async Task<PostgreSqlContainer> StartAsync(bool setMainEnv)
    {
        var container = new PostgreSqlBuilder("docker.io/library/postgres:17-alpine").Build();
        await container.StartAsync();
        if (setMainEnv)
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Facilitator", container.GetConnectionString());
        }

        return container;
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Container;

    /// <inheritdoc />
    public virtual async Task DisposeAsync() => await _probe.DisposeAsync();

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
        data["actorEvidenceRef"] = "identity-evidence:test"; // unverified scheme: workload-only, admitted
        data["expectedContextVersion"] = expectedContextVersion;
        return root.ToJsonString();
    }
}
