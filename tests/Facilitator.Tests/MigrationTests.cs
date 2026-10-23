namespace Facilitator.Tests;

/// <summary>
/// §26.1 item 1: against a fresh container, app startup runs
/// <c>MigrateAsync</c> (Database:AutoMigrate=true), leaving the
/// <c>facilitator</c> schema with the coordination tables and their CHECK
/// constraints. Assertions are pure information_schema queries, so the test
/// is order-independent even though the container is shared.
/// </summary>
[Collection("postgres")]
public class MigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Startup_MigratesSchema_TablesAndCheckConstraintsExist()
    {
        // Any authed request forces a factory start, which migrates (advisory-
        // lock serialized) before the first endpoint runs.
        using var factory = new KernelTestFactory(fixture);
        using var client = factory.CreateAuthedClient();
        await KernelOps.GetJsonAsync(client, "/api/works", System.Net.HttpStatusCode.OK);

        var tables = await fixture.TextListAsync(
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = 'facilitator'");
        foreach (var table in new[] { "works", "work_events", "inbox", "outbox", "dead_letters" })
        {
            Assert.Contains(table, tables);
        }

        var checks = await fixture.TextListAsync(
            "SELECT constraint_name FROM information_schema.check_constraints " +
            "WHERE constraint_schema = 'facilitator'");
        foreach (var name in new[]
                 {
                     "works_state_check",
                     "work_events_kind_check",
                     "inbox_state_check",
                     "outbox_status_check",
                     "outbox_aggregate_type_check",
                 })
        {
            Assert.Contains(name, checks);
        }
    }
}
