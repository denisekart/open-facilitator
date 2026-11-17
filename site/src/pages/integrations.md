# Supported integrations

An integration translates between an external system and the kernel. It normalizes external events into standard CloudEvents and executes approved outbound writes. Every integration runs team-side using your credentials and speaks the published wire contract.

| Goal | Component | Direction | Distribution |
| --- | --- | --- | --- |
| Create Work and handle approvals in chat | **Mattermost**, **Telegram** | Bidirectional | Standalone service package |
| Ingest issues/PRs and gate forge writes | **GitHub**, **Forgejo**, **GitLab** | Inbound webhook + gated outbound | Adapter and connector pair |
| Execute workloads with agents or scripts | **oh-my-pi**, **Codex**, **deterministic**, **custom** | Team-side child process | Harness package |
| Provision isolated workspaces on demand | **OCI launcher** (Docker) | Outbound | Provisioning service |
| Stream events through a message bus | **Kafka** ingress leg | Inbound | Optional transport |
| Authenticate operators through single sign-on | **Any OIDC provider** | Identity | Configuration |

## Chat providers

Inbound messages and slash commands create Work items. Outbound status updates, approval prompts, and results return directly to the originating chat thread.

| Provider | Mechanism |
| --- | --- |
| **Mattermost** | Bidirectional adapter (`@ofac/mattermost-adapter`). Chat threads become Work context, approvals resolve in-channel, and slash commands open Works. |
| **Telegram** | Bot-based adapter (`@ofac/telegram-adapter`). Messages create Works and approval prompts route to configured chats. |

Each chat adapter runs as an independent service on your network. Adapters declare a `media` facet in their capability manifest to manage native file uploads. Forges attach images to issues or markdown notes according to declared size limits.

When a completion response includes media, the adapter uploads files where permitted. If media exceeds limits, the adapter writes an audit row and a brokered link to ensure no output is dropped.

Providers also support `context.fetch`. A `thread://` URI resolves through the source adapter, which retrieves thread history and stores it as a `context.history` artifact. When a route defines a `context` block, the kernel compiles thread history, work attributes, and memory recalls into a digest-pinned document delivered with the assignment.

## Forge source hosting

Forge integrations operate as an adapter and connector pair. The adapter consumes webhooks, while the connector executes privileged writes subject to approval policies.

| Forge | Inbound operations | Outbound operations |
| --- | --- | --- |
| **GitHub** | Webhooks for issues, pull requests, and comments normalize into Work events. | Comments and pull request creation execute team-side under human approval gates. |
| **Forgejo** | Webhooks normalize into standard CloudEvents envelopes. | Privileged repository writes execute team-side following approval. |
| **GitLab** | Webhooks normalize into standard CloudEvents envelopes. | Privileged repository writes execute team-side following approval. |

This separation keeps inbound automation isolated from outbound writes. The connector is the only component holding forge credentials.

```mermaid title="The forge pair — inbound is automated, outbound is gated"
%%{init: {"theme": "base", "themeVariables": {"primaryColor": "#f7f9fb", "primaryBorderColor": "#ab8612", "primaryTextColor": "#21262e", "lineColor": "#676f7c", "secondaryColor": "#faf2d6"}}}%%
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

_Inbound webhook events normalize automatically through the adapter. Outbound writes to the forge require human approval and execute team-side through the connector._

## Execution harnesses

The Gateway spawns one harness child process per assignment through a line-oriented stdio contract.

| Harness | Architecture |
| --- | --- |
| **Deterministic** (built into runtime) | Scripted workers providing deterministic inputs and outputs. Serves as the conformance baseline. |
| **oh-my-pi** (`@ofac/harness-omp`) | Autonomous agent runtime with multi-step reasoning and tool execution. |
| **Codex** (`@ofac/harness-codex`) | Coding agent operating as a workspace child process. |
| **Custom** (`@ofac/adapter` SDK) | Any executable process: language models, data pipelines, or human checklists. Communicates via line-oriented JSON. |

## Workspace provisioning

The provisioning launcher creates isolated workspace containers on demand. The `@ofac/provisioning-oci` package controls container runtimes over an outbound link, requiring no inbound network listeners.

## Transports and event buses

- **SignalR** serves as the default bidirectional link transport, supporting session multiplexing and automatic reconnection.
- **HTTP long-polling** provides a fallback transport for restrictive networks using identical envelope schemas.
- **Kafka** acts as an optional ingress consumer, ingesting external events into the coordination pipeline with idempotent deduplication.

## Identity providers

The kernel validates tokens against any **OIDC-compliant** identity provider, such as Keycloak, Entra ID, or Okta. Human operators, automated services, and agent workloads authenticate using distinct claim mappings on standard tokens.

## Infrastructure dependencies

The kernel requires three infrastructure components: PostgreSQL for relational state, S3-compatible storage for artifacts, and an OIDC provider for identity. See [Artifacts & installation](/artifacts) for configuration variables and container image tags.

## Where to go next

- Build a custom adapter using the SDK: [Extend it yourself](/extend).
- Inspect connection boundaries on the interactive map: [Explore the architecture](/architecture).
- View event timelines and delivery ledgers: [The admin console](/console).
- Deploy the standalone stack: [Get running (humans)](/guide/human).
