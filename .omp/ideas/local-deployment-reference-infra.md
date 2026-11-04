Let's develop this idea - the same approach as we did for the aws reference infra, but make it completely deployable "at home" Either using docker, barebones on a linux machine, or any option in between. Let's just develop the idea for now and write down our thoughts here.
Ask me questions, let's make this a discussion. 

# Design (settled — basis for the real plan)

## Shape and placement

- **A distributed home system, not a one-box demo.** Multiple machines
  at home (dev computers, an LLM server, a bunch of servers): the
  Facilitator kernel on one box, Gateways + workers on the others. The
  real multi-machine topology of the design, run for free on LAN-grade
  hardware — proving the outbound-link architecture with no cloud.
- **`examples/local/`** — sibling to `examples/aws`. Same fork-me
  posture. `reference/` stays the acceptance-required test harness
  (fixed ports, seeded demo, `referencectl verify`); it must NOT absorb
  deployment ergonomics. Share what's shareable: the built images and
  the env contract (`Storage__S3__*`, `OTEL_EXPORTER_OTLP_*`,
  `Identity__Oidc__*`), nothing else.

| | reference/ | examples/local |
|---|---|---|
|Answers|"does the system work?"|"how do I run this at home?"|
|Machines|one, fixed|several, operator-assigned|
|Names|hardcoded hostnames|Tailscale names + TLS (Caddy)|
|State|ephemeral volumes|persistent volumes + backup story|
|Gate|`referencectl verify`|`localctl smoke`, not acceptance law|

## Operating model

1. **Kernel box, deployed once**: kernel + Postgres + MinIO + Caddy +
   Aspire + `identity:` config. One command (`localctl up kernel`).
2. **Each PC, once each**: a *node bundle* = Gateway + `oci-launcher`
   (the existing `@ofac/provisioning-oci` launcher — it IS the
   imagined scaler: a link-plane endpoint executing the kernel's
   `provision.start/stop` against the local Docker socket, proven in
   `reference/`) + workspace images.
3. **Workers are never declared.** They're kernel decisions at
   runtime, executed by the node's launcher. Not in the roles file.
4. **Registration is manual per machine** (= the enrollment law,
   unchanged): admin mints a short-lived token in the admin UI (the
   Google-logged-in operator), `localctl enroll` on the box consumes
   it, re-enroll-on-401 is the rotation story.
5. **Permissions**: one human, one Google subject; workspace roles
   ride the identity, so the admin UI "just works" from any box.
   Everything after enrollment runs through the admin UI/chat.

## Roles file + `localctl`

- The spine, like the AWS example's `envs/*.json`. Two static unit
  kinds only: `kernel` + `nodes[]` (plus `images:`, domains, backup
  path, external-service endpoints).
- **Rendered, not hand-written**: a domain change must touch one file.
  `localctl render` emits per-box bundles (the primitive);
  `localctl up|enroll <box>` ssh-pushes (the verb) — the
  `deploy`-on-`synth` shape.
- Verbs: `render`, `up`, `enroll`, `status` (health probes per box +
  link-plane connection states), `smoke`, `logs`, `backup`, `upgrade`.
- Runtime: docker-compose-first (compose files + orchestrator script,
  the `localctl` sibling of `referencectl`/`deployctl` — orchestrator
  not runtime). No Ansible/cloud-init tier: the rendered compose file
  IS the fork-me declarative unit. (systemd/quadlet tier: out of
  first cut.)

## Kernel box stack

- **Caddy** = the home analog of the AWS ALB: the single door. One
  small hostname-keyed Caddyfile, `reverse_proxy` to kernel/gateway/
  MinIO console; only 443 opens toward the tailnet; webhooks ride the
  same site block.
- **Postgres: compose-bundled `postgres:17`** with a named volume for
  `PGDATA`; every stateful service (postgres, MinIO data, Aspire
  data-dir, Caddy config/certs) rides a named volume — the templates
  make ephemeral state impossible. External-pg escape hatch = the
  existing `ConnectionStrings__Facilitator` seam.
- **MinIO** for S3 (path-style endpoint seam already exercised by
  `reference/`: `Storage__S3__Endpoint=http://…:8333`).
- **Aspire dashboard standalone** for observability: one container,
  OTLP in from every box (`OTEL_EXPORTER_OTLP_*` env, no collector
  pipeline files = zero config), all three signals, `Resume`
  persistence mode with its SQLite DB on a named volume — survives
  restarts, and the hard retention caps (100k entries/signal) are the
  disk quota a home box needs. Honest caveat on record: Aspire labels
  this dev/short-term persistence, not a durable backend — the right
  trade for "last few weeks, zero config".

