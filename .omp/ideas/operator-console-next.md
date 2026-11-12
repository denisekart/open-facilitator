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
4. Gap 4 (hit details) and Gap 6 (runtime view) — independent, either can go first.
