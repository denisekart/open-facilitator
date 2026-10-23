using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Persistence;

/// <summary>Applies pending EF migrations at startup, serialized across replicas.</summary>
public interface IDatabaseInitializer
{
    /// <summary>
    /// Take <c>pg_advisory_lock(420001)</c> on a dedicated connection, run
    /// <c>MigrateAsync</c>, unlock. A no-op when <c>Database:AutoMigrate=false</c>.
    /// </summary>
    Task InitializeAsync(CancellationToken ct);
}

/// <summary>
/// Startup migration gate: the advisory lock keeps a future second replica
/// from racing the same <c>MigrateAsync</c>; config
/// <c>Database:AutoMigrate</c> (default true) disables it for tests without a
/// database.
/// </summary>
public sealed class DatabaseInitializer(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    IConfiguration configuration,
    ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    /// <summary>Advisory-lock key reserved for schema migration.</summary>
    public const int MigrationLockKey = 420001;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (configuration["Database:AutoMigrate"] == "false")
        {
            logger.LogInformation("Database:AutoMigrate=false — skipping startup migration");
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);

        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.CommandText = $"SELECT pg_advisory_lock({MigrationLockKey})";
            await lockCommand.ExecuteScalarAsync(ct);
        }

        try
        {
            logger.LogInformation("Applying pending EF Core migrations");
            await db.Database.MigrateAsync(ct);
        }
        finally
        {
            await using var unlockCommand = connection.CreateCommand();
            unlockCommand.CommandText = $"SELECT pg_advisory_unlock({MigrationLockKey})";
            await unlockCommand.ExecuteScalarAsync(ct);
        }
    }
}
