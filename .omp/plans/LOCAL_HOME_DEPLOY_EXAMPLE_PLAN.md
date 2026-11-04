# Plan: `examples/local/` — home deployment reference infra (+ minted enrollment tokens in the kernel)

## Context

The idea (`.omp/ideas/local-deployment-reference-infra.md`): mirror the AWS reference example with a fully self-hosted, at-home deployment — Facilitator kernel on one box, Gateway+launcher node bundles on the others, no cloud. All AI-free: every verification during dev/CI/release must run with no LLM and no human in the loop (deterministic worker, scripted smoke). `reference/` stays the acceptance test harness and must NOT absorb deployment ergonomics; `examples/local/` is a fork-me example that answers "how do I run this at home".

Two deliverables:
1. **Kernel change (small, additive):** mintable, revocable enrollment tokens — the idea assumes "admin mints a short-lived token"; as-built enrollment uses ONE static config secret (`Link:EnrollmentToken`, consumed at `POST /api/link/v1/enroll`). Both modes must coexist (reference/, adapters, e2e untouched). Plus a mint/revoke card in the SPA Links panel.
2. **`examples/local/`:** roles file + `localctl` (TypeScript, `examples/local/tools/localctl`) that renders per-box docker-compose bundles and drives them (`render/up/enroll/status/smoke/logs/backup/upgrade`), with a single-host **sim mode** as the automated gate (`make example-local-smoke`), plus full operator docs.

User-settled decisions (do not re-litigate):
- TLS: multi-box = Tailscale + Caddy `tls internal`; **plain-LAN hostnames are first-class too** (roles file carries opaque host addresses; `https://<host>` with Caddy `tls internal`, operator trusts the CA). Sim mode drops Caddy/TLS entirely (plain 127.0.0.1 ports, like `reference/`).
- Webhooks: Tailscale **Funnel in first cut** for GitHub/Mattermost recipes; GitHub/Mattermost legs are **docs-only recipes**, not smoke-verified.
- Smoke IdP: **Keycloak stub** (same pattern as `reference/keycloak/realm.json`); Google OIDC is the documented day-one provider.
- Images: **build-local default** (compose builds from repo Dockerfiles so smoke never depends on ghcr); roles-file `images:` entries accept `name@sha256` registry digests for real installs; `localctl upgrade` re-resolves only registry refs and records old→new in a **`envs/<env>.lock.json` sidecar**.
- Node bundle = **persistent team gateway + oci-launcher** (elastic kernel-provisioned workers). Node boxes expose NO inbound API surface (gateway diagnostics bind container loopback; launcher has no health port) — only sshd (for `localctl` push) and outbound HTTPS/WebSocket.
- Smoke: plumbing legs hard-fail; Aspire telemetry leg soft (warn).
- Topology template: kernel + 2 nodes, 1 team (`home`).
- Gate: `make example-local-smoke`, NOT part of `make test` (same posture as examples/aws: smoke never in hosted CI).
- Token lifecycle: minted token short-lived until first use (default 60 min); after first use it becomes that endpoint's standing re-enroll secret (re-enroll-on-401 keeps working) until revoked/expired.

## Step 1 — Kernel: minted enrollment tokens

As-built anchors (read before editing):
- `src/facilitator/Kernel/Link/LinkEndpoints.Enroll.cs` — `POST /api/link/v1/enroll`, body `EnrollRequestDto{token, endpointId, instanceId, trustMode, role?, team?}` (`LinkDtos.cs`), 201 `EnrollResponseDto{enrollmentId, trustMode, linkToken, expiresAt}`. Static-secret compare in `LinkEnrollmentSecret.cs`.
- `src/facilitator/Kernel/Link/LinkEnrollmentStore.cs` — has `RevokeAsync` with no HTTP caller; wire it here.
- `src/facilitator/Kernel/Shared/Persistence/Entities.cs` + `Configurations/` + existing EF migrations folder (locate via existing `Migrations/` dir under the Persistence folder — confirm name at edit time).
- Gate table: `identity:authorization:operations` bindings; `AdminEndpoints.cs` for the `/api/admin/*` pattern.
- Tests: `tests/Facilitator.Tests/Link/EnrollmentTests.cs`, `tests/Facilitator.Tests/Admin/` conventions.

