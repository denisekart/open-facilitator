# Plan 11 — Approval lifecycle: ledger, action digest, atomic single use, revocation

**Status:** ⬜ not-started (🔵 replanning → 🟡 in-progress → 🟢 done via the Implementation Status table below)
**Phase:** C. **Depends on:** 09 (human principal, audit spine) + 10 (`kernel:` evidence, effective principal). Plan 12 (Gateway policy `confirm` effect) is the first producer/consumer of this lifecycle.
**Branching:** work and commit **directly on `main`** (standing directive). Do not push.
**Size judgment:** kept as ONE plan — ledger, digest, dispatch, and the human REST pair are one vertical: an approval nobody can answer proves nothing. Split candidate if execution drags: the REST pair + `kernel:` evidence bridging as an 11b, decidable once Steps 1–4 are green.
**Questions may be asked during planning, but not during implementation.**

## Objective

Give the kernel its half of spec §12.1 / appendix §7: the approvals ledger goes from schema-only (`facilitator.approvals`, created in the baseline migration, zero behavior code) to the authority of the approval lifecycle — record an `approval.required` (already a frozen schema: `approvalId`, `actionDigest sha256:`, `eligibleActors`, `nonce`, `expiresAt`, `createdAt`), accept `approval.respond` (already in `KnownCommands`/`KNOWN_COMMAND_TYPES`, currently fall-through-with-no-effect) as a business command, RFC 8785-canonicalize + SHA-256 the action payload, enforce **atomic single use**, expiry, and revocation (`approval.revoked` schema exists, kernel code doesn't), and mint `kernel:` evidence records so Plan 10's evidence validator can vouch for the human approver. Replay of a consumed or stale approval denies even when the command JSON is byte-identical (§12.2).

## Expected state after

- Both fall-through dispatch sites (`CloudEventIngress.IngestEventAsync` ~`:87-117`, `LinkIngress.AdmitAsync` ~`:78-125`) dispatch `approval.respond` to the ledger with conditional-update single use; a second respond gets a typed `approval-consumed` ProblemDetails / receipt, never a second business effect.
- The digest of an approval record recomputes identically in .NET (`Jcs.Net`) and TS (`canonicalize`) against the existing golden fixtures (`protocol/conformance-fixtures/canonical/approval-prehash.*`); a new golden set covers the full approval record shape.
- Minimal human surface: `GET /api/works/{id}/approvals` + `POST /api/works/{id}/approvals/{approvalId}/respond` (human principal from Plan 09; eligible-actor check server-side; the admin SPA later builds UI on exactly this pair).
- `approval.revoked` handling marks the row `revoked`; consumers (this plan's respond path; Plan 12's re-check-before-apply) must observe it.
- Audit: every approve/deny/expiry/revocation writes `authorization.evaluated`-style rows with action digest and evidence ref. §26.1 item 5 replay half + item 8 have their test basis.

## Known facts (verified 2026-10-25; re-verify, code wins)

1. Ledger schema-only as-built: `ApprovalEntity` (`Entities.cs:311-337`): `ApprovalId, WorkId?, ActionDigest, State="pending", Nonce, EligibleActors jsonb, ExpiresAt, CreatedAt, ConsumedAt?`; table config `Configurations/ApprovalEntityConfiguration.cs:12-27`, check `approvals_state_check` over `pending|approved|denied|expired|revoked|consumed`. No repository/service reads or writes it. The schema needs no migration for the lifecycle fields EXCEPT re-verify `revoked` reachability + any `decided_by`/`evidence_ref` columns you need — one migration max.
2. Wire as-frozen: `command.approval.respond.v1.json` (requires `commandId, approvalId, decision ∈ {approved,denied}, actorEvidenceRef`, optional `comment ≤1024`), `event.approval.required.v1.json`, `event.approval.revoked.v1.json`; generated types exist both languages (`WireTypes.cs:279,591`, TS `types.ts:229-235`). No schema change expected; if the record shape needs a field, it's additive-minor and rides the fixture update in both harnesses.
3. Canonicalization as-built: golden `protocol/conformance-fixtures/canonical/approval-prehash.json` + `.sha256` (`sha256:28cb…50b91`) + `approval-record.json` (appendix §7.2 shape); .NET `Jcs.Net` in `protocol/tools/Validate.Fixtures/Program.cs:105-116`; TS `canonicalize@^2.1.0` in `src/protocol`. Kernel-side reuse: extract/replicate the digest helper into the kernel (one internal `ActionDigest.Compute(JsonElement)`), golden-checked, not re-invented.
4. Fall-through dispatch sites to hook (paths above); `ProblemErrors` extension-point pattern; timeline note `command.accepted` precedent; `AppendTimelineNoteAsync` for Work-visible records.
5. `kernel:` evidence scheme registered by Plan 10 — a consumed approval must emit an evidence record id (or the approval row itself IS the evidence lookup target; decide at Step 2 against 10's as-built validator interface, code wins).
6. `workId` on approvals is nullable today; approval-required flows without a Work (provider-originated) stay supported — nullable stays unless code disagrees.

## Locked decisions (planning-approved)

- State machine explicit (same guard-table style as Work/Assignment): `pending → approved | denied | expired | revoked`; `approved → consumed` on the single use. Conditional `UPDATE … WHERE state='pending'` (and `= 'approved'` for consume) inside the business transaction — the DB is the single-use arbiter, no locks beyond that.
- `actionDigest` is COMPUTED kernel-side over the canonical action payload the Gateway declares (the `approval.required` payload's action object per appendix §7.2) and compared to the Gateway-provided digest — mismatch ⇒ reject the event (422, typed). The kernel never trusts a digest it didn't recompute.
- Respond authority: the responder's effective principal (Plan 09 human token, or Plan 10-validated evidence on a Link-surface respond) MUST intersect `eligibleActors` (`user:` = `oidc:<iss>|<sub>` form from 09, `group:` = normalized groups from the mapper). Expiry checked before eligibility. Non-authoritative.
- `expired` materializes lazily (respond/GET after `expiresAt` flips + audits) — no background timer (deny-by-default, no scheduler debt).
- The REST pair is the approval surface of record; nothing UI-side in this plan.

## Steps

0. **Research (mandatory start).** Spec §12.1 + §12.2 + appendix §7 re-read; as-built: entity/config, both fall-through sites, `ProblemErrors`, Plan 09 audit writer + principal form, Plan 10 evidence validator interface, fixture canonical set + both harness runners.
1. Kernel `ActionDigest.Compute` + golden cross-check against `canonical/approval-prehash.*` + new fixtures for the full-record shape (both languages, `make protocol` drift gate green).
2. `ApprovalLedger` service (record-from-event, respond, revoke, lazy-expire, consume) + migration for any missing columns; wire `approval.required` recording + `approval.revoked` into both ingress dispatch sites; audit writes.
3. REST pair in `WorkEndpoints.cs` (or new `ApprovalEndpoints.cs` partial — follow the endpoints-file convention) with eligible-actor + expiry + principal checks; respond mints the `kernel:` evidence record.
4. Single-use + replay tests (below) and receipt/error classes; OpenAPI/`docs/protocol.md` response-shape notes.
5. Conformance fixtures: valid/invalid approval lifecycle classes (respond-after-consume, respond-after-expire, digest-mismatch, non-eligible actor).
6. Docs + skills + roadmap bookkeeping.

## Testing requirements

- Golden digest: kernel-computed digest == fixture sha256; a reordered-keys/unicode-variant payload digests identical (RFC 8785 properties, both languages).
- Race: two concurrent responds — exactly one `approved→consumed`, other gets typed consumed; two concurrent record inserts same `approvalId` — one row.
- Replay-after-consume with byte-identical command JSON ⇒ deny (§12.2's exact claim); respond-after-expiry ⇒ typed expired; revoke-then-respond ⇒ deny `revoked`; non-eligible actor ⇒ 403 + audit.
- Integration through the Link surface: a gateway-shaped `approval.required` recorded, human REST respond, `kernel:` evidence then validates through Plan 10's validator (end-to-end trust loop with the Keycloak container).

## Documentation requirements

- `docs/security.md`: approval lifecycle (states, digest law, single use, revocation-before-apply rule), audit-record schema delta.
- `docs/protocol.md`: approvals endpoints + command/event semantics as implemented.
- `.omp/skills/facilitator-protocol` + `facilitator-architecture` same commit; appendix §7.2 example promoted to a fixture if not already.
- This file's Implementation Status table per step; roadmap Plan 11 row flip.

## Architecture fit

Spec §12.1–12.2 + appendix §7: kernel-side transaction truth of the trust plane. Gateway-side evaluation and the confirm round-trip are Plan 12; privileged provider writes (Phase F) consume both.

## Additional resources

Spec §12.1–12.2, §11.4; appendix §7; RFC 8785; existing canonical fixtures; EF Core conditional-update docs.

## Implementation Status

|Step|State|
|---|---|
|0 Research|Not started|
|1 ActionDigest + goldens|Not started|
|2 ApprovalLedger + ingress wiring|Not started|
|3 REST pair + kernel: evidence|Not started|
|4 Single-use/replay tests + docs notes|Not started|
|5 Fixtures|Not started|
|6 Docs + skills|Not started|

States: Not started / In progress / Partially implemented: <what>/ Fully implemented: <evidence>/ Blocked: <reason>.

## Assumptions & contingencies

- Replanning or optimization may be required before implementation: if Plan 10 landed `kernel:` evidence as a table of its own, Step 3 writes there; if 10 made the approval row the evidence target, Step 3 points at the ledger — follow 10's as-built, don't fork the concept.
- If the frozen `approval.required` schema proves unable to carry the action payload needed for digest recomputation, the fix is an additive field (minor 1.1) with fixtures updated in both harnesses in the same commit — a frozen-schema amendment is the only planning-approved contract change here.
- Lazy expiry means a GET can flip state; keep the write on the read path conditional and audited, and revisit a janitor only if read amplification shows (it won't at this scale).

## Verification

1. `dotnet build` zero warnings; approvals suite green incl. the race tests; `dotnet run --project protocol/tools/Validate.Fixtures` + `pnpm -C src/protocol test` green with new fixtures.
2. New-behavior proof: scripted loop — record via `/api/ingress/events` (`approval.required`), GET pair as eligible user (Keycloak token) ⇒ 200 + pending row; respond approved ⇒ consumed; replay byte-identical respond ⇒ typed denial + audit row `decision=deny reason=already-consumed`.
3. `make link-e2e` green (Link-surface respond path).
4. Full gate: `make lint && make build && make test && make docs-check`; CI green = 🟢.
