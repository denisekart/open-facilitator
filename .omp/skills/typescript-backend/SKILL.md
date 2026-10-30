---
name: typescript-backend
description: Use for backend-TypeScript packages in this repo (adapters, connectors, gateway children) — package anatomy, workspace-link composition, adapter SDK child surface, better-sqlite3 ledger conventions, env-config and supervisor exit-code law.
---

# Backend TypeScript (this repo)

Exemplars: `src/adapters/communication/mattermost` (link-plane adapter),
`src/adapters/harnesses/deterministic` (Gateway child),
`src/adapters/harnesses/adapter-sdk` (`@ofac/adapter`).

## Package anatomy (tsup, node22)

- `package.json`: ESM (`"type": "module"`), `exports: { ".": dist/index }`,
  `bin: { "<name>": "./dist/bin/<x>.js" }`, `files: ["dist"]`.
  Scripts: `build: tsup src/index.ts src/bin/<x>.ts --format esm --dts --target node22 --silent`,
  `test: vitest run`, `typecheck: tsc --noEmit -p tsconfig.json`.
- Deps: internal packages `workspace:*`, externals `catalog:`
  (typescript/tsup/vitest/better-sqlite3+@types/yaml/jose are catalog-pinned;
  `allowBuilds: better-sqlite3:false` — prebuilds only).
- `tsconfig.json` extends root `tsconfig.base.json`, `noEmit`,
  `lib:["ES2024","DOM"]`, `types:["node"]`, includes src+test+vitest.config.
- `vitest.config.ts`: unit tier `include:["test/**/*.test.ts"]`, no live provider.
- New package → add to `pnpm-workspace.yaml` packages AND `Dockerfile.node`
  (repo ROOT, not reference/): one manifest-COPY pair + one build-COPY pair +
  one `pnpm deploy --filter=@ofac/<pkg> --prod /deploy/<name>` entry.
- Bin law (`mattermost/src/bin/adapter.ts`): `main(argv, env)`; first positional
  subcommand; help ⇒ 0, config error ⇒ 2, runtime ⇒ 1; importable-guard
  `if (process.argv[1] !== undefined && import.meta.url === new URL(\`file://${process.argv[1]}\`).href)`.

## workspace-link composition (link-plane adapters)

- Core from `@ofac/workspace-link`: `buildEvent, createLinkClient, enroll,
  LinkClient, LinkLogger, LinkTokenProvider, dataschemaFor, createConsoleLogger`.
  Node pieces from `@ofac/workspace-link/node`: `SignalrTransport,
  SqliteLedger, HttpPollTransport, postCapabilities, digestOfManifest`
  (browser-safe core split). `run()` is NOT exported — compose it yourself.
- Startup order (mattermost `index.ts`): provider identity (`api.me()`) →
  `enroll({baseUrl, token: enrollSecret, endpointId, instanceId, trustMode:
  "isolated-executor", role: "child", team})` → token provider with
  `refresh: enrollSelf` (401 rotation) → `postCapabilities(...)` (manifest
  with `dataschemaFor(id)` per capability) → `createLinkClient`.

## @ofac/adapter child surface (Gateway children)

Located `src/adapters/harnesses/adapter-sdk`. Exports `"."` + `"./testing"`.
- `createAdapter(options)`: `childWorkspaceId` (`<gatewayId>/<name>`), `token`
  (fallback `OFAC_CHILD_SECRET`), `manifest: CapabilityManifestV1`,
  `transport: {socketPath}|{host,port}`, `descriptor`, `hooks: HarnessHooks`,
  `kernel: {baseUrl, token}` REQUIRED, `restartBackoffMs` (`[]` disables).
  Returns `{ready(), status(), close(), events}`; reconnect w/ backoff,
  registry survives.
- `HarnessHooks.onExecute(ctx)`: resolving `{kind:"completed"}` means the
  harness published `assignment.completed.v1` ITSELF (SDK never fabricates);
  SDK publishes `assignment.failed.v1` on `{kind:"failed"}`/throw. `RunContext`:
  `executionId, command` (verbatim assign envelope — ops params ride
  `command.data.inputs`, op id in `command.data.capabilityId`), `signal`,
  `publishEvent`, `publishProgress`.
- Childwire: registration rides the `hello` frame
  `{v:1,type:"hello",childWorkspaceId,token,manifest}` — child sends it first;
  gateway answers `{v:1,type:"register-ack",accepted,reason?}`. NDJSON, 256 KiB gate.
- Worker exit-code law: 0 clean · 1 runtime · 2 config · 3 register-denied w/
  backoff off.

## better-sqlite3 ledger conventions

Own file per component (`ledger.ts` / `post-ledger.ts` pattern):
`chmodSync(path, 0o600)` on an existing file, `pragma journal_mode = WAL`,
`user_version` gate (0 ⇒ DDL+set; mismatch ⇒ throw). Idempotency-ledger
table: `<key> TEXT PRIMARY KEY, provider_object_id TEXT NOT NULL, ...,
created_at INTEGER` + `ON CONFLICT(<key>) DO NOTHING`; consult the ledger
BEFORE any provider mutation (replay returns the recorded id).

## Env-config law (`config.ts`)

Typed `ConfigError` names the missing key; required keys fail closed;
inline-token env wins over `*_TOKEN_FILE`; state path default
`./ofac-<name>.sqlite3`; log level whitelist via `OFAC_ADAPTER_LOG`.

## Supervisor-unit exit-code law

Units (`reference/container/units/N-*.sh`, Feature-baked mirrors) are
1-line-thin sh wrappers — process logic stays TS. Exit: 0 clean · 1 runtime
(restart, budget `OFAC_SUPERVISOR_MAX_RESTARTS` default 10, sleep 2) · ≥2
terminal. TERM trap: kill child, exit 0.
