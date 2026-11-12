# Operator console: the gaps a real operator hits on day one

## Why

The console reskin made every existing surface legible, but the surface
itself still only answers "search for a thing, inspect a work". An operator
sitting with a live kernel immediately wants to *browse the queue, follow a
trace, and act* — and three of those paths dead-end today. This idea lists
the gaps, split by what the kernel **already serves** (pure UI work) versus
what needs a small read-model on the kernel side.

## Gap 1 — There is no queue: you can only find works you already know about

The health strip says `awaiting a human: 3 pending`, but nothing lets you
*see* those three works. Search needs a query string; there is no
"everything, newest first, filtered by state".

- Kernel today: `GET /api/admin/summary` gives state counts; search requires
  a non-empty query (`search-query-required`).
- Shape: a **Works view** (list, default filter `awaitingapproval` +
  `inprogress`, sortable by age) backed by a small
  `GET /api/admin/works?state=&limit=` read-model next to the existing
  `AdminSearchStore`. The health strip's "awaiting a human" chip becomes a
  link that opens exactly that filter.
- Payoff: turns the console from lookup tool into triage tool. This is the
  backbone feature of an operator console.

## Gap 2 — The audit trail exists server-side but has no surface

`GET /api/admin/audit` (+ `/audit/export`) and the "audit denies" sparkline
exist, but an operator can never answer "who did what, when, and what was
refused?" — the deny count is a number with no way to expand it.

- Kernel today: the endpoints and the durable audit ledger already exist.
- Shape: an **Audit view**: filterable table (actor, action, verdict,
  time-bucketed, relative time like everything else), plus a download
  button on `/audit/export` (NDJSON). Deny chips in the health strip link
  to `verdict=deny`.
- Pure UI work.

## Gap 3 — Correlation ids go nowhere

The inspector's timeline shows a copy-friendly trace chip, but pasting that
id into search dead-ends: the search store ILIKes text columns and, for a
guid-shaped query, equality-matches *message id* columns only — never
`work_events.correlation_id`. The one cross-cutting join key the kernel
guarantees is inert in the UI.

- Shape: either make search match `correlationId` exactly (kernel-side,
  cheap: one predicate beside the existing guid-equality arm in
  `AdminReadStores.SearchAsync`), or make the trace chip a button that
  filters the audit view by that id. Prefer both.

## Gap 4 — Non-work search hits are dead ends

`openHit` only reacts for works. A `projects` hit can't open
`GET /api/admin/projects/{id}` (which exists); an `artifacts` hit can't
offer download bytes; `messages`/`memories` hits are display-only.

- Shape: a generic slide-over detail panel per hit kind, fed by existing
  endpoints; artifact metadata gains a content link (the kernel already
  brokers artifact bytes to principals with the read scope).

## Gap 5 — No deep links: console state lives only in memory

URL is always `/`. You cannot paste "look at this work" to a teammate, and
a reload loses the inspector, the open view, and filters.

- Shape: encode state in the hash — `#/work/<id>`, `#/links`,
  `#/works?state=awaitingapproval`. No router dependency; a tiny
  hash-parse derived state in App.svelte. Pairs with Gap 1.

## Gap 6 — Ops levers are invisible: replicas, provisioning, sweeps

Three admin endpoints have no UI at all: `GET /api/admin/replicas` (which
kernel replicas hold leases — HA debugging),
`GET /api/admin/provisioning` (launcher health), and
`POST /api/admin/sweeps/{name}` (manual sweep trigger, an operator action
that today requires curl).

- Shape: a small **Runtime view**: replica table, provisioning status, and
  sweep buttons (with the change-reason header discipline the replay flow
  already established). Mutations gated on `admin.mutate` like everything
  else.

## Gap 7 — AiDeck: from "a chat that files actions" to a real operator assistant

The chat plane exists (sessions, propose-then-confirm action cards, slash
verbs, honest fallback, one-tx audit) and its safety laws are right. What
it lacks is everything *around* a good conversational experience. Ordered
cheap → structural:

**The governing rule for everything in this gap: the chat has no privileges
of its own.** AiDeck is an operator surface, and the operator behind it —
system (`kernel-operator`/`framework-admin`) or team (`team-operator`) —
must be able to reach *exactly* what their role already reaches through
the API: any information they can read, any action they can otherwise
execute. Concretely:

- The decoder's world (prompt context + read-tool loop + action catalog)
  is filtered **by the requesting principal's effective access**, not by a
  chat-specific allow-list beyond it. A team operator's "what's stuck?"
  sees their team's queue; the same words from a system operator see
  everything. The team dimension already exists on works, assignments,
  artifacts, and the audit rows — the chat must inherit it, never flatten
  it or exceed it.
- Reads inside a turn execute as the operator's own principal through the
  same gates as a direct API call (this is already the law for the confirm
  path; 7c extends it to the read side).
- The catalog is the *union* reachable by operators, and each turn carries
  only the entries that principal could actually invoke — the model cannot
  offer a team operator a kernel-wide sweep, because the principal cannot.
