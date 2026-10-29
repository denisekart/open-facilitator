# Plan 26 — Kafka input adapter + Ollama decision provider implementation

**Status:** ⬜ not-started (flips 🔵 at the replanning gate, then 🟡)
**Depends on:** 25 as-built (decision-provider seam + routing call sites). 22/23 sufficient for the ingest-plane reuse. **Phase F leg 5 of 9.**
**On approval:** this file is the working detail design; keep the Implementation Status table current.
**Granularity:** two deliverables sharing one law (advisory/optional periphery around the Postgres authority); split into 26a/26b if the Kafka consumer governance or the Ollama eval harness inflates independently.
**Questions may be asked during planning, but not during implementation** — decisions are locked below; when reality diverges, infer from code/spec per the contingencies section and prefer the simpler robust option.

## Context
Spec §7.5 (Kafka "introduce when measurements show need" — its presence must stay a seam demo, not a dependency) + §10.2 (decision provider advisory) + §21.2 steps 8–9 (event-stream intake, model-assisted disambiguation). As-built: Kafka boots in the full overlay (`compose.full.yaml:37`, `KAFKA_TAG=4.2.2`) with zero consumers; `IDecisionProvider` + deterministic impl land in 25; Ollama boots (`:56`, `OLLAMA_TAG=0.40.9`) with zero callers. This plan adds the third provider on 22's ingest plane (Kafka → normalized events, at-least-once law explicit) and the model-backed `IDecisionProvider` implementation with the advisory boundary tested.

## Known facts (as-verified 2026-10-29; code wins, divergence goes in the status table)
- **Ingest plane (22 as-built):** `POST /api/integrations/{provider}/events`, provider-events+inbox same-tx, dedupe on `(provider, provider_delivery_id)`, role binding `provider.ingest`.
- **Inbox already models at-least-once:** CloudEvents source+id dedupe (`InboxEntityConfiguration.cs:33`) + provider pair (`:35-37`); duplicate arm exists (`CloudEventIngress.cs:79-85`). §7.5's law (offsets ≠ business completion) maps to: Kafka offsets commit on ingest-POST 2xx, business completion tracked solely by kernel state machines.
- **Decision seam (25 as-built):** `IDecisionProvider` advisory over the kernel-computed allowed set; `routing-defaults.provider` selects implementation; out-of-set rejection + throw-fallback law tested there.
- **`research-worker` example:** `examples/full/research-worker` is a README stub; the deterministic harness (`src/adapters/harnesses/deterministic/`) is the non-code-path worker; 25's routing makes capability-targeted Works land on it without seeding.
- **Kafka image:** bitnami/apache KRaft single-node per 17's overlay, SASL/plain per overlay config (verify auth mode at step 0).

