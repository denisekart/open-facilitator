# The workflow plane

The workflow plane is where coordination rules stop being code and become
data: declarative flows, authored and versioned like configuration, executed
by a deterministic interpreter. It does not run work — it decides what
happens next. Every flow is a document validated against the same JSON
schema the kernel enforces, and every run is a database row, not a process.
For the vocabulary (events, work, teams, envelopes), start with
[overview.md](overview.md); for wire shapes and the event-type registry,
[protocol.md](protocol.md).

## Definitions are immutable versions

A flow definition is keyed by `(id, version)`. Saving a change creates a new
version; existing versions never mutate, and every running instance pins the
version it started with — editing a flow mid-run changes future runs, never
an in-flight one.

The operator surface is `/api/admin/workflows/*`. A specific version is
controlled with:

```
POST /api/admin/workflows/definitions/{id}/versions/{v}/toggle
```

carrying `enabled` and/or `shadow` booleans. That gives three effective
states:

- **active** (`enabled: true, shadow: false`) — instances run normally.
- **shadow** (`enabled: true, shadow: true`) — instances start and are
  recorded but never act; the shadow report shows what they *would* have
  done.
- **inactive** (`enabled: false`) — the definition is not subscribed; events
  skip it.

Toggling refreshes the trigger-subscription set immediately, so a freshly
enabled definition fires on the very next event. Mutations carry
`X-Change-Reason` and `If-Match` and leave audit rows, exactly like the rest
of the admin plane (see [security.md](security.md)).

## Triggers

Every definition carries exactly one trigger source — the schema enforces a
strict `oneOf` (`protocol/json-schema/workflow-definition.v1.json`,
`$defs/trigger`):

- **`on.event`** — a CloudEvents type from the protocol registry (pattern
  `org.facilitator.event.<type>.v<N>`; the mirrored event schema must
  exist). Optional `where` matchers are a map of dotted `data.*` paths to
  matcher values; all must match (AND).
- **`on.schedule`** — a cadence spec, verbatim law of the schedule plane:
  `interval:<n>m`, `daily:HH:mm`, or `cron:<5 fields>`, evaluated by the
  shared schedule-spec parser.
- **`invoked: {}`** — a callable-only marker: the flow starts exclusively
  from a parent's `invoke` step, never by events or a schedule.

## The step palette: a two-tier law

The JSON schema validates **fifteen** step kinds, but the v1 interpreter
executes **thirteen**:

`emit`, `wait`, `timer`, `approval`, `decide`, `select`, `fan-out`,
`fork-work`, `work-create`, `sign-over`, `escalate`, `invoke`, `signal`

(`WorkflowDefinitionValidator.ExecutableKinds`.)

The other two palette kinds, **`join`** and **`memory`**, validate
structurally — the editor accepts them and `POST .../definitions/validate`
passes them — but a definition using them **refuses to enable**: the
enable-time check returns 422 with error class `step-kind-not-supported`
(`EnableTimeErrors`). The interpreter does not grow speculatively; each kind
lands with its consumer. So the rule is simple: *schema says it parses,
enable says it runs, and only the thirteen above run.*

## Instance lifecycle

An instance is a row. These are the only states it holds, and every arrow is
an act the kernel itself records. Parks (approval, wait, timer) move a run
out of `running` and cost it nothing but storage; a `shadowed` definition's
instances never enter `running` at all.

```mermaid title="Workflow instance lifecycle: every arrow is a kernel act"
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#2b313b",
"primaryBorderColor": "#c96a2d", "primaryTextColor": "#f3f4f6",
"lineColor": "#9ca3af", "secondaryColor": "#3d332d"}}}%%
stateDiagram-v2
    [*] --> running: triggered (event, schedule, invoke)
    [*] --> shadowed: triggered by a shadow definition
    shadowed --> shadowed: every leg only recorded
    running --> waiting: park (approval, wait, timer)
    waiting --> running: woken (decision, match, deadline)
    running --> completed: end reached
    running --> failed: leg failed unhandled
    running --> cancelled: operator cancel
    running --> paused: operator pause
    paused --> running: operator resume
    completed --> [*]
    failed --> [*]
    cancelled --> [*]
```

## Durability: parks are rows, not threads

Waiting never costs a thread. A `wait`, `approval`, or `timer` step ends the
current execution turn by writing a park row — with a deadline when the step
has one — and the runner returns. A wake (an approval decision, a matching
event, the sweeper's deadline pass) finds the row, flips the park with a
conditional update so first-flip-wins against every race, and advances the
instance from where it stopped. A process restart, a replica failover, or a
three-week approval changes nothing: the state of every parked run lives in
the database, and the sweeper closes timed-out parks on whichever replica
holds the advisory lock that tick.

## Operator controls

All under `/api/admin/workflows` (same authorization as the admin plane):

| Operation | Endpoint |
|-----------|----------|
| Cancel / pause / resume an instance | `POST /instances/{id}/cancel` · `/pause` · `/resume` |
| Retry or skip a failed step | `POST /instances/{id}/retry-step` · `/skip-step` |
| Step log of a run | `GET /instances/{id}/log` |
| What shadow runs would have done | `GET /shadow/report` (optional `?since=`) |
| Export / import definitions (YAML) | `GET /definitions/export` · `POST /definitions/import` |
| Trigger types, commands, capabilities, callable flows, teams | `GET /registry` |
| The editor grammar (the validator's own schema) | `GET /schema` |

Import never overwrites: a file whose `(id, version)` collides with a stored
row is skipped — the same law the gallery seeder follows.

## The shipped gallery

The kernel ships eight built-in definitions in
`src/facilitator/Kernel/Workflows/Gallery/`, seeded at boot as
`source='gallery'`, `enabled=false` rows — seeding only fills missing rows
and never overwrites an operator edit, so shipping a gallery entry changes
no behavior until someone toggles it.

| Definition | Purpose |
|------------|---------|
| `agentic-research` | Composition entry point: a mention asks for research, the flow `invoke`s the callable `ensure-capacity` sub-flow and waits for its output, then assigns the research capability on the capacity that landed. |
| `assign-rules` | Template of every derived `assign-rule-<provider>-<n>` rule: a labeled trigger issue arrives unbound — create work with the rule's capability and ack the thread. |
| `ensure-capacity` | Callable-only sub-flow: assign harness spin-up, wait for completion, publish the endpoint the harness landed on; its completion hand-off resumes the awaiting parent. |
| `nudge-escalation` | The patience curve as data: after a waiting assignment goes quiet, wait, nudge once, wait again, escalate to the owner (`timer` + `escalate` only). |
| `pr-on-manifest` | Work completes carrying a result manifest naming a PR request: open the change request and fan out per-comment legs; no request, settle silently. Deterministic, no model consulted. |
| `review-fork` | A review arrives on already-terminal work: `fork-work` opens a successor, links it, repoints the binding, and tells the channel — one indivisible effect. |
| `router-ingress` | The globally-hosted bot's inbound leg: a guard chain resolves a router-team mention's channel to its one owning team and signs the frozen artifact over before work is dispatched there. |
| `team-home` | The counterpart of `router-ingress` on the owning team: once content is signed over, the team's own flows act on it with no router leg at all. |

## Where to go next

Event types, envelope shapes, and the registry every `on.event` trigger
must agree with: [protocol.md](protocol.md). The objects flows act on and
the laws they obey: [overview.md](overview.md). Authorization, audit, and
the change-reason discipline every mutation here carries:
[security.md](security.md). The interactive editor for all of this is the
Flows section of the embedded console (site route `/#/workflows`).