**1a. Entity + migration.** New table `facilitator.enrollment_tokens`:
- `id uuid pk`, `token_hash text not null unique` (SHA-256 of the minted token, same hashing law as `link_enrollments`), `label text not null`, `team text not null`, `role text not null` (`gateway` or `child`), `created_by text` (subject claim of minter), `created_at timestamptz`, `expires_at timestamptz`, `consumed_at timestamptz null`, `consumed_endpoint_id text null`, `link_enrollment_id uuid null` (set on consumption; links to the minted enrollment for revoke).
- Register in `Entities.cs`, configure under `Configurations/`, add one EF migration (migrations run automatically at startup via `DatabaseInitializer` — no deploy changes).

**1b. Mint/revoke API** (new file `src/facilitator/Kernel/Link/EnrollmentTokenEndpoints.cs`, mapped next to the other Link/admin routes):
- `POST /api/admin/enroll-tokens` — body `{label, team, role, ttlMinutes?}`. Team must exist in the team roster (403 `team-unknown` mirroring enroll law). `ttlMinutes` default **60**, clamp 1–10080. Token literal: `"et_"` + 32 random bytes base64url. Response 201 `{id, token, expiresAt}` — token returned **only here**; persist hash only.
- `GET /api/admin/enroll-tokens` — list: `{id, label, team, role, createdAt, expiresAt, consumedAt, consumedEndpointId, revokedAt}` (never the token).
- `DELETE /api/admin/enroll-tokens/{id}` — revoke: set `revoked_at` AND call `LinkEnrollmentStore.RevokeAsync(link_enrollment_id)` when consumed, so the issued link token dies immediately. 404 unknown, idempotent on already-revoked.
- New operation-gate binding id **`enrollment-tokens.manage`**, `methods:["POST","GET","DELETE"]`, `paths:["/api/admin/enroll-tokens", "/api/admin/enroll-tokens/{id}"]`, `rolesAnyOf:["framework-admin"]`. Add it to the gate table shipped in kernel default `appsettings.json` config (and `reference/config/facilitator.json` — additive, existing gates untouched).

**1c. Consumption law in `POST /api/link/v1/enroll`.** Extend the token check (keep existing static-secret path byte-identical):
1. Look up SHA-256(token) in `enrollment_tokens`.
2. No row → existing static-secret comparison (unchanged behavior, incl. rate limit `enroll` 30/min).
3. Row found: reject 401 `bad-token` if `revoked_at` set or `expires_at` passed. If `consumed_at` null → first use: require request `endpointId`/`team`/`role` to match the token row (403 `token-mismatch` otherwise — `gw.`-prefix role law unchanged); mark consumed (endpoint id, enrollment id). If already consumed → this is a **re-enroll**: allow iff `consumed_endpoint_id` equals request `endpointId` AND team/role match (rotation semantics unchanged — token re-issues, old link token stops validating). Wrong endpoint reusing a consumed minted token → 401.
4. Minted-token enrollments ignore the static secret entirely; static secret continues to work for anyone who knows it (documented break-glass).

**1d. Kernel tests** (`tests/Facilitator.Tests/Link/EnrollmentTokenTests.cs`, reuse `LinkTestHarness`):
mint→enroll(gw.home, team home)→201+link token works; second box enrolling with the consumed token under a different endpointId → 401; re-enroll same endpointId rotates the link token (old token 401); expired minted token → 401; revoked token → 401 AND previously-issued link token now 401; list never contains the raw token; DELETE twice → second is 204/404 per existing admin conventions; gate: `team-member` token POSTs → 403. Static-secret regression: existing `EnrollmentTests` still pass unmodified.

## Step 2 — SPA: mint card in Links panel

`src/facilitator/ui/src/components/LinksView.svelte` gains an "Enrollment tokens" card (only rendered for `framework-admin`, following the existing role-gating idiom in that file):
- Form: label, team (dropdown from `/api/admin/summary` team list — confirm the source at edit time), role (gateway|child), TTL minutes (default 60). Button → `POST /api/admin/enroll-tokens` via the existing admin fetch helper in `src/facilitator/ui/src/lib/`; show the returned token once, copy-to-clipboard, "shown only once" warning.
- Table: label, team, role, status (`fresh|consumed→endpointId|expired|revoked`), expires, revoke button → DELETE + refresh.
- vitest/jsdom unit test per existing SPA test conventions: mint renders token; revoke calls DELETE; non-admin renders nothing.

