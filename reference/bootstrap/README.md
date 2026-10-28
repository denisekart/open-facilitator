# Reference bootstrap

`bootstrap.sh` seeds the demo path against a running slim stack: a
`slim-demo` team-alpha project, one `research.analyze` Work for it over the
public API, and one `work.assign` outbox row (with `pg_notify`) dispatched to
the `gw.team-alpha` Gateway — the SQL insert stands in until the routing
slice assigns works itself. `generate-secrets.sh` creates `reference/.env`
from `.env.example` with generated secrets when none exists.
