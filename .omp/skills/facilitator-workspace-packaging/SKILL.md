---
name: facilitator-workspace-packaging
description: Dev Container Features (workspace-link, gateway, harness-adapter), Gateway modes and trust reporting, HarnessAdapter lifecycle contract, and team configuration layout for the Open Facilitator Framework.
---

> **Status:** the Link client runtime is IMPLEMENTED (Plan 04): `src/workspace-link` (`@ofac/workspace-link`) + the `ofac-linkd` bin — the headless supervisor entrypoint (spec S:409/§8.2): enroll → capabilities POST → connect → serve, SQLite state file for replay, echo/stub handler modes, `--transport signalr|http-poll`, exit codes 0/2/3/4. Plan 06's workspace-link Feature MUST start the Link from the entrypoint/supervisor via `ofac-linkd run` (or the SDK as a library), never rely on `postStartCommand`, and mount the enrollment secret / link token at runtime. Features, Gateway runtimes, and HarnessAdapters named below remain spec-proposed targets, not existing code. Update this skill the moment a slice lands so implemented reality replaces spec proposals.

# Workspace Packaging & Gateways

Normative source: `docs/specs/open_facilitator_framework_architecture.md` §3.2, §6, §8, §9, §18. The onboarding promise: a team adds **one Feature, a URL, and a short-lived enrollment credential** to an *existing* `devcontainer.json` — no harness rewrites, no model/vendor changes, no new AGENTS.md or CI conventions.

## When to use

- Building or changing anything under `features/`, `src/gateway/`, `src/workspace-link/`, `src/adapters/harnesses/`, `examples/`.
- Deciding deployment topology for a team, or writing docs about trust/isolation.
- Implementing a harness wrapper for Pi/OpenCode/Codex/Claude/custom/deterministic runtimes.

## Feature family (each = `devcontainer-feature.json` + `install.sh`)

| Feature | Installs | Use |
|---|---|---|
| `workspace-link` | TS Link runtime, CLI, event client, context/artifact helpers | Directly connected transient worker |
| `gateway` | Link library + gateway mode, local policy engine, optional child listener, connector/provisioner ports | Embedded in any devcontainer or standalone controller |
| `harness-adapter` | Auto-detection + adapter runner, language-neutral child protocol | Existing agent harnesses |
| `memory-client` (opt) | recall/retain tools + MCP bridge | Workspace-scoped memory |
| `artifact-client` (opt) | digest/manifest upload/download | Research/docs/datasets |

Install scripts: idempotent, no first-use privileged enrollment, **never embed secrets at image build**; credentials mounted at runtime. Headless images start the Link/Gateway from the entrypoint/supervisor — do not assume `postStartCommand` runs. Publish pinned SemVer majors, `linux/amd64`+`arm64`, checksums, SBOMs; Feature version ≠ protocol major.

## Gateway modes & trust truth

- **Embedded**: Feature beside the agent, normally 1 worker; privileged connectors allowed at team discretion; persistence SQLite/mounted volume. Co-located policy is *operational guidance*, not a security boundary against arbitrary in-container code — say so in docs/manifests/audit metadata.
- **Shared**: same package, own container/VM, 0..N children, independently enforceable credentials, always-on or event-triggered.
- **Direct Link**: no Gateway; workspace holds team-approved credentials and does the same policy checks itself.
- Report `trustMode` (`shared-runtime` | `separate-process-same-host` | `isolated-executor`) honestly; admin surfaces show it; a stopped Gateway stays logically registered with deliveries parked in Facilitator Postgres. Restart reconciles accepted assignments against its local SQLite ledger.

Privileged action sequence (never short-circuit): authenticated command → exact capability/resource/actor extraction → policy check → approval evidence → credential ceiling check → scoped connector execution → action ledger + event. Gateway rejection of a routable request is a normal result.

## HarnessAdapter contract (§8.3)

```ts
interface HarnessAdapter {
  start(request: StartRequest): Promise<{ nativeRunId: string }>;
  steer(nativeRunId: string, msg: SteeringMessage): Promise<void>;   // unsupported → report, never silently reinvent
  observe(nativeRunId: string): AsyncIterable<HarnessEvent>;
  status(nativeRunId: string): Promise<HarnessStatus>;               // enables reconcile + nudge
  interrupt(nativeRunId: string, reason: string): Promise<void>;     // cooperative; hard kill = provisioner
  respond(nativeRunId: string, input: InputResponse): Promise<void>; // answer the outstanding question only
}
```

Adapter owns **no AI configuration** — native harness keeps prompts/skills/tools/models/session. Persist `assignmentId/executionGeneration ↔ nativeRunId` in local SQLite where resume is supported; otherwise advertise `resume: restart-with-context`. Descriptor declares executable, invocation (`cli|stdio-jsonrpc|http|sdk`), lifecycle support, output schemas, tool *requests* (requests, not grants).

## Provisioning (§18)

`WorkspaceProvisioner.start(spec, assignmentId, operationId)/status/stop` — idempotent `operationId`. Flow: admission reserves capacity → launch with short-lived assignment-bound enrollment credential → worker connects outbound, verifies assignment, emits `ready` → only then execution goes active. Defaults (configurable): `minWorkers=0, maxWorkers=3, idleShutdownMinutes=15, startDeadlineMinutes=5, maxExecutionHours=4, maxStartRetries=2`. Never kill a silent long-running research run without declared heartbeat expectations. In the full reference the Docker socket belongs to `workspace-launcher` (allowlisted images/network/commands), **not** `team-gateway`.

## Team config layout

`.facilitator/{gateway.yaml,harness.yaml,capabilities.yaml,policies.yaml,memory.yaml}` beside the team's existing `agents/`, `AGENTS.md`, `src/`. Everything optional; a research-only worker may have no `src/` and no repo at all.