## Network + TLS

- **Tailscale (locked posture).** Every box gets a stable MagicDNS
  name (`box.tailnet-name.ts.net`): name resolution, NAT traversal,
  and remote access are the network's job — no /etc/hosts rendering,
  no LAN-DNS requirement, no port-forwarding. The example documents
  `*.example.com` as the placeholder domain for now.
- TLS: Caddy acme vs `tls internal` + `tailscale serve` is a
  one-line template switch — plan-time decision.
- Node boxes: outbound-only, OTLP pointed at the kernel box's
  dashboard over the tailnet; no per-node collector.

## External services (connection recipes, nothing deployed by the example)

- **Google** — identity/OIDC. Kernel binds `Identity__Oidc__*` (that's
  how the AWS stack points at Cognito), so it's config. Operator
  section: GCP project → OAuth consent screen (External) → OAuth
  client (Web) with `https://<kernel-host>/…` redirects → client
  id/secret into the env file.
- **Admin bootstrap — no kernel work needed.** The kernel's `identity:`
  role engine (role → OR-lists of AND-conditions on claim pointers,
  operation-gate table, DB-backed refresh) makes "I am the admin" a
  config rule: `identity:roles:admin` matching `/sub` equals your
  Google subject. The example documents how to find your `sub` (login
  once, read the token/identity endpoint) and where to paste it.
- **GitHub** — forge leg via the existing `@ofac/github` +
  `@ofac/github-connector` adapters, connected to GitHub as it is.
- **Mattermost** — same pattern via `@ofac/mattermost`: operator
  deploys it separately; config just points at it.
- Plan-time follow-up (flagged, not solved): webhook reachability for
  GitHub/Mattermost (Tailscale Funnel or equivalent) and
  webhook-vs-poll ingress posture.

## Images + backup

- **Digest-pinned** like the AWS example + `reference/versions.env`:
  every `images:` entry is a `name@sha256:…` URI; compose renders with
  digests only.
- **`localctl upgrade`** is the stay-current verb: re-resolve each
  tag to its current digest, record old→new (in-file or changelog —
  plan-time detail), re-render, roll each box.
- **Backup: single location, host folder OUTSIDE Docker** (path in the
  roles file, e.g. `/var/backups/facilitator/`): dated dumps — pg_dump
  + MinIO bucket sync + Aspire db + Caddy state — that survive
  `docker compose down -v` and stay plain files to rsync. Restore =
  documented steps. No replica story.

## The gate: what the example promises

- **`localctl smoke`** proves end-to-end on fresh hardware: enroll a
  second machine → create a Work (admin UI) → kernel dispatches to
  that machine's launcher → worker container runs there → result
  readable on the timeline → whole path visible in the Aspire
  dashboard. Until the LLM adapter exists the smoke worker is the
  deterministic example image: the claim is "the plumbing works on
  your hardware"; the LLM harness later changes one `images:` entry,
  not the story.
- **LLM server = just another node box.** The example ships NO model
  endpoint defaults — operator fills the LLM URL + API key. The LLM
  harness adapter is a named follow-up work item that hooks into this
  example; the deterministic worker is explicitly NOT the demo face.
- **"Day one" README section (in scope)**: after smoke passes, a short
  UI walkthrough — sign in with Google (find your `sub`, set the admin
  rule), create a team, point a workspace at your GitHub repo, launch
  a work, watch the timeline. Action-keyed prose, so UI churn costs
  doc edits only.
- Machine-prep section per box kind is a first-class artifact: node
  box = Docker Engine + socket access + outbound HTTPS to the kernel,
  nothing listening inbound; LLM box = same + "your model server
  answers from this box".

# Discussion log

Decisions were made in three rounds of discussion (shape/menu/auth →
operating model/Postgres/LLM/observability/Tailscale → backup/scope/
GitHub+Mattermost/pinning/smoke/chat-walkthrough). Every open question
closed: roles-file shape, admin bootstrap, enrollment seam, Postgres,
LLM-node posture, observability, network/TLS, backup, scope +
external services, image pinning + upgrade, smoke claim, day-one
walkthrough. Remaining plan-time details are inline above (ts.net cert
mode, webhook ingress, upgrade changelog form).

Only edit this file with the decisions we made along the way. I want this to be a layout that we can use later to make an actual plan.
