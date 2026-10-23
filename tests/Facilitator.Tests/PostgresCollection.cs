namespace Facilitator.Tests;

/// <summary>
/// Binds every Postgres-backed suite to one shared container
/// (<see cref="PostgresFixture"/>); classes in the collection run serially.
/// </summary>
[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}
