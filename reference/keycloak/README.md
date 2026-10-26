# Keycloak realm fixture

`realm.json` is **fixture infrastructure**, not product: the kernel itself is
vendor-neutral (OIDC + JWKS + RFC 6901 claim pointers only, spec §11). This
realm is the shared dev/test IdP that mints the bearer tokens every tier
authenticates with — the .NET suite (`KeycloakFixture`), the TS e2e harnesses
(`KEYCLOAK_URL`), CI service steps, and local manual runs.

Contents (plan 09): realm `facilitator`; realm roles `framework-admin`,
`kernel-operator`, `e2e-operator`; groups `/teams/team-alpha`,
`/teams/team-beta` surfaced as the `/groups` claim via a group-membership
mapper; clients `facilitator-api` (public, audience `facilitator-api`,
password grants for alice/bob/carol) and `facilitator-cli` (confidential,
client-credentials only, secret `e2e-cli-secret`, service account holds
`kernel-operator` + `e2e-operator`). Access-token lifespan 300 s — every
consumer must re-mint, which keeps token rotation exercised.

Users: `alice` (framework-admin, team-alpha), `bob` (team-beta only),
`carol` (no roles/groups — negative case). Passwords equal usernames; this
realm never leaves dev/CI.

Keycloak 26 import quirks (do not "clean up" the fixture): the service-account
user needs `"serviceAccountClientId": "facilitator-cli"` (else the auto-created
service account collides on import), and users need `firstName` AND `lastName`
(else password grants fail `Account is not fully set up` via UPDATE_PROFILE).

Start it (tag pinned in `../versions.env`):

```bash
docker run -d --name facilitator-idp -p 8480:8080 \
  -v "$PWD/reference/keycloak/realm.json:/opt/keycloak/data/import/realm.json:ro" \
  "quay.io/keycloak/keycloak:$(grep '^KEYCLOAK_TAG=' reference/versions.env | cut -d= -f2)" \
  start-dev --import-realm
```

Issuer: `http://localhost:8480/realms/facilitator` (matches
`identity:oidc:authority` in `appsettings.Development.json`). CLI token:

```bash
curl -s http://localhost:8480/realms/facilitator/protocol/openid-connect/token \
  -d grant_type=client_credentials -d client_id=facilitator-cli \
  -d client_secret=e2e-cli-secret | jq -r .access_token
```
