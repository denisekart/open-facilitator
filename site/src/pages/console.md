# The admin console

Every kernel serves a small operator console at its own root URL — no
separate deployment, no separate login. It talks to the same public API
you do, which means anything you can see here, an agent can query too.

It opens on a sign-in gate: any account from your OIDC provider with an
operator role gets in.

## Search, and the life of one Work

Search covers works, projects, assignments, artifacts, and memories.
Opening a Work gives you the full inspector: its assignment history with
fencing generations, its approvals, its result manifests, and the
complete event timeline — every state change with actor, timestamp, and
payload.

![Searching Works in the console](console-explorer.png)
_Search “analyze the reference”, and the kernel answers with matching
Works, a live fleet summary, and throughput metrics._

![The Work inspector](console-work.png)
_One Work's inspector: assignment completed on the team's Gateway,
result manifest available with digest-verified artifacts, the full
timeline underneath — `created`, routing, two artifact filings, a memory
candidate._

This is the “who did what, when, and who allowed it” view — it is why
teams keep the kernel around after the novelty of agents fades.

## Fleet views

Under the search box, four surfaces:

- **Links** — every team Gateway currently dialed in: endpoint id,
  session age, last acknowledged event. The honest rule the view
  follows: the database lease is the only authority — a stale socket
  means offline, never “probably up”.
- **Deliveries** — the outbound ledger: what the kernel sent, what was
  acknowledged, what is a poison message. Replay one; try to replay it
  twice; the ledger refuses the second time.
- **Config** — stored operator intents, edited with versioned
  `If-Match` writes, each change audited with a reason.
- **AiDeck** — a chat surface for operator identities: ask about works,
  trace a decision, all through the same governed API.

## What the console deliberately is not

It is an inspector and a control panel, not a dashboard builder. There
are no custom widgets and no analytics pipeline: the summary strip,
counters, and timelines come from the same queries the API exposes. If
you want Grafana, the API and the database underneath will feed it —
both are yours.

## See it yourself

The [guided tour](/docs/getting-started) boots a full environment on one
laptop — kernel, console, a demo team, and works already executed —
without you installing anything beyond Docker.