- Today's single `ai-operator` gate becomes an **entry gate only**: it
  decides who may open a chat, never what a turn may contain. Role scope
  does the latter. (Team-scoped chat may also simply reuse
  `team-operator`; worth deciding whether `ai-operator` stays a distinct
  role or becomes an alias — the entry-gate question is policy-file
  material either way.)
- This makes the degraded-mode honesty role-aware too: the canned verb
  list is the verbs *this* principal can use, not the global set.

### 7a. Turn rendering (pure UI)

- **Basis badge per reply.** The kernel stores `basis` (slash / model /
  fallback / degraded-*) on every message but the API/UI drop it. Surface
  it as a tiny chip on each assistant bubble — an operator must be able to
  tell "the model answered" from "the gateway was down and this is the
  canned list" at a glance. One field on `ChatMessageView`.
- **Action cards speak human.** Cards currently show
  `POST /api/works/{id}/steer (json)`. Render the catalog `Summary`
  instead ("Add a steering directive to a Work"), method+path demoted to a
  mono subtitle, and pretty-print `body` collapsed behind a disclosure
  (the raw preview exists; make it fold at ~5 lines).
- **Diff-style confirm.** For writes, the card should show *what changes*
  (field: old → new where the catalog carries a BodyHint), not just the
  body blob — same vocabulary as the config view's conflict display.
- **Pending-state discipline.** After confirm, the card must move to a
  terminal state even on executor failure (the executor already records
  `failed`); today a reopened session replays rows, so wire the existing
  `status` through the same badge vocabulary as everything else, and
  show the failure detail inline (it is already on the row).
- **Session hygiene**: rename/delete session (the title is seeded from the
  first turn; operators want to clean up); unread/updated marker when
  another tab confirms an action (poll exists for actions — extend to the
  session list); auto-scroll pin with a "jump to latest" affordance once
  the operator scrolls up.
- **Quick verbs**: a slash-verb palette (⌘K-style or `/`-triggered
  dropdown) sourced from the registry **filtered to this principal**
  (`GET /api/chat/verbs` or embedded in preferences), so slash commands
  are discoverable instead of memorized; the degraded-turn canned list
  already knows the verbs — make those clickable, same filter.

### 7b. Approval ergonomics (small kernel + UI)

Confirm/cancel today is binary per action. Operators deciding on a
proposal want the reason trail:

- **Decision note on confirm** (optional text, stored on the action row +
  audit, same law as approvals: durable, exactly-once). The approvals
  table already models comment-on-decision; mirror it.
- **Expiry honesty**: pending actions live under the gauge sweeper; the
  card should count down ("expires in 4 min — it will be swept, not
  silently kept"), because a silently-expired proposal is worse than a
  refused one.
- **Per-action confirm for reads, batch for writes**: "confirm all" is
  right for a batch of GETs, dangerous for five transitions. Group card
  copy should say what *kind* of effect it runs (read / write /
  state-change) — the catalog method is enough to label it.
- **Link to the audit row**: every executed action gets an audit entry;
  the card should deep-link it (needs Gap 2's audit view to land first).

### 7c. The read-side tool loop (the structural one, kernel)

Today the decoder may only *propose*; answering "what's stuck right now?"
means proposing a GET to the operator. Add a bounded tool phase to the
turn:

- The decoder may issue up to N (say 3) **allow-listed GETs from the
  catalog slice this principal could invoke**, executed kernel-side with
  the operator's own principal (no privilege invention — the same gate the
  confirm path enforces), bodies trimmed to a token budget, fed back
  before the final `{reply, actions[]}`. Team-scope filters apply inside
  the fetch itself: a team operator's `GET /api/works` returns their
  team's works, because it *is* their API call.
- Read-tool calls are rendered as a collapsed "looked at:
  GET /api/admin/summary" trail on the assistant bubble — the answer is
  trustworthy only because the operator can see what was fetched.
- Everything else stays: writes are still propose-then-confirm; a read
  failure degrades to the honest fallback, never a guess.
- This is the same fetch power Gap 1/2's read models expose — the queue
  and audit views make the *facts* cheap to fetch, and the tool loop makes
  the assistant able to *cite* them. Build 7a/7b first; 7c rides on the
  endpoints they need anyway.

## Explicitly not on this list

- Websocket/live push — the no-websockets polling design is a documented
  choice (15 s summary / 60 s metrics), keep it.
- Approval *decisions* stay on the chat plane (buttons in chat are the
  primary surface); the console inspector remains the fallback, not the
  driver.
- Memory promotion/review UI — belongs with the memory-plane work, not the
  console.
- Any dashboard/charting framework — the sparklines cover the need.

## Suggested iteration order

1. Gap 5 (hash state) first — cheap, and every later view hangs off it.
2. Gap 1 (works queue + kernel list endpoint) — the backbone.
3. Gap 2 (audit view) + Gap 3 (trace join) — same table component.
4. Gap 7a + 7b (chat rendering + approval ergonomics) — no new kernel
   surfaces beyond two fields (`basis`, decision note); makes the existing
   chat plane genuinely usable day-to-day.
5. Gap 4 (hit details) and Gap 6 (runtime view) — independent, either can go first.
6. Gap 7c (role-scoped read tool loop) — structural, but it rides on the
   role-filtered read models Gaps 1/2 build anyway; doing it later is what
   keeps it cheap.
