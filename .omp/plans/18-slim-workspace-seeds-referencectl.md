# Plan 18 — Slim reference environment, leg 2: Gateway + worker in Compose, seeds, referencectl

**Depends on:** 17 (🟢 implemented). **Phase E leg 2 of 5.** Chain: 17 → 18 → 19 (reference env), 20 → 21 (operator surface).
This file supersedes the prior draft of `18-slim-workspace-seeds-referencectl.md`; after approval its content replaces that file verbatim.

## Context

Spec §21.1's slim profile must be a one-command demo: after Plan 17 the stack boots a healthy, authenticated, S3-backed Facilitator — but nothing links to it. This plan adds the team-owned half: a `demo-workspace` compose service (Gateway + deterministic worker in one container, the §9.1 mode-(b) embedded story over the localhost child leg), a `reference/bootstrap/` seed pass that rides the real API/SQL surface (demo project, demo Work, one `work.assign` outbox row), and a repo-root `./referencectl` CLI (`up slim` / `down` / `status` / `token` / `work`; `verify` is reserved for Plan 19). After this plan a fresh clone reaches a linked, policy-gated, dispatching workspace path with `./referencectl up slim`, and Plan 15's `kernelUrl` kill rule is executed.

**Process rules for the implementer:**
- Inspect the as-built anchors below before writing each artifact; code wins over this document, and divergence gets recorded in the Implementation Status table with a reason.
- Consult existing proven invocations before inventing new ones: `src/adapters/harnesses/deterministic/test/e2e/worker.e2e.test.ts` is the code-proven reference for the enroll→project-seed→assign→completion flow this plan automates over compose; `examples/slim/deterministic-research-worker/{README.md,.facilitator/*.yaml}` is the proven container-path config pair.
- Iterative problem-solving: when a step misbehaves, try the straightforward fix consistent with these anchors first; after two failed iterations, step back and re-simplify that piece rather than adding machinery. Prefer clear, composable, well-bounded scripts over clever ones.
- Where provider/tooling behavior is uncertain (Keycloak form-post flags, compose `--wait` semantics, pnpm), consult current provider docs on the web; infer missing details from existing code/specs — **questions may be asked during planning, but not during implementation.**

## As-built facts (verified 2026-10-28; re-check anchors before editing)

