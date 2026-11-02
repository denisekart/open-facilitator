# Acceptance run

`verify.sh` — the slim reference environment's acceptance script, sourced by
`referencectl verify` (run it from the repo root:
`./referencectl verify --json`, `--only <ids>` reruns a leg and its
prerequisites). 11 ordered steps over a booted-but-unseeded stack
(`up slim --no-seed`): every hop is proven through public read
models, the Postgres planes, S3, and the Aspire dashboard — no product-code
hooks, no dev-only routes. Exit 0 iff every step passes.

The script seeds its own works (append-only demo data; teardown is
`down -v`), continues past failures with per-step reports, and skips steps
whose dependencies failed. Waits ride timeline/inbox event kinds, never
wall-clock sleeps. The hosted CI jobs are commented out in
`.github/workflows/ci.yml` (free-tier compute): this run, locally, is the
gate.