## Step 3 — `examples/local/` tree + roles file

New pnpm workspace member (add to `pnpm-workspace.yaml`): **`examples/local/tools/localctl`**, package name `example-local-deploy` (mirrors `example-aws-deploy`), scripts `build`(tsup/tsc), `test`(vitest), `typecheck`. Repo gates cover it automatically (`make lint`, typecheck). No runtime deps beyond node builtins + `yaml` (catalog); it shells out to `docker`, `ssh`, `scp`, `curl`.

```
examples/local/
  AGENTS.md                      # ≤40-line signpost: purpose, sim-vs-real law, anchors
  README.md                      # operator guide (Step 6)
  versions.env                   # third-party pins: POSTGRES_TAG, KEYCLOAK_TAG, MINIO_TAG, ASPIRE_TAG, CADDY_TAG, TAILSCALE_TAG (mirrors reference/versions.env, incl. *_DIGEST empty fields)
  envs/example.json              # multi-box template (kernel + pc1 + pc2)
  envs/sim.json                  # single-host simulation env (same schema, topology=sim)
  localctl                       # bash wrapper: exec node tools/localctl/dist/index.js "$@"
  tools/localctl/src/...         # render.ts, env.ts (roles-file load+validate), compose.ts (sim driver), ssh.ts (multi-box driver), smoke.ts, backup.ts, upgrade.ts, status.ts
  templates/kernel/              # compose template + config (see Step 4)
  templates/node/
  keycloak/realm.json            # example realm (Step 5)
```

**Roles file schema** (`envs/*.json`, validated in `tools/localctl/src/env.ts` with `:?`-style fail-closed errors naming the key — mirror `examples/aws/lib/env.ts` posture):
- `name`, `topology: "sim" | "lan" | "tailscale"`.
- `kernel: { host, ports?: {kernel, idp, aspireUi}, teams: ["home"], enrollmentTtlMinutes? }` — `host` is an opaque address: tailscale MagicDNS name, LAN hostname/IP, or (sim) the in-network service name `facilitator`.
- `nodes: [{ host, gatewayId: "gw.<slug>", teams: [...], launcher: true }]` — workers are NOT declared (kernel runtime decisions, launcher executes).
- `images: { kernel, node, worker }` — each either omitted/`"local"` (compose builds from repo: `src/facilitator/Dockerfile`, root `Dockerfile.node`, worker = `reference/container/Dockerfile.elastic` pattern copied into `examples/local/templates/kernel/` as `Dockerfile.worker` so the example is self-contained) or a `name@sha256:…` digest string.
- `tls: "internal" | "acme"` (kernel-box Caddy; ignored when `topology: "sim"`).
- `tailscale?: { keyEnv: "TS_AUTHKEY", funnel: bool }`.
- `backupPath` (host folder outside Docker, e.g. `/var/backups/facilitator`).
- `secretsFile` — path to a `.env` (gitignored) holding `OFAC_ENROLLMENT_TOKEN`, `POSTGRES_PASSWORD`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `IDP_CLI_SECRET`, `CHILD_SECRETS_*`, generated by `localctl init` with `openssl rand -hex 16` (mirrors `reference/bootstrap/generate-secrets.sh`).
- `externals?: { github?, mattermost? }` — endpoint/secret fields consumed by the docs recipes only.
- `sshUser?` (default `facilitator`), `remoteDir?` (default `/opt/facilitator`).

`localctl render --env NAME` is the primitive: reads roles file + secrets file + `versions.env`, emits deterministic per-box bundles under `.out/<env>/<box>/` (`compose.yaml`, `.env`, config files) and writes/updates `envs/<env>.lock.json` (resolved image refs; for registry refs, resolves tag→`name@sha256` via `docker buildx imagetools inspect`, records `{from,to,timestamp}` entries appended on upgrade). Vitest snapshots of rendered compose for both topologies are the unit gate.

## Step 4 — Compose templates

