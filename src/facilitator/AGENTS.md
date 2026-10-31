# src/facilitator

## Purpose
The Facilitator kernel (.NET 10 / ASP.NET Core): durable coordination —
ingress, work lifecycle, the workspace-Link server, dispatch — on PostgreSQL.
One process, thin feature folders over one shared kernel
(Storage, Memory broker, Auth, Dispatch, Persistence, CLI, metrics)
plus the read-mostly Admin surface.

## Truth anchors
- `Kernel/Work/` — work lifecycle (endpoints, ingress pipeline, state machine,
  `EfCoordinationStore` = this feature's persistence).
- `Kernel/Routing/` — the routing funnel (`RoutingSink`, dispatcher channel
  `routing`): the ONLY producer of `work.assign` rows + the
  `routing_decisions` audit ledger; intents are appended by Work/Provider
  writers as `routing:<base>:<key>` outbox rows.
- `Kernel/Link/` — workspace-link server (enroll, hub, admission, deliveries,
  receipts, ledger).
- `Kernel/Admin/` — `/api/admin` operator surface (read stores, config store
  with the If-Match/`X-Change-Reason` law, replay + re-mark mutations);
  authorization is config bindings only (`admin.read`/`admin.mutate`).
- `ui/` — the embedded operator SPA (Svelte 5 + vite, `@ofac/ui`): `pnpm -r
  build` stages hashed assets into `wwwroot/` (git-ignored output), served by
  the static pipeline + `GET /ui-config`; browser tier in `tests/Facilitator.E2E`.
- `Kernel/Shared/` — not a feature: Auth, Dispatch, Persistence, CLI, metrics,
  Storage (artifact byte port + ledgers), Memory (broker + provider port).
- `Kernel/KernelServiceCollection.cs` — composition root; `AddSharedKernel` /
  `AddWorkFeature` / `AddLinkFeature` / `AddStorageFeature` / `AddMemoryFeature`.
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
