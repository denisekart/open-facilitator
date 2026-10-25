# Plan 09 — Identity foundation: Keycloak realm, claim-pointer mapper, kernel-edge human identity

**Status:** ⬜ not-started (🔵 replanning → 🟡 in-progress → 🟢 done via the Implementation Status table below)
**Phase:** C (first). **Depends on:** 03 (Link server, as-built). Plan 10 consumes this plan's realm, mapper, and audit spine; Plan 11 consumes the human principal.
**Branching:** work and commit **directly on `main`** (standing directive from Plans 03–08). Do not push.
**Size judgment:** kept as ONE plan — realm, mapper, ingress cutover, and audit spine are one vertical identity slice; a mapper without the cutover ships unproven, a cutover without the realm has nothing to validate against. Split candidate if execution drags: the audit table (`authorization.evaluated` spine) as a 09b, decidable once the mapper + realm are green.
**Questions may be asked during planning, but not during implementation.**

## Objective

Replace the kernel's shared-token ingress auth (`Auth:BootstrapToken`, literal `dev-token`) with real OIDC against Keycloak 26: a startup-configurable claim-pointer mapper (spec §11.2, RFC 6901 paths, deny-on-missing-claim — never a wildcard), a realm export as the single identity source of truth for tests, dev, and the future reference environment, human identity as the verified pair `(iss, sub)` (§11.1), and an `authorization.evaluated` audit spine (appendix §3.2 step 5) that later plans write into. The Link/workload plane (enrollment, tokens, `actorEvidenceRef`) is NOT touched here — that is the next plan.

## Expected state after

- Kernel boots only with a valid `identity:` block outside Development; every `/api/*` request (except `/api/link/v1*`) carries a verified OIDC bearer; `dev-token` and `BootstrapTokenValidator`/`BootstrapAuthStartupCheck` are gone (clean cutover, no dual path).
- An incorrect claim-pointer path yields **no** roles/scopes, and a write-permission decision made from it denies; issuer/audience/lifetime violations are 401 with typed ProblemDetails.
- `facilitator.audit` records every authorization decision with effective principal, decision, and evidence ref; no long-lived tokens stored.
- Every test tier (xUnit, link-e2e, gateway-e2e, worker-e2e, example-smoke) authenticates with tokens minted by a Keycloak container importing `reference/keycloak/realm.json`. §26.1 item 5 (claim-path/issuer/audience failures) has its test basis.

## Known facts (verified 2026-10-25; re-verify, do not re-decide; if code diverges, the code wins — record the divergence in the Implementation Status table)

