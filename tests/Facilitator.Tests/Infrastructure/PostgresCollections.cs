namespace Facilitator.Tests.Infrastructure;

// Per-feature xUnit collections: classes INSIDE a collection run serially,
// collections run in parallel against the one shared container
// (<see cref="PostgresFixture"/>). A new Postgres-backed class joins the
// existing collection of its feature folder; it never spawns a container.

/// <summary>Work-lifecycle suites (serial among themselves).</summary>
[CollectionDefinition("postgres:work")]
public sealed class WorkCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<KeycloakFixture>;

/// <summary>CloudEvent-ingress suites (serial among themselves).</summary>
[CollectionDefinition("postgres:ingress")]
public sealed class IngressCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<KeycloakFixture>;

/// <summary>
/// Workspace-Link + dispatcher suites share ONE collection: the dispatcher's
/// global outbox claim and the link pump's materialization race over the same
/// rows, so they run serially together; every other feature still parallels.
/// </summary>
[CollectionDefinition("postgres:dispatch-link")]
public sealed class DispatchLinkCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<KeycloakFixture>;

/// <summary>Migration/schema-boundary suites (serial among themselves).</summary>
[CollectionDefinition("postgres:persistence")]
public sealed class PersistenceCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<KeycloakFixture>;
