# Embedded workflow plane: make the implicit workflows explicit, operator-editable, and hidden

**Status:** exploration, leaning decision — external engines ruled out, in-house plane sketched through authoring + instance mapping.
**Governing law:** everything the system does today must be possible to
express with the plane (§5) — it is a strict superset of the status quo,
never a subset with a few nice new tricks.
**Explored:** deep kernel audit (200 .cs, 44 tables, 30 migrations), the 2026
open-source engine landscape (license + activity verified via GitHub/NuGet
APIs), control-surface inventory, and a concrete shape for defining, storing,
editing, visualizing, and *running* flows.

---

## 1. The observation

The Facilitator *behaves* like a workflow engine but *encodes* its workflows
implicitly. "What happens next" is a property of C# arm logic, not data.

One premise correction first: **"harness vs. integration" is not a routing
decision in the kernel.** Both are a link endpoint advertising a capability;
`RoutingSink` picks by liveness ∩ manifest ∩ team. The only "integration-ness"
expressed anywhere is two hardcoded capability constants
(`vcs.change_request.*`, `vcs.issue.*`) whose dispatches bypass the funnel.
The implicit thing is the **sequence around** the routing, not the destination.

Today's implicit workflows, as C#:

- `ProviderEventStore` (~81 KB of arms): labeled issue → create Work → append
  `routing.requested.v1` effect; review on a terminal Work → fork successor
  (`ForkTerminalAsync`); pipeline events move items. Each is "when X in
  context Y, do Z" — a workflow transition written as a branch.
- `ResultIngress`: manifest completes → record → maybe dispatch PR-creation
  ops → settle. A saga, hand-written.
- `RoutingSink`: the funnel's decision law. Keep as C# — it's algorithmic.
- Chat decode (`ChatProcessor` → `DecodeSink` → `CommandApplier`): a
  propose/validate/apply loop against the closed `ActionCatalog`.
- Time: six `BackgroundService` sweeps — a timer subsystem reimplemented
  six times with the same advisory-lock + deterministic-command-id law.

The only declarative seams today: `AssignRules` (label → capability/endpoint),
`policy-file.v1.json` (authorization, not orchestration), capability
manifests, scriban templates, the schedules table, and `admin_config` — whose
`escalation` and consent-throttle resources are **stored but read by no code**
(operators can already configure behavior nothing executes).

**The measured cost of one declarative-sized behavior:** the review-event
family landed as **45 files across 7 areas** (schema → registry → codegen →
both ingresses → timeline-kind migration → adapter allow-lists → gateway
policy → UI config mirror → ActionCatalog → xUnit + e2e + scenario bindings +
verify legs). Growth counters at HEAD: 81 routes, 21 commands, 28 timeline
kinds, 9 hosted loops, 44 tables, 114 scenario bindings.

**The question:** should a small workflow/durability plane live *inside* the
kernel — the only interaction surface being the Facilitator itself — so
expanding functionality means *defining a workflow* instead of *coding a
feature*?

## 2. The durability question is already answered

The kernel implements what a durable engine sells (verified anchors):

| Engine concern | Present today |
|---|---|
| State machine | `WorkState.StateGuard` declarative edges → DB CHECKs (legality **is** data already) |
| Single-transaction mutation | `ICoordinationStore` (advisory lock → guard → fenced UPDATE → event append → outbox → `pg_notify`) |
| Engine loop | `OutboxDispatcher` (`SKIP LOCKED` leases, reclaim, 5-attempt backoff, DLQ + admin `replay:<id>`) + sweeps; `LISTEN/NOTIFY` wake |
| Idempotency | 3 inbox uniques; `outbox.command_id` UNIQUE; deterministic ids everywhere; `RoutingIntent.AssignmentIdFor` = UUIDv5 ⇒ **pre-computable activity ids** |
| Fencing | `works.version` / `current_execution_generation` (422 `stale-generation`) / link `session_epoch` |
| Timers | lazy columns (`available_at`, `expires_at`, `next_run_at`) + sweeper passes, each individually switch-offable, each with a public `SweepOnceAsync` + admin trigger |
| Joins/wake | `DependencyGate.WakeGatedRoutingIntentsAsync` — a timer-fire UPDATE an engine can drive verbatim |
| Audit | every allow/deny recorded; per-Work timeline as event log |

What's missing is **not** durability. It is: (1) a first-class *definition*
object, (2) declared compensation, (3) an *instance* you can inspect/cancel as
a unit across works, (4) operator editability, (5) replay-from-history (nice,
not required — the snapshot+event-log hybrid covers audit).

**Conclusion (settled): build the definition layer on the substrate we
certify; do not adopt an external engine.** The engine-landscape appendix (§10)
records why nothing on the market fits "hidden inside the product, Postgres
only, permissive, active".

## 3. The plane, in one paragraph

A `WorkflowSink : ISink` on a synthetic `workflow.kernel` channel (precedent:
`routing.kernel`) reads **definitions** (JSON, schema-validated, versioned
rows) and advances **instances** (`workflow_instances` + `workflow_step_log`)
using the existing inbox/outbox loop as its clock, the existing command-id law
as its idempotency (`wf:<def>:<instance>:<step>:<n>`), the approval ledger as
its human step, `IDecisionProvider` as its (advisory-only) brain, and
`DependencyGate` mechanics for joins. It implements the port the architecture
spec already reserves — **`IWorkCoordinator`** (§7.5/D-16/§29.3, spec'd but
never built) — so this is a spec act, not a deviation. ~1.5–2 kLOC of
interpreter; no new process, no new database, nothing visible to end users.

### Definition sketch

Canonical artifact is a JSON Schema in `protocol/schemas/`
(`workflow-definition.v1.json`) — same regime as every other wire artifact,
validated by the validator we already ship. Predicates reuse the
**policy-file matcher law** (exact / `*` / trailing-glob — no expression
language, no DSL). Steps are a **flat list + `on:` edges** (a graph, not
nested code) so text and visual views are 1:1 with storage.