**Kernel bundle** (`templates/kernel/`) — services modeled on `reference/compose.slim.yaml` (reuse its healthcheck idioms: `pg_isready`, node fetch for no-shell images, `restart: on-failure:5`, one kernel `env_file`):
- `facilitator` (kernel image; env per the reference contract verbatim: `ConnectionStrings__Facilitator`, `Link__EnrollmentToken`, `Link__HeartbeatSeconds=5`, `Storage__Provider=s3` + `Storage__S3__Endpoint/Bucket/Region/AccessKey/SecretKey` → MinIO, `Otel__Endpoint` + `OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire:18890` + `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, `Link__Teams=["home"]`, `identity` section via mounted `facilitator.json` incl. the `enrollment-tokens.manage` gate binding and an `admin` role rule matching your OIDC `sub`/realm role, `Provision__LauncherEndpoint`, `Provision__Workloads__0__*` with `Image`=rendered worker image, `Network`=rendered shared network, `Labels__io.facilitator.managed=kernel-provisioned`, `$$`-indirected `Env__*` values for the launcher to expand — copy the pattern from `reference/config/facilitator.env`). Migrations run automatically (`Database:AutoMigrate` default on).
- `postgres` (`postgres:${POSTGRES_TAG}`, named volume, healthcheck, init SQL single db — no keycloak db: Keycloak `start-dev` embedded H2 is fine for an example stub, unlike reference's KC_DB=postgres).
- `minio` (`minio/minio:${MINIO_TAG}`, named volume, bucket `facilitator-artifacts` auto-created via `MINIO_ROOT_*` + one-shot `minio/mc` init container; kernel uses path-style `Storage__S3__ForcePathStyle=true`).
- `keycloak` (`start-dev --import-realm` → `keycloak/realm.json`, issuer hostname rendered: `http://id.localhost:<idpPort>` for sim, `https://<kernelHost>/id` reverse-proxied through Caddy for lan/tailscale — issuer string MUST byte-match `identity:oidc:authority`, reference's alias law).
- `aspire` (`mcr.microsoft.com/dotnet/aspire-dashboard:${ASPIRE_TAG}`, `Resume` persistence mode, SQLite volume, OTLP HTTP 18890).
- `caddy` + `tailscale` (lan/tailscale topologies only): one hostname-keyed Caddyfile, `tls internal`, `reverse_proxy` kernel + keycloak + MinIO console + Aspire UI, single 443; `tailscale` sidecar with `TS_AUTHKEY`, Funnel enabled when `funnel: true`, webhooks ride the same site block. Sim renders neither.
- Named volumes for every stateful service (postgres, minio, aspire, caddy) — templates make ephemeral state impossible.

**Node bundle** (`templates/node/`) — node-runtime image, two services:
- `gateway`: supervisor pattern from `reference/container/units` (gateway + deterministic-worker child over in-container unix socket, config `gateway.yaml`/`worker.yaml`/`capabilities.yaml` mirroring `reference/config/workspace/`, `stateDir` named volume → `<stateDir>/token` persistence). Env: `OFAC_KERNEL_URL` (rendered), `OFAC_ENROLL_SECRET` (the minted token — see Step 5), `OFAC_CHILD_SECRET`. No published ports (diagnostics stay container-loopback — **invariant: node boxes expose no inbound API surface**).
- `oci-launcher`: verbatim wiring from `reference/compose.provisioning.yaml` (`user: root`, `/var/run/docker.sock` mount, `launcher_state:/var/lib/ofac`, `OFAC_ENDPOINT_ID=launcher.<node>`, `OFAC_TEAM`, `OFAC_STATE_PATH`, `OFAC_PROV_ALLOWED_IMAGES` = rendered worker image, `OFAC_PROV_ALLOWED_NETWORKS` = rendered shared network).
- OTLP: node services get `OTEL_EXPORTER_OTLP_ENDPOINT` pointed at the kernel box (`http://aspire:18890` on the shared network in sim; `http://<kernelHost>:18890` via tailscale/lan — a plain exporter port, no collector config files, zero-config law).

**Driver split** (`tools/localctl`):
- sim: three local compose projects on one host — `ofackernel` (kernel bundle, published `127.0.0.1:${LOCAL_*_PORT}` ports: kernel 7488, idp 7480, aspire UI 7490, OTLP 7091) + `ofacnode-<name>` per node; shared **external** docker network `ofac-local` created by `localctl up` (containers of different projects resolve each other by service name; nodes add `extra_hosts: host-gateway` only if a recipe needs the host). No Caddy, no tailscale, plain http. Hosts-file note: operator adds `127.0.0.1 id.localhost` (documented + checked by `localctl status`).
- lan/tailscale: `localctl up <box>` / `enroll <box>` scp-push the rendered bundle to `<sshUser>@<host>:<remoteDir>` and run `docker compose up -d` over ssh. Workers on a node's launcher are socket-born (label-swept on `localctl down`, mirroring referencectl's `sweep_managed`).

