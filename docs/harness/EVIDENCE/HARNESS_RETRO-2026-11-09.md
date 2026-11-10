# Harness Retro — Generic Improvements (evidence-mined)

Provenance: one workspace, one multi-week agent-session window. Sources: a per-window
telemetry snapshot (tool-call/failure/loop/dead-reference/thinking-level counters), a
diff of the full harness layer (root agent doc, rules directory, plan directory, project
skills, CI workflows, gate scripts, container config) against the repository's root
commit, and an inventory of the user-level managed-skill store. Every item below is
stated after stripping repo names, product nouns, commit SHAs, and
workspace-specific counts; a mechanism survives only if its statement survives that
stripping.

## 1. Gate mechanisms

- **Drift gate over the agent docs themselves.** A CI meta job runs a script that walks
  every agent-facing doc, extracts every backticked path token and markdown link target
  (skipping globs, placeholders, URLs, anchors, route literals), resolves each against
  the tree, and fails on any dangling reference. Motivation observed in telemetry: a
  steady stream of read/grep/edit calls against paths that no longer exist — the same
  failure the gate converts from wasted agent turns into a red build.
- **Hard line cap on signpost docs, enforced by the gate** (soft budget ~40 lines,
  enforced cap ~80). An area doc is an index — purpose, invariants invisible in code,
  anchors — never restated behavior. Uncapped area docs decay into behavior summaries
  that rot faster than code comments.
- **Negative assertions on docs.** The gate fails on banned prose patterns, not only on
  missing ones: a citation of implementation-plan status inside an area doc, and a
  stale project-status sentence inherited from the initial commit. The dominant
  corruption mode of agent docs is stale-but-unflagged truth, which no amount of correct
  linking catches.
- **Signpost coverage rule with explicit exemptions.** Every source area of the tree
  must either own a signpost doc or appear on an explicit exemption list the gate
  checks. An area that grew code without gaining a signpost is invisible to an agent
  that loaded only the root doc.
- **One status anchor, link-only.** Implementation status lives in exactly one plan
  index; the root doc links it and the gate bans mirrored status prose anywhere else.
  Mirrors are the first thing to lie.
- **Regenerate-and-diff drift gates for every generated surface.** Code generation,
  schema compilation, and pinned image digests all get a CI step that regenerates the
  artifact and fails on a non-empty `git diff`. A committed generated file with no
  regeneration check is a committed staleness guarantee.
- **Manifest-key completeness check.** A meta job asserts every key the
  container-orchestration toolchain expects exists in the pinned-versions manifest, so
  a renamed service fails at PR time, not at acceptance-run time.
- **Parked gates are stated, not deleted.** A hosted job disabled for runner-budget
  reasons stays visible as a commented job carrying: the disable reason, the fix
  condition that would re-enable it (e.g., a recorded exec bit finally committed), and
  the local command that still proves the same property as an acceptance requirement.
  A silently removed job loses the knowledge that the proof exists and where it moved.
- **Exec-bit trap for committed launcher scripts.** A script launched locally through an
  interpreter passes while a CI job invoking it directly dies on a non-executable mode
  recorded in git. The first hosted run is the only environment that checks mode bits;
  commit the executable bit or wrap CI invocation in an interpreter.
- **Config off-switches carry their reason.** Harness and tool config entries that
  register a capability but stay disabled (model fallback chains, advisors, MCP-style
  servers) sit as commented or `enabled: false` blocks with an inline one-line reason
  and a pointer to when they activate. Registered-and-unused integration entries with no
  reason are inventory noise: telemetry shows zero calls against them across the window.

## 2. Evidence discipline

- **Status flips require named gate evidence.** A roadmap/plan row moves to done only in
  a commit whose message names the passing gate run — tier, tool, and step counts.
  "Implemented" without a cited green run is a claim, not a state.
