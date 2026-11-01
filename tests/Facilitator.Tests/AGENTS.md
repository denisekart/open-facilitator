# tests/Facilitator.Tests

## Purpose
xUnit suite for the Facilitator kernel: HTTP/SignalR/CLI-level behavior against
a real kernel + Postgres (testcontainers), plus schema-validation tests. Public
surface first: tests drive the product through its wire interfaces, not
internals.

## Truth anchors
- `Infrastructure/` — the collections, `KernelTestFactory` (in-proc server),
  `KeycloakFixture` (one shared realm container minting every test bearer —
  never mint tokens any other way), and `TestDbProbe` (the only sanctioned
  DB-state accessor).
- Feature folders mirror `../../src/facilitator/Kernel/<Feature>`; a kernel
  feature and its test folder change together.
- Wire assertions reuse `@ofac/protocol` fixtures/conformance verdicts rather
  than re-deriving them.

## Develop here
- Run one slice: `dotnet test --filter "FullyQualifiedName~<Folder>"`.
- New Postgres-backed test class: join an existing per-feature collection
  (`[Collection("postgres:<feature>")]`); never spin a container per class.
  The dispatch-link family runs on its OWN container (`DispatchDbFixture`):
  a global outbox claim or a lease sweep on a foreign replica's dispatcher
  or fake clock destroys this family's drain/sweep assertions.
- DB assertions go through `TestDbProbe` methods; raw SQL strings do not appear
  in test files.
- Timing: keep waits bounded and generous-but-small; the whole suite has a
  wall-clock budget (see root `AGENTS.md`), so a new fixed sleep needs a reason.

## Invariants
- Tests are independent: GUID-scoped work/endpoint ids, no cross-class state;
  classes in different collections run in parallel.
- Dev-only endpoints (`/api/dev/*`) exist because tests run the server in
  Development; production behavior of those routes is out of scope here.
