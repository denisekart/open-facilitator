Red-team security assessment of the built application

Goal: an adversarial review of the **deployed** open-facilitator stack —
kernel, gateway, workspace-link, adapters, and the wires between them —
whose purpose is to falsify `docs/security.md`, not restate it. The repo has
a strong *designed* posture (trust/data-plane split, three identities,
enrollment + delegation evidence, artifact-store and memory-broker laws,
approval lifecycle) and essentially zero *verified* posture: no SAST, no
dependency audit, no secret scanning, no image scanning in CI. Round 1
proves each law holds against a live attacker; every confirmed gap gets
fixed and pinned by a regression test. This is a loop, not an event.

## Shape

- **Live target, full stack.** devcontainer compose shape: facilitator
  kernel + Postgres 17 + gateway + workspace-link + at least one harness
  adapter and one provider adapter. Attacks run against HTTP/WS endpoints
  and cross-process wires, not against source in the abstract.
- **Laws become abuse cases.** Each law/section of `docs/security.md`
  (endpoint exposure, ingress hard limits, provider ingress, enrollment +
  delegation, approval lifecycle, policy engine round-trip, artifact-store
  laws, memory-broker laws, roles as runtime-mutable claim rules) is
  restated as "what query/sequence breaks this?" — enforcement-order bugs,
  races, replays, and boundary leaks are the target class, not CVEs.
- **Deferred table is part of the input.** Each existing Deferred row
  (e.g. refresh-token/session lifecycle) is triaged: fix, accept with
  rationale, or scope-in — the doc stays the living record.

## Attacker profiles (one per trust boundary)

1. **Anonymous webhook sender** — the one web-exposed face (`/api/ingress/*`):
   signature-verification bypasses, replay, rate-limit exhaustion, oversized
   / malformed payloads, CloudEvents-schema smuggling through the
   adapter-side HMAC seam.
2. **Unauthenticated / low-privilege UI user** — endpoint exposure matrix
   vs. actual auth gating on `/api/admin/*`, `/api/works`, SPA; claim
   spoofing; stale claims after a runtime role change.
3. **Compromised team-side gateway/workspace** — leaked or replayed enroll
   secret, enrollment-token enumeration, a workspace acting outside its
   delegation evidence, link-challenge hijack.
4. **Malicious harness/worker** — poisoned or oversized artifacts,
   presigned-URL abuse, result manifests that violate store laws,
   approval-loop stalls.
5. **Hostile provider response** — adapter trust: payload size, redirect
   following, credential echo, webhook forgery into provider ingress.
6. **Co-tenant / confused-deputy** — ACL-before-search on the memory
   broker, artifact GC racing a live read, cross-team leaks via shared
   kernel.
7. **Concurrent-replica adversary** — the two-replica HA shape as attacker:
   advisory-lock windows, double-approval, outbox double-publish.
8. **Supply chain** — any dependency shipped by the .NET kernel, gateway,
   protocol/workspace-link packages, or images.

## Method — three layers

- **Design layer:** abuse-case derivation from the posture doc; cross-
  boundary logic review (approval round-trip, claim-rule evaluation order,
  replica races). `oracle`-grade reasoning; no tool substitutes for this.
- **Implementation layer (permanent CI gates, not one-off):** CodeQL
  and/or semgrep over `src/facilitator` + TS packages; `pnpm audit` and
  NuGet vulnerability audit (`packages.lock.json` already pins the graph);
  secret scanning (gitleaks or equivalent); image scanning for
  `src/facilitator/Dockerfile` + `Dockerfile.node` (trivy/grype). Gate
  `ci.yml`; release workflow re-checks.
- **Live layer:** scripted attacks per profile above against the composed
  stack — reuse `features/` + scenario-plane machinery so each attack is a
  runnable walk, not a manual session.

## Orchestration

Fan out one agent per attack surface (ingress, identity/OIDC + roles,
enrollment/workload plane, artifacts + memory broker, gateway
child-transport + policy loop, adapters/provider ingress); `oracle` for
cross-boundary findings; light agents wire the CI gates. One integration
owner merges findings to avoid duplicate remediation.

## Deliverables

- **Findings register** (`.omp/plans/` or a dedicated doc): per finding —
  severity, attacker profile, repro (script or walk), which posture law it
  violates, fix status.
- **Phased remediation:** P0 exploitable-now, P1 posture-law violations,
  P2 hardening. Each fix lands with a regression abuse-case walk so the
  scenario-plane audit machinery keeps it honest.
- **Accepted risks** merged into the `docs/security.md` Deferred table with
  rationale — posture doc and reality converge.
- **CI scan gates** merged and green.

## Non-goals

- External/compliance audit (SOC2 etc.) — this is engineering-facing.
- Production infra hardening (covered by the AWS/GCP reference-infra
  ideas); this assesses the application as deployed by the reference shape.
- Pentest tooling sprawl — scripted, reproducible attacks only.