```yaml
# Operator's view (YAML sugar; canonical store is JSON). PR review loop:
id: pr-review-loop
version: 3
title: Retry failing PR builds, escalate after three
description: >
  When a review asks for changes, post the failing log to the thread,
  re-dispatch the build, and block a human if the third attempt fails.
on:
  event: org.facilitator.event.provider.review.v1
  where: { data.reviewDecision: changes_requested }
steps:
  - id: log-failures
    emit: { command: command.communication.post,
            to: $work.thread, text: "{{ event.data.failingLog }}" }
  - id: retry-build
    emit: { command: command.work.assign,
            capability: $rule.capabilityId,
            inputs: { note: "{{ event.data.body }}" } }
  - id: await
    wait: { event: event.assignment.completed.v1, correlate: $step.retry-build.workId,
            timeout: 2h, onTimeout: escalate }
  - id: escalate
    on: timeout|third-failure
    do: { approval: { question: "Build failed 3×. Merge anyway?",
                     approvers: $work.owner } }
compensate:
  on: failed { emit: { command: command.communication.post, ... } }
```

Closed step palette (deny-by-default, size-capped), each step mapping to an
already-proven mechanism:

| Step | Executes as | Proven by |
|---|---|---|
| `emit` | any envelope in `KnownCommands` (22) or a capability dispatch via routing intent | the entire outbox plane |
| `wait` | inbox correlate + lazy `available_at` deadline | approval waits |
| `timer` | sweeper-driven deadline column | schedules |
| `approval` | digest-pinned single-use approval record | `ApprovalIngress` |
| `decide` | `IDecisionProvider` ChoiceQuestion, confidence floor, audited | `RoutingSink.ChooseAsync` ladder |
| `select` | copy/project JSON fields between step results (the glue arms do in C# today) | new, trivial |
| `fan-out` / `join` | create-batch DAG law + `DependencyGate` wake | `DagLaw` |
| `fork-work` | terminal-work successor convention, generalized | `ForkTerminalAsync` |
| `escalate` | notification ledger + triage block | `NotificationLedger` |
| `recall` / `retain` / `promote` (§4.5) | memory broker scopes; `promote` = distill instance step-log → durable scope | `MemoryIngress` + `IMemoryProvider` |

## 4. The control surface: is there enough for operators to be interesting?

The honest inventory of moves:

- **Triggers (~21 + 3):** every wire event (`provider.issue/review/mention/
  message/pipeline/reaction/change_request`, `assignment.completed/failed/
  input.required`, `approval.decided`, `artifact.available`, `work.progress`,
  …), schedules (`cron:`/`daily:`/`interval:` — already exist), and webhook
  ingress. New triggers only appear when a new adapter ships an event — but
  that's the protocol growing, which is exactly what should require code.
- **Actions (22 commands + an open set):** the closed command catalog (post/
  reply/react, create/steer/nudge/cancel works, approvals, memory
  recall/retain, artifact uploads, provisioning) **plus any capability id** —
  the kernel doesn't know what capabilities *mean*; `vcs.pr.merge` and a
  team-defined `agent.summarize-week` route identically. **This is the
  escape hatch that keeps the surface from ever being "small": a capability
  step is an arbitrary team-side task, and the fleet advertises it, so the
  operator vocabulary grows with adapter installs without kernel changes.**
- **Waits:** any event, any approval, any deadline.
- **Brain — AI is already shaped to be a step.** The kernel's AI surface is
  not a chatbot bolted to the side; it is two narrow ports whose discipline
  is exactly what a workflow step needs, and they map onto steps almost
  verbatim (§4.1).
- **Limits, stated plainly:** operators **orchestrate, they do not
  transform**. Predicates are matcher-boring (good — policy-file proved that
  law is enough for authorization, it's enough for triggers too). Reshaping
  payloads is where the plane is weakest, hence the `select` step; anything
  heavier is a capability. The failure boundary is deliberate: *flows route,
  capabilities compute.*

Three composite examples, all expressible in the vocabulary above today:

1. *changes-requested review → post failing log to thread, re-dispatch coding
   capability with the reviewer's note, third failure blocks a human.*
2. *Friday 09:00: fan out per-project `summarize-week` capabilities → join →
   dispatch one synthesis task → post digest to channel → ask the lead to
   approve filing top follow-ups as issues.*
3. *research mention → create work, fan out three `search.*` capabilities →
   join → synthesis task → `memory.retain` the result → reply with link.*

### 4.1 AI as steps — the three tiers

The kernel today exposes exactly two AI ports, and they + the capability
system give the palette a naturally stratified AI surface. Nothing new to
invent; each tier is an existing mechanism wearing a step hat:

| Tier | Step | Maps onto (existing) | Semantics |
|---|---|---|---|
| **Choose** | `decide` | `IDecisionGateway.DecideAsync`: `ChoiceQuestion` (closed choice list) / `PredicateQuestion` (bool), `DecisionAnswer` with `Confidence`, `ConfidenceFloor = 0.5`, out-of-set ⇒ deterministic `model-fallback`, `AiUnavailableException` ⇒ degrade-never-fail | structured branch: answers only select among operator-written branches; degrade branch is mandatory in the definition (a flow can't be smarter than its gateway being up) |
| **Write** | `generate` | `IAiGateway.CompleteAsync` with a scriban `TemplateId` + `MaxOutputTokens` | text production into a step output (a chat post body, an issue comment); bounded by template + token cap; never parsed as authority — it flows as data |
| **Compute** | capability `emit` | routing intent → harness endpoint (an LLM or an agent *is* a team-side endpoint advertising e.g. `agent.classify-tickets`) | the heavy tier: full harness sessions, tools, artifacts; the flow sees it as any other dispatch/wait pair |

Governance that rides along for free (already law, just addressed per-step):
`AiPolicyStore` (the `admin_config:llm` policy — **inert when absent**, so a
flow with AI steps is born disabled until an operator enables AI),
`loop-budget` + `AiActionGaugeSweeper` (per-flow/actor budgets — a runaway
definition is throttled by the same gauge as a runaway chat), the AI ledger
(`AiLedgerStore` rows land in the flow's step log as the step's
`evidence_ref`, so the console shows "model answered X, confidence .62,
fallback available" inline), and the authority law unchanged: **AI proposes,
deterministic policy applies** — a `decide` answer can pick a branch, never
skip an approval or issue a command by itself.

Design rules that fall out:
- **Every AI step must name its degrade.** `decide` without an explicit
  fallback branch is a validation error — this makes "what happens when the
  model is down?" a definition-time question, not an incident.
- **`generate` output is data, always rendered through templates**, never
  eval'd as control flow — keeps the no-scripting law intact even with an LLM
  in the box.
- **Tier ladder as operator guidance:** the console suggests
  `decide` → `generate` → capability in that order (cheapest, most bounded,
  most auditable first), mirroring the "flows route, capabilities compute"
  boundary — an LLM step is still routing-shaped intelligence (choose,
  summarize-for-a-human), while *doing the thinking about a work* is what
  harnesses are for.

### 4.2 Implicit capabilities: making the kernel's AI a routable member of the fleet

There is a gap the tier table hides, and it violates the coverage law from the
other direction: **the kernel's AI today is a capability the system *has* but
never *advertises***. `CommunicationDecision`, the reply composer, the
routing advisory, chat decode — these call `IAiGateway`/`IDecisionGateway`
directly, as private method calls. Nothing in the routing plane can see them,
so "send this work to a model" is not a thing a flow (or the funnel) can
*route to*; it's a thing specific code does. The fix:

**A synthetic fleet member: the kernel enrolls itself as a virtual endpoint.**

- At boot, the kernel creates a well-known endpoint — say
  `kernel.model` — with a synthetic `link_enrollment` +
  always-live `link_session` (system team, non-revocable while AI policy is
  enabled), advertising a **generated capability manifest**: `ai.classify`,
  `ai.summarize`, `ai.compose`, `ai.decide-routing`, `ai.pick-branch`, each
  with a descriptor (input/output shapes per the template registry). The
  manifest is derived from the template + decision-configuration the kernel
  already holds — no new config surface.
- **Zero new machinery:** `RoutingSink` already selects by
  liveness ∩ manifest ∩ team — a virtual session passes the same query as a
  real gateway's. A flow step `emit assign { capability: ai.summarize }` is
  therefore an ordinary intent: it goes through the funnel, gets a routing
  decision row, honors triage, and produces a Work whose execution *is* a
  model call, with the completion event arriving through the same
  `assignment.completed` leg any harness uses. The model call executes
  in-process (the sink for `kernel.model` is the AI gateway itself), so there
  is no network hop — the enrollment is a *representation*, not a proxy.
- **The override pattern this unlocks (the actual payoff):** because the
  built-in is now just a candidate, a team can plug in a real endpoint —
  their own fine-tuned summarizer, a different provider, an agent — that
  advertises the *same capability id* with a richer descriptor. Specificity
  ordering (the existing candidate law) prefers the more precise advertiser;
  `preferenceEndpointId` and the configured-provider rule still apply. The
  operator **shadows the implicit capability with an explicit one** without a
  kernel change — exactly the "expand functionality by defining, not coding"
  goal, applied to the system's own internals. The console's capability
  picker, being registry-driven (§6), automatically shows the shadowing
  choice.
- **Governance stays at the one existing chokepoint.** `AiPolicyStore`
  (`admin_config:llm`) gates whether the virtual enrollment exists at all
  (policy inert ⇒ manifest absent ⇒ flows' AI steps take their declared
  degrade branch — the "AI was never on" state is a *visible* empty fleet, not
  a hidden failure). The `loop-budget` gauge and AI ledger key on capability,
  so per-flow budgets read naturally ("`ai.summarize`: ≤20 calls/hour for
  scheduled flows"). Every model use is now a routing decision row: allow and
  deny audited like any dispatch.
- **Where it does *not* apply, said plainly:** the synchronous micro-steps —
  a `decide` branch pick inside a step — call the gateway in-process and
  should *not* pay a work/assignment round-trip; virtual capabilities are for
  AI-as-*work* (input payload, output artifact, joins). The law: **if the
  answer fits in the step's transaction, it's a step; if it needs a timeline,
  it's a capability.**
- **Coverage-law bonus:** the existing implicit AI call sites
  (`CommunicationDecision`, composer, advisory) become expressible as flows
  that dispatch `ai.*` capabilities — §5's exempt verdicts for them can be
  downgraded to "expressible, retained for latency" rather than "exempt,"
  keeping the plane a stricter superset.

Two adjacent implicit capabilities get the same treatment cheaply, because
they're already envelope-backed and just lack an advertisement: the
**artifact store** (`sys.artifact.store|fetch` — a harness on another team
"routing to" shared storage) and the **memory broker** (`memory.recall|retain`
are commands today; a `kernel.memory` virtual endpoint makes them *work-level*
steps a flow can fan out and join). Provisioning reconcilers deliberately do
**not** join the virtual fleet — reconcile loops are substrate (§5's
mechanism class), and giving them manifests would suggest operators can
configure them when they really can't.

### 4.3 The description plane: a name and a story for every moving part

**The gap, verified:** the system's vocabulary is almost entirely *structural*.
`capability-manifest.v1.json` has `id`, `execution`, `inputSchema`,
`outputKinds` — **no `title`, no `description` field exists on a capability.**
Commands are wire constants in `KnownCommands`. Steps in a definition have
mechanical `id`s. The one place prose lives is a Work's `objective` string —
and it works beautifully there: it's what humans read in the console, what
gets rendered into decode prompts, and what the advisory model sees. The
lesson generalizes: **wherever we put natural language, both humans and the
AI use it; where we only put ids, they pattern-match on identifiers.**

**Design rule: every addressable thing in the plane carries a mandatory
`title` + `description` (and optional `when`/`notes`) beside its id** — and
these are *validated, stored, rendered, and retrieved* like any other field:

| Thing | Gets | Who reads it |
|---|---|---|
| Definition | `title`, `description` ("what this flow does and why"), per-step `title` | console graph labels, gallery cards, AI drafting context |
| Trigger | `description` ("a review came in asking for changes") | console, error messages, trace names |
| Capability (manifest v2) | `title`, `description`, `examples[]` | routing advisory prompt, console capability picker, flow editor palette |
| Work | `objective` (exists) + `summary` at completion | already everywhere; a flow step can generate it (a `generate` step on the terminal leg) |
| Instance | derived `title` = definition title + trigger digest ("PR review loop · on !412") | flow view, notifications |
| Command | registry-level `summary` (already in `ActionCatalog` for chat — promote it to the command registry itself) | palette, AI drafts, audit lines |

Where the prose comes from, four cheap paths:

1. **Human-authored is the default and the gate.** A definition with an empty
   description fails console-save validation (warn) and API validation (cap).
   Gallery entries ship curated prose — the gallery is *documentation as
   data*; the console needs no help text because the flows carry their own.
2. **AI-generated first draft, human-owned.** The editor's "describe this"
   button runs a `generate` step over the definition JSON (template in the
   existing registry) and proposes the title/description as a diff — prose
   generation, the same propose/validate law. Prose generated *from* structure
   can't smuggle behavior in, so this is the safest AI touch in the whole
   plane.
3. **Derived for the implicit world.** When §5's observe-and-promote renders
   a C# arm as a read-only definition, it fills descriptions from the arm's
   own artifacts: the doc-comment atop the arm method, the scenario binding's
   description, the audit `basis` strings. If we extract review-fork as an
   arm→flow, we must write the sentence — and that sentence *is* the spec of
   the migration.
4. **Runtime descriptions are rendered, not stored.** An instance's human
   line ("waiting 14m for build #412 to finish") comes from a per-step-kind
   renderer template + step data — one narration law, no drift.

**Why this is load-bearing for AI, not just UI.** Three consumer loops close
when prose exists everywhere:

- **Drafting:** "route failed PR reviews back to the coder, escalate on the
  third" is only translatable to a definition if the catalog of dots has
  human language on it — the AI picker retrieves steps/capabilities/events by
  their descriptions (embeddings optional; a description-tagged registry
  rendered into the prompt is enough at this scale, and stays explainable).
- **Routing:** the advisory ladder gets strictly better with manifest
  descriptions in the `ChoiceQuestion` context ("team's summarizer: cheap,
  lossy; agent-summarizer: full report") — same set of candidates, more
  informed pick, unchanged law.
- **Operation:** an operator facing a blocked instance reads *titles* in
  notifications and the flow view; audit lines become
  "flow *pr-review-loop* (retry failing PR builds, escalate after 3) chose
  endpoint X" instead of ids. This is the difference between a console you
  can *read* and one you must memorize.

**Governance rules keep the prose honest** (description drift is real drift):
descriptions are versioned with the object they describe (immutable pair),
they're capped (no prompt-bloating: per-field character limits, total
definition budget — the existing template/`MaxOutputTokens` discipline),
capability descriptions live in the manifest so a shadowing endpoint must
out-describe the built-in it replaces (the palette shows both, side by side),
and — the sharp edge — **matching stays deterministic forever:** triggers
never fuzzy-match on descriptions; prose informs humans and advisory prompts,
never selection. The sentence "flows route, capabilities compute" gets a
postscript: **descriptions explain, they never decide.**

### 4.4 The semantic catalog: what else is missing before AI can build flows

Descriptions answer *"what is this thing called."* Building a flow needs more:
*"what may I connect to what, what will happen, what does it cost, what can I
undo."* Audited at HEAD: the event schemas carry **good field-level prose**
(enum meanings, laws like "a comment is not a review"), but everything
*behavioral* about the dots is either C# doc-comments, tribal knowledge, or
`ActionCatalog`'s five-line summaries. Concretely missing, ordered by how
much each unlocks for an AI author:

1. **Effects & eligibility (the connector typing).** *"Which dots may wire to
   which, and what happens when they do."* Per command/capability: expected
   input shape (exists for capabilities via `inputSchema`, missing for
   commands), admissible context ("post only onto a thread-backed work"),
   and **what it does** — one line of postcondition, machine-adjacent
   ("creates an assignment; work goes `planning→active`"). This is the field
   whose absence today makes AI drafting a guessing game: the model can
   invent a syntactically valid flow that is semantically inert (a
   `communication.post` on a work with no thread). *Enables: drafting without
   hallucinated edges; the console palette can grey out illegal connections
   for humans too.*
2. **Inverses & safety class (the undo vocabulary).** Per step-kind:
   `reversible` / `compensable(how)` / `irreversible` (merge is irreversible;
   a chat post is retractable; an assign is cancellable until accepted —
   already knowable from the state guard!). Plus a `destructive` flag.
   *Enables: the AI (and the validator) refuse to auto-place irreversible
   steps after an `approval` that hasn't been asked; compensation drafts
   become mechanical ("for each irreversible step, name its compensation or
   mark accepted-risk"). This is the field that makes "AI proposes,
   deterministic policy applies" work on *risk*, not just syntax.*
3. **Idempotency & duplication semantics.** "Is re-delivery of this trigger
   harmless?" "Does emitting this command twice collide (command-id law) or
   double-post?" The kernel *has* this law but doesn't *state* it per
   command. *Enables: AI chooses safe retries; the retry-policy per step
   stops being cargo-culted.*
4. **Cost, latency, and side-effect class per capability.** Rough tiers
   (`free/local`, `tokens`, `human-time`, `money`) and expected duration
   (`seconds`/`minutes`/`hours`/`human-paced`). Descriptors say *what* a
   capability takes, not *what it costs to wait for*. *Enables: the drafting
   AI puts `timeout` values that aren't fiction; budget-aware planning
   against `loop-budget`; the console warns "this Friday flow dispatches 40
   paid-capability works" before enabling.*
5. **Example payloads per event/command — the few-shot corpus.** The
   conformance fixtures (`protocol/conformance-fixtures/valid/*`) already
   *are* realistic example envelopes, generated by the people who wrote the
   adapters, kept honest by CI. Nobody renders them for authors. *Enables:
   the drafting prompt embeds 2–3 real envelopes per referenced type —
   highest-leverage single addition, near-zero new content, because we
   generate the corpus from tests we already run.*
6. **Work-state awareness (the flow's physics).** The Work state machine is
   the one truly complete semantic artifact in the system (`StateGuard`
   generates the wire vocabulary and the DB CHECKs). Expose it as data:
   allowed transitions + which commands/capability-dispatches cause them.
   *Enables: AI authors reason about "can I still cancel here?" the same way
   the store does; the validator rejects flows whose step ordering is
   state-impossible — and does so *before* shadow-mode burns a week.*
7. **Causality annotations (what implies what).** "An `assignment.completed`
   for a work implies a prior `routing.requested` and a consumed approval (if
   required)." Cheap to state as pairs, and *enables: derive-triggers (§6) to
   suggest "flows that plausibly chain: A ends where B begins" — the
   connect-the-dots assistant that composes gallery entries into pipelines
   instead of inventing new ones.*
8. **Anti-goals / notes.** One line per dot for "what this is *not*"
   (steer ≠ re-dispatch; react ≠ reply). Cheap, and the exact place today's
   doc-comment wisdom lives — promote it, don't rewrite it.

**The build shape (important: this is a generator, not a documentation
project).** Author the missing semantics **once per command in the protocol
source itself** — a `semantics` block in each command schema (or a sidecar
`affordance-catalog.v1.json` in `protocol/`, same codegen as wire types), and
**generate** the catalog by merging: schema field descriptions (exist) +
semantics block (new, ~5 lines per command) + `ActionCatalog` summaries
(exist, move them) + conformance fixtures (exist) + `StateGuard` edges
(exist, export them) + capability manifests (descriptions per §4.3). Ship it
as:

- a **`GET /api/registry` endpoint** — the live catalog: types, prose,
  eligibility, cost class, example envelopes, *and current advertised
  capability instances*. The flow editor palette and the AI drafting prompt
  consume the *same* payload. Drafting-against-reality for free: an AI
  (or human) cannot reference a dot that isn't in the catalog, and the
  validator re-checks the same catalog at apply time — a prose lie in a
  description can mislead a reader, but a missing eligibility entry fails the
  build.
- **Versioned with the wire schemas** — a definition drafted against catalog
  v1 validates against catalog v1, so upgrade-time breakage is visible in
  the same conformance-suite regime the envelopes already use.
- **Gallery entries cross-referenced** — each starter flow names the catalog
  rows it uses, so "what can a flow even do here" is answerable by example
  search ("find flows using approval steps") as well as by prose.

The end state this buys: an author — human or model — gets **the same page**.
One registry, rendered as a palette for humans and a prompt for the AI,
containing names, stories, connectors, undo classes, costs, and real
examples. The AI drafting story stops depending on a smart model and starts
depending on a complete manifest — which, unlike taste, we can actually test.

### 4.5 Memory and context: the flow's short-term mind and the system's long-term one

Shared memory is already a first-class plane — the kernel brokers
`memory.retain`/`memory.recall` with four scopes (`work:`, `project:`,
`team:`, `private:`), ACLs and a read law, a ranked `postgres-fts` reference
profile behind an `IMemoryProvider` port, candidate→entry promotion with
per-project retention. What it has never had is a **producer that accumulates
deliberately**: today memory gets written by harnesses mid-assignment and by
chat candidates. The flow plane makes context-building a *designed*
behavior — which is the difference between "the system can store notes" and
"the system gets better at the thing it keeps doing".

**Two memory horizons, one law between them:**

- **Short-term = the step log.** An instance's step outputs *are* its working
  memory (every step already records `evidence_ref` + effect ids). The
  mistake would be to build a parallel context store per instance — instead:
  **later steps read earlier step outputs via `select`/template refs, and
  nothing else.** Bounded by definition size, dies with the instance's
  retention, inspectable in the console checklist with zero new machinery.
- **Long-term = the broker, addressed by scope.** A step that wants memory to
  outlive the flow uses `recall` (advisory context, injected into a later
  step's inputs — never a branch condition, matching law: memory *informs*,
  policy *decides*) and `retain` (a sink step: writes an entry, no branch
  value, so it cannot make a flow fork on retrieval fuzziness).
- **The bridge is a step kind, not a subsystem: `promote`.** "Copy the useful
  residue of this instance's step log into a `project:` scope" — one call,
  with the digest/summary optionally produced by a `generate` step (a
  `ai.summarize` capability per §4.2, so a team can swap in their own
  consolidator). This is the *only* place the two horizons touch, which keeps
  the instance store from silently becoming a second memory product — the
  broker law ("kernel brokers, teams decide what's worth remembering") stays
  intact: the *flow author* decided, at design time, what gets promoted.

**Why flows are the best memory producer the system can have.** Today
retention is opportunistic (a harness happened to retain something). A flow
knows its own arc, so the author can place memory exactly where knowledge
forms:

- *Terminal-leg distillation:* every completed instance ends with a
  `generate → retain(project:)` pair — "what happened, what was learned"
  written by design, keyed to the flow + work, so a *second* run of
  `pr-review-loop` can `recall` how the last four resolved. That is the
  "build up context" property, expressed as data.
- *Cross-work continuity:* fork chains (review-fork) today carry context by
  re-hydrating envelopes; a flow can instead have the predecessor `retain`
  and the successor `recall` at `work:`-addressed-project scope — cheaper,
  and it survives the fork boundary the way humans actually hand work over.
- *Chat↔flow feedback:* a chat-decoded request starts a flow; the flow's
  terminal `retain` gives the *chat* plane something to recall next week
  ("we've escalated this vendor three times") — memory becomes the shared
  context layer between the system's two intelligence surfaces (chat decode
  and flow drafting), both consumers, flows as producer.
- *Drafting memory closes the loop:* the AI flow-author (§4.4 catalog prompt)
  recalls `project:`-scope entries of past instances of the same
  definition — "this flow's escalations mostly involve provider X" — making
  operator-AI conversations about a flow context-aware without any new
  store.

**Governance rides the existing law, unchanged:** recall/retain steps are
ACL'd by the caller's team/scope like any `memory.*` operation; candidates
and entries keep their retention homes (per-project `memory` config,
`MemoryRetentionService`); a flow cannot write a scope its work's team can't
address; `sharing.*` stays a team-side profile the kernel reads as fact. One
new audit-friendly nicety falls out for free: because retains are *steps*,
the console can show **which flow produced which memory** — provenance the
opportunistic producer never had (an entry's provenance now names a flow run
and a step, not just a session).

**And the honest limits:** the broker is still a broker. No auto-extraction,
no implicit consolidation, no "flows learn" mysticism — promotion is a
step someone authored (or an operator approved via the AI's `generate`
proposal). Retrieval remains ranked FTS by default; a team wanting agentic
memory (extraction, reflection, vector/`hindsight` profiles) opts in
team-side through the existing `IMemoryProvider` seam — from the flow's
point of view, a smarter memory product is... a capability, or a better
profile behind the same port. The step palette never grows a mind of its
own.

### 4.6 What the limits mean, plainly

Operators **orchestrate, they do not transform**. Predicates are
matcher-boring (good — policy-file proved that law is enough for
authorization, it's enough for triggers too). Reshaping payloads is where the
plane is weakest, hence the `select` step and the `generate`/capability
ladder; anything heavier is a capability. The failure boundary is deliberate:
*flows route, capabilities compute* — and after §4.2, capabilities include
the kernel's own mind.

## 5. The coverage law: everything the system does today must remain possible

**Law (user-stated, non-negotiable): the plane must be able to express every
behavior the kernel exhibits today.** Not "eventually migrate everything" —
*expressible*, so the plane is a superset of the status quo and "keep the
current way of working" is always an option, not a fallback out of necessity.
This turns a vague ambition into a checkable acceptance test:

**The parity ledger.** Every implicit-workflow site gets a written
`workflow-definition.v1` document — a gallery entry with a scenario walk that
matches the arm's existing tests. Three verdicts per site, each legitimate:

- **Expressible → gallery.** The definition exists, shadow-runs (§9), and may
  replace the arm (or not — both states prove the law).
- **Mechanism, not workflow → exempt, named.** Things that are substrate, not
  orchestration, are *by definition out of the plane's job*: the state guard,
  candidate selection and the advisory ladder (algorithmic ordering), the
  outbox/link delivery planes, fencing, DAG admission, notification cadence
  resolution, the approval digest law. The plane *runs on* these; Temporal
  itself doesn't "express" its own event loop either.
- **Not yet expressible → palette gap.** The definition draft is written
  *first*; whatever it needs (a step, a matcher, a correlation key) grows the
  closed palette *before* that behavior may migrate. No arm migrates into a
  definition that quietly can't say what the arm said.

Audit of today's implicit sites against that law:

| Today's behavior (C# site) | Verdict | Expression / exemption |
|---|---|---|
| Labeled issue → create work + routing intent (`ProviderEventStore` arms) | expressible | trigger: `provider.issue` where `labels` match; steps: `emit work.create`, `emit assign` — the gallery's `assign-rules` entry (also retires the `AssignRules` config seam, which is just a degenerate 2-step flow) |
| Terminal-work review → fork successor (`ForkTerminalAsync`) | expressible | trigger: `provider.review` where work terminal; step: `fork-work` + chat reply — `review-fork` entry |
| Manifest complete → PR/comment dispatch (`ResultIngress` + hardcoded `vcs.*` constants) | expressible | trigger: `assignment.completed`; `decide` (manifest field present?) → capability dispatch — `pr-on-manifest` entry; the hardcoded constants become definition data |
| Schedules create work + notify owner (`ScheduleSweeper`) | expressible | trigger: schedule; steps: `emit assign` + `escalate` — proves the schedule trigger *is* a flow trigger, one mechanism instead of two |
| Nudge law (`NudgeSweeper`: remind, escalate to triage) | expressible | flow with `timer` + `escalate` steps, per-flow policy knobs — and this is exactly where the orphaned `admin_config:escalation` resource finally gets read |
| Consent/approval expiry (`ApprovalSweeper`) | mechanism, exempt | the `approval` step *consumes* this law; expiry semantics stay in the approval ledger (single-use digest-pinned records are substrate) |
| Session-loss reconcile, workload reconcile arms (`SessionLossSweeper`, provisioning reconcilers) | mechanism, exempt | reconcile loops over link/provision state are infrastructure, comparable to lease reclaim; re-expressing them as flows would add no operator power and lose hysteresis detail |
| Chat decode propose→validate→apply (`ChatProcessor`/`DecodeSink`/`CommandApplier`) | expressible, but stays C# | the propose/confirm law is stricter than a flow step could enforce, and it's one codepath serving all of chat; it may *invoke* flows and appear as a `decide`-like primitive later — exempt with reason, not by omission |
| Communication decision (`CommunicationDecision`: reply/react/silent) | expressible as `decide` | when a team wants to configure it per project, a 3-branch `decide` step is that config — natural first "new" behavior written *as data only* |
| Triage block on no-capability (`RoutingSink.TriageOnce/Untriage`) | mechanism, exempt | it *is* the no-route law of the dispatch plane |
| Webhook ingress → provider event (`WebhookIngressEndpoints`) | mechanism | trigger *surface*, not sequence; flows start from what it emits |

Two consequences worth stating plainly:

1. **The plane never removes a seam, it only adds one.** Every existing
   config surface (`AssignRules`, schedules, templates, `admin_config`) keeps
   working; where a flow can *subsume* one (`AssignRules`), the subsuming
   definition ships enabled by default and importing old config auto-generates
   it — a user upgrading sees identical behavior even having opened the
   editor.
2. **Exemptions are the law's teeth, not its escape.** Each "mechanism"
   verdict names *what class of thing it is* (guard, dispatch, reconcile,
   digest), so "we couldn't express it" can never hide behind "it felt like
   substrate". The Temporal litmus test we hold ourselves to: Temporal users
   keep timers/fencing as SDK primitives too — a plane that must re-express
   its own event loop would be over-claiming.

This law also flips the adoption risk model of §9: the plane can never *lose*
a behavior on the way in, because an arm is only switched off when its ledger
entry's walk passes in shadow mode against the arm's own recorded effects.
The worst failure mode the design permits is "an arm stays C# longer than
planned."

## 6. Authoring: creating, storing, managing flows

### Storage & lifecycle

- Table `workflow_definitions`: one row per **(id, version)** — immutable once
  an instance references it (same immutability law as approval digests).
  Payload: canonical JSON + content digest; columns for `enabled`, `team`,
  `supersedes`, `source` (hand/ai/derived — see provenance below).
- Management rides the existing admin plane, not a new product surface:
  `GET/PUT /api/workflows/definitions` (+ `/validate`, `/toggle`,
  `/versions`), an `admin_config`-style registry entry so the console config
  layer picks it up for free, and audit rows for every state change — allow
  *and* deny, same ledger as routing decisions.
- **Validation is schema-first:** a definition may only reference envelope
  types in the registry, commands in `KnownCommands`, capability ids shaped
  per `common.v1.json` idToken, and matcher values that type-check against
  the referenced payload schemas. We validate wire traffic with these schemas
  already; a flow is one more consumer. Invalid drafts can never half-apply.
- **Activation law:** a definition edit only affects *new* instances; running
  instances pin (id, version). A "migrate" action may hand live instances to
  the new version step-wise; no silent mid-flight morphs.
- **Git-ability:** `GET /api/workflows/definitions/export` (directory of
  YAML-serialized files) + import. Teams that want PR-review for their flows
  get it without the kernel caring; the DB stays the runtime truth.

### Ways to create a flow (AI drafting is one of five)

1. **Palette editor (connect-the-dots).** The console form editor is driven
   *by the closed catalog*: a step-kind dropdown (the 9-step palette), and
   for every field, a picker populated from the live registry — event types
   from the schema registry, commands from `KnownCommands`, capability ids
   from *advertised manifest entries across connected fleets* (so operators
   only ever connect dots that actually exist right now), teams/projects
   from the directory. This is the same trick `ActionCatalog` uses for chat:
   the model/operator may only SELECT, never invent. A drag canvas can come
   later; a form graph is honest and 1:1 renderable.
2. **Gallery / starter pack.** The migration (§9) extracts existing arms into
   definitions — the kernel therefore *ships* with its first gallery
   (`review-fork`, `pr-on-manifest`, `assign-rules`, `nudge-escalation`
   expressed as data). Operators clone a gallery entry and edit it. This is
   the highest-leverage authoring path because it teaches the language by
   showing the system's own behavior.
3. **Observe-and-promote ("derive from reality").** For each implicit arm
   still in C#, the console renders a **read-only generated definition**
   (the arm's trigger/effects, reverse-described from the audit + timeline
   rows the arm already writes). "Fork to editable" produces a draft a human
   can tweak and enable — the migration tool, exposed as a feature. Same
   trick for a *running* work: "this Work has flowed through steps A→B→C —
   save this path as a flow template" (trace-mining from the timeline, which
   is already an event log).
4. **AI drafting.** Chat/console proposes a definition via new
   `ActionCatalog` entries (`POST /api/workflows/draft`, `POST …/validate`).
   The model writes YAML against the schema; the deterministic validator
   accepts or kills it; a human enables. Propose/confirm law unchanged — AI
   drafts, schema applies. The drafting prompt is *rendered from the
   semantic catalog* (§4.4) — names, stories, connectors, cost classes and
   real fixture envelopes, the same registry the human palette consumes, so
   the model can only connect dots that exist and are described.
5. **Natural-language *editing* as diff.** "make the retry wait 4 hours"
   → model emits a schema-validated JSON-Patch against the stored document →
   preview diff → approve. Cheaper and safer than regenerating whole files.

### Representation: text and picture

- **Canonical: JSON.** It's the protocol's native tongue (schemas, codegen,
  conformance fixtures, the validator all already exist), and it round-trips
  with the visual form without a parser invention.
- **Human face: YAML.** Serialize the same document; no separate YAML
  *language*, just the sugar view — one source of truth, two renderings.
- **Picture: a single static diagram, not a canvas app.** Flows are 5–15
  nodes; the console renders definition and live instance **with the same
  renderer** — a definition shows the graph grey; an instance overlays
  current node, done edges, wait state, timer remaining. Mermaid-SSR
  precedent already exists on the site; the admin SPA can do better with
  plain SVG. The point of sharing the renderer: *the map of the territory
  cannot lie*, because instance and definition literally draw the same
  graph.
- **Text view of a live instance** = the step log, rendered as a
  vertical checklist (done/current/pending), which is what
  `workflow_step_log` rows *are*. No second narration engine.

## 7. Mapping a flow onto what's actually happening

The design-time graph must be inspectable against running reality — "this
job is in stage 3 of *that* flow".

**Instance state (two tables, boring on purpose):**

```
workflow_instances:  id, def_id+version, team/project, trigger_event_id,
                     status(running|waiting|completed|failed|cancelled),
                     current_step, deadline_at, work_ids[], started/updated
workflow_step_log:   instance_id, seq, step_id, status,
                     effect_command_id, evidence_ref, timestamps
```

**Correlation is by reference, not by ownership.** The law: **works remain
the primary durable aggregate; an instance never *is* a work — it points at
works.** A step that creates a work records the work id in its step log
(works already get an `workflow_instance_id` nullable column, mirroring the
`work_links` precedent for forks). Consequences:

- **Console Work view** gains one strip: `in flow "PR review loop" v3 ·
  step 3/5 "await build" · waiting since 14m` — a join, not a new model.
- **Console Flow view** is the operator-console-next queue idea with a
  second axis: instances list (status/age/flow/team), click → definition
  graph with the live node highlighted + step checklist + the works it
  touches. Instance ↔ Work navigation is bidirectional via the recorded
  refs.
- **Timeline:** flow steps emit existing timeline kinds where one already
  fits (assign, approval, progress) plus one new `workflow.stage` kind — so
  every existing audit/timeline surface (including the planned audit view)
  picks up flows for free, and a flow that only did timeline-visible things
  is reconstructable from the audit ledger alone.
- **Operator actions per instance:** pause (stop advancing, lets in-flight
  assignments finish naturally), cancel (cancel only the works it created
  that are still non-terminal — it may not touch what it didn't create),
  skip-step and retry-step (both = append a compensating log row + advance;
  deterministic ids make them safe), re-drive-after-fix (rides the
  `replay:<id>` dead-letter precedent). Every action is a fenced, audited
  admin command.
- **Failure surface:** a step that can't emit lands in the *same*
  `dead_letters` law as any outbox row — operators learn one failure story,
  not two.
- **OTel:** instance id as a span link across hops (consistent with the
  durable-queue trace law in the OTel idea) — flows become visible in the
  dashboard before they're visible in the console.
- **Version skew is visible, not silent:** an instance on an old version
  shows `v2 (v4 current)` with the migrate action; an archived definition
  still renders for its instances.

**Determinism/testing:** the plane inherits the scenario-walk discipline —
`SweepOnceAsync`-style driven clock, waits observed via timeline/inbox rows,
admin sweep triggers. A definition is testable the way a walk script is:
seed event → drive sweeps → assert step log. The gallery ships with its
walks.

## 8. What "hidden" means here, concretely

- End users: chat, approvals, works, timeline — byte-identical. A flow step
  that posts to chat is *a post*, not "a workflow ran".
- Operators: the console's queue/flow views + definition editor; nothing to
  install, nothing to reach beside the Facilitator.
- The only interaction layer is the Facilitator: the definition API, the
  admin console, the chat plane, and exported YAML are all its surfaces.
  There is no second UI, port, or service to firewall.

## 9. Migration (strangler, each step shippable alone)

Spec law applies verbatim (§29.3): preserve causation, assignment
correlation, approvals, replay semantics; **shadow/reconciliation run before
switching a coordinator.**

1. **Skeleton:** two instance tables + `WorkflowSink` + definition store,
   zero extracted arms — plane exists, nothing routed through it.
2. **Review-fork** (`ForkTerminalAsync`): first real definition; run
   *shadow-mode* (plane logs what it would do; arm still acts); compare a
   week of reconciliation; then swap the arm to the gallery definition.
3. **PR-on-manifest** (`ResultIngress` + `PrRequestCapability`) — kills the
   two hardcoded capability constants.
4. **`AssignRules` → triggers** — first user-visible declarative win; label
   routing becomes a definition every provider ships enabled.
5. New behaviors become console edits. Sweeper consolidation (nudge →
   timer-step) is optional later work, not a prerequisite.

## 10. Appendix: engine landscape (verified 2026-11-13; permissive + active)

| Engine | License | Status | Ruled out because |
|---|---|---|---|
| Temporal | MIT | ✅ daily | own orchestration server = beside-the-product state plane; violates Postgres-only footprint (spec: Temporal is an *optional adapter*, and "don't make a team install Temporal for one devcontainer") |
| Kestra | Apache-2.0 | ✅ very active | Java control-plane platform; YAML data orchestration beside the product |
| Hatchet | MIT | ✅ active | no .NET worker SDK — the kernel can't host workflow code |
| Inngest | SSPLv1 | ✅ | not permissive |
| Restate | BSL 1.1 | ✅ | source-available, not permissive (architecturally lovely, sadly disqualified) |
| DBOS Transact | MIT | ✅ very active | **right architecture** (in-process, your Postgres) but .NET SDK is a community alpha (5 releases, stalled since May) |
| Elsa 3.x | MIT (.NET Foundation) | ✅ very active | only credible buy; embeddable — but brings a parallel instance lifecycle fighting `WorkState`, re-implements our idempotency/audit laws as middleware, JSON-graph authoring is verbose; a vendor-major-upgrade risk inside the kernel core |
| workflow-core | MIT | 🟡 slow | subcedes nothing we don't have |
| CNCF Serverless Workflow | spec | ✅ | not an engine; a donor for step/trigger vocabulary if we ever need interop |

**Re-evaluation triggers:** DBOS .NET ≥1.0 → re-run build-vs-buy *for the
runtime loop only* (their checkpoint loop could replace the hand-rolled
advance, definition layer stays ours). Definition language outgrows the
9-step palette (loops/subflows/>20 kinds) → revisit Elsa behind the
`IWorkCoordinator` port rather than growing a Turing turtle. Arms keep
growing (ProviderEventStore past ~100 KB) → accelerate §9 steps 3–4. A real
multi-resource compensation saga appears → build `compensate` first.

## 11. Risks, honestly

- **Micro-language rot.** Bound it: closed step palette, matcher law only,
  no scripting, size caps, schema validation, deny-by-default commands.
  Anything that smells like general programming is a capability instead.
- **Engine bugs are kernel bugs.** Mitigated by blast radius: one tested
  interpreter + gallery walks replace N hand-rolled arms; shadow-mode
  migration; the plane is `enabled=false`-able at boot.
- **Two sources of truth creep.** Only the instance tables may say where a
  flow is; works stay primary; console joins, never merges.
- **Console scope.** The flow editor is a real UI investment; the
  observe-and-promote path keeps it honest (every editor surface starts as
  a rendering of behavior that already exists).
- **Catalog rot.** The semantic catalog (§4.4) is only trustworthy if it
  stays generated, not hand-maintained: every row derives from schema/codegen
  artifacts CI already checks (fixtures, `StateGuard`, codegen summaries), so
  a stale row fails a build the same way a stale wire type does. A hand-typed
  catalog would decay into worse than nothing — documentation that lies to
  the drafting AI.

## 12. Out of scope

- Gateway `policy-file` authorization law (authorization stays deterministic
  and team-side; the plane only orchestrates).
- User-visible workflows (flows are operator surface only, like the audit
  ledger).
- Cross-kernel flows (one Facilitator = one engine, one Postgres = one truth).
- ETL/data orchestration — Kestra beside the product when that day comes.
- REPL *for* the definition language — YAML/JSON + console editor cover it.
