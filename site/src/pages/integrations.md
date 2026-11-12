# Supported integrations

Everything that talks to the outside world plugs in at the edges, runs
**team-side**, and speaks the same wire contract. This is what works
today — no roadmap items on this page. Type to filter; every category is
a section with its own detail.

| Want to…                                              | Use                                                 | Direction                        | Ships as                   |
| ----------------------------------------------------- | --------------------------------------------------- | -------------------------------- | -------------------------- |
| Let humans create Work and answer approvals from chat | **Mattermost**, **Telegram**                        | two-way                          | standalone adapter service |
| Feed issues/PRs into Work and gate forge writes       | **GitHub**, **Forgejo**, **GitLab**                 | inbound webhook + gated outbound | adapter + connector pair   |
| Execute assignments with LLM agents or scripts        | **omp**, **Codex**, **deterministic**, **your own** | team-side child process          | harness package            |
| Spawn fresh linked environments on demand             | **OCI launcher** (Docker)                           | outbound                         | provisioning service       |
| Stream provider events through a bus                  | **Kafka** ingress leg                               | inbound                          | optional transport         |
| Put the kernel behind your corporate login            | **any OIDC provider**                               | identity                         | configuration              |

## Chat providers

Inbound messages and commands become Work events; outbound confirmations,
approval prompts, and results come back as chat.

| Provider       | What it does                                                                                                                      |
| -------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| **Mattermost** | Full two-way provider: threads become Work context, approvals are delivered and answered in-channel, slash commands create Works. |
| **Telegram**   | Bot-based chat leg: Works created from messages, approval prompts delivered to chats.                                             |

Each chat adapter runs as its own small service (`@ofac/mattermost-adapter`,
`@ofac/telegram-adapter`) on your side of the network, with a
webhook-in / API-out shape you can audit.

## Forges (source hosting)

Forge integrations come as a pair: an **adapter** that consumes webhook
events, and a **connector** that performs privileged writes — and every
privileged write can ride a human approval.

| Forge       | Inbound today                                                      | Outbound today                                                      |
| ----------- | ------------------------------------------------------------------ | ------------------------------------------------------------------- |
| **GitHub**  | Webhooks: issues, pull requests, comments → normalized Work events | Issue/PR comments, PR creation — approval-gated, executed team-side |
| **Forgejo** | Webhooks, same envelope family                                     | Same privileged-write pattern                                       |
| **GitLab**  | Webhooks, same envelope family                                     | Same privileged-write pattern                                       |

The pair pattern matters: inbound normalization is safe to automate,
outbound writes are the part humans want to control — so they are a
separate, policy-gated component with its own identity.

## Execution harnesses

The harness is what actually does an assignment. The Gateway spawns one
per assignment through a small adapter contract.

| Harness                                           | Shape                                                                                                                                                         |
| ------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Deterministic** (`@ofac/harness-deterministic`) | Scripted workers: fixed inputs → fixed outputs. The reference implementation and the conformance baseline.                                                    |
| **oh-my-pi** (`@ofac/harness-omp`)                | Drives an autonomous agent runtime with tool use and multi-step reasoning.                                                                                    |
| **Codex** (`@ofac/harness-codex`)                 | Drives a Codex-based coding agent as a workspace child.                                                                                                       |
| **Your own** (`@ofac/adapter` SDK)                | Anything you can spawn: a language model loop, a Python pipeline, a checklist a person fills in. The contract is 3 lifecycle messages plus a result manifest. |

## Provisioning

A provisioning launcher can stand up a fresh, linked workspace container
on request — the mechanism behind “spin up a clean environment for this
Work”. The shipped launcher (`@ofac/provisioning-oci`) drives an OCI
runtime (Docker) through an outbound link, so nothing needs an inbound
port.

## Transports and event buses

- **SignalR** is the reference link transport (resumable, multiplexed).
- **Plain HTTP long-poll** is the honest fallback for restrictive
  networks — the same contract, no smart transports.
- **Kafka** is available as an ingress leg: provider events can arrive
  through a topic instead of a webhook, with the same idempotent
  admission.

## Identity

Any **OIDC-compliant** provider is a login authority: the kernel itself
needs nothing proprietary (Keycloak and Azure AD are exercised in the
acceptance suite). Team-side humans, services, and agent identities are
just different claims on the same tokens.

## Storage

The kernel's hard requirements are three commodities, each behind a
configuration block:

- **PostgreSQL** — the durable record. The only mandatory dependency.
- **S3-compatible object storage** — artifacts (MinIO, SeaweedFS, R2,
  S3 all pass the same checks).
- **An OIDC provider** — identity.

---

Want something that isn't on this page? The integration surface is the
protocol: see [Extend it yourself](/extend).
