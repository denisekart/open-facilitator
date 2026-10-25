# Plan 10 — Workload identity: enrollment binding, client-credentials option, actorEvidenceRef → effective principal

**Status:** ⬜ not-started (🔵 replanning → 🟡 in-progress → 🟢 done via the Implementation Status table below)
**Phase:** C. **Depends on:** 09 (realm, claim mapper, audit spine — all consumed, not rebuilt).
**Branching:** work and commit **directly on `main`** (standing directive). Do not push.
**Size judgment:** kept as ONE plan — enrollment team-binding, workload OIDC option, and evidence validation all land on the same `ILinkTokenValidator`→principal seam; splitting would ship a half-enriched `LinkPrincipal` with two intermediate wire shapes. Split candidate if execution drags: drop the optional OIDC-client-credentials enrollment mode (keep enrollment-token + evidence) — decidable at Step 3 only.
**Questions may be asked during planning, but not during implementation.**

## Objective

Complete the trust plane at the Link edge (spec §11.3, appendix §3.2): enrollments bind to real teams (kill the seeded `default` team), an enrolled workload cannot claim a role it wasn't enrolled as (Gateway vs child), and `actorEvidenceRef` stops being opaque pass-through bytes: the kernel validates evidence per the appendix §3.2 algorithm and derives an **effective principal** (workload identity + optional verified human delegation) that admission audits and that Plans 11/12 authorize against.

## Expected state after

- `POST /api/link/v1/enroll` requires the enroll body's claimed role/endpoint to match the credential: two enrollment modes — (a) enrollment secret + endpoint pre-registration per team, (b) OIDC client-credentials token whose `azp`/`sub` maps to a registered endpoint (audience `facilitator-link`). A child's token cannot enroll/manifest as a Gateway (§11.3).
- Team slugs are explicit (`teams` table or config seed of `team-alpha`/`team-beta` from the realm); the `default`-team on-demand seeding is deleted (clean cutover); manifest `ownerTeam` mismatch keeps 403 `team-mismatch`, now against a real roster.
- `actorEvidenceRef` on commands is resolved+validated when it points at kernel-checkable evidence (evidence kinds registry; unresolvable ⇒ recorded, non-authoritative, never grants privilege — appendix §3.3); validated delegation can extend the workload principal only when the enrollment is marked `acceptsDelegatedClaims`.
- Every admission decision writes `authorization.evaluated` with workload + (optional) human principal; §26.1 item 5's evidence half passes.

## Known facts (verified 2026-10-25; re-verify, code wins)

