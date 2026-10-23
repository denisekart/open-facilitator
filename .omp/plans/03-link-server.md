# Plan 03 — Link server: SignalR hub and HTTP fallback

**Status:** ⬜ not-started
**Depends on:** 02

Status legend: ⬜ `not-started` → 🔵 `replanning` (mandatory gate) → 🟡 `in-progress` → 🟢 `done`. This plan starts with a replanning pass (flip to 🔵): re-verify assumptions against the specs and prior plans' state, run the research below, finalize this file's detail design, then flip to 🟡.

## Objective

Expose the Facilitator side of the Workspace Link: SignalR hub (`Hello`, `Envelope`, `Receipt`, `Heartbeat` server methods; `Welcome`, `Deliver`, `FlowControl`, `Disconnect` client methods) plus the provider-neutral HTTP fallback (`POST /api/link/v1/events`, `GET /api/link/v1/deliveries?wait&limit`, `POST /api/link/v1/deliveries/{id}/receipt`, `GET /api/link/v1/capabilities`, `POST /api/link/v1/enroll`), handshake/version negotiation, `link_sessions` lease records, and outbox→delivery wiring with `maxInFlight` backpressure.

## Replanning gate

- Decide auth interim (Plan 07 lands OIDC; interim: static enrollment tokens bound to a team record) vs building 03 after 07 — recommended: 03 first with pluggable `ILinkAuthenticator`; token-validation swap in 07.
- Decide delivery-cursor semantics (appendix: resume cursor is a hint, receipt ledger is truth).

## Scope

- Hub per §5.3 with authenticated principal → endpoint/team binding.
- Session epoch/`leaseUntil` in `link_sessions`.
- Delivery rows generated from outbox for online sessions only (owner-replica routing via LISTEN/NOTIFY hint, §7.3 — single-replica path now, lease fields ready).
- HTTP fallback identical semantics.
- Dead-letter visibility endpoint.

## State after

A scripted fake link client can enroll, hello/welcome, receive a queued `work.assign` delivery, post a receipt, reconnect with a new epoch, and replay only un-acked deliveries. §26.1 item 2 basics + receipt-loss (item 3) pass for the server side.

## Testing

xUnit + SignalR client integration tests: happy handshake, stale resume cursor handled, dropped receipt → redelivery, duplicate redelivery → `duplicate` receipt not re-executed, unknown command → typed `unsupported`. Fixtures consumed from `protocol/conformance-fixtures/`.

## Documentation

`docs/protocol.md` handshake + HTTP fallback sections with exact JSON (appendix §14 wants normative hello/welcome examples — author them here); `.omp/skills/facilitator-protocol` update.

## Architecture fit

§5.3–5.4, §6, §7.3. The transport edge of the kernel.

## Additional resources

Spec §5.3/§5.4/§6, §7.3, appendix §5.6, §10.1 (must-have handshake tests), §16.3 envelope example; SignalR .NET docs (connection affinity, streaming).