1. Auth as-built: `Kernel/Shared/Auth/` = `TokenCheck.cs` (`IIngressTokenValidator.Validate(string?) → {Ok,Unauthorized,Unavailable}`, 503 fail-closed on Unavailable), `BootstrapTokenValidator.cs` (config `Auth:BootstrapToken`, dev literal `dev-token`), `BootstrapTokenMiddleware.cs` (guards `/api/*` except `/api/link/v1*`; 401/503 ProblemDetails), `StartupChecks.cs` (`BootstrapAuthStartupCheck` fail-closed gate), `ILinkTokenValidator.cs` (untouched by this plan). Registrations: `KernelServiceCollection.cs:58-60,126`. Pipeline: `Program.cs:28-34`.
2. Ingress surface with no principal today: only pass/fail token check; `POST /api/ingress/events` (`WorkEndpoints.cs:76-84`) and `/api/dev/*` affordances all ride `dev-token` (`KernelTestFactory.CreateAuthedClient()`, e2e literals `KERNEL_TOKEN="dev-token"` in `src/workspace-link/test/e2e/harness.ts:20`, `LinkTestHarness`, `ci.yml:243` smoke).
3. Config: no `identity:` block exists. Pin `KEYCLOAK_TAG=26.7.5` already in `reference/versions.env`; `reference/keycloak/` is a README placeholder ("lands with Plan 11" — stale pointer to the old numbering; the realm export is THIS plan's deliverable).
4. Persistence: entities + baseline migration pattern in `Kernel/Shared/Persistence/Entities.cs` + `Configurations/` + `Migrations/`; add one migration. Timeline-append precedent: `IngressStore.AppendTimelineNoteAsync`.
5. Tests: Testcontainers.PostgreSql shared-container fixture (`Infrastructure/PostgresFixture.cs`), `KernelTestFactory`, `TestDbProbe`; no Keycloak anywhere yet. Test project is feature-foldered post-hardening-plan — put identity tests in the folder matching the source area.
6. `pnpm-workspace.yaml` catalog is the dep-pin convention; CI `meta` job enforces `versions.env` key completeness — do not add a Keycloak pin there without the key.
7. Metrics/tracing: `KernelMetrics` (meter `facilitator`), `KernelActivity.Source`; audit counters belong there.

## Locked decisions (planning-approved, not re-decidable)

- **Keycloak testcontainer for tests/dev, `reference/keycloak/realm.json` as the single source of truth** — no second auth path, no kernel-issued dev JWTs. Testcontainers `Testcontainers.Keycloak` (or raw `GenericContainer` with `--start-dev --import-export=realm.json` if the module lags 26.7.x — decide in research, prefer the module).
- Realm layout: realm `facilitator`; two teams `team-alpha`/`team-beta`; groups `/teams/team-alpha/…`, realm roles, client `facilitator-api` (audience for user tokens), client `facilitator-cli` (client-credentials, for CLI + e2e harness tokens), demo users incl. one non-member for negative tests; protocol mappers place groups/realm roles in tokens.
- Claim-pointer config schema mirrors spec §11.2 verbatim (YAML→IConfiguration `identity:oidc:{authority,audience,requireHttps}` + `identity:claims:*` pointer lists + `identity:mapping:*` AnyOf lists). Staged startup validation: RFC 6901 syntax + known shapes + non-empty mapping terms; malformed config = fail-closed boot (precedent: existing startup checks).
- Human identity canonical form is `(iss, sub)`; `originatingActor` string form `oidc:<iss>|<sub>` for audit/persistence; `preferred_username` is display-only.
- New `facilitator.audit` table: `audit_id uuid, occurred_at, actor (text, normalized principal or null), action, resource, decision, reason, evidence_ref, detail jsonb, trace_id`. One append helper `AuditWriter`; every authorization decision writes it.

## Steps

0. **Research (mandatory start).** Re-read: spec §11.1–11.2, appendix §3.2–3.3; the Known-facts files; Keycloak 26 import/export + `--start-dev` docs; `Testcontainers.Keycloak` release status; ASP.NET Core JWT bearer + `AuthenticationSchemeOptions` events for 401 ProblemDetails mapping; how each e2e harness spawns services (pattern to copy for Keycloak). Record findings in the first commit message.
1. `identity:` config binding + staged validation + fail-closed startup check (`IdentityStartupCheck`); `appsettings` + `.env.example` wiring.
2. Claim mapper: RFC 6901 extraction (`string-array`, `space-delimited-string`, `string-array-or-space-delimited`), normalization to `FacilitatorPrincipal(Subject, DisplayName, Groups, Scopes, Permissions, Amr, Acr, Roles)`, `identity:mapping:*` AnyOf evaluation → platform roles; missing/malformed ⇒ empty set, never wildcard. Golden unit tests incl. Keycloak's dual group/`realm_access/roles` shapes.
3. Replace ingress auth: `AddJwtBearer` (authority/audience/requireHttps from config, events → 401/503 ProblemDetails matching existing types), authorization policy on the kernel-token surface, delete `BootstrapTokenValidator`/middleware guard logic (keep middleware as the ProblemDetails translator or fold into events — code-wins judgment in research). Ingress/CLI/`/api/dev/*` principals now real; write `authorization.evaluated` audits on permission decisions.
4. `facilitator.audit` table + `AuditWriter` + metrics counter; timeline stays the Work-facing record.
5. `reference/keycloak/realm.json` + `reference/keycloak/README.md` rewrite (no plan-number prose).
6. Test/dev cutover: Keycloak fixture in `Facilitator.Tests` (shared container, realm import, token cache); `KernelTestFactory.CreateAuthedClient()` → real tokens (user + client-credentials variants); e2e harnesses (`workspace-link`, `gateway`, adapters) start Keycloak per-file (mirror the per-file Postgres pattern) and fetch client-credentials tokens; `ci.yml` jobs unchanged except any env var the harness needs; `example-smoke` same. `docs/getting-started.md` curl walkthrough re-tokenized.
7. Docs + skills: `docs/security.md` identity model + claim-pointer guide (operator-facing); `.omp/skills/facilitator-architecture` auth section → as-built; roadmap Plan 09 row flip.

## Testing requirements

- Keycloak container + realm import integration suite: happy path; wrong claim-pointer config ⇒ no roles ⇒ 403 on a permissioned write (never wildcard); issuer and audience violation ⇒ 401; expired token ⇒ 401; unmapped user (valid token, no group/scope match) ⇒ deny with audit row; audit rows contain no long-lived tokens.
- Cross-language unchanged: protocol/fixture gates untouched (no wire change in this plan).
- E2E tiers prove token rotation works in practice: client-credentials token TTL is short (realm sets 300 s); a long-running e2e crossing the TTL still passes (token factory re-fetches).

## Documentation requirements

- `docs/security.md`: identity model (three identities, human plane this plan), claim-pointer guide with the exact config schema + worked Keycloak example, audit-record schema.
- `reference/keycloak/README.md`: what the realm is for and who consumes it.
- `docs/getting-started.md` updated auth steps; `.omp/skills/facilitator-architecture` same commit (AGENTS.md maintenance rule).
- This file's Implementation Status table, same commit as each step; roadmap Plan 09 entry ⬜→🟡 at first commit, 🟢 on CI green.

## Architecture fit

Spec §11.1–11.2 + appendix §3: the human/operator plane of the kernel edge built in 02/03. Deliberate sequencing: this plan owns only the human plane so the mapper and audit spine are stable before the workload plane (10) and the approval consumers (11/12) stack on them.

## Additional resources

Spec §11.1–11.2, appendix §3.2–3.3; Keycloak 26 server docs (realm import/export, protocol mappers, `--start-dev`), `Testcontainers.Keycloak` package docs; RFC 6901; ASP.NET Core RemoteAuthentication/JwtBearer events docs; existing `StartupChecks.cs` for the fail-closed precedent.

## Implementation Status

|Step|State|
|---|---|
|0 Research|Not started|
|1 identity: config + validation|Not started|
|2 Claim mapper + golden tests|Not started|
|3 Ingress cutover + audits|Not started|
|4 facilitator.audit + AuditWriter|Not started|
|5 realm.json + reference docs|Not started|
|6 Test/dev/e2e token cutover|Not started|
|7 Docs + skills + roadmap|Not started|

States: Not started / In progress / Partially implemented: <what>/ Fully implemented: <evidence>/ Blocked: <reason>.

## Assumptions & contingencies

- Replanning may be required before implementation starts: if `Testcontainers.Keycloak` can't run 26.7.5, fall back to `GenericContainer` with the pinned image — the realm file stays the source of truth either way; record in the table.
- If e2e per-file Keycloak startup makes wall-clock budgets blow (< 10 min .NET budget, e2e tiers), move to one shared Keycloak per suite/per make-target (mirrors the Postgres shared-container decision) — decide at Step 6 measurement, do not pre-optimize.
- If the Keycloak container slows CI beyond tolerance, the `meta` job keeps pin checks only and identity integration tests land in the `dotnet` job as today — no separate IdP job unless measurements demand it.
- Stale plan-number prose elsewhere (`Makefile:64`, `README.md:53,60,90`, `docs/*` "Plan 11/12" pointers) is the final hardening plan's scrub, not this plan's — fix only pointers this plan directly invalidates (`reference/keycloak/README.md`).

## Verification

1. `dotnet build` zero warnings; identity unit/integration suite green (`dotnet test --filter Identity`).
2. Negative proof (the new behavior): boot the kernel with `identity:claims:groups:path=/nonexistent` → permissioned write → 403 + `authorization.evaluated` audit row `decision=deny reason=no-role-match` (observable via `TestDbProbe`).
3. E2E: `make link-e2e && make gateway-e2e && make worker-e2e` green with tokens from the container (grep harness logs for fetched token expiry < TTL).
4. Full gate: `make lint && make build && make test && make docs-check`; CI green = 🟢.