- **No production assignment emitter exists.** `work.assign` rows only enter `facilitator.outbox` via the Development-only `POST /api/dev/link-outbox` (`src/facilitator/Kernel/Link/LinkEndpoints.cs:61-63`), and the slim kernel container runs **Production** (no `ASPNETCORE_ENVIRONMENT`). No routing slice creates `assignments` rows. The worker e2e seeds the identical flow with raw SQL + `assignEnvelope` (`worker.e2e.test.ts:187-192, 248-260`); completion truth for a demo flow is the `facilitator.inbox` row (`ResultIngress.cs` resolves by GUIDs in the completed event, and a projectless work "answers manifest-not-found" — team scope resolves via work → project → team, so the demo Work **must** carry a `projectId`).
- Outbox law: dispatcher LISTENs on channel `facilitator_outbox` and degrades to ≤5 s polling (`PostgresNotifications.cs:14`, `OutboxDispatcher.cs:19-20`); required columns per `OutboxEntityConfiguration.cs`: `outbox_id, recipient_endpoint_id, aggregate_type, event_type, command_id, envelope(jsonb), status, available_at, attempts, created_at` (+ nullable `aggregate_id, work_id, lease_owner, leased_until, last_error, processed_at`). Link recipients are addressed `link.<endpointId>` (`LinkEndpoints.cs` dev affordance: `$"link.{endpointId}"`).
- Admission of a link-plane command evaluates `data.actorEvidenceRef`; an unknown scheme (e.g. `identity-evidence:`) is **Valid-but-unverified** and admitted with audit (`EvidenceValidator.cs:16,66-70`; the CI example seeds exactly `identity-evidence:demo`).
- Enrollment: `POST {kernel}/api/link/v1/enroll`, secret/JWT in the **body** (no bearer), `{token, endpointId, instanceId, trustMode, role?, team?}` → 201 `{enrollmentId, trustMode, linkToken, expiresAt}`; mode-(a) shared secret vs `Link:EnrollmentToken` (compose passes `Link__EnrollmentToken: ${DEMO_ENROLLMENT_TOKEN}`, `compose.slim.yaml:99`); mode-(b) OIDC CC JWT bound to `Link:WorkloadIdentities` by `(azp, endpointId)` — the mounted config maps `ofac-gateway-alpha → gw.team-alpha / team-alpha / gateway / acceptsDelegatedClaims:true` (`reference/config/facilitator.json:8-23`); `gw.`-prefixed endpointId requires role `gateway`; rate limit 30/min/IP. A `gw.` prefix is a Gateway endpoint; a child enrolls a non-`gw.` endpointId with role `child` (worker-e2e precedent: `det-e2e-child`).
- Gateway config (`src/gateway/src/config.ts`, deny-unknown-keys): required `apiVersion: facilitator.framework/v1`, `kind: Gateway`, `gatewayId` (`^gw\.[a-z0-9]([a-z0-9-]*[a-z0-9])?$`), `team`, `facilitatorUrl` (no default), `trustMode`, `stateDir`; `mode: standalone` allows `children: []` + late child hello + `childTransport` (unix `socket.path` **required**, resolved against `stateDir`; optional `tcp.port` always binds 127.0.0.1). Env overlays: `OFAC_ENROLL_SECRET`, `OFAC_LINK_TOKEN`, `OFAC_CHILD_SECRET`, mode-(b) triple `OFAC_OIDC_TOKEN_URL/OFAC_OIDC_CLIENT_ID/OFAC_OIDC_CLIENT_SECRET` (win per-key; env-only triple must be complete). `diagnostics.port` binds **127.0.0.1** only, `/status` no auth; CLI `ofac-gateway run --config <path>` (env `GATEWAY_CONFIG`), exit law 0 clean · 1 runtime · 2 config · 3 protocol · 4 enrollment-rejected.
- Worker config (`src/adapters/harnesses/deterministic/src/config.ts`): `apiVersion/kind/childWorkspaceId/token/kernelUrl?/kernelToken?/transport{socket.path|xor tcp}/manifestFile/workDir/stepMs/heartbeatSeconds/restartBackoffMs`; relative paths resolve against the config file's dir; env `OFAC_KERNEL_URL`/`OFAC_KERNEL_TOKEN`/`OFAC_CHILD_SECRET`/`WORKER_CONFIG`. `kernelUrl` is **optional** today; the harness hard-throws when unset (`harness.ts:112-116`) — the remaining dev path is config-level optionality (`config.ts:27-28,190-192`, `worker.ts:37-44` conditional `kernel`, `adapter-sdk/src/types.ts:103-110` optional, `adapter.ts:288-296` conditional). Artifact uploads post to `{kernelUrl}/api/link/v1/events` with the **child link token** (the Gateway child secret is not a kernel credential).
- The devcontainer example's `.facilitator/harness.yaml` sets **no** `kernelUrl` and the harness-adapter unit (`features/src/harness-adapter/install.sh:47-73`) sets no `OFAC_KERNEL_*` env → the CI `example-smoke` job's completion assertion (`ci.yml:339-345`) is stale-since-Plan-15 (kernelUrl kill rule). The unit self-enrollment fix in Step 3 repairs it.
- Child wire: NDJSON, hello `{v:1,type:"hello",childWorkspaceId:"<gatewayId>/<name>",token,manifest}`; per-child `children[i].token` wins over global `OFAC_CHILD_SECRET`; neither ⇒ `child-auth-disabled` warn + allow. Capabilities: standalone Gateway POSTs nothing at boot; each child hello carries the manifest, child churn triggers a debounced union POST to `POST /api/link/v1/capabilities` (204; `ownerTeam` must equal the enrollment's team).
- Image: `Dockerfile.node` builds `/app/{gateway,linkd,worker}`, runtime `node:22-bookworm-slim`, `USER node` (uid 1000), no EXPOSE/HEALTHCHECK, no writable-state dir yet. Debian-slim ships no curl/wget → in-container probes use `node -e "fetch(...)"`. Release job already publishes `node-runtime:{tag,latest}`.
- `reference/` today: 5-service compose (`name: facilitator-demo`), `.env.example` keys `DEMO_POSTGRES_PASSWORD, DEMO_KEYCLOAK_ADMIN_PASSWORD, DEMO_S3_ACCESS_KEY, DEMO_S3_SECRET_KEY, DEMO_ENROLLMENT_TOKEN`; `versions.env` has `OCI_REGISTRY`, `FRAMEWORK_TAG=dev`, no node-runtime-specific key (the image reuses `FRAMEWORK_TAG`). CI `meta` job (`ci.yml:53-71`) enforces the `versions.env` key list. Makefile: `compose-slim/-full/-down` real, `example-slim-up` = devcontainer-cli up. Kernel CLI: only `work create|get|timeline|steer` with `--url/--token` (create sends `{objective}` only — no capabilityId/projectId flags).
- Kernel in-network URL is `http://facilitator:8080` (container binds 8080; host maps 8088). Keycloak network alias makes `http://id.localhost:8080` resolvable **inside** the compose network as well as from the host — the issuer-byte-match law applies to in-container token minting too.
- Realm (`reference/keycloak/realm.json`): clients `facilitator-cli`/`e2e-cli-secret` (kernel-operator), `ofac-gateway-alpha`/`gateway-alpha-secret`, users `alice/alice` (framework-admin, `/teams/team-alpha`). Token mint form POST: `grant_type=client_credentials&client_id=facilitator-cli&client_secret=e2e-cli-secret` to `http://localhost:8080/realms/facilitator/protocol/openid-connect/token`.

## Locked decisions

1. **One container, two processes:** compose service `demo-workspace` runs Gateway (`standalone`, empty fleet) + deterministic worker over a unix-socket child leg inside the container (TCP `127.0.0.1` leg can't cross containers as shipped; a sidecar is overkill). Entrypoint reproduces the Plan 08 supervisor story: `features/shared/ofac-supervisor.sh` bind-mounted + two unit scripts, no devcontainer CLI.
2. **Dispatch seed = bootstrap SQL outbox insert + `pg_notify`** (user decision): zero kernel code change, wire behavior identical to the future routing slice, same idiom as the e2e tiers. `POST /api/dev/link-outbox` is *not* promoted.
3. **Gateway identity = realm workload mode (b):** `workload` triple inline in `reference/config/workspace/gateway.yaml` (`tokenUrl: http://id.localhost:8080/realms/facilitator/protocol/openid-connect/token`, `clientId: ofac-gateway-alpha`, `clientSecret: gateway-alpha-secret`) — matches the kernel's `WorkloadIdentities` binding and the e2e harness pattern. Child kernel credential = **unit self-enrollment** (user decision): the worker unit script mode-(a)-enrolls a child endpoint at startup (retry until kernel live) and exports `OFAC_KERNEL_TOKEN` — this also repairs the devcontainer example.
4. **`kernelUrl` becomes a required config key** (Plan 15 kill rule, clean cutover): no optional path, no compatibility shim.
5. **`referencectl` is a repo-root bash script** over `docker compose` + `curl` + `jq` + `docker compose exec postgres psql` — an orchestrator, not a runtime. `verify` verb reserved (Plan 19).
6. Team/gateway identity for the demo: `team-alpha` / `gw.team-alpha` (already in the mounted kernel config roster + realm + binding table). Demo child endpointId: `demo-worker-child`. Demo project name: `slim-demo`. Policy: `research.analyze` allow, `research.publish` confirm (demo approval contract, mirrors the example README).

## Steps

### 1. `kernelUrl` cutover (code; lands first, keeps every tier green alone)
- `src/adapters/harnesses/deterministic/src/config.ts`: make `kernelUrl` required (absent ⇒ config error, exit 2, message naming the key `kernelUrl`); keep `kernelToken` optional (defaults to the child token — `adapter.ts:289-296` already falls back).
- `src/adapters/harnesses/deterministic/src/bin/worker.ts:37-44`: pass `kernel: { baseUrl, ...(kernelToken) }` unconditionally.
- `src/adapters/harnesses/adapter-sdk/src/types.ts` + `adapter.ts:288-296`: `kernel: { baseUrl: string; token?: string }` becomes a **required** `createAdapter` option; `RunContext.kernel` non-optional.
- `src/adapters/harnesses/deterministic/src/harness.ts:112-116`: delete the `ctx.kernel === undefined` throw.
- Update every caller: `xd://lsp` references on `createAdapter`/`bootWorker`/`kernelUrl` — expected: `worker.ts`, unit tests (`test/harness.test.ts` already passes `kernel`), e2e `startWorker` (already passes `kernelUrl`). The embedded gateway children and linkd don't use `@ofac/adapter` — confirm via references, don't touch.
- Update docs/comments that call kernelUrl optional or mention the retired interim (worker package README, `.omp/skills/facilitator-workspace-packaging` if cited).

### 2. Unit self-enrollment + example fix (Features payload)
- `features/src/harness-adapter/install.sh`: in the generated `30-harness-adapter.sh` unit, before the run loop: when `OFAC_KERNEL_URL` and `OFAC_ENROLL_SECRET` are set and `OFAC_KERNEL_TOKEN` is not, loop (≤60 × 2 s, then exit 1 with a named failure) a `curl` POST to `${OFAC_KERNEL_URL}/api/link/v1/enroll` with body `{"token":"$OFAC_ENROLL_SECRET","endpointId":"${OFAC_WORKER_ENDPOINT:-demo-worker-child}","instanceId":"unit-$$","trustMode":"isolated-executor","role":"child","team":"${OFAC_WORKER_TEAM:-team-alpha}"}`, extract `.linkToken` (jq-free: `sed` extraction is fine in sh, or require jq only where already available — devcontainer base image has curl; parse with `node -e` since every Feature image has node), `export OFAC_KERNEL_TOKEN`.
- `examples/slim/deterministic-research-worker/.facilitator/harness.yaml`: add `kernelUrl: http://host.docker.internal:8088`. Top-level `worker.yaml` already sets `kernelUrl: http://localhost:8088` — leave.
- `.devcontainer/devcontainer.json` `containerEnv`: add `"OFAC_KERNEL_URL": "http://host.docker.internal:8088"`.
- Example README: fix the stale "`dev kernel seeds default`" claim (§Troubleshooting) to the team-alpha/team-beta roster reality, and document the unit's self-enroll step.
- Proof: existing `example-smoke` CI job (`ci.yml:266+`) — its completion assertion now actually passes.

### 3. Container payload for `demo-workspace`
- `Dockerfile.node`: add `RUN install -d -o node -g node /var/lib/ofac` (writable state root owned by uid 1000). No other change.
- New `reference/container/units/10-gateway.sh` and `reference/container/units/30-harness-adapter.sh`: adapted from the Feature heredocs (`features/src/gateway/install.sh:52-74`, `install.sh:47-73`) — same exit-code law (`rc ≥ 2` terminal, restart budget `OFAC_SUPERVISOR_MAX_RESTARTS` default 10, sleep 2) — pointing at `GATEWAY_CONFIG=/opt/ofac/config/gateway.yaml`, `WORKER_CONFIG=/opt/ofac/config/worker.yaml`, and binaries at `/app/gateway/dist/bin/gateway.js`, `/app/worker/dist/bin/worker.js`. The worker unit carries the **same self-enroll block** as step 2 (shared snippet; keep both copies literal and identical — a `reference/container/units/README.md` one-liner notes the duplication is deliberate).
- New `reference/config/workspace/gateway.yaml`: `mode: standalone`, `children: []`, `gatewayId: gw.team-alpha`, `team: team-alpha`, `role: gateway`, `facilitatorUrl: http://facilitator:8080`, `trustMode: isolated-executor`, `workload:` triple (decision 3), `stateDir: /var/lib/ofac/gateway`, `childTransport.socket.path: child.sock`, `policy.path: policy.yaml`, `diagnostics.port: 8099`, `heartbeatSeconds: 5`.
- New `reference/config/workspace/policy.yaml` (`policy-file.v1` law, first-match-wins + implicit deny, copy the e2e example shape): `owner: team-alpha`; row 1 `capability: research.publish`, `resources: ["*"]`, `actorAnyOf: []`, `effect: confirm`; row 2 `capability: research.analyze`, `resources: ["*"]`, `actorAnyOf: []`, `effect: allow`.
- New `reference/config/workspace/worker.yaml`: `apiVersion/kind` per schema, `childWorkspaceId: gw.team-alpha/deterministic`, `kernelUrl: http://facilitator:8080`, `transport.socket.path: /var/lib/ofac/gateway/child.sock`, `manifestFile: /opt/ofac/config/capabilities.yaml` (copy of `examples/slim/.../capabilities.yaml` — `ownerTeam: team-alpha`, `research.analyze` v1 + `research.publish` v1), `workDir: /var/lib/ofac/worker`, `stepMs: 800`, `heartbeatSeconds: 5`. No `token:`/`kernelToken:` — env supplies both (`OFAC_CHILD_SECRET`, self-enrolled `OFAC_KERNEL_TOKEN`).

### 4. Compose service + env pins (`reference/compose.slim.yaml`)
- New service `demo-workspace` (name per spec §22.2 template): `build: {context: .., dockerfile: Dockerfile.node}`, `image: ${OCI_REGISTRY:?set in versions.env}/node-runtime:${FRAMEWORK_TAG:?set in versions.env}` (reuses `FRAMEWORK_TAG`; no new versions.env key), `depends_on: facilitator: condition: service_healthy`, volumes: `workspace_state:/var/lib/ofac`, `../../features/shared/ofac-supervisor.sh:/opt/ofac/ofac-supervisor.sh:ro`, `./container/units:/opt/ofac/units:ro`, `./config/workspace:/opt/ofac/config:ro`; `entrypoint: ["/bin/sh","/opt/ofac/ofac-supervisor.sh"]` (supervisor's `UNITS_DIR` is hardcoded `/usr/local/share/openfacilitator/units` — override with `environment: UNITS_DIR` only after making the supervisor read `${UNITS_DIR:-/usr/local/share/openfacilitator/units}` and `${LOG_DIR...}`; that one-line edit to `features/shared/ofac-supervisor.sh` is part of this step, Feature tests (`features/test/*/`) must stay green), `OFAC_LOG_DIR=/var/lib/ofac/logs`.
- env: `OFAC_ENROLL_SECRET: ${DEMO_ENROLLMENT_TOKEN:?set in .env}` (child self-enroll), `OFAC_CHILD_SECRET: ${DEMO_CHILD_SECRET:?set in .env}`, `OFAC_KERNEL_URL: http://facilitator:8080`, `OFAC_WORKER_TEAM: team-alpha`, `GATEWAY_CONFIG`/`WORKER_CONFIG` per step 3.
- healthcheck (Debian slim has no curl): `["CMD","node","-e","fetch('http://127.0.0.1:8099/status').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"]`, `interval: 5s`, `retries: 30`, `start_period: 20s`; ports `127.0.0.1:8099:8099` (host-reachable diagnostics; docker-proxy connects inside the container netns so the 127.0.0.1 bind is fine).
- New top-level volume `workspace_state: {}`.
- `reference/.env.example`: add `DEMO_CHILD_SECRET=change-me-child` with `# consumer: Gateway child-wire admission + worker hello token` comment. If the `meta` job key-completeness check also covers `.env.example` (verify `ci.yml:53-71`), extend its list.

### 5. Bootstrap seeds (`reference/bootstrap/`)
- `reference/bootstrap/bootstrap.sh` (bash, `set -euo pipefail`, no args; runs from host with docker + curl + jq):
  1. wait-loop: Keycloak `GET http://localhost:8080/realms/facilitator/.well-known/openid-configuration` and kernel `GET http://localhost:8088/health` until 200 (≤120 s, bounded, named failure).
  2. idempotence guard: `docker compose exec -T postgres psql -U demo -d facilitator -tAc "SELECT 1 FROM facilitator.projects WHERE name = 'slim-demo'"` — if 1, print `already seeded` and exit 0.
  3. mint operator token: client_credentials form POST (`facilitator-cli`/`e2e-cli-secret`).
  4. create Work via REST (capabilityId + projectId ride the body — CLI has no flags for them): `POST http://localhost:8088/api/works` `{"objective":"slim demo: analyze the reference environment","capabilityId":"research.analyze","projectId":"<pid>"}` Bearer operator token → capture `.workId`.
  5. create Project via SQL (no REST endpoint exists): `INSERT INTO facilitator.projects (project_id, team_id, name, config, created_at) VALUES (gen_random_uuid(), (SELECT team_id FROM facilitator.teams WHERE slug='team-alpha'), 'slim-demo', '{}', now()) RETURNING project_id;` — runs **before** step 4 (reorder 4/5).
  6. dispatch via outbox SQL (the only Production-mode assignment path, locked decision 2): build the `work.assign` envelope JSON byte-for-byte per `assignEnvelope` (harness.ts:487-512) with fresh UUIDs (`commandId`, `workId` = step 4, `assignmentId`, `actorEvidenceRef: "identity-evidence:slim-demo"`, `capabilityId: "research.analyze"`, `inputs.objective` matching step 4, `expectedDeliverables: [{"kind":"analysis.report","mediaType":"text/markdown"}]`, deadline 2036), then `INSERT INTO facilitator.outbox (outbox_id, recipient_endpoint_id, aggregate_type, aggregate_id, event_type, command_id, envelope, work_id, status, available_at, attempts, created_at) VALUES (gen_random_uuid(), 'link.gw.team-alpha', 'work', '<workId>', 'org.facilitator.command.work.assign.v1', '<commandId>', '<envelope>'::jsonb, '<workId>', 'pending', now(), 0, now()); SELECT pg_notify('facilitator_outbox','');`
- `reference/bootstrap/generate-secrets.sh`: when `reference/.env` absent, copy `.env.example` and replace `change-me-*` values with `openssl rand -hex 16` (or `-` fallback to `/dev/urandom` + `od`); leave `DEMO_ENROLLMENT_TOKEN`/`DEMO_CHILD_SECRET` generated too. Referenced by `referencectl up` only when `.env` is missing.
- Replace the "planned, not built" stub README with a factual two-sentence description (no plan-number prose — docs-check).

### 6. `referencectl` (repo root, mode 755)
Common compose invocation (mirror the Makefile): `cd reference && docker compose --env-file versions.env --env-file .env -f compose.slim.yaml [ -f compose.full.yaml flag ] -p facilitator-demo`.
- `up slim`: `generate-secrets.sh` if no `.env` → `up -d --build` → `--wait --timeout 240` (all services healthy; name failing services on timeout) → `bootstrap.sh` → print the port map (`http://localhost:8088` kernel, `http://id.localhost:8080` Keycloak login `alice/alice`, `:8333` S3, `:18888` Aspire, `:8099` gateway diagnostics) + next-command hints (`./referencectl status`, Plan 19's `verify`).
- `up slim --no-seed` flag: skip bootstrap (Plan 19's verify seeds its own data).
- `down [-v|--volumes]`: `down` / `down -v`; `FULL=1` env adds the overlay (Makefile parity).
- `status`: compose `ps` table; `curl -fs http://localhost:8088/health`; `curl -fs http://127.0.0.1:8099/status | jq '{gatewayId, state, session, children, awaitingApproval}'`; exit non-zero if either probe fails.
- `token [--user U --password P]`: default prints a `facilitator-cli` client_credentials access token; `--user` mints a password-grant token via `client_id=facilitator-api`.
- `work`: end-user demo loop — mint token, `POST /api/works` (objective from `--objective`, default string; projectless is fine for the human path since dispatch is only seeded by bootstrap), then print `timeline` via `GET /api/works/{id}/timeline`.
- Unknown verb / missing jq: usage line + exit 2. Every network probe bounded (curl `-fs --max-time`); no unbounded waits.
- `Makefile`: add `compose-slim-seed`-equivalent? No: replace the `compose-slim` hint line to point at `./referencectl up slim`; keep the raw compose targets as the compose-level entry (both tested paths, one bootstrap owner).

### 7. CI extension (`.github/workflows/ci.yml`)
Extend `ref-env-bringup` (PR tier): replace the manual `cp`+`up -d --build`+wait block with `./referencectl up slim` (single source of truth); existing assertions (health, realm, 401, bearer dead-letters, dashboard spans, bucket) stay; add:
- gateway online: `curl -fs http://127.0.0.1:8099/status | jq -e '.state=="ready"'` and `.children[] | select(.state=="connected")` (the worker registered).
- dispatch completed across containers: poll (≤60 s) `docker compose exec -T postgres psql -U demo -d facilitator -tAc "SELECT 1 FROM facilitator.inbox WHERE payload->>'type'='org.facilitator.event.assignment.completed.v1'"` → proves enroll→assign→worker→artifact→receipt hop end-to-end at PR tier (same assertion the example-smoke tier uses, over the compose stack).
`docker-build` job already builds `node-runtime:ci`; `meta` untouched unless a `.env.example` key check exists.

### 8. Docs + skills (factual, no plan-number prose)
- `reference/README.md`: extend the port map with `:8099` gateway diagnostics + `demo-workspace` service description + the `referencectl` loop; `reference/AGENTS.md`: drop the bootstrap-not-built anchor, add `container/units/`, `config/workspace/`, `bootstrap/bootstrap.sh` truth anchors (≤40 lines, signpost rules).
- `docs/getting-started.md`: replace the manual docker-run walkthrough's kernel section with the `./referencectl up slim` loop (up/down/status only; §28 full rewrite stays a later-plan job — phrase without plan numbers).
- `.omp/skills/facilitator-reference-env` + `facilitator-workspace-packaging`: as-built slim reality (demo-workspace topology, one-container child law, bootstrap SQL-seed placeholder until the routing slice lands, unit self-enroll).

## Critical files & anchors

| File | Anchor | Why |
|---|---|---|
| `src/adapters/harnesses/deterministic/test/e2e/worker.e2e.test.ts` | `:164-179, 187-192, 248-260` | proven child enroll, project SQL, `assignEnvelope` seed — copy shapes verbatim |
| `src/adapters/harnesses/deterministic/src/config.ts` + `src/bin/worker.ts` | `:27-28, 190-192` / `:37-44` | the `kernelUrl` optionality to delete |
| `src/gateway/src/config.ts` | TOP_LEVEL_KEYS + `parseChildTransport` | closed schema for `config/workspace/gateway.yaml` (unknown key = boot failure) |
| `features/src/harness-adapter/install.sh` | `:47-73` | unit body template to port to `reference/container/units/30-harness-adapter.sh` + self-enroll insertion point |
| `src/facilitator/Kernel/Shared/Persistence/Configurations/OutboxEntityConfiguration.cs` | whole file | exact outbox column set for the bootstrap INSERT |

## Verification

New-behavior checks (beyond `make build test lint docs-check` + `pnpm -r typecheck`):
1. **Kernel-level proof of Step 1:** `pnpm -C src/adapters/harnesses/deterministic test` — config without `kernelUrl` fails with exit 2 naming the key; existing harness/e2e tiers green (`example-smoke` CI now passes honestly — its 344-345 psql completion assertion is the new-behavior check for Step 2).
2. **Local bring-up (WSL arm64, the acceptance run):** after `make compose-down FULL=1` + removing `reference/.env`: `./referencectl up slim` → all 6 services healthy incl. `demo-workspace`; `curl 127.0.0.1:8099/status | jq .children` shows `{"childWorkspaceId":"gw.team-alpha/deterministic","state":"connected"}`; `GET http://localhost:8088/api/link/v1/capabilities` with a gateway stateDir token returns the `research.analyze` manifest (or assert via the demo-workspace log's capabilities-POST line).
3. **Dispatch end-to-end:** within ~60 s of up, `docker compose exec postgres psql -U demo -d facilitator -tAc "SELECT payload->>'type' FROM facilitator.inbox ORDER BY received_at DESC LIMIT 3"` shows the two `work.progress` rows + `event.assignment.completed.v1` for the seeded assignmentId; the artifact blob exists in bucket `facilitator-artifacts` (aws-cli `s3 ls`), and `GET /api/works/<workId>/manifests` (alice token) returns 1 manifest with the kernel-verified digest.
4. **Round-trip idempotence:** `./referencectl down -v && ./referencectl up slim` twice → same observable state, no stale link sessions, no double seeds (guard prints `already seeded` when run against a seeded stack without `down -v`).
5. **`./referencectl status`** exits 0 with the stack up, non-zero after `docker compose stop facilitator`; `./referencectl work` creates a Work and prints a timeline; `./referencectl token` prints a JWT whose `sub` is the `facilitator-cli` service account.
6. **Negative seed proof:** bootstrap against a stack whose `demo-workspace` is stopped leaves the outbox row pending (dispatcher backs off, no dead-letter within 2 min) — proves dispatch targets the live gateway, not the seed script itself.
7. **CI:** `ref-env-bringup` green on a PR touching compose (incl. new assertions); `meta` green; full local gate `make build && make test && make lint && make docs-check`.

## Assumptions & contingencies

- No production routing slice exists; the outbox SQL seed is an honest placeholder until it lands (roadmap routing work owns replacement). If an implementer finds a production assign path added since this pass, prefer it and record the divergence.
- If the supervisor `UNITS_DIR` env override breaks a `features/test` scenario, instead copy `ofac-supervisor.sh` bytes into `reference/container/` (the "last Feature rewrites same bytes" law already blesses duplication) and mount the copy — record either way.
- If the capabilities-GET assertion needs a gateway bearer unavailable to the host, assert via the `demo-workspace` container log line (gateway logs the union POST) rather than adding a diagnostics route — no gateway code change in this plan.
- Link-token lifetime is 720 h; the demo child's self-enrolled token expiring mid-day is out of scope (no rotation in the unit script; a restart re-enrolls).
- If `example-smoke` still fails after the unit fix, the stale claim is elsewhere in that job's dev-kernel flow: re-verify against the dev config roster (`appsettings.Development.json` `Link:Teams`), don't delete the assertion.
- Two failed iterations on any step ⇒ re-simplify that piece (plan-level rule), preferring fewer files/env vars over new abstractions.

## Implementation Status

| Step | Status | Notes |
|---|---|---|
| 1 kernelUrl required cutover | Fully implemented | config/bin/types/adapter + callers cut over; unit tiers green |
| 2 Unit self-enroll + example fix | Fully implemented | unit self-enrolls via OFAC_ENROLL_SECRET; example devcontainer fixed |
| 3 Container payload (units + configs) | Fully implemented | reference/container/units + config/workspace + /var/lib/ofac volume |
| 4 Compose `demo-workspace` + env pins | Fully implemented | deviation: 8099 NOT published (loopback-only bind inside the container is unreachable via docker-proxy); probes run via `compose exec` |
| 5 Bootstrap seeds + generate-secrets | Fully implemented | retry loop now captures the kernel's answer (`http=NNN + body`) on failure |
| 6 referencectl CLI | Fully implemented | up/down/status/token/work; status probes gateway in-container |
| 7 CI bring-up extension | Fully implemented | ref-env-bringup asserts ready+child in-container, completed-inbox via psql |
| 8 Docs + skills refresh | Fully implemented | README port row, reference-env skill, plan status |

Verification deviations (observed, plan wrong — behavior is correct):
- V6 negative seed: with `demo-workspace` stopped the assign row is
  `dead_letter` after 5 attempts within 2 min (dispatcher's existing
  retry policy), not `pending`-forever. The proof stands: dispatch
  targets the live gateway; a stopped one burns attempts and dead-letters.
- Cold-start seed 400 `body is not valid JSON`: psql `-tA` prints the
  `INSERT 0 1` command tag after the RETURNING id; the captured
  PROJECT_ID carried a newline into the JSON body (warm re-runs took the
  SELECT path and masked it). Bootstrap now takes `head -n1`.

Plan-external fix (required by verification 3): the adapter-sdk artifact client
never sent `command.artifact.confirm_upload` after a presigned PUT, so with the
S3 store the bytes landed but rows stayed `pending` (`manifest-not-found`).
Client now settles the direct-to-store leg; stub kernel gained a `presign` mode
exercising it.

Status vocabulary: Not started → In progress → Partially implemented → Fully implemented, or Blocked (with reason). Update this table whenever a step lands; record deviations from the locked decisions with a one-line reason.
