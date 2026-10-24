# Plan 07 — HarnessAdapter contract + deterministic worker harness

**Status:** ⬜ not-started — implementation begins ONLY after its replanning gate flips this to 🔵.
**Phase:** B. **Depends on:** 06 (Gateway multiplexing + child transport 🟢).
**Part of:** roadmap entry 06 (split): this plan is the harness-adapter half; the Feature/packaging half is Plan 08.

## Normalization from prior plans (assumed state)

Plan 06 gives children a real transport to the Gateway with a serializable message vocabulary (register/steer/status/stop + event publish). This plan turns that internal vocabulary into the **public HarnessAdapter lifecycle contract** (§8.3): an adapter is a process that speaks the child protocol and wraps an agent harness (or a deterministic script) without owning its behavior. Assumes Plan 04's `linkd` echo/stub handlers exist as the "no harness" placeholder — this plan replaces them for the demo path but keeps `linkd` untouched. Assumes Gateway POSTs union manifests (06) — the deterministic worker's manifest flows through that path, no direct-to-Facilitator enrollment.

## Objective

Define the HarnessAdapter contract as machine-readable artifacts + a reference implementation: a deterministic demo harness (scripted research worker, §21.1) that accepts a `work.assign`, produces a Markdown analysis artifact file, completes with a result manifest, and survives steer. Ship `@ofac/adapter` (small TS package: child-protocol client + lifecycle scaffold so third-party adapters import one library) and `src/adapters/harnesses/deterministic/`. No Dev Container Features yet — the worker runs as a plain process pointed at a running Gateway (Plan 08 packages it).

## Replanning gate (mandatory initial research)

1. Spec §8.1–8.3 (harness authority law, adapter lifecycle: `start/steer/status/stop` + native-session persistence hook), §15.2–15.3 (result manifests, what `assignment.completed` must carry), §21.1 (slim acceptance script the demo must pre-figure), §23.1 review-comment steering semantics (steer must land on a running execution).
2. Plan 06 detail design: the exact child message vocabulary, frame schema location, auth — the adapter contract MUST be a documented profile of it (message subset + versioned `apiVersion`), not a parallel protocol.
3. Plan 04 SDK: what an adapter can reuse for outbound envelopes (`buildEvent`, `dataschemaFor`) — adapters publish via the Gateway relay, so determine the event-relay message shape 06 froze.
4. Artifact reality check: artifact store is renumbered-Plan 11 (not built). The deterministic worker writes a local file + references it in the manifest as `storageUrl: file://` — replanning decides whether that's a conformant interim (appendix §8 external-reference trust rules) or whether a minimal kernel artifact endpoint should be pulled forward; pick the boring conformant option, no fake store.
5. External: prior art for thin wrapper adapters (no new dependency unless gate proves one needed; `@ofac/adapter` stays zero-dep besides workspace packages).

## Scope

- Contract artifacts: `protocol/json-schema/adapter.v1.json` (child-protocol frames: `register`, `command.deliver`, `command.accept|reject|unsupported`, `event.publish`, `steer`, `cancel`, `status.request|response`, `heartbeat`) — child↔Gateway internal, versioned under the protocol namespace only if 06 already put it there; otherwise a documented internal schema set.
- `@ofac/adapter` package (`src/adapters/harnesses/adapter-sdk/` or per 06's layout decision): `createAdapter({ manifest, onExecute, onSteer, onCancel, statusProvider })`, connects to Gateway socket, handles the lifecycle, exposes an in-memory execution registry with native-session-persistence hook (opaque `sessionRef` string the harness may set; carried in status).
- Deterministic harness (`src/adapters/harnesses/deterministic/`): scripted steps (fixed sleep + template Markdown from the assignment inputs), writes `report.md`, completes with `resultManifest.outputs[0]` (digest computed, `file://` ref), progress event mid-run, honors `steer` (appends directive to the report), `cancel` (kills script, execution `cancelled`).
- `examples/slim/deterministic-research-worker/`: config + manifest + README wiring Gateway+worker against a dev Facilitator (no containerization yet — plain processes, documented commands).
- Lifecycle contract test harness: a reusable test kit (spawn Gateway + fake Gateway-side, drive adapter through full lifecycle incl. crash-mid-execution → `lost` reconcile) — Plan 08's Feature tests and roadmap-era 16's provisioning reuse this kit.

## State after implementation

`make`-documented local loop: Facilitator + Gateway + deterministic worker; a `work.assign` seeded via dev affordance completes end-to-end with a verifiable Markdown artifact (digest checks, file exists) and a result manifest on the kernel timeline; steering a running execution changes its output. The adapter contract is implementable by a third party from the schema + README alone (the deterministic worker is the worked example). Slice-3 completion; pre-figures slice-6 acceptance.

## Testing

- vitest: contract kit green against the reference adapter (full lifecycle, steer-mid-run, cancel, crash→`lost`→reconcile, manifest/digest correctness).
- e2e tier (extend the Gateway CI tier): real deterministic worker process; concrete proof: seeded assign → timeline shows progress + `assignment.completed` with `resultManifest` passing the Plan 01 `result-manifest.v1.json` schema; steer observable in the artifact content.
- Shared fixtures: adapter frames get valid/invalid fixtures if they join `protocol/` (gate decision); both harnesses green.

## Documentation

- `docs/advanced.md`: HarnessAdapter contract chapter (lifecycle diagram, message table, authority law: harness behavior stays inside the harness).
- `src/adapters/harnesses/deterministic/README.md` + package README for `@ofac/adapter` (write-your-own-adapter walkthrough).
- `.omp/skills/facilitator-workspace-packaging`: HarnessAdapter contract → implemented reality (currently spec-proposal wording).

## Architecture fit

§8 — the adapter is thin, the harness is authoritative, the Gateway mediates; §15 — typed outputs as completion truth. This is the second half of "execution authority stays team-side": the kernel routes to `gw.<slug>`, the Gateway routes to a child, the child's adapter translates to whatever the harness actually is. Plan 08 ships this exact process pair in containers; roadmap-era 16 (zero-to-N) launches/stops exactly these.

## Additional resources

- Spec §8, §15.2–15.3, §21.1, §23.1; appendix §8.2–8.3 (external artifact refs, manifest example), §12.3 (test-mode expectations for lifecycle tests).
- As-built: Plan 05/06 Gateway code, Plan 04 SDK (`buildEvent` etc.), `linkd` handlers as minimal-harness contrast.
- External: Dev Container Features spec (read now, consumed by Plan 08 — this plan's adapter boundary must not preclude Feature packaging).
