namespace Facilitator.ScenarioWalks;

/// <summary>
/// The whole walk family shares ONE collection and its own fresh pair of
/// containers: xUnit constructs one <see cref="PostgresFixture"/> and one
/// <see cref="KeycloakFixture"/> per collection in this process, so the
/// fake-clock-driven walks never share a DB with the per-feature families
/// (which live in the other assembly's process anyway). Inside the family
/// the collection serializes: strictly sequential walks, one container, no
/// fixed sleeps.
/// </summary>
[CollectionDefinition("postgres:walks")]
public sealed class WalkCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<KeycloakFixture>;
