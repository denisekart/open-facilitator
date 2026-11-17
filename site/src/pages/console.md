# The admin console

The console is the operator screen built into the kernel: one page served
by the kernel itself, at the kernel's own address, for the humans who run
it — answering "what is stuck, and who allowed it". No separate
deployment, no separate login: it talks to the same public API you do, so
anything visible here an agent can query too.

It opens on a sign-in gate: any account from your login provider with an
operator role gets in.

Every view is a URL. `#/works?state=blocked,review` opens the triage
queue pre-filtered, `#/work/<id>` reopens an inspector,
`#/audit?correlation=<id>` opens the exact decision trail a timeline
chip points at — paste it into chat, reload, share it; the console
lands where you left it, even through the sign-in round-trip.

## Search, and the life of one Work

Search covers works, projects, assignments, artifacts, and memories —
and a correlation id is a search key: paste a trace id from a timeline
chip or an audit row and the Work it belongs to surfaces. Every hit
opens something: projects and works into their inspectors, artifacts to
their metadata and brokered content, memories to their entry view.
Opening a Work gives you the full inspector: its assignment history with
fencing generations, its approvals, its result manifests, and the
complete event timeline — every state change with actor, timestamp, and
payload.

![Searching the console](../assets/console-explorer.png)
_Search an objective, and the hit opens the Work it belongs to —
artifacts, memories, and messages are the other search sections. The
menu switches surfaces; every one of them is a URL._

![The Work inspector](../assets/console-work.png)
_The same Work opened: a pending approval names its approver, the
timeline rows carry actor and timestamp, and the approve button is the
same audited write the API would take._

This is the “who did what, when, and who allowed it” view, and it is the
screen you live in once agents start touching production. Below: what
every menu entry opens, and what the console deliberately leaves
out.

## Every surface in the menu

The header menu (a drawer on narrow screens) opens, entry by entry:

- **Works** — the queue: everything, newest first, filterable by state;
  a team-scoped operator automatically sees only their teams' works.
- **Flows** — the workflow plane: every definition (live, shadow, or
  off), every run it performed, and an editor that draws the flow you
  are authoring. Open a run and the same picture wears its state — the
  leg that landed, the park it waits on, the failure you can retry —
  with pause, skip, retry, and cancel fenced by what the run's status
  legally allows. Authoring lives here too:
  [Authoring flows](/workflows).
- **Monitor** — running workflow instances and the fleet health strip:
  replica status, queue depth, link uptime. The honest rule the strip
  follows: the database lease is the only authority — a stale socket
  means offline, never “probably up”.
- **Deliveries** — the outbound ledger: what the kernel sent, what was
  acknowledged, what is a poison message. Replay one; try to replay it
  twice; the ledger refuses the second time.
- **Audit** — the decision spine, filterable by allow/deny, actor,
  action, and correlation id, with a CSV export.
- **Runtime** — which replica answers, the provisioning ledger, and
  manual sweeps: forcing a sweeper demands a change reason and audits
  the trigger, like every other human act on live state.
- **Team** — every team Gateway dialled in: endpoint id, session age,
  last acknowledged event, enrollment and identity-key revocation.
- **Config** — the live routing policy: stored operator intents, edited
  with versioned `If-Match` writes, each change audited with a reason.
  Includes `media-policy`: the media plane's federation law — size and
  count defaults, per-kind retention, per-team allow/forbid lists and
  the history-forwarding switches (absent row = the defaults above).
- **AiDeck** — a drawer, not a screen: a chat surface for operator
  identities. Ask “what's stuck?” and the answer shows the reads it
  made to get there — GETs the asking operator could run themselves,
  never more than that role can reach. Proposals carry a human summary,
  a folded body, an optional decision note, and a link to the audit row
  they produce; a proposal nobody confirms visibly expires.

## What the console deliberately is not

It is an inspector and a control panel, not a dashboard builder. There
are no custom widgets and no analytics pipeline: the summary strip,
counters, and timelines come from the same queries the API exposes. If
you want Grafana, the API and the database underneath will feed it —
both are yours.

## Where to go next

- See it live: the [guided tour](/docs/getting-started) boots a full
  environment on one laptop — kernel, console, a demo team, and works
  already executed — with Docker the only thing you install.
- Author what the Flows menu entry shows: [Authoring flows](/workflows).
- See what feeds the record: [Supported integrations](/integrations).
- Run your own kernel and open this console on it: [Get running](/guide/human).
