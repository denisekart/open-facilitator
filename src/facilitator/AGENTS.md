# src/facilitator

## Purpose
The Facilitator kernel (.NET 10 / ASP.NET Core): durable coordination —
ingress, work lifecycle, the workspace-Link server, dispatch — on PostgreSQL.
One process, three folders: two features and one shared kernel.

## Truth anchors
- `Kernel/Work/` — work lifecycle (endpoints, ingress pipeline, state machine,
  `EfCoordinationStore` = this feature's persistence).
- `Kernel/Link/` — workspace-link server (enroll, hub, admission, deliveries,
  receipts, ledger).
- `Kernel/Shared/` — not a feature: Auth, Dispatch, Persistence, CLI, metrics.
- `Kernel/KernelServiceCollection.cs` — composition root; `AddSharedKernel` /
  `AddWorkFeature` / `AddLinkFeature` mirror the folders.
- `Migrations/` + `Kernel/Shared/Persistence/Configurations/` — schema truth,
  one `IEntityTypeConfiguration<T>` per entity.
- Wire truth lives outside: `../protocol/dotnet/Ofac.Protocol`.

## Develop here
- `dotnet build OpenFacilitator.slnx -c Release` — 0 warnings is law
  (TreatWarningsAsErrors). Tests: `tests/Facilitator.Tests`, run per folder
  with `--filter "FullyQualifiedName~<Folder>"`.
- New service: register in the feature-extension method of its folder.
- New entity: add a configuration class, then prove the model didn't shift
  with an empty `dotnet ef migrations add` diff.
- Dev-only endpoints (`/api/dev/*`) map only in Development; tests run the
  host in Development.

## Invariants
- Raw SQL lives only inside `Kernel/Shared/Persistence` classes and only for:
  advisory locks, `pg_notify`/`LISTEN`, `FOR UPDATE SKIP LOCKED` claims,
  version-fenced UPDATEs. No endpoint lambda injects a DbContext.
- Work state transitions go through the coordination store's fenced update —
  never an ad-hoc UPDATE of `facilitator.works`.
- Delivery-receipt parity: SignalR `Ack` and HTTP receipts both go through
  `CommitAsync` in `Kernel/Link/LinkReceipts.cs`.
- Test classes live in `../../tests/Facilitator.Tests/<Feature>` mirroring
  `Kernel/<Feature>`.
