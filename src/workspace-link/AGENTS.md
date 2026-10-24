# src/workspace-link

## Purpose
Workspace Link SDK + `linkd` runtime (TypeScript, Node 22): the outbound
connection a workspace/gateway uses to receive commands and publish events to
the Facilitator, with a durable SQLite ledger and SignalR/HTTP-poll transports.

## Truth anchors
- `src/core/` — runtime-agnostic law (client state machine, ledger interface,
  handshaker, dispatch, faults). `src/node/` — Node-only IO (transports,
  SQLite ledger, enroll). Nothing in `core` may import Node built-ins.
- `package.json` `exports` (`.`, `./node`) is the public surface; everything
  else is internal.
- Payload/frame shapes: `../protocol/schemas` (e.g. `link-delivery.v1.json`);
  handshake/flow law: `../../.omp/skills/facilitator-protocol/SKILL.md`.
- CLI env/options: `README.md` + `src/core/config.ts`.

## Develop here
- `pnpm -C src/workspace-link test` (unit tier, no containers),
  `pnpm -C src/workspace-link typecheck`, `pnpm -C src/workspace-link build`.
- e2e tier (`test:e2e`): needs `FACILITATOR_URL` + `FACILITATOR_PG`; each test
  file boots its own Facilitator, files run serially (timing determinism).
- `test/client.test.ts` is the client's behavior oracle — prefer making it
  pass unchanged over editing its assertions.

## Invariants
- Tests import ONLY the two public entries (`../src/index.js`,
  `../src/node/index.js`); no deep imports into `src/core` or `src/node`.
- Outbox drain stays serial per session — event ordering is wire law, not an
  implementation detail.
- `@ofac/protocol` does all schema validation; this package never re-declares
  wire shapes.
