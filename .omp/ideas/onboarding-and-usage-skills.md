# Idea: operator/consumer skill pack (refined)

**Original intent (kept):** a self-sufficient set of AI-harness skills (SKILL.md +
resources) covering installing, operating, and developing against Open
Facilitator, for admins and teams. The repo landing page gets a clear
instruction set, and the repo gets a structure so an AI can "install" these
skills into a client's harness. The user of a skill has NO access to this
repository — the system is a black box. These skills are completely separate
from the repo-development skills in `.omp/skills/`.

---

## 1. As-built facts that shape the design (evidence)

- **Two clean API planes, already machine-describable.** Human plane: bearer
  OIDC on `/api/*`, RFC 9457 problem errors with type URIs
  `https://errors.openfacilitator.org/v1/<code>` (`.omp/skills/facilitator-architecture/SKILL.md`,
  "Kernel (as-built)"). Link plane: `/api/link/v1/*` link-token auth
  (`src/facilitator/Kernel/Link/LinkEndpoints.Enroll.cs`). There is an
  as-built OpenAPI for the link plane: `protocol/openapi/link-v1.yaml`.
- **Authorization is data, not folklore.** The gate table
  (`identity:authorization:operations` bindings: `work.write`,
  `approvals.respond`, `admin.read`, `admin.mutate`,
  `enrollment-tokens.manage`, … with `rolesAnyOf`) lives in
  `reference/config/facilitator.json` — it ships as a config file the
  operator actually deploys, so a skill can carry it verbatim as a resource.
- **The operator runbook already exists, in prose, in two places:**
  `examples/local/README.md` (bring-up order, enrollment hand-off, Google
  OIDC day-one, webhooks/Funnel, backup/restore, upgrade, day-one
  walkthrough) and `reference/README.md` (ports, token recipe, fail-closed
  startup, security profile). These are the natural seeds for the skill
  resources — but they cite repo-internal paths that must be stripped or
  replaced by bundled artifacts.
- **Concrete operator-visible quirks a skill must encode** (each is already
  documented in-repo, i.e. real support questions): no work-submit form in
  the SPA yet — teams POST `/api/works` (`examples/local/README.md`
  day-one step 4); issuer-alias law (`id.localhost` hosts entry,
  `examples/local/AGENTS.md`); minted token vs static secret law (gateway =
  minted, children/launcher = static, `examples/local/AGENTS.md`
  "Enrollment hand-off"); `PATCH /api/admin/config/{resource}` needs
  If-Match + `X-Change-Reason` (`src/facilitator/Kernel/Admin/AdminEndpoints.cs`);
  dead-letter replay is `POST /api/admin/dead-letters/{id}/replay`.
- **Distribution reality check:** every `@ofac/*` npm package is
  `"private": true` (gateway, workspace-link, adapters, protocol) — nothing
  consumer-installable exists on npm today. Consumers get the system via
  Docker Compose bundles / images (`examples/local`, `reference/`), not
  `npm install`. This constrains the "develop against it" skill: v1 must be
  HTTP-first (curl + OpenAPI), with the TS packages documented as
  source-repo-sourced, not registry-sourced.
- **Skill format precedent in-repo:** `.omp/skills/<name>/SKILL.md` with
  `name` + `description` frontmatter (see
  `.omp/skills/facilitator-architecture/SKILL.md`). The new pack should use
  the same format so it drops into Claude Code and omp alike.

## 2. Audiences (sharpened from "admins and teams")

Three personas, not two — "teams" splits in two, because the API surface
they touch differs by the gate table:

| Persona | Who | Touches | Gate roles |
|---|---|---|---|
| **Operator/Admin** | runs the kernel box + node boxes | compose bundles, `/api/admin/*`, SPA admin panels, IdP config, backups | `framework-admin`, `kernel-operator` |
| **Team member** | consumes the system day-to-day | `POST /api/works`, timeline, approvals, artifacts, memory, chat | `team-member` |
| **Workspace integrator** | connects a machine/harness | `POST /api/link/v1/enroll`, SignalR hub `/hubs/link` or HTTP fallback, `/deliveries` + receipts, capabilities, artifact upload | link token (gateway/child) |

The integrator is the persona the draft's "developing against it" actually
splits into: **using the team API** (team member) vs **building the outbound
leg** (integrator). Different credentials, different failure modes.

## 3. Skill inventory (proposal)

Five skills. One skill per persona-task, not one mega-skill: descriptions
are the trigger surface, and "install" vs "submit a work" vs "my gateway
won't enroll" are distinct triggers. Each is `<name>/SKILL.md` (≤150 lines,
decision tree + pointers) + `references/` + `assets/`.