## Step 5 — Enrollment hand-off (as-built law, corrected)

`localctl enroll <node>`:
1. `localctl enroll-token --node <name>` mints via `POST /api/admin/enroll-tokens` (role gateway, node's team) using an admin token obtained from the example Keycloak (`facilitator-cli` client_credentials, same trick as `referencectl token`).
2. The minted token is written into the node bundle's `.env` as `OFAC_ENROLL_SECRET` (gateway + launcher both use it; gateway persists the rotated link token at `<stateDir>/token`; re-enroll-on-401 replays the minted token, which stays valid as this endpoint's standing secret).
3. Push + `docker compose up -d` on the box.
The static `Link__EnrollmentToken` remains configured on the kernel (break-glass + kernel-side workload identities), documented as such. Day-one with real Google: an admin with the static secret (or the CLI client) mints via API/curl/SPA; the example's Keycloak is just the pre-Google bootstrap path.

**Example realm** (`examples/local/keycloak/realm.json`): realm `facilitator`; clients `facilitator-api` (public, PKCE, redirect `http://localhost:7488/*` + rendered kernel URL) and `facilitator-cli` (confidential, secret from secrets file, service-account roles `kernel-operator`,`framework-admin` via `/realm_access/roles`); one user `admin/admin` (framework-admin + kernel-operator + `/teams/home`). Role rules + gate table in the rendered `facilitator.json` mirror `reference/config/facilitator.json` (copy, trim to `home` team).

## Step 6 — `localctl smoke` (the automated gate, AI-free)

