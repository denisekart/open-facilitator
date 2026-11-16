# Supported integrations

An integration is a small program of yours that translates between one
external system and the kernel: it turns what that system sends into
events the kernel understands, and it performs the writes a human has
allowed. Every one runs **team-side** on your credentials and speaks the
same wire contract, so this page is a list of what works today rather
than a roadmap. Type to filter; every category is a section with its own
detail.

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
webhook-in / API-out shape you can audit. Every adapter declares a
`media` facet in its capability manifest, and every provider speaks media
in both directions — chat providers post files natively, the forges
carry images as issue attachments or note markdown, each within the
caps its facet declares. A completion reply attaches manifest outputs
only where the facet allows it, and degrades honestly (a brokered link
line + one audit row per media act) everywhere else — nothing ever
vanishes silently. Every provider also answers `context.fetch`: a
`thread://` reference is routed to the source adapter, which reads the
thread window from the platform and answers as a stored
`context.history` artifact. A route may also declare a `context` block: the kernel then
folds the thread history, work fields and memory recalls into a
digest-stamped document the harness receives with its assign, so "what
did the agent actually see" is a queryable answer.

## Forges (source hosting)

Forge integrations come as a pair: an **adapter** that consumes webhook
events, and a **connector** that performs privileged writes — and every
privileged write can ride a human approval.

| Forge       | Inbound today                                                      | Outbound today                                                      |
| ----------- | ------------------------------------------------------------------ | ------------------------------------------------------------------- |
| **GitHub**  | Webhooks: issues, pull requests, comments → normalized Work events | Issue/PR comments, PR creation — approval-gated, executed team-side |
| **Forgejo** | Webhooks, same envelope family                                     | Same privileged-write pattern                                       |
| **GitLab**  | Webhooks, same envelope family                                     | Same privileged-write pattern                                       |

The split is deliberate, and it is the same shape for every forge:
inbound normalization on one side, an approval-gated connector on the
other, each with its own identity. Read it as the picture below.

```mermaid title="The forge pair — inbound is automated, outbound is gated"
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#EAF1F9",
"primaryBorderColor": "#0B5FBF", "primaryTextColor": "#1F2328",
"lineColor": "#5B6B7C", "secondaryColor": "#FDF0DC"}}}%%
flowchart LR
  F["Forge<br/>(GitHub, Forgejo, GitLab)"]
  subgraph T["Team environment"]
    A["Adapter<br/>verify webhook, normalize"]
    C["Connector<br/>privileged write"]
  end
  K["Kernel<br/>record + approval"]
  H["Human<br/>named approver"]
  F -->|"webhook"| A
  A -->|"normalized event"| K
  K -->|"approval prompt"| H
  H -->|"approve (once)"| K
  K -->|"gated command, outbound link"| C
  C -->|"comment, PR"| F
```

_The one claim: inbound traffic is safe to automate, so it is one plain
service; a write back into the forge is a separate component that only
acts after a human decision, and it is the only thing that holds the
forge credential._

## Execution harnesses

The harness is what actually does an assignment. The Gateway spawns one
per assignment through a small adapter contract.

| Harness                                           | Shape                                                                                                                                                         |
| ------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Deterministic** (in the team runtime base image) | Scripted workers: fixed inputs → fixed outputs. The reference implementation and the conformance baseline.                                                  |
| **oh-my-pi** (`@ofac/harness-omp`)                | Drives an autonomous agent runtime with tool use and multi-step reasoning.                                                                                    |
| **Codex** (`@ofac/harness-codex`)                 | Drives a Codex-based coding agent as a workspace child.                                                                                                       |
| **Your own** (`@ofac/adapter` SDK)                | Anything you can spawn: a language model loop, a Python pipeline, a checklist a human fills in. The contract is line-oriented JSON: a start command, optional steer and stop, your progress events, your terminal event with a result manifest. |

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

Any **OIDC-compliant** provider is a login authority: the kernel needs
nothing proprietary from it (Keycloak is the provider the acceptance suite
exercises). Team-side humans, services, and agent identities are just
different claims on the same tokens.

## What it runs on

The kernel's hard requirements are three commodities you already run —
PostgreSQL for the record, an S3-compatible store for artifacts, an OIDC
provider for identity — each behind a configuration block. The picture and
the coordinates are on [Artifacts & installation](/artifacts).

## Where to go next

- Turn a new system into an integration: [Extend it yourself](/extend).
- See where these edges appear on the map: [Explore the architecture](/architecture).
- Watch a Work collect its chat and forge traffic: [The admin console](/console).
- Or stop reading and [get running](/guide/human).
