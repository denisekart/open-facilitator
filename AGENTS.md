# Project Context (Open Facilitator Framework)

## What we are building

**Open Facilitator Framework** — portable, pluggable, multi-workspace orchestration of autonomous, assisted, and deterministic workloads across independently operated (optionally transient) execution environments. One persistent **Facilitator** (durable coordination kernel: ingress, routing, work breakdown, human interaction, governed memory, artifact registry, Link hub, admin SPA) + team-owned **Gateways** and **Workspaces** that connect *outbound* and keep execution authority (SCM/issue/deploy credentials stay team-side). Harnesses (Pi/OpenCode/Codex/custom/deterministic) stay authoritative for their own agent behavior; we wrap them with small adapters.

**Status: greenfield.** Nothing is implemented yet — `docs/specs/` is the single source of truth. The repo is currently just specs + this omp workspace layer. Do not assume any module, schema, endpoint, table, or CLI named in the specs exists until it is built.

## Specifications (read before designing or reviewing anything)

- `docs/specs/open_facilitator_framework_architecture.md` — normative architecture: domain model (§4), CloudEvents/Link wire contract (§5–6), scaling & coordination (§7), packaging/Features (§8), Gateways & policy (§9, §12), identity (§11), communication adapters (§13), memory (§14), artifacts/results (§15), data model (§19), reference Compose (§21–22), slices & conformance (§26).
- `docs/specs/open-facilitator-appendix.md` — normative clarifications: trustMode, actorEvidenceRef validation, RFC 8785 approval hashing, outbox/inbox patterns, memory ACLs, conformance must-haves.

## Stack

- **Facilitator:** .NET 10 / ASP.NET Core (SignalR hub, EF Core + PostgreSQL 17, embedded single-page admin UI, OTLP). Reference infra: Keycloak, SeaweedFS, standalone Aspire dashboard.
- **Gateway / Workspace Link / harness adapters / Features:** TypeScript on Node.js (npm packages + Dev Container Features + OCI images).
- **Admin SPA frontend:** Svelte 5 + Tailwind + shad/svelte (matches the `svelte` profile).
- **Reference environment:** Docker Compose, slim + full profiles (Mattermost, Forgejo, Kafka KRaft, Ollama); everything pinned in `versions.env`.
- **Protocols:** CloudEvents 1.0, JSON Schema, OpenAPI 3.1, OIDC/OAuth2 (RFC 6901 claim pointers), OTLP; Dev Container Features spec; optional seams for A2A, Kafka, Temporal/Orleans, pgvector, Hindsight.

`.omp/config.yml` declares `stack: dotnet-aspire-svelte` (the closest matching scaffold preset for the Facilitator + admin SPA). This is **not** a Zephyr/embedded project; ignore zephyr/zmk user-level skills unless working on an embedded worker harness.

## Active omp Profiles
- dotnet-aspire
- svelte

## Profiles Location
User-level core skills and agents are active from ~/.omp/agent/skills/ and ~/.omp/agent/agents/ (baked into the base image).
Project-level overrides can be placed in .omp/skills/ and .omp/agents/.

## Native Workflow
- **Delegation**: Use the `task` tool to delegate work to specialized agents (e.g., `task(agent="backend-expert", task="...")`).
- **Memory**: Use the `recall` tool to check for prior context, and `retain` to save new project-wide learnings (`reflect` for synthesized answers across many memories).
- **Handoff**: Use `/continue` to save state and resume a fresh session when context becomes too heavy.
- **Plan Mode**: Use the native `--plan` flag (or `Alt+Shift+P` in-session). The agent submits plans via the `xd://propose` approval dialog; approved plan files live in `.omp/plans/`. The `plan-guidance` skill encodes the plan-file contract.
- **HTTP**: NEVER inline curl/wget/fetch in bash; use the `fetch` MCP tool or `ctx_fetch_and_index` (the bash guard blocks inline HTTP).

## Project Skills (.omp/skills — augment, keep current)

| Skill | Covers |
|---|---|
| `facilitator-architecture` | Ownership law, state machines, Postgres-first coordination, extension seams, design gate |
| `facilitator-protocol` | CloudEvents envelope, message classes, receipts/idempotency/fencing, handshake, HTTP fallback |
| `facilitator-workspace-packaging` | Dev Container Features, Gateway modes/trust, HarnessAdapter contract, provisioning |
| `facilitator-reference-env` | slim/full Compose, versions.env, referencectl loop, hostnames/ports, debugging hooks |

**Maintenance rule (project policy):** every skill in `.omp/skills/` starts as a spec-derived projection of unbuilt code. Whenever an implementation slice lands (schema, endpoint, table, CLI, Feature, adapter, compose service), update the corresponding skill in the same change so it describes implemented reality — including newly implemented items that had no prior skill coverage.