## Locked decisions
1. **Kafka adapter (`src/adapters/kafka`, TS, compose sidecar):** consumer group owned by the adapter; each consumed record → one normalized ingest POST on 22's plane with `provider_delivery_id` = `(topic, partition, offset)`; commit only on kernel 2xx-accepted/duplicate answer (§7.5: at-least-once, dedupe is the kernel's inbox, the adapter may re-deliver, never loses by committing early). Additive schemas: `provider.kafka.record.v1` (envelope with topic/partition/offset/key/value) + fixtures. No new kernel arm beyond the generic provider-event arm — records become `work.create`-style intents via a thin mapping rule in config (topic → work intent), so no per-topic kernel code.
2. **Offsets are delivery metadata:** adapter restart resumes from committed offsets; a kernel-side duplicate of the same `(topic,partition,offset)` settles `duplicate` — the §26.1 item 14 test asserts exactly one business command from a doubled delivery.
3. **Ollama decision provider = kernel-side optional service:** new kernel implementation of 25's seam (`model` selector): bounded HTTP POST to Ollama `/api/generate` (timeout ≤ 2 s, small permissive model pinned in `versions.env` already), prompt = allowed candidates + work objective, response parse to candidate id, **strict allow-list check** (any unparseable/unauthorized answer → deterministic fallback + audit flag `model_advisory_fallback`). No streaming, no retries beyond one, circuit-open after N consecutive failures (retry-policy config semantics where natural).
4. **Latency/limits bound:** decision call sits off the hot transaction (routing effect already async); model call timeout budget named in config `decisions:ollama{endpoint,model,timeoutMs,maxFailuresBeforeOpen}` (fail-closed to deterministic when section absent ⇒ slim unaffected).
5. **`examples/full/research-worker`:** thin wrapper (or capability-manifest alias + config) of the deterministic harness on the non-code path, wired in the full overlay with capability `org.facilitator.research.v1`; the Kafka demo routes topic-record intents to it via 25's routing.
6. **Decision audit (§10.2):** allowed-candidate set + chosen candidate + model reference + fallback flag recorded per consult; visible in admin timeline/audit (20's read models surface audit already).
7. **E2E law:** live legs (Kafka produce → Work → research completion; ambiguous routing consults Ollama in a real container) join `make provider-e2e` (local gate). CI keeps contract/unit tiers with an Ollama fake + an in-process Kafka-protocol fake at the adapter boundary.

## Steps
0. **Research (mandatory, no code).** Kafka KRaft auth mode in the as-built overlay + consumer-group offset semantics (Kafka 4.2 docs); Ollama 0.40 `/api/generate` API + small-model options already pulled/pullable offline; 25's as-built seam + audit row shape; 22's ingest contract. Re-verify anchors.
1. **Kafka adapter package + compose service:** consumer loop, offset law, vitest contract tests (recorded record fixtures, doubled-delivery dedupe handled by kernel leg), `Dockerfile.node` filter += `@ofac/kafka-adapter`.
2. **Protocol:** `provider.kafka.record.v1` schema + fixtures, registry regen, both harnesses green; topic→intent mapping config in the adapter.
3. **Ollama decision provider:** kernel implementation + config section + allow-list/fallback/audit tests (fake HTTP handler); circuit-open behavior; slim stack proves zero behavior change when the section is absent.
4. **Research-worker example + full wiring:** overlay services (adapter consumes demo topic), seed script produces a Kafka record, routing routes to research capability.
5. **Live proof (local gate):** produce one record → exactly one Work + completion via research worker; kill+restart adapter mid-flight → doubled delivery yields one business command (§26.1 item 14); two-candidate ambiguity with `routing-defaults.provider="model"` + live Ollama → decision audit shows allowed set + chosen + model ref; Ollama stopped → routing still succeeds deterministic-first. Transcripts to status table.
6. **Docs + skills:** advanced.md model-advisory boundary + Kafka seam rationale (§7.5 quote as-built), versions.env note, skills.

## State after
§21.2 steps 8–9 run in full Compose: event-stream intake through a provably optional broker, disambiguation by a real model that can only advise. §26.1 item 14 basis lands.

## Testing
Adapter vitest contract suite; kernel xUnit provider suite (fake HTTP); live local-gate legs incl. the doubled-delivery race and model-down fallback; slim verify regression (must be literally unchanged — slim has no Kafka/Ollama).

## Documentation
advanced.md sections, adapter README, skills, `.github/workflows/README.md` if CI fakes add jobs.

## Architecture fit
§7.5/§10.2/§13 — broker and model proven to be optional adapters around the Postgres authority; third consumer of the ingest plane.

## Additional resources
Spec §7.5/§10/§13.6/§21.2; Kafka KRaft docs; Ollama API docs.

## Critical files & anchors
- 22's ingest endpoint + `InboxEntityConfiguration.cs:33-37` — the dedupe laws this plan leans on.
- 25's `IDecisionProvider` + audit row — the seam Ollama implements.
- `reference/compose.full.yaml:37,56` + `versions.env:3,8` — the idle services this plan wakes.

## Verification
build/lint/test/docs-check; new suites green; provider-e2e full legs green on clean full stack; model-down fallback proven by container stop.

## Assumptions & contingencies
- If no small model is pullable offline in the dev env, the live leg pulls at first run (documented, `ollama_models` volume persists); CI never touches Ollama.
- If per-record topic mapping config proves too rigid for the demo, one adapter-side transform script per topic (sandboxed, declarative) — no kernel plugin system.
- If Ollama latency variance makes the 2 s budget flaky in live proof, raise the timeout config (law = bounded + fallback, not a specific number).
- **Replanning or optimization may be required before implementation begins.**

## Implementation Status
| Step | Status | Notes |
|---|---|---|
| 0 Research | Not started | |
| 1 Kafka adapter | Not started | |
| 2 Protocol | Not started | |
| 3 Ollama decision provider | Not started | |
| 4 Research-worker example | Not started | |
| 5 Live proof | Not started | |
| 6 Docs + skills | Not started | |

Status vocabulary: Not started → In progress → Partially implemented → Fully implemented, or Blocked (with reason). Record deviations from locked decisions with a one-line reason.
