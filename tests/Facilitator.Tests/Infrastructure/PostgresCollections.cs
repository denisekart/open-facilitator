namespace Facilitator.Tests.Infrastructure;

// Per-feature xUnit collections: classes INSIDE a collection run serially,
// collections run in parallel. Most feature collections share the main
// Postgres container (<see cref="PostgresFixture"/>); the dispatch-link
// family owns a dedicated one (<see cref="DispatchDbFixture"/>). A new
// Postgres-backed class joins the existing collection of its feature folder;
// it never spawns its own container.

/// <summary>Work-lifecycle suites (serial among themselves).</summary>
[CollectionDefinition("postgres:work")]
public sealed class WorkCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<KeycloakFixture>;

/// <summary>
/// Artifact suites against the s3 provider (the s3 matrix): same Postgres +
/// Keycloak containers, plus the shared SeaweedFS S3 container. Serial among
/// themselves; runs parallel to the fs leg (distinct rows, distinct buckets).
/// </summary>
[CollectionDefinition("postgres:work:s3")]
public sealed class WorkS3Collection : ICollectionFixture<PostgresFixture>, ICollectionFixture<KeycloakFixture>, ICollectionFixture<SeaweedFsFixture>;

/// <summary>CloudEvent-ingress suites (serial among themselves).</summary>
[CollectionDefinition("postgres:ingress")]
public sealed class IngressCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<KeycloakFixture>;

/// <summary>
/// Workspace-Link + dispatcher suites share ONE collection AND one dedicated
/// container: the dispatcher's global outbox claim races every other host's
/// dispatcher over the WHOLE outbox table (SKIP-LOCK is per-row, not per-
/// collection), and the link pump's lease sweep deletes foreign sessions, so
/// this family needs a DB no foreign replica's dispatcher or fake-clock pump
/// can reach. Inside the family the collection serializes the claim/pump
/// race; every other feature still parallels against the main container.
/// </summary>
[CollectionDefinition("postgres:dispatch-link")]
public sealed class DispatchLinkCollection : ICollectionFixture<DispatchDbFixture>, ICollectionFixture<KeycloakFixture>;

/// <summary>Migration/schema-boundary suites (serial among themselves).</summary>
[CollectionDefinition("postgres:persistence")]
public sealed class PersistenceCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<KeycloakFixture>;