| Skill | Purpose | Trigger (description gist) | Key resources |
|---|---|---|---|
| `ofac-install` | First-time bring-up of a deployment from a distribution bundle (compose files handed over by whoever rendered them): prereqs, secrets, IdP bootstrap, first admin, first enrollment token, smoke checklist | "install / set up / first boot of a Facilitator" | `references/bringup-runbook.md` (from `examples/local/README.md` §Bring-up, de-repo'd), `references/idp-bootstrap.md` (Keycloak→Google, claim-rule for `framework-admin`, how to find your `sub`), `references/env-vars.md` (kernel config keys, from `reference/config/facilitator.env` + fail-closed startup law) |
| `ofac-operate` | Day-2: status, config changes, enroll/revoke tokens, dead letters, backup/restore, upgrade, troubleshooting | "my Facilitator is down / slow / 403 / how do I upgrade" | `references/runbooks/*.md` (dead-letter-replay, enroll-token lifecycle incl. consume/revoke law, config-patch law If-Match + X-Change-Reason, backup-restore, upgrade+lockfile), `references/troubleshooting.md` (401 vs 403 vs 409 vs 422 → cause → fix table, incl. issuer-alias and clock-skew gotchas), `references/admin-api.md` (every `/api/admin/*` route + response shape, harvested from `AdminEndpoints.cs` at release time) |
| `ofac-team` | Submit and follow work, respond to approvals, fetch artifacts/manifests, use memory; SPA tour incl. its current gaps | "submit a work / approve / get my results / what happened to my task" | `references/submit-a-work.md` (curl recipe incl. token acquisition + `projectId` discovery via `/api/admin/search`), `references/timeline-reading.md` (event kinds → what they mean to the user), `references/approvals.md`, `references/memory.md`; `assets/openapi-team.yaml` |
| `ofac-connect` | Attach a machine: enroll (gateway vs child role law), link-token auth, SignalR vs HTTP long-poll fallback, deliveries/receipts, capabilities, artifact content push, heartbeat/lease semantics | "connect a gateway / enroll an endpoint / link plane" | `references/enroll-law.md` (minted-token lifecycle, static-secret break-glass, role law, 401 codes), `references/http-fallback.md` (from `docs/protocol.md` fallback table), `assets/openapi-link-v1.yaml` (verbatim `protocol/openapi/link-v1.yaml`), `assets/link-delivery.v1.json` |
| `ofac-reference` | Shared lookup: error-code table (all `https://errors.openfacilitator.org/v1/*` codes), CloudEvents envelope + type registry, gate/role table, versioning law | loaded by the others ("what does error X mean") | `references/errors.md`, `references/wire-types.md`, `references/gate-table.md` (verbatim `identity:authorization:operations` block) |

Tradeoff considered and rejected: a single `open-facilitator` skill with all
of the above — context bloat for every trigger, and a team member's harness
should never load enrollment-token revoke law. A 4-skill split (merging
`ofac-reference` into each) was rejected for the same duplication reason.

`ofac-reference` is a resource-only skill (no independent trigger beyond
"explain a Facilitator error/type"); if a harness doesn't support skill
cross-references, its files are duplicated-by-include at build time (see §5
generator).

## 4. Layout: in-repo home + installed shape

**In-repo: top-level `skills/`** (sibling of `protocol/`, matching the
README's "every directory carries a README" law). Rejected: under `docs/`
(docs is prose, these are shipped artifacts); under `.omp/` (dot-dir =
harness-internal, but this pack targets ALL harnesses).

```
skills/
├── AGENTS.md                 # signpost: these ship to consumers; never cite repo paths inside dist/
├── README.md                 # the landing-page target: what, for whom, how to install
├── manifest.json             # {version, commit, built, files:[{path,sha256}]} — the self-sufficiency contract
├── build.mjs                 # generator: hand-written sources + harvested refs → dist/
├── ofac-install/SKILL.md
├── ofac-install/references/… # hand-written, black-box prose (the authored work)
├── ofac-operate/…
├── ofac-team/…
├── ofac-connect/…
├── ofac-reference/…
└── dist/                     # built, gitignored: per-skill trees with harvested
                              # assets injected (openapi, gate table, error codes)
```

Split between **hand-written references** (runbooks — judgment content) and
**generated assets** (API/schemas/error/gate tables — harvested from
`protocol/openapi/link-v1.yaml`, `protocol/cloudevents-schemas/`,
`reference/config/facilitator.json`, and a route inventory of
`*Endpoints.cs`). `make skills` builds `dist/` + `skills-<ver>.tgz`;
`make skills-check` (alongside `make docs-check`) fails when a protocol or
gate-table change forgot to rebuild the pack.

**Installed shape** = the skill directory, copied whole:
`~/.claude/skills/<name>/`, `.omp`-equivalent, `.cursor/rules`-adjacent —
whatever the harness root is. Nothing in an installed file may mention a
repo path or assume `git`; the only external dependencies are the
deployment's own URLs.

## 5. Self-sufficiency law (the sharp version)

The draft's "assume this system is a black box" needs teeth, or the
runbooks rot into "go read examples/local/README.md in the repo":

1. **Closed world:** every fact a skill asserts is in the skill's own
   files. No relative links out of the skill dir; no "see the repo".
2. **Provenance, not pointers:** every generated asset records in
   `manifest.json` which repo file + commit it was harvested from. The
   *generator* knows the repo; the *skill* never does.
3. **Version pairing:** manifest carries the framework version
   (`v*` tag) the pack was built against; each SKILL.md frontmatter
   includes it. A v1 pack does not document target-design features that
   exist only in `docs/specs/` — the status-law of
   `.omp/skills/facilitator-architecture/SKILL.md` applies: document
   implemented reality, and where a gap is operator-relevant (no SPA
   work-submit form yet), document the gap itself.
4. **Bundle-relative commands only:** recipes run against
   `https://<kernelHost>` and a distribution-provided compose bundle, never
   `./referencectl` or `dotnet run --project src/facilitator` (those exist
   only for repo devs).

## 6. Install mechanism — options and recommendation

| Option | How | For | Against |
|---|---|---|---|
| **A. Release tarball + installer** | `skills-<ver>.tgz` on the GitHub release; `install.sh` (also runnable by the AI: `curl … | sh -s -- --harness claude`) detects harness roots (`~/.claude/skills`, `.omp` agent dir, `$SKILLS_DIR`) and unpacks + records `manifest.json` | works offline, no registry, AI-executable, zero new infra | manual URL for now (no published releases yet — pairs with the licensing/packaging gap) |
| B. npm package `@ofac/skills` | `npm i -g @ofac/skills && ofac-skills install` | familiar update path | all `@ofac/*` packages are `private:true` today — implies a whole-registry publishing decision (open question Q1); npm-as-asset-carrier is awkward for Claude-style dirs anyway (they're not modules) |
| C. `git clone`/sparse-checkout of the repo | clone, copy `skills/dist` | one mechanism | breaks the black-box posture (user suddenly holds the monorepo), and self-update tempts reading repo docs that are dev-facing |

**Recommendation: A**, with the directory format itself as the real API —
`cp -r ofac-operate ~/.claude/skills/` is a valid install for humans, and
`install.sh` is a convenience the AI can run and verify against
`manifest.json`. Update = re-run installer; it diffs the manifest and
reports changed skills. Revisit B only if/when the org goes public on npm.

## 7. Landing page change (repo-side)

`README.md` gains one short section, "Use it with your AI assistant", after
the demo entry point: 3-line copy-paste installer, the 5-skill table (one
line each: who it's for), and a pointer to `skills/README.md`. Add the
`skills/` row to the "Repository layout" tree. State the separation law
explicitly so future agents don't merge the two skill trees: *`.omp/skills/`
= repo-development rules; `skills/` = shipped operator/consumer artifacts.*

## 8. Acceptance criteria (for the eventual plan)

1. In a container with a harness but **no repo checkout**, install the
   pack; a cold agent given only "bring up my Facilitator bundle in
   `~/facilitator/`" follows `ofac-install` to a healthy `GET /health`.
2. Cold agent answers, citing only skill files: submit a work (correct
   route, body, token recipe, 201 + traceparent); interpret a 403 (gate
   table); mint + revoke an enrollment token; replay a dead letter.
3. `grep -rE 'src/facilitator|referencectl|examples/|docs/specs|reference/'
   skills/dist` finds nothing (closed-world check, in `make skills-check`).
4. `make skills-check` fails when `protocol/openapi/link-v1.yaml` or the
   gate table changes without a rebuild (drift gate, same posture as
   `make docs-check`).
5. Every command in every runbook was executed against a live
   `referencectl up slim` stack during authoring (the pack is proven, not
   transcribed-then-hoped).

## 9. Open questions (user decisions, not research items)

- **Q1 — Distribution identity:** is there appetite to un-privatise
  `@ofac/*` (npm publish) as part of this, or does v1 stay images+tarball?
  (Determines whether `ofac-connect` documents `npm i @ofac/gateway` or
  "use the bundle's node-runtime image" — today only the latter is true.)
- **Q2 — Harness targets:** Claude/omp `SKILL.md` only, or also emit
  AGENTS.md-style/Cursor rule shapes from the same sources? (Affects
  `build.mjs` scope; v1 can be SKILL.md-only since the format is
  open enough that other harnesses can ingest it.)
- **Q3 — Scope of `ofac-connect`:** does it cover writing a custom harness
  adapter (the `@ofac/adapter` SDK, `src/adapters/harnesses/`) or stop at
  the link plane + the shipped harnesses? The SDK is unpublished, so
  adapter-authoring docs risk documenting source code that moves weekly.
  Suggest v1 stops at the link plane.
- **Q4 — Provider recipes:** do GitHub/Mattermost webhook recipes ship in
  the pack (they're currently docs-only legs of `examples/local/README.md`)
  or wait until the adapters are less in-flux? Suggest: include the webhook
  sink facts (`POST /api/ingress/webhooks/{provider}`, signature-verified
  adapter-side) but mark provider setup as provider-owned.
- **Q5 — Which deployment is "the install" the skill teaches:** the
  `examples/local` compose bundle is the natural black-box artifact, but
  it is explicitly a *fork-me example, not a product*. Does the install
  skill teach "your distributor handed you a rendered bundle" (bundle-
  agnostic, my recommendation) or bless the local example as THE install
  path?