1. Link auth as-built: `ILinkTokenValidator.ValidateAsync(token) → LinkPrincipal(EndpointId, TeamId, TrustMode)` (`Shared/Auth/ILinkTokenValidator.cs:7,15-18`, doc'd as the OIDC clean-cutover point); `StoreLinkTokenValidator` (`LinkEnrollmentStore.cs:152-167`, SHA-256 base64url lookup, `RevokedAt`/`ExpiresAt` gates); `LinkBearerHandler` mints claims `ClaimTypes.Name`, `facilitator.team`, `facilitator.trust_mode` (`LinkBearerHandler.cs:50-55`, constants `LinkHub.cs:39-41`); `?access_token=` only on WS upgrade.
2. Enrollment as-built: `LinkEndpoints.Enroll.cs` (secret `Link:EnrollmentToken`, dev literal `enroll-dev`, rate-limit `enroll` policy, upsert-by-endpointId rotation, `Link:TokenLifetimeHours` default 720); `EnrollRequestDto(Token, EndpointId, InstanceId, TrustMode)` (`LinkDtos.cs:22`); trustMode whitelist server-side + DB check constraint. **No role/kind field**: Gateway-vs-child is not expressed at all today. Team binds to slug `default` seeded on demand (`LinkEnrollmentStore.cs:66-71`).
3. `actorEvidenceRef`: schema `$defs/evidenceRef` pattern `^[a-z0-9-]+:.{1,256}$` (`common.v1.json`); REQUIRED in every command data schema; kernel treats it as opaque bytes (`CloudEventIngress`/`AdmissionGate`/`IngressProcessor` never read it); fixtures exist (`valid/command.*.json`, `invalid/command.assign.no-evidence.json`).
4. Admission path: `LinkIngress.AdmitAsync` (`Kernel/Link/LinkIngress.cs:49-127`) → `AdmissionGate.ScreenAsync` (size→parse→receipt-refusal→envelope validation→`KnownCommands` gate→`work.assign`-from-link ban→dedupe). Both fall-through dispatch sites carry the principal.
5. From Plan 09: `FacilitatorPrincipal`, `identity:` config block, `facilitator.audit` + `AuditWriter`, Keycloak realm `facilitator` with clients — extend the SAME realm (new workload clients/roles), no second realm.
6. Gateway/SDK enroll call-sites: gateway `runtime.ts:518-519` sends trustMode in enroll body; SDK `enroll()` in `src/workspace-link` (token factory already async — refresh-compatible).

## Locked decisions (planning-approved)

- Evidence kinds registry (kernel-side validators): `oidc:` — a signed JWT checked against realm JWKS with issuer/audience/expiry/`delegation:true` per appendix §3.2; `kernel:` — a kernel-issued evidence record id (created by Plan 11's approval flow) looked up in DB. Unknown scheme ⇒ evidence NOT validated, principal = workload only, audit records `evidence=unverified:<scheme>`. Non-authoritative free-form fields are recorded, never grant privilege (§3.3).
- `link_enrollments` gains columns via one migration: `team_slug`-backed FK to a new `teams` table (slugs `team-alpha`, `team-beta` seeded from `reference/keycloak` parity + config), `role` (`gateway` | `child`), `accepts_delegated_claims bool`. Enrollment body carries `role`; a `child` enrollment's token may never POST a Gateway-shaped manifest. Mode (b) OIDC enrollment requires the client's `sub` registered against an endpoint record.
- `LinkPrincipal` widens to `LinkPrincipal(EndpointId, TeamSlug, TrustMode, Role)`; hub claims gain `facilitator.role`. Effective principal = workload + validated evidence subject, computed once in admission, carried through both ingress doors.
- Clean cutover: the `default`-team seeding branch is deleted, not feature-flagged; e2e harnesses enroll against `team-alpha` (realm parity).

## Steps

0. **Research (mandatory start).** Appendix §3.2–3.4, spec §11.3–11.4 re-read; Plan 09's as-built mapper/realm/audit (it lands first — code wins on divergences); Keycloak client-credentials flow + JWKS validation options in .NET (`AddJwtBearer` multi-audience); how `AdmissionGate` audit rows are written today (`AdmissionGate.AuditAsync`, `CloudEventIngress.cs:52-59`).
1. Teams table + migration (`teams`, enrollment columns, drop default-seed path); realm gains workload clients (`ofac-gateway-alpha`, `ofac-worker-alpha`) with roles.
2. Enrollment v2: role field, pre-registration check, mode (b) OIDC-client-credentials arm, rotated-token semantics unchanged; 401/403 typed errors extended in `ProblemErrors`.
3. Evidence validators (`oidc:`, `kernel:`) + effective-principal derivation in `AdmissionGate`/`CloudEventIngress`; `authorization.evaluated` writes with both planes; metrics.
4. SDK/Gateway: enroll body gains `role`; token factory supports client-credentials mode (fetch+refresh) alongside static enrollment tokens; `linkd` gains the flag; example manifests/configs updated.
5. Conformance fixtures: new invalid classes (child-claiming-gateway, evidence wrong-issuer, evidence expired, unknown-scheme passthrough) + `KNOWN_COMMAND_TYPES` untouched; both-language harnesses run them.
6. Docs + skills + roadmap bookkeeping.

## Testing requirements

- Integration (Keycloak container): child token enrolling as `gateway` role rejected; mode (b) enrollment round-trip incl. rotation semantics; manifest `ownerTeam` against real roster (403 `team-mismatch` preserved); evidence: valid `oidc:` delegation extends principal (audit proves effective principal), wrong-issuer/expired ⇒ deny, `kernel:` unknown id ⇒ non-authoritative.
- Replay: same evidence ref reused past its expiry denies.
- E2E: gateway-e2e + worker-e2e flip from static tokens to client-credentials tokens end-to-end with zero kernel special-casing; §26.1 item 5 evidence-side green.

## Documentation requirements

- `docs/security.md`: workload identity section (modes, rotation, role law), evidence validation algorithm as implemented, effective-principal rules.
- `docs/protocol.md`: enroll request/response v2 fields (additive, minor version 1.1 semantics — record decision in OpenAPI notes).
- `.omp/skills/facilitator-protocol` + `facilitator-architecture` as-built (AGENTS.md maintenance rule, same commit).
- This file's Implementation Status table per step; roadmap Plan 10 row flip.

## Architecture fit

Spec §11.3 + appendix §3.2 steps 1–5: the workload plane and evidence resolution. After this plan the kernel knows WHO (human+workload+effective); Plans 11/12 decide WHAT they may do.

## Additional resources

Spec §11.3–11.4, appendix §3.2–3.4; OIDC client-credentials + JWKS docs; Keycloak service-account docs; existing `LinkEnrollmentStore` rotation code.

## Implementation Status

|Step|State|
|---|---|
|0 Research|Not started|
|1 Teams + enrollment migration|Not started|
|2 Enrollment v2|Not started|
|3 Evidence validators + effective principal|Not started|
|4 SDK/Gateway role + token factory|Not started|
|5 Fixtures|Not started|
|6 Docs + skills|Not started|

States: Not started / In progress / Partially implemented: <what>/ Fully implemented: <evidence>/ Blocked: <reason>.

## Assumptions & contingencies

- Replanning or optimization may be required before implementation: if pre-registration of endpoints (mode a) is judged redundant once mode (b) exists, keeping only enrollment-secret mode + role field is a valid simplification — the invariant that must survive is "an enrolled child cannot claim to be a Gateway."
- If JWKS fetch latency shows in admission hot path, bounded-cache the issuer keys (appendix §11.2's bounded-caching rule for directory adapters) — record, don't pre-build.
- `linkd`/Gateway config surface for client-credentials may collide with the Feature options 1:1 mirror rule (`features/AGENTS.md`); if new env vars are needed, the Feature options update rides this same change.

## Verification

1. `dotnet build` zero warnings; identity+link suites green.
2. New-behavior proof: enroll with a worker client-credentials token claiming `role=gateway` ⇒ 403 typed error, audit row; assign a command with an expired `oidc:` evidence ref ⇒ admission deny, `authorization.evaluated decision=deny reason=evidence-expired` visible via probe.
3. `make link-e2e && make gateway-e2e && make worker-e2e` green; `pnpm protocol` + fixture gates green.
4. Full gate: `make lint && make build && make test && make docs-check`; CI green = 🟢.