- **As-built vs target, two layers, one-way sync.** Skills and area docs describe
  implemented reality (sections literally headed "as-built"); target design lives in the
  spec/plan layer, with the code authoritative for what exists. The maintenance rule: a
  change that lands a schema, endpoint, table, or CLI command updates the matching skill
  in the same commit.
- **Plan archaeology is scrubbed at exit.** When a phase completes, plan numbers and
  spec-section citations are stripped out of shipped checks, tests, and skill text while
  the encoded behavior stays. Plan identifiers rot on the first renumber; behavior laws
  do not.
- **Runbooks are written from executed drills.** Each failure-scene runbook opens with a
  disambiguation from its neighboring scene ("a live session with a silent worker is
  this scene; a dead session is the other one"), then a "law from shipped code"
  section, then copy-pasteable diagnostic queries. Agents follow a law plus a query far
  more reliably than narrative prose.
- **Acceptance run as one exit-code tool.** The whole acceptance story is a single
  command against a clean volume: ordered steps (login, create, dispatch, roundtrips,
  restart-resume, trace-correlation) that seed their own state, wait by polling event
  kinds on the timeline rather than wall-clock sleeps, and exit 0 iff every step passes.
  Restart steps checkpoint through durable pauses (park an approval, kill the
  kernel-launched worker container, verify exactly one retryable failure and a
  re-dispatch to completion) so resilience is proven at the acceptance tier, not only in
  unit tests.
- **Negative assertions in the acceptance set.** At least one step asserts emptiness —
  dead-letter queue empty after the whole run. A suite that only asserts arrivals passes
  on a pipeline that silently produced junk.
- **Verified-date stamps on captured lessons.** Captured debugging skills carry a
  verification date and the dependency version they were verified against, so a future
  session knows when the lesson starts costing re-verification.

## 3. Performance discipline

- **A wall-clock budget on the primary test command, documented in the root agent doc.**
  The budget is the reason the suite's structure exists: shared infrastructure fixtures,
  bounded-but-small waits, and a rule that every new fixed sleep must justify itself.
  When a budget breaks, the response is tier-splitting the suite, not raising the
  number.
- **Shared-infrastructure fixtures with contention carve-outs.** One database container
  and one identity-provider container shared process-wide; the identity provider is the
  only sanctioned token minter. Assertions sensitive to interference (queue drains,
  lease sweeps, gauge values, time-travel garbage collection) get their own dedicated
  container, because a foreign dispatcher's sweep or a foreign fake-clock advance on a
  shared database corrupts exactly those assertions. This was paid for twice as
  intermittent full-suite-only flakes.
- **Runner-budget-aware scheduling.** Heavy jobs run on a nightly schedule plus manual
  dispatch instead of every push when hosted runner minutes are capped; each job carries
  an explicit timeout equal to the platform's per-job limit.
- **Long-job telemetry-before-relaunch rule.** For any job plausibly running 20+
  minutes, the launch command itself embeds the evidence a timeout must leave behind:
  full unfiltered output redirected to a log file, timestamped phase markers, a child
  process-tree and daemon-health snapshot, and a bound that lets partial evidence
  survive the kill. If a prior identical-thickness attempt already timed out, re-running
  it unchanged is a defect: add instrumentation or vary the experiment (isolate one
  scenario, add stock controls). One fully instrumented long run beats several blind
  shorter reruns that cannot answer where they stalled.
- **Never pipe a long suite straight into a narrowing filter.** A `grep`/`tail` on
  stdout discards the post-stall trail precisely in the case where the trail is the
  point.

## 4. Multi-agent orchestration

- **Plans are tracked, reviewable artifacts** in the repo (a numbered plan directory
  with one status index), not conversation state. A handoff command entry point reads
  the plan directory, so any session — or any spawned agent — resumes from the file.
- **Phase commits double as sub-agent checkpoints.** Multi-phase plans commit per phase
  with the phase named in the message; a crashed or context-lost agent resumes from the
  last phase commit rather than from a re-derived mental model.
- **Behavioral rules scope to plans and task lists, not only to tool calls.** A
  guard-rail rule (e.g., "never push to a remote; end at a local commit") is declared
  with matcher conditions evaluated against bash commands, todo-list entries, and
  assistant text. A rule that only blocks the tool call still permits the agent to
  plan, promise, and announce the forbidden action.
- **Sub-agent failure budget as a telemetry row.** Task-tool dispatches and their
  failures are counted per window (failures were a small single-digit percentage of
  dispatches); a rising ratio points at under-specified task briefs or shared-file
  contention, i.e., one integration owner per shared edit set, interfaces declared in
  the task context.

## 5. Large-output handling under context compression

- **Compression plus a narrowing pipeline is the evidence-loss combination.** Once a
  session window can compress old tool results, a long command that already filtered
  its own output leaves nothing to recover. The recovery mechanism is the
  long-job-launch rule (full log to disk, phase markers) — disk state survives every
  compression; a truncated transcript does not.
- **Oversized-file read denial forces chunked access.** Reads of large files are refused
  whole and must be re-issued with ranges or routed through an index/search over
  captured output. The block count is a telemetry signal: a doc the harness refuses to
  read whole is a doc that violates its own size budget.
- **Docs are shaped for the reader that paginates.** Bounded signpost files, one scene
  per runbook, and one-topic skills keep per-read cost constant; the telemetry read/edit
  ratio is what makes bounded docs measurable rather than aesthetic.
- **Where the harness provides an execute-and-summarize device, print the answer.**
  Scripts compute over large payloads and emit only the aggregate; raw payloads stay on
  disk at a stable path for follow-up queries.

## 6. Container / browser toolchain runbook

- **The browser tier is provisioned separately from the unit tier.** A separate command
  installs the browser binary (its first-run download path and cache location echoed to
  the operator) and runs the browser suite against a real locally-hosted server plus
  containerized database and identity provider; the main test command never silently
  pulls browser dependencies.
- **E2E harnesses that spawn their own server take external infrastructure by
  environment variable with documented fallbacks**, including a no-client-binary escape
  hatch for database administration (run the client through a container exec).
  Prerequisite echo lines naming required services and ports are part of the target
  itself.
- **Host-gateway access is baked into the dev container's environment**, not discovered
  per-scenario: a single documented container-environment variable pointing at the
  host-side service.
- **Heavy all-in-one dependency images need a minimal profile on constrained hosts.**
  Self-contained forge/analytics-style images are booted with their metrics, alerting,
  and registry subcomponents disabled (memory contingency), their web UI polled before
  bootstrap, and an API-token bootstrap preferred because current versions refuse
  password-based app auth.
- **Container-CLI scenario-test handshake hangs are worked around at the right layer.**
  When a devcontainer-CLI feature-test handshake stalls forever on its container-start
  event wait (observed on WSL2 Docker), the CLI's build phase and the scenario scripts
  are fine — run the scenario containers directly instead of debugging the suite.
- **Teardown enumerates non-orchestrated containers.** Containers launched
  dynamically by the application under test are not members of the orchestration file;
  a "clean volume" rerun must remove them by label selector, otherwise the next
  acceptance run silently inherits a stale fleet.
- **Pre-public history scrub is a two-layer gate.** Before a repository becomes public,
  scan both the tracked working tree and every historical blob (everything in the
  packfile ships forever), with a false-positive triage pass for fixture-shaped strings.

## 7. Custom-surface e2e discipline

- **One named command per e2e surface** (protocol-link tier, gateway tier, worker tier,
  browser tier, live-provider tier), each echoing its prerequisites instead of assuming
  them; the aggregate command excludes the surfaces that need provisioning and says so.
- **Wire contracts are proven twice: once with the in-repo client, once with a
  deliberately foreign-shape client** written against the published schemas, driven by a
  config-driven conformance runner that emits a JSON report uploaded even on failure.
  A contract only exercised by its own implementation is unproven.
- **Live-tier suites support step selection** (run one leg by name) so an expensive
  live tier is resumable per-leg after a failure instead of monolithic.
- **Browser-tier coverage mirrors interactive states symmetrically**: anonymous
  redirect, identity display, search→detail→timeline drill-down, online/offline chips,
  replay actions, and a config edit proven from both sides (success and conflict).
- **Acceptance-required env beats hosted CI.** When hosted compute is unavailable, the
  local reference environment is formally the gate ("runs locally, acceptance-required")
  rather than the acceptance story being quietly downgraded to unit tests.

## 8. Doc architecture

- **Root agent doc = truth pointers + commands + area signpost table + follow-the-truth
  request paths.** The request-path section walks a request through the entry route,
  processing stages, and persistence hop by file anchor, so an agent edits the seam
  rather than grepping for it. Nested docs follow "nearest file wins": a tool that
  loaded only the root doc opens the nested signpost of every directory it touches.
- **Layer separation with one-way promotion:** backlog ideas → numbered plans →
  as-built skills/docs; runbooks live in the operator docs tree, keyed by failure scene.
  Each layer has exactly one home, and the gate enforces the boundaries (no plan-status
  prose in area docs, no behavior restatement in signposts).
- **CI workflow README is a table of jobs × "what it proves"**, including
  disabled-but-parked jobs. The pipeline becomes reviewable as an evidence portfolio,
  not a pile of YAML.
- **Pinned toolchain versions in manifest files** (SDK pin, runtime engine floors, a
  versions manifest for image tags, digest-pinning for third-party images) with a CI job
  that resolves current digests and commits the update back, so supply-chain drift is a
  bot commit, not an audit.

## 9. Telemetry-driven self-improvement loop

- **One snapshot device per window** writing machine-readable JSON: per-tool call
  counts, per-tool failure counts, loop detections, dead-reference counts, read-guard
  blocks, thinking-level changes, plugin/integration usage versus registration. A
  retro that reads this file argues from distributions, not from memory.
- **Registered-vs-used is a first-class comparison.** Integrations listed in project
  config with zero observed calls across the window get disabled-with-reason or
  deleted; plugins present in the fingerprint with versions make regressions
  bisectable.
- **Captured-skill hygiene.** Lessons captured during sessions land in a user-level
  store. The store splits naturally into tool-generic procedures (portable) and
  repo-coupled runbooks. Repo-coupled entries never travel with a repo merge or a
  machine switch — they belong promoted into the repo's own skills directory, leaving
  the user-level store for genuinely cross-project procedure. Near-duplicate captures
  of the same procedure are a dedupe signal at capture time.
- **Telemetry signals map to harness changes, not to agent scolding.** The counters
  below motivated specific items earlier in this document:

| Signal | Action |
|---|---|
| Dead-reference counters on read/grep/edit | Ship a path-resolution gate for agent docs; delete or redirect stale pointers |
| High edit-failure ratio (edits failing against recently-read files) | Re-read-before-edit discipline; smaller edit anchors; check whitespace/EOL churn |
| High failure ratio on shell calls | Move fragile ad-hoc sequences into named scripted targets with prerequisite checks |
| High loop count on one tool/session | Truncating pipeline or flaky step: add phase markers, unfiltered log capture, then isolate the flake |
| Thinking-level escalations clustered on one task shape | The task exceeds default effort: raise effort for that call class, or split the task |
| Read-guard blocks | The doc or tool output violating its size budget: split at source, paginate at reader |
| Many wait calls with long gaps | Long-job telemetry-before-relaunch rule applies to the offending command |
| Registered integrations with zero calls | Disable with an inline reason or delete |
| High read/edit ratio overall | Docs shaped for chunked reading; verify area-doc size caps hold |