Deterministic, no humans, no LLM. Sequence (mirror `reference/checks/verify.sh` law: waits ride timeline/inbox event kinds via polling helpers, per-leg continue-and-report, final exit code):
1. Pre-clean (`localctl down --env sim -v`) then `localctl up --env sim` (builds local images: kernel via `src/facilitator/Dockerfile`, node-runtime via root `Dockerfile.node`, worker via the example's `Dockerfile.worker`; `--no-build` reuse flag for iteration).
2. Hard: kernel `GET /health` 200 within 240s.
3. Hard: mint CLI token (Keycloak client_credentials); seed team exists (from `Link__Teams`) and seed one project via `docker exec` psql INSERT (mirrors `reference/bootstrap/seed-lib.sh:seed_project` — no REST project route exists).
4. Hard: mint enrollment token for node1 + node2, bring both node bundles up, poll `GET /api/admin/links` until both `gw.*` endpoints `online:true` (60s).
5. Hard (persistent-gateway leg): `POST /api/works` (Bearer CLI token) `{objective, capabilityId:"research.analyze", projectId}` → poll `GET /api/works/{id}/timeline` until `assignment.completed.v1` (90s) → exactly one output manifest.
6. Hard (elastic leg): `PATCH /api/admin/config/workload-scale` (min1, If-Match `*`, `X-Change-Reason: localctl-smoke`) → wait for a container labeled `io.facilitator.managed` on the shared network (60s, host docker CLI) → dispatch a second work at the elastic capability → timeline completes → `GET /api/admin/provisioning` shows a succeeded provision op → scale back to min0, wait launcher GC-removes the container.
7. Hard: `GET /api/admin/deliveries?state=dead_letter` count 0.
8. Soft: Aspire query API (UI port, unsecured-anonymous) shows >0 spans for the traces stamped on the two outbox rows (pattern from verify step `trace-correlation`); missing → warn line, not failure.
9. Report table (leg, status, duration) + exit code.
Cleanup: `localctl down --env sim` (volumes kept unless `-v`).

**Makefile**: add `example-local-smoke` to `.PHONY` + target: `pnpm -C examples/local/tools/localctl build && node examples/local/localctl smoke --env sim --clean-exit`. Prereq: Docker Engine; runs from a clean tree; NOT wired into `make test`/`make lint` beyond build/typecheck of the localctl package.

`AGENTS.md` signpost (40-line law): purpose, sim-vs-real law (sim is the gate; multi-box posture documented, verified manually), node-box no-inbound invariant, anchors (roles schema, templates, kernel token endpoints).

## Step 7 — Operator docs (`examples/local/README.md`)

Mirror `examples/aws/README.md` structure: Shape (mermaid: kernel box + N node boxes, all links outbound) → Laws (outbound-only nodes, digest-or-local images, rendered-not-handwritten, lockfile law) → Machine prep per box kind (kernel: Docker+compose v2 (+tailscale for that topology); node: Docker Engine + docker-group socket access + outbound HTTPS to kernel + sshd for push, nothing listening inbound; LLM box = node + "your model server answers from this box") → Bring-up order (`init` → render → up kernel → enroll-token → up node → smoke) → Enrollment hand-off section (static secret vs minted token law, revoke) → Google OIDC recipe (GCP project → OAuth consent External → Web client → redirects `https://<kernelHost>/*` → `identity__oidc__*` + `ui__oidc*` env in the roles file's externals; how to find your `sub` and paste the admin role rule) → Funnel/webhooks (GitHub/Mattermost: Funnel on the site block; both adapters are config recipes pointing at operator-run services, not deployed here) → Backup/restore (`localctl backup`: `pg_dump` + `mc mirror` of the artifacts bucket + aspire db + caddy data → dated dir under `backupPath` (host folder, outside Docker, survives `compose down -v`); restore = documented steps) → Upgrade (`localctl upgrade` re-resolves registry refs, writes lock.json diff, re-render, per-box `compose pull && up -d` roll) → Day-one walkthrough (sign in with Google, find `sub`, admin role rule, browse Links panel — mint a token for a third machine, submit a work via curl (no work-submit form exists in the SPA today — say so), watch timeline) → Tests & gates (`pnpm -C examples/local/tools/localctl test` + `make example-local-smoke`; the multi-box LAN run is the same commands against real boxes).

## Verification

1. `dotnet test --filter "FullyQualifiedName~EnrollmentTokenTests"` — new green; `dotnet test --filter "FullyQualifiedName~Link"` — existing enrollment tests still green unmodified (static-secret coexistence proof).
2. `pnpm -C examples/local/tools/localctl test` — render snapshots: sim compose has no caddy/tailscale services, plain ports, shared network `ofac-local`; lan render has Caddy `tls internal`; digest-pinned roles file renders `image: name@sha256:…` and nothing else; missing secret key fails with the key name in the error.
3. SPA: existing ui test command green incl. new card test.
4. `make example-local-smoke` on a clean checkout with Docker: full leg sequence above passes end-to-end; observable: two gateways online in `/api/admin/links`, two completed works with manifests, managed worker container appears then is GC'd, zero dead letters, Aspire UI at the printed port shows the spans. Negative probe during development: kill node2's gateway container mid-run → status leg reports it offline (smoke runs before/after so ordering makes this informational).
5. Regression: `./referencectl up slim --no-seed && ./referencectl verify` still exits 0 (kernel migration + enroll-route change must not touch the reference path) — run once at the end.

## Assumptions & contingencies

- Migrations folder name/location in the Persistence tree is as-is (confirm at edit time; if the project uses one canonical Migrations dir, add there).
- If the Aspire dashboard image version in `reference/versions.env` (`ASPIRE_TAG`) lacks `Resume` persistence on arm64, fall back to `Transient` mode for the example and note it in README (telemetry leg is soft anyway).
- If `docker buildx imagetools inspect` is unavailable in the sim environment, `localctl upgrade`/render treats it as registry-unreachable: keep last lock digest and warn (sim mode never needs it — build-local images).
- MinIO chosen over reference's SeaweedFS deliberately (canonical home S3, path-style, trivial bucket-init sidecar); if its arm64 manifest fails on this dev host, swap to the reference's SeaweedFS service line verbatim.
- Deterministic worker capability id is `research.analyze` as in reference config (copy its `capabilities.yaml`); if the deterministic harness advertises a different id, use that literal in smoke, don't invent a second capability.
