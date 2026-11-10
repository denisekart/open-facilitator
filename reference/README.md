# Reference environment

Slim/full Docker Compose deployment inputs. `./referencectl up slim`
from the repo root boots and seeds the demo path: PostgreSQL, Keycloak (realm
`facilitator`), SeaweedFS (S3 artifact store), the Aspire dashboard (OTLP),
the Facilitator kernel container, and `demo-workspace` — a team-owned
workspace container running a standalone Gateway (`gw.team-alpha`) plus the
deterministic worker over an in-container unix child leg, enrolled and
policy-gated. One seeded `work.assign` then dispatches end-to-end.

## Bring up / tear down

```bash
./referencectl up slim        # build + wait healthy + seed the demo path
./referencectl up slim --no-seed   # stack only (seeds own data)
./referencectl status         # service table + kernel + gateway probes
./referencectl verify         # acceptance run: 11 steps over the --no-seed stack
./referencectl down           # down -v with -v; FULL=1 adds the overlay
make compose-slim / compose-full / compose-down   # raw compose path
```

The first `up` copies `.env.example` to `.env`; **edit `.env` for real use** —
it holds the Postgres password, Keycloak admin password, S3 credentials, and
the Link enrollment token, all plaintext loopback defaults. Every image tag
comes from `versions.env`; every secret from `.env`; compose files interpolate
both verbatim and fail hard on a missing key.

## Ports (all bound to 127.0.0.1)

|Port|Service|Notes|
|---|---|---|
|`8080`|Keycloak|realm `facilitator`; issuer `http://id.localhost:8080/realms/facilitator` (also a compose network alias), admin `demo-admin`|
|`8088`|Facilitator|`GET /health` anonymous; APIs need a bearer token|
|`8333`|SeaweedFS S3|bucket `facilitator-artifacts`, credentials from `.env`|
|`18888`|Aspire dashboard|UI + telemetry REST API (`/api/telemetry/spans`)|
|`18890`|Aspire OTLP/HTTP|kernel exporter target|
|`8099`|Gateway diagnostics|**not published** — binds the workspace container's loopback; `./referencectl status` probes it via `compose exec`|
|`3000`/`8065`/`9092`/`11434`|full overlay|forgejo / mattermost / kafka / ollama|
|`8066`|full overlay|mattermost-adapter `GET /healthz` (no inbound provider port — D1)|

## What the kernel runs with

The container runs **Production**: configuration arrives via
`config/facilitator.json` (mounted read-only, see `config/README.md`) plus
`.env`-interpolated environment variables that override it. Fail-closed
startup checks (identity, storage, enrollment token, Postgres) reject an
incomplete environment — remove one secret and `up` shows the named offenders.

Operator token for the APIs:

```bash
curl -s http://localhost:8080/realms/facilitator/protocol/openid-connect/token \
  -d grant_type=client_credentials -d client_id=facilitator-cli \
  -d client_secret=e2e-cli-secret | jq -r .access_token
```

## Full profile (`FULL=1` / `make compose-full`)

The overlay adds forgejo, Mattermost Team Edition
(site `http://chat.localhost:8065`), kafka, and ollama — plus the Plan 22
provider-ingest pair: `mattermost-init` (one-shot: opens signup on first
boot, creates the `facilitator` bot + PAT, publishes the PAT to the shared
`mm_provision` volume, exits) and `mattermost-adapter` (runs the
`@ofac/mattermost-adapter` from the node-runtime image; `GET /healthz` on
:8066 loopback). The facilitator container gets its `providers:mattermost`
section via `Providers__Mattermost__*` env, so the slim stack stays
provider-less. Live proof: `make provider-e2e` (six steps: boot, link
challenge, mention→Work, completion reply, echo suppression,
reaction-never-consent).

### Telegram leg (opt-in `telegram` profile)

Telegram has no init one-shot — the bot identity is minted outside Docker, so
two manual steps precede `docker compose --profile telegram ... up`:

1. Create the bot with **@BotFather** (`/newbot`), write the token to the
   `tg_provision` volume as `/provision/telegram-bot-token`, and set
   `Providers__Telegram__BotUserId` in
   `reference/config/facilitator-providers.env` to the bot's numeric id
   (the committed `000000000` is a placeholder — an explicit id is required
   because the kernel's identity probe cannot read `getMe`'s nested shape
   without vendor vocabulary).
2. Add the bot to each group it should serve and disable its privacy mode
   (or make it an admin) so it sees ordinary posts; list the admitted chats
   in `TELEGRAM_CHATS` (`.env`) as `chatId[:label[,label]]`, e.g.
   `-1001234567890:ofac:coding` — an admitted chat labeled `ofac:coding`
   routes mentions to the coding lane, any other admitted chat to the
   global research lane.

Then `tg-adapter` runs the `@ofac/telegram-adapter` (poll mode,
`GET /healthz` on :8078 loopback). Mention a chat that is *not* in
`TELEGRAM_CHATS` and the bot answers once with the chat id and the line to
add — the onboarding path, no kernel involvement.

## Security profile

`start-dev` Keycloak, non-TLS loopback ports and plaintext local secrets are
**reference-only**: real deployments use Keycloak production
mode, HTTPS, secret injection, hardened S3 credentials, restricted OTLP
endpoints, and tenant isolation.
