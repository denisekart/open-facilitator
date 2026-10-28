# Postgres init scripts

Initialization SQL mounted by the reference Postgres container at
`/docker-entrypoint-initdb.d` (spec §22.2). `01-databases.sql` creates the
`keycloak` and `mattermost` databases on first initialization; the
`facilitator` database itself arrives via `POSTGRES_DB`. Runs only when the
`pg_data` volume is empty — a schema change to those side databases needs a
fresh volume (`make compose-down`).
