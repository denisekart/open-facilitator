# Reference environment

Slim/full Docker Compose deployment inputs (spec §22). `make compose-slim`
from the repo root boots: PostgreSQL, Keycloak (realm `facilitator`),
SeaweedFS (S3 artifact store), the Aspire dashboard (OTLP), and the
Facilitator kernel container — healthy, authenticated, S3-backed, and
exporting traces/metrics. Gateway/worker services join this stack in the
compose services tier.

## Bring up / tear down

```bash
make compose-slim    # five services, builds the kernel image
make compose-full    # + forgejo, mattermost, kafka, ollama (mattermost
                     # ships amd64-only: on arm64 it boots under emulation, slow)
make compose-down    # down -v (append FULL=1 after compose-full)
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
|`3000`/`8065`/`9092`/`11434`|full overlay|forgejo / mattermost / kafka / ollama|

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

## Security profile

`start-dev` Keycloak, non-TLS loopback ports and plaintext local secrets are
**reference-only** (spec §22.2): real deployments use Keycloak production
mode, HTTPS, secret injection, hardened S3 credentials, restricted OTLP
endpoints, and tenant isolation.