## Orchestration (main agent = orchestrator, not worker)
- The main agent plans, delegates, and verifies. It may apply trivial single-file edits and single-command checks inline, but delegates anything multi-file or multi-step.
- Delegate independent work to **isolated** subagents via the `task` tool; `task` is fire-and-forget. Independent items are automatically backgrounded (`forceTopLevelAsync: true`).
- Each subagent runs in its own context — results are summarized back, so the orchestrator's context stays lean.
- Route by work type: research (incl. external/docs/API) → `scout` + `web_search`, codebase research → `scout`, code review → `reviewer`, Facilitator/.NET → `backend-expert`, admin SPA/UI → `frontend-expert`, Aspire/OTLP → `dotnet-aspire`, tests/QA + conformance suite → `quality-assurance`, protocol/architecture decisions → `oracle`, docs → `documentation-specialist`.
- TypeScript Gateway/Link/Features have no dedicated user-level agent yet — route to `task`/general agents referencing `facilitator-protocol` + `facilitator-workspace-packaging` skills, or add a project-level agent under `.omp/agents/` when this area gets busy.

## Repository layout (spec §22.1 — **repo root IS the `framework/` tree**; no `framework/` subdir)

```text
├── src/
│   ├── facilitator/              # .NET: HTTP, SignalR Link hub, event loop, admin SPA (wwwroot embed, Plan 12)
│   ├── gateway/                  # TypeScript runtime, connectors, provisioner ports
│   ├── workspace-link/           # TypeScript Link runtime + SDK
│   └── adapters/                 # mattermost / forgejo / kafka + harnesses/
├── protocol/                     # cloudevents-schemas, json-schema, openapi, conformance-fixtures
├── features/                     # gateway, workspace-link, harness-adapter (Dev Container Features)
├── examples/                     # slim/deterministic-research-worker, full/coding-worker, full/research-worker
├── reference/                    # compose.slim.yaml, compose.full.yaml, versions.env, keycloak realm, seeds, checks
├── docs/                         # getting-started, protocol, security, advanced, contributing (+ specs/ = normative source of truth)
└── Makefile
```

Conformity gate for anything new: spec §25.3 design-gate questions (see `facilitator-architecture` skill). Delivery is vertical slices (spec §26): transactional kernel → workspace link → gateway/adapters → identity/policy → artifacts/memory → slim reference → provider full → multi-work → replicas → hardening.

## MCP servers
- User-level (image): `fetch`, `docker`, `context-mode` (+ disabled `git`, `time`, `puppeteer`).
- Project-level (`.omp/mcp.json`): `playwright` (admin SPA / reference-services E2E, headless Chromium) and `postgres` (disabled until `referencectl up slim` seeds the Facilitator DB).

## Knowledge base
- This workspace has a knowledge base: KB name = repo directory name, store `~/.omp/knowledge/`. On questions about project files/code not open in context, search the KB (`knowledge_search`) before re-reading files. After meaningful edits to indexed content or after new major docs, run `knowledge_update` on the KB.
- Use `knowledge_add` once at session start if `knowledge_show` has no KB for this repo. Never index `~`, `node_modules`, `bin/`, `obj/`, `dist/` (extension defaults already exclude them).

## Project-Scoped Artifacts (always under the project .omp/, never the user home)
- Plans   → .omp/plans/<slug>.md
- Skills  → .omp/skills/
- When producing a plan, write it under the project .omp/ (never the session-local local:// root or the user home ~/.omp/agent) — it must live with the repo so it persists and is reviewable.

## Scaffolding
- Run `/scaffold` to generate the recommended project structure for the current stack — but only once implementation starts; keep the repo spec-only until then.

## Self-improvement
- If this file or any `.omp/` file references a tool or agent that is not available in this image, delegate one repair pass: `task(agent="repo-doctor", task="audit and repair this repo's .omp/ layer and AGENTS.md")`. Repeat whenever `sync-omp-defaults.sh` or an image update lands.
- To collect this instance's harness experience: `task(agent="harness-retro", task="7-day retro")` — run after significant field sessions or roughly weekly; it prints a pasteable findings document. `/rsi-export [days]` gives the raw telemetry skeleton without an agent run.
- Intake: when a HARNESS FINDINGS document is pasted, delegate it verbatim to `task(agent="harness-retro", ...)` — it checks the ledger, implements valid `[upstream]` proposals in `build/`, and records dispositions. `/rsi-intake <file>` runs the mechanical validate/dedup pass first.
- All skill changes update existing `SKILL.md` files in place; new skills only when ≥2 distinct findings cannot be mapped into any existing skill.
- Full loop guide: `docs/harness-self-improvement.md`.
