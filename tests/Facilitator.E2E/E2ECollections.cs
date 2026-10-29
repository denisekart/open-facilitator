namespace Facilitator.E2E;

/// <summary>
/// One Postgres + Keycloak fixture pair for the whole E2E tier (shared
/// containers, sequential tests — the same singleton law as the kernel
/// suites' named collections, re-declared here so the tier owns its own
/// serialization domain).
/// </summary>
[CollectionDefinition("e2e")]
public sealed class E2ECollections : ICollectionFixture<PostgresFixture>, ICollectionFixture<KeycloakFixture>;
