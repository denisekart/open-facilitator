# Facilitator AI Decoder: AiDeck, slash-command plane, admin chat

## Context

Today the kernel parses exactly one slash command (`/link`, `src/facilitator/Kernel/Integrations/ProviderEventStore.cs:437,587`); every other human mention becomes a verbatim Work objective, and admin interaction is raw REST. Add an in-process AI subsystem ("AiDeck") that decodes natural language into kernel intents — via an external LiteLLM-hosted gateway — with **graceful degradation**: a deterministic slash-command plane that works with zero AI, and per-decision-point optional **real decision models (JEV-style, served by LiteLLM's `/v1/decisions` API)** falling back to LLM classifier prompts. Plus a confirm-before-action admin chat screen with stored history, audit attribution, and an explicit, never-auto-granted `ai-operator` role.

**Iron rule (applies to every step):** deterministic tests NEVER call a real AI. All AI flows go through the two ports (`IAiGateway` for chat completions, `IDecisionGateway` for `/v1/decisions`); the test host ships canned-response fakes for both by default. Real-LLM conformance is manual, post-implementation, out of scope here.

## Locked design decisions

- AiDeck lives **in-process** in the kernel (new `src/facilitator/Kernel/Ai/` folder); it calls external LiteLLM over HTTP. One deployable.
- Two wire formats, two ports. Language: **OpenAI Chat Completions** (`POST {base}/v1/chat/completions`) via `IAiGateway`. Decisions: **real decision models (JEV-style) via LiteLLM `POST {base}/v1/decisions`** (OpenAI Decisions format, LiteLLM ≥ v1.104.2) via `IDecisionGateway` — typed `predicate`/`choice`/`score` questions answered as probabilities, not text generation. Config declares **named endpoints** (`decoder` = chat model; `decision` = decision model name in the LiteLLM `model_list`, e.g. `typesafe/jev-latest`). Decision models are strictly optional: every decision point degrades `decisions API → full LLM JSON-classifier prompt → deterministic kernel order`.
- Prompt templates: **Scriban** (Jinja-like, sandboxed). Embedded assembly resources + optional directory override.
- Single **command registry** consumed twice: the deterministic slash parser AND the LLM's tool/command list rendered into the decode prompt. The AI is a second parser over one source of truth.
- Slash commands parse **synchronously** in the provider-event arm (no network). AI decode of non-slash text is **async via outbox** (channel `decode`) so no ingest tx/ack is held open across LLM HTTP.
- Chat UI: request/response (no SSE); actions are **proposed only**, confirmed by the admin, executed **with the confirming admin's own bearer token** against the kernel's own loopback HTTP — RBAC, gate, audit inherited exactly; LLM has zero unilateral write power.
- AI availability is layered: `Ai` file config (endpoints/secrets, fail-closed at boot) × `admin_config` `llm` row (hot policy; **absent row = AiDeck inert** — the NudgeSweeper precedent).

## Step 1 — AiDeck skeleton: port, gateway, options, config

**Packages** (`Directory.Packages.props` + `src/facilitator/Facilitator.csproj`): add `<PackageVersion Include="Scriban" Version="5.12.1" />` (confirm latest stable at implementation time) and matching `PackageReference`. Central CPM, no version in the csproj.

**File config** — new section, bound once at composition (copy `ProviderRegistry.Load` fail-closed style, `Kernel/Integrations/ProviderOptions.cs:127-181`):

```
"Ai": {
  "Endpoints": {
    "decoder":  { "BaseUrl": "http://litellm:4000", "Model": "qwen-32b",   "ApiKey": "...", "TimeoutSeconds": 20 },
    "decision": { "BaseUrl": "http://litellm:4000", "Model": "jev-latest", "ApiKey": "...", "TimeoutSeconds": 5 }
  },
  "TemplateDirectory": null   // optional override; null = embedded only
}
```
No `Ai` section at all ⇒ AiDeck inert everywhere (no boot error — AI is optional by spec). An endpoint that is present but missing `BaseUrl`/`Model` ⇒ composition-time throw (fail-closed). Secrets stay in file/env, never DB.

**Port + gateway** — new files under `src/facilitator/Kernel/Ai/`:

```csharp
public interface IAiGateway {
    // One chat-completion call. Throws AiUnavailableException on timeout/connect/5xx.
    Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken ct);
}
public sealed record AiRequest(
    string EndpointName,            // "decoder" (only chat endpoint today)
    string TemplateId,              // e.g. "decode" — recorded in ledger + fixtures
    string SystemPrompt,
    string UserPrompt,
    int MaxOutputTokens);
public sealed record AiCompletion(string Text, int PromptTokens, int CompletionTokens);
public sealed record DecisionRequest(
    string EndpointName,                        // "decision"
    string InputText,                           // the state to judge
    IReadOnlyList<DecisionQuestion> Questions); // <= 32, named
public abstract record DecisionQuestion(string Name, string Instructions);
public sealed record PredicateQuestion(string Name, string Instructions) : DecisionQuestion(Name, Instructions);
public sealed record ChoiceQuestion(string Name, string Instructions, IReadOnlyList<string> Choices) : DecisionQuestion(Name, Instructions);
public sealed record DecisionAnswer(string Name, bool? Predicate, string? Choice, double Confidence); // refusal => all-null row
public interface IDecisionGateway {
    // One LiteLLM /v1/decisions call. Throws AiUnavailableException on timeout/connect/5xx.
    Task<IReadOnlyList<DecisionAnswer>> DecideAsync(DecisionRequest request, CancellationToken ct);
}
```

`OpenAiChatCompletionsGateway : IAiGateway` — request body `{model, messages:[{role:"system"},{role:"user"}], max_tokens, temperature:0}`; `Authorization: Bearer`; non-2xx/connect/timeout ⇒ `AiUnavailableException` (never return garbage). HTTP client via `AddHttpClient("ai")` (introduce `IHttpClientFactory` — repo has none yet; keep one named client); 10 s–`TimeoutSeconds` per-endpoint CTS. Internal ctor seam taking an `HttpMessageHandler`, mirroring `S3ArtifactStore` (`Kernel/Shared/Storage/S3ArtifactStore.cs:78`).

`LiteLlmDecisionsGateway : IDecisionGateway` (`Kernel/Ai/LiteLlmDecisionsGateway.cs`) — `POST {BaseUrl}/v1/decisions`, body per the OpenAI Decisions format LiteLLM serves (docs.litellm.ai/docs/decisions, proxy ≥ v1.104.2):
```json
{ "model": "<decision endpoint Model>",
  "input": [ { "type": "message", "role": "user", "content": [ { "type": "input_text", "text": "<InputText>" } ] } ],
  "questions": [
    { "type": "predicate", "name": "<name>", "instructions": "<instructions>" },
    { "type": "choice", "name": "<name>", "instructions": "<instructions>",
      "choices": [ { "value": "<choice>" }, ... ] } ] }
```
Response `answers[]` arrives in question order: predicate ⇒ `{"type":"predicate","name","probability"}` → `DecisionAnswer(Name, Predicate: probability>=threshold, null, Confidence: probability)`; choice ⇒ `{"type":"choice","name","choice","probabilities":[...],"confidence"}` → `DecisionAnswer(Name, null, choice, confidence)`; a `{"type":"refusal"}` answer or a missing name ⇒ all-null row (provider treats it as no-preference). No Scriban here — questions are built in code from the typed question list (only `Instructions` strings may come from a small const table per decision point, keeping phrasing stable). Same auth header, same `AiUnavailableException` law, same `HttpMessageHandler` ctor seam. `usage.input_tokens/output_tokens` recorded on spans.

**Templates** — `src/facilitator/Ai/Templates/*.scriban` as `EmbeddedResource`; loader resolves `Ai:TemplateDirectory` first per-template, else embedded. Template ids (exact filenames): `decode.scriban`, `chat.scriban`, `reply-composer.scriban`, `decision-routing.scriban`, `decision-communication.scriban`. `PromptRenderer` (Scriban, `Template.Render` with a member-name allow-list context — templates are data, no C# host functions). Each template ends with the **output contract** spelled out and requests JSON only.

**admin_config `llm` resource** — add `"llm"` to `Admin/AdminConfigStore.cs` `Resources` + a `Validators` entry (plain field checks, 16 KiB cap):

```json
{ "enabled": true,
  "verbosityDefault": "balanced",              // terse | balanced | proactive
  "replyMode": "model",                        // "model" = LLM composes replies; "template" = canned
  "decisionEndpoint": "decision",              // null => level-2 LLM classifier for decision points
  "decisionMode": "auto",                      // auto | llm | off
  "maxOutputTokens": 600 }
```
Absent row ⇒ inert (no decode, slash plane only). Client spec added to `src/facilitator/ui/src/lib/config-shapes.ts`.

**DI** — new `AddAiFeature()` private extension in `Kernel/KernelServiceCollection.cs`, one line in `AddKernel` after `AddProvisioningFeature` (`:40-52`): register `AiOptions` singleton, `IAiGateway` (`OpenAiChatCompletionsGateway`) and `IDecisionGateway` (`LiteLlmDecisionsGateway`) in prod — the test host swaps **both** in Step 7; `PromptRenderer`, `CommandRegistry` (Step 2), `AiLedgerStore`, sinks/services from Steps 3–6.

## Step 2 — Command registry + deterministic slash parser

**Storage**: `admin_config` resource `"commands"` (registry, validators, If-Match + `X-Change-Reason` + history + audit for free — same pattern as `llm`). Payload:

```json
{ "commands": [
  { "verb": "run-ci-tests", "description": "Run the CI test pipeline for a branch",
    "capabilityId": "ci.run", "args": [ { "name": "branch", "required": false } ] } ] }
```
Fields: `verb` (lowercase `[a-z0-9-]+`, unique, no leading slash), `description` (feeds `/help` AND the decode prompt), `capabilityId` (routing seed for the created Work), `args[]` (name/required only; values are positional-or-flag text captured verbatim). Absent row = built-ins only. Validator: reject dup verbs, bad verb charset, empty description.

**Built-ins** (defined in code, cannot be deleted; registry can add custom verbs): `/help`, `/work <objective>`, `/status [work-id]` (default: Work bound to this conversation), `/cancel [work-id]` (cancels bound Work; requires the requester's provider identity to map to an actor with `work.write` binding via `provider_identities`, else polite denial), `/approve <approval-id> <accept|reject>` (existing approvals plane), `/link` — **existing behavior, stays exactly as coded today** in `ProviderEventStore.cs:437,587` (its check moves to run before the parser, not through it).

**Parser** — `Kernel/Ai/CommandParser.cs`:

```csharp
public static class CommandParser {
    // text already mention-stripped. Returns null when no leading "/verb".
    public static ParsedCommand? TryParse(string text, CommandRegistry registry);
}
public sealed record ParsedCommand(string Verb, IReadOnlyDictionary<string,string> Args, string Rest);
```
Parse = first token starts `/`, matches registry verb (built-in or custom), args mapped positionally by declared order, remainder in `Rest`. Unknown verb ⇒ parsed with `Unknown: true` so the reply is "unknown command, try /help" (deterministic, no AI).

**Applier** — `Kernel/Ai/CommandApplier.cs`: given `(ParsedCommand, Reference, actorProviderAccountId, deliveryId)` in a caller-provided `FacilitatorDbContext` tx, produce the effect: `/work` and custom verbs ⇒ the exact Work-creation steps `MentionArmAsync` uses today (find-or-create team project, `CommunicationBindingEntity`, routing intent via `AppendRoutingIntentForWork` pattern, `Kernel/Integrations/ProviderEventStore.cs:431-533,911-941`) with objective = `Rest` (+ rendered args), capability = registry entry's `capabilityId`; `/status` ⇒ reply with bound-Work state read like `AdminReadStores` work view; `/cancel` ⇒ call `IngressProcessor.TransitionAsync(workId, {to:"cancelled", expectedVersion, reason}, actor)` (`Kernel/Work/IngressProcessor.cs:114`) — confirm the exact cancelled wire value + legal edge from `StateGuard.TryParseWork` at edit time; if the edge is illegal the applier replies "cannot cancel in state X" instead of forcing it; `/approve` ⇒ call `ApprovalIngress.HandleRespondAsync` directly (`Kernel/Work/ApprovalIngress.cs:113` — the same arm `CloudEventIngress` dispatches `approval.respond.v1` to), constructing the same JsonElement body the REST path would carry; `/help` ⇒ deterministic reply listing verbs+descriptions rendered from the merged registry (built-ins + custom). All replies via the existing `ReplyEnvelope`/`AppendCommand` pattern (`ProviderEventStore.cs:1006-1035`), idempotency key `cmd:<verb>:<deliveryId>`.

**Arm hook-in** — `ProviderEventStore.MentionArmAsync` + `MessageArmAsync`: after the `/link` check and identity gate, run `CommandParser.TryParse` against the loaded registry; on match ⇒ `CommandApplier` + ack, done (never falls through to "verbatim objective"). On `/help`/unknown ⇒ reply per above. **When AiDeck is inert** (`llm` row absent/disabled) and text is non-slash: today's verbatim-Work behavior **unchanged**, and the ack reply additionally gets one appended line: `Commands: /help <bot-name> works without AI.` (hint per decision). **When AiDeck is live** and non-slash: append outbox row `decode.kernel` (Step 3) instead of creating a Work, and react (not reply) as provisional ack per `replyVsReaction` config.

## Step 3 — Decode pipeline (outbox sink) + AI ledger

**Outbox row**: recipient `decode.kernel`, eventType `org.facilitator.kernel.decode.requested.v1` (internal channel like `kernel.log` — no protocol/json-schema change needed; internal payloads don't carry wire schemas), envelope `{text, reference(conversationTarget shape), actorProviderAccountId, providerDeliveryId, workId?}`. Deterministic commandId `decode:<deliveryId>` ⇒ crash-replay idempotent via the existing unique-constraint law (`RoutingSink` exemplar).

**`DecodeSink : ISink { Channel => "decode" }`** (`Kernel/Ai/DecodeSink.cs`; channel = text before first `.` of recipient, `Kernel/Shared/Dispatch/ISink.cs`). Per row, in order:
1. Load `llm` row + `commands` row + context (Step 4). If disabled/gateway throws `AiUnavailableException`: apply **fallback** — verbatim-Work path via `CommandApplier`-shared helper + reply hint — count `fallback`, return true (never DLQ a decode on AI-down). On transient network *within* attempt budget, `return false` lets dispatcher backoff retry (5 attempts → fallback path via dead-letter... simpler: attempts >= 2 ⇒ fallback, matching poison-drain law).
2. Render `decode.scriban` — system: role law + **command list rendered from the registry** (verb, description, args) + output contract; user: recent conversation turns (Step 4), memory hits (Step 4), the message. Output contract (exact):
```json
{ "intent": "command" | "chat" | "unknown",
  "command": "<registry verb or null>",
  "args": { "<arg>": "<value>" },
  "reply": "<short clarification or null>",
  "confidence": 0.0 }
```
3. Strict parse. `intent=command` + verb in registry + confidence >= 0.5 ⇒ apply via `CommandApplier` (same applier as slash — one execution path). Anything else (parse fail, out-of-registry verb, low confidence, `unknown`) ⇒ deterministic fallback (verbatim Work + hint) — the AI may only **select** from the registry, never invent effects. `intent=chat` ⇒ compose reply only (Step 5), no Work.
4. Write the ledger row (both directions, below) in the same tx as the effect; `PostgresNotifications.NotifyOutboxAsync` wake inside tx.

**Ledger table** `facilitator.ai_turns` (context store + audit of every AI touch):
`turn_id uuid pk, occurred_at timestamptz, conversation_key text (provider|channel|root), actor text? (oidc actor for chat plane; null for provider plane), provider_account_id text?, direction text check in ('in','out'), text text, basis text check in ('slash','model','fallback'), template_id text?, decoded jsonb?, endpoint_name text?, prompt_tokens int?, completion_tokens int?, trace_id text?`.
Entity in `Entities.cs` + `Configurations/AiTurnEntityConfiguration.cs` (snake_case columns, `ix_ai_turns_conversation_key_occurred_at`) + **one migration `AddAiDecoderChat`** for all Step 3+6 tables (`dotnet tool restore && dotnet tool run dotnet-ef migrations add AddAiDecoderChat -p src/facilitator`; AGENTS law: prove empty diff after).

## Step 4 — Context builder (text RAG)

`Kernel/Ai/DecodeContextBuilder.cs` builds the prompt facts, all text-retrieval, no vectors:
- **Conversation memory**: last 8 `ai_turns` rows for the `conversation_key` (chronological) — enables "run it again" / "cancel that".
- **Memory FTS** (user decision: in v1): top-3 hits via the existing broker path — call `MemoryBroker` recall (`Kernel/Shared/Memory/MemoryBroker.cs:268`, provider `postgres-fts`) with the message text as query, scope intersection the caller's verified grants, `Purpose: "ai-decode"`. Scope selector = the team owning the conversation's Work/binding; unbound conversation ⇒ team-scoped query keyed on provider tenant. Every hit gets its existing `memory_usage` row for free (broker law).
- **Work facts** when a binding exists: work status, last 3 `work_events` timeline entries.
Deterministic ordering (recency, then memory score, then id). Rendered into the template under explicit headings so prompt size stays bounded (cap 4 KiB context, drop oldest turns first).

## Step 5 — Reply composition + verbosity + decision provider

**Preferences table** `facilitator.actor_preferences`: `actor text pk` (`oidc:iss|sub` form), `verbosity text check in ('terse','balanced','proactive')`, `extras jsonb default '{}'`, `updated_at`. Precedence: actor row > `llm.verbosityDefault`. Provider-plane actors resolve through `provider_identities` to an oidc actor; unmapped ⇒ default.

**Reply path** (`replyMode: "template"` or gateway down ⇒ deterministic template replies: the existing canned strings ("On it.", "Work reached X.") + `/help` render — no LLM ever required to talk to a human). `replyMode: "model"` ⇒ `reply-composer.scriban` (system: verbosity instruction per actor pref — terse: ≤1 sentence, no pleasantries; balanced: 1–3 sentences + outcome; proactive: offer next step) + facts (decode result, work state) ⇒ plain text ≤ `maxOutputTokens`; on `AiUnavailableException` fall back to the template reply (never swallow the update). All composed replies go out via `ReplyEnvelope` and are ledgered (`direction='out'`).

**`ModelDecisionProvider : IDecisionProvider { Name => "model" }`** (`Kernel/Ai/ModelDecisionProvider.cs`) — implements the stored-accepted-but-unimplemented `"model"` value of `routing-defaults.provider` (existing); registered alongside `DeterministicDecisionProvider` (RoutingSink already takes `IEnumerable<IDecisionProvider>`). Advisory-only law preserved: any null return = "no preference, deterministic order stands", audited basis `model-fallback` exactly as `RoutingSink` already does (`Kernel/Routing/RoutingSink.cs:229-256`). Null-return conditions: `llm` row disabled, no decision capability at all (see ladder), answer refusal, chosen candidate outside the allowed set, or confidence < 0.5.

Two-level ladder per decision point (this is the "decision models optional" mechanism):
1. **Decisions API** (primary, when the `decision` endpoint is configured in `Ai:Endpoints`): `IDecisionGateway.DecideAsync` with the point's typed questions —
   - `Routing`: one `ChoiceQuestion("preferred_endpoint", "Which endpoint should execute this work?", allowed candidate ids)` + input text = intent facts (capability id, objective, team, candidate specificity/openedAt rendered as plain text lines). Answer choice = the preferred `EndpointId`; anything else (refusal, off-set value) ⇒ null.
   - `Communication` ("should I reply if something fails"): one `ChoiceQuestion("action", ..., ["reply","react","silent"])` + input text = event kind + failure detail + conversation recency. Consumed by the completion/progress reply site (`EfCoordinationStore.AppendProviderCompletionReply`, `Kernel/Work/EfCoordinationStore.cs:497-537`): consult provider first; null ⇒ today's behavior verbatim.
   `AiUnavailableException` from the decisions call ⇒ fall to level 2 (never fail the routing decision).
2. **LLM classifier fallback** (when `decision` unconfigured, decision call unavailable, or `llm.decisionMode: "llm"`): the existing `IDecisionProvider` path already has the facts — render `decision-routing.scriban` / `decision-communication.scriban` through `IAiGateway` on the `decoder` endpoint, same question phrasing, strict JSON contract `{"preferred": "<id>|null", "rationale": "...", "confidence": 0..1}` / `{"action": "reply|react|silent", "rationale": "...", "confidence": 0..1}`; unparseable/low-confidence ⇒ null.
No decision endpoint AND no decoder endpoint ⇒ level 3: return null unconditionally (pure deterministic order — kernel fully functional with zero AI).
`llm` resource gains `"decisionMode": "auto"` (`auto` = decisions API when configured else LLM fallback; `llm` = force level 2; `off` = level 3).

## Step 6 — Admin chat plane + UI

**Tables** (same migration):
- `facilitator.chat_sessions`: `session_id uuid pk, actor text (oidc form), title text (first user turn truncated 80), created_at, last_message_at`.
- `facilitator.chat_messages`: `message_id uuid pk, session_id fk fk-cascade, role check in ('user','assistant'), text, basis check in ('model','fallback','slash'), template_id text?, created_at, trace_id`.
- `facilitator.chat_actions`: `action_id uuid pk, session_id fk, message_id fk, method text, path text, body jsonb?, description text, status check in ('pending','executed','failed','cancelled') default 'pending', status_code int?, result jsonb?, created_at, executed_by text?, executed_at timestamptz?` — history browsable forever; audit rows point at `chat_actions:<id>`.

**Endpoints** — new `Kernel/Ai/ChatEndpoints.cs`, `app.MapChatApi()` line in `Program.cs` after `MapAdminApi`; every route carries a gate binding (the AdminEndpoints law — no `.RequireAuthorization`):

| Route | Binding `rolesAnyOf` |
|---|---|
| `POST /api/chat/sessions` | `chat.use` |
| `GET /api/chat/sessions?all=true` (others' sessions) | `chat.read-all`; no flag ⇒ own only |
| `GET /api/chat/sessions/{id}` + `/messages` | `chat.use` (own) or `chat.read-all` |
| `POST /api/chat/sessions/{id}/messages` `{text}` | `chat.use` |
| `GET /api/chat/actions/{id}` | `chat.use` (own session) |
| `POST /api/chat/actions/{id}/confirm` \| `/cancel` | `chat.execute` |
| `GET`/`PUT /api/chat/preferences` | `chat.use` (self only) |

**Message flow** (`ChatProcessor.cs`): store user msg → render `chat.scriban` (system: role law, the **same registry command list** + an exact allow-listed endpoint catalog rendered from code — `METHOD path — summary — body-shape`: all `/api/admin/*` ops incl. `PATCH /api/admin/config/{resource}` (note the `X-Change-Reason` requirement), `POST /api/works`, approval respond; user: message + last 10 session messages) → gateway → contract (exact):
```json
{ "reply": "markdown-ish plain text",
  "actions": [ { "method": "POST", "path": "/api/teams", "body": {}, "description": "Create team 'foo-bar' and return its joining token" } ] }
```
Actions = **zero or more proposals, nothing executed yet**; path must be on the allow-list (prefix match against the rendered catalog) or the whole turn degrades to `reply` + audit basis `fallback` ("I couldn't map that to an action"). Assistant msg + `chat_actions` rows (status `pending`) + audit `chat.message` in one tx. Gateway down ⇒ fallback: `CommandParser` on the raw text (admin may literally type `/work ...`) else honest "AI unavailable" reply. Response to the POST = the assistant message + actions (request/response, no SSE).
**Confirm**: status-guard UPDATE `pending→executed|failed` (rowversion-fenced, single execution), executed by `ChatActionExecutor` calling the kernel's own loopback HTTP (`HttpClients`: base = `http://localhost` + request scheme/port from the confirming request, or a configured `Ai:SelfBaseUrl`) forwarding **the confirming admin's bearer token** — gate middleware, RBAC, and `AuditWriter` fire as if the admin clicked the UI. Audit rows for both proposal (`chat.action.proposed`) and execution (`chat.action.executed`, `Decision allow`) carry `detail: {"aiInitiated": true, "chatSessionId": ..., "actionId": ...}`, actor = the admin principal (`http.GetFacilitatorPrincipal()?.Actor`) — attribution = admin requested, AI executed. Non-2xx ⇒ status `failed`, result body stored.
**Audit read**: add `GET /api/admin/audit?action&actor&limit&since` under binding `admin.read` (`Admin/AdminReadStores.cs` pattern) — closes the existing "no audit read endpoint" gap the chat history screen needs.

**Role/grants** — seed in `appsettings.Development.json` identity roles: new role `ai-operator` (rule: `realm_access.roles` contains `ai-operator`), bindings `chat.use` (`/api/chat/**` GET/POST), `chat.execute` (`/api/chat/actions/*/confirm|cancel`), `chat.read-all` (`/api/chat/sessions` GET w/ all — binding on same path is fine, handler checks role for `all=true` semantics), each `rolesAnyOf: ["ai-operator"]`. NOT added to `framework-admin`/`kernel-operator` seeds — never auto-granted. `reference/keycloak/realm.json`: add realm role `ai-operator` to `alice` (drives E2E). UI: `auth.svelte.ts` role set gains `chat` panel gated on `ai-operator`.

**UI** (`src/facilitator/ui/`): panel state machine in `App.svelte` gains `"chat"` → new `src/components/ChatView.svelte` (hand-rolled primitives + `data-testid` law: `chat-input`, `chat-send`, `chat-message-{n}`, `chat-action-{verb}`, `chat-action-confirm-{id}`, `chat-action-cancel-{id}`, `chat-pending`): message list (GET messages), composer POST (shows assistant text + action cards with machine description + method/path/body preview + Confirm/Cancel chips, status badge after confirm poll GET), session list sidebar (browsable history), verbosity select wired to `/api/chat/preferences`. Types in `src/lib/types.ts`, fetches through existing `getJson`/`makeApiFetch` (`src/lib/api.ts`). No router, no new deps.

## Step 7 — Simulation rule: fake gateway, guard, scripted handler

**`KernelTestFactory`** (`tests/Facilitator.Tests/Infrastructure/KernelTestFactory.cs`): add `AiFixtures` option — a `FakeAiGateway` **and** `FakeDecisionGateway` (one new Infrastructure helper file, mirrors `fakeClock`/`sinkOverride` swap idiom in `ConfigureTestServices`) replace `IAiGateway` and `IDecisionGateway` **unconditionally** under the test host:
- `Enqueue(templateId, responseJson)` FIFO per template for chat calls; unmatched call ⇒ recorded + returns canned `{"intent":"unknown"}`-shaped decode (never throws unpredictably).
- `EnqueueDecision(questionName, answer)` FIFO; unmatched decision call ⇒ recorded + returns refusal-shaped all-null answers (⇒ provider null ⇒ deterministic order — tests that don't care about decisions stay deterministic for free).
- `Requests`/`DecisionRequests` lists record prompts/questions for assertions; `Unavailable()` toggle ⇒ both ports throw `AiUnavailableException` (degradation tests).
- **Guard test** (`tests/Facilitator.Tests/Ai/AiSimulationGuardTests.cs`): resolve `IAiGateway` + `IDecisionGateway` from `KernelTestFactory` ⇒ assert both are the fakes; plus a reflection check that neither `OpenAiChatCompletionsGateway` nor `LiteLlmDecisionsGateway` instance exists in the host's container. Rule text added to `tests/Facilitator.Tests/AGENTS.md` and `src/facilitator/AGENTS.md`: *deterministic tests never construct a real gateway; AI behavior is fixture-replayed.*
- **`ScriptedLlmHandler : HttpMessageHandler`** (same Infrastructure folder): queue of `(pathMatches, responseJson)` pairs — serves canned `/v1/chat/completions` AND `/v1/decisions` responses (incl. a canned `answers[]` array with predicate/choice/refusal shapes) — lets tests exercise the REAL `OpenAiChatCompletionsGateway` and `LiteLlmDecisionsGateway` wire paths (request body shape, auth header, refusal mapping, timeout→fallback) with zero network.
- **No-network law**: an `xUnit` collection fixture or guard test asserts no `Ai` endpoint section is set by any test host (default = no endpoints ⇒ real gateways would throw at composition), so a forgotten swap fails loudly instead of reaching the internet.

**New tests** (join existing Postgres collections, no new container):
- `tests/Facilitator.Tests/Ai/CommandParserTests.cs` — pure: built-in + custom registry verbs, unknown verb, arg mapping.
- `tests/Facilitator.Tests/Ai/SlashCommandTests.cs` — (`postgres:ingress` collection, `ProviderIngressTests.cs` as the template): mention `/work build the thing` → Work + binding + reply with the deterministic objective; `llm` row absent + plain-text mention → today's verbatim Work **and** hint line present in reply.
- `tests/Facilitator.Tests/Ai/DecodeSinkTests.cs` — drain outbox manually (`DrainOnceAsync` idiom): fixture decode `{"intent":"command","command":"run-ci-tests",...}` → Work with capability `ci.run` + ledger `basis='model'`; junk fixture → verbatim-Work fallback + `basis='fallback'`; `Unavailable()` → fallback, row not DLQ'd.
- `tests/Facilitator.Tests/Ai/ModelDecisionProviderTests.cs` — the ladder: (a) `routing-defaults.provider="model"` + decision endpoint configured + `ScriptedLlmHandler`-backed `LiteLlmDecisionsGateway` returning in-set `choice` answer → preferred endpoint chosen, `routing_decisions` row basis `model`; (b) off-set/refusal answer → deterministic order + `model-fallback` audit basis; (c) `decisionMode: "llm"` + `decision` endpoint absent → level-2 chat-completions classifier fixture drives the same assertion; (d) no AI at all (no `Ai` section) → provider returns null, deterministic order, zero gateway calls.
- `tests/Facilitator.Tests/Ai/ChatPlaneTests.cs` — create session → POST message (fixture: reply + one `POST /api/works` action) → confirm → assert Work created, `chat_actions.status='executed'`, audit row has `aiInitiated:true` + actor=alice; cancel path; `all=true` role gating; preferences PUT→reply verbosity.
- vitest: none forced (chat UI logic is thin); Playwright covered next.

## Step 8 — Observability

`Kernel/Shared/Dispatch/KernelMetrics.cs`: counters `facilitator.ai.decode.total{basis}`, `facilitator.ai.gateway.errors{endpoint}`, `facilitator.ai.chat.messages{basis}`, `facilitator.ai.chat.actions{status}`; observable gauge `facilitator.ai.chat.actions.pending` from a cached field (never sync DB in gauge callback). Spans on existing `KernelActivity.Source` around gateway calls (`ai.decode`, `ai.chat.turn`, `ai.action.execute`), tags endpoint + template id + token counts.

## Critical files & anchors

| File | Why |
|---|---|
| `src/facilitator/Kernel/Integrations/ProviderEventStore.cs` (`MentionArmAsync:431`, `MessageArmAsync:583`, `AppendCommand/ReplyEnvelope:1006-1035`, `AppendRoutingIntentForWork:911`) | parser/applier hook-in; reply + Work-creation primitives to reuse |
| `src/facilitator/Kernel/KernelServiceCollection.cs:40-52` | `AddAiFeature()` registration point (one feature line) |
| `src/facilitator/Kernel/Admin/AdminConfigStore.cs` (`Resources`/`Validators`, :234 `routing-defaults.provider:"model"` seam) | new `llm` + `commands` resources |
| `src/facilitator/Kernel/Shared/Dispatch/ISink.cs` + `Kernel/Routing/RoutingSink.cs` | decode sink template; advisory decision + audit vocabulary |
| `tests/Facilitator.Tests/Infrastructure/KernelTestFactory.cs` | fake-gateway swap; simulation enforcement point |

## Verification

1. `make build` (zero warnings) ; `dotnet test --filter "FullyQualifiedName~Ai"` — the Step 7 suites green with `FakeAiGateway` + `FakeDecisionGateway`; run with `--filter "FullyQualifiedName~Ai|...~Integrations|...~Dispatch"` to prove no ingest/dispatch regression.
2. **Wire-path proof (new behavior, no network):** test composing `ScriptedLlmHandler` + real `OpenAiChatCompletionsGateway` (internal-ctor seam): POST a `mention.v1` non-slash event to the test host → captured outbound reply equals the fixture-composed text; handler returning 503 → verbatim-Work fallback + hint reply. Exact assertion: outbox `command.communication.reply.v1` envelope `text` and `ai_turns.decoded` jsonb.
3. Migration: `dotnet ef migrations add` then empty-diff guard test as per AGENTS law.
4. **UI/chat E2E:** `pnpm --filter @ofac/ui build && make admin-e2e` — new Playwright case in `tests/Facilitator.E2E/AdminInspectorTests.cs` pattern (fake gateway fixture proposing `POST /api/admin/dead-letters/{id}/replay`): login alice (now `ai-operator`), open chat panel, send "replay the stuck dead letter", expect action card `data-testid=chat-action-confirm-*`, click, assert executed badge + `GET /api/admin/audit` shows `aiInitiated:true`; login as `facilitator-cli`/non-ai user → no chat panel.
5. `make lint`, `make docs-check`, `pnpm -r --if-present typecheck`.
6. `make docs-check` + update `.omp/skills/facilitator-architecture` (+ `src/facilitator/AGENTS.md` area table with the `Kernel/Ai` signpost) in the same change (repo doc-sync law).
7. Real-stack conformance (user, manual): point `Ai.Endpoints.decoder` at LiteLLM and `Ai.Endpoints.decision` at a decision model (any LiteLLM ≥ v1.104.2-supported JEV-style provider, e.g. `typesafe/jev-latest` in the proxy `model_list`). (a) mention "can you please run ci tests and let me know the outcome" against a registry containing `run-ci-tests` — verify intent + reply; (b) set `routing-defaults.provider="model"` + `decisionMode:"auto"` and watch the proxy log for a `/v1/decisions` request carrying the `preferred_endpoint` choice question; kill only the decision model → verify level-2 LLM classifier takes over; (c) kill LiteLLM entirely, re-mention, verify degraded hint reply. Out of CI.

## Assumptions & contingencies

- If `KernelWorkflowCancellation`/approval-store internals don't line up as expected at `CommandApplier` time (exact `ApprovalIngress` symbol unverified): implement `/approve` via a POST replay to the existing `/api/ingress/events` command path in-process; do not invent a new approvals API.
- If `POST /api/teams` (team roster mutation for the "create team + token" example) doesn't exist as an admin endpoint today (roster seeding is config-driven per `TeamRosterSeeder.cs`), the action catalog in v1 lists only existing endpoints and the example degrades to work-creation actions — no new team-CRUD endpoint is in scope.
- If Scriban 5.12.1 is not the current stable, use latest 5.x; no other engine switch.
- `Ai:SelfBaseUrl` for the loopback executor defaults to reconstructing the confirming request's own scheme+host (`HttpContext.Connection`); set it explicitly only behind TLS termination mismatch.
- Decision-model quality/latency tuning and per-team budgets are explicitly out of this increment.
