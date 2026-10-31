#!/usr/bin/env bash
# Seed the slim reference environment's demo path: a team-alpha project
# (SQL — no REST project route exists), one Work for it over the public API,
# and one work.assign outbox row (+ pg_notify) so the dispatcher delivers to
# the linked demo-workspace Gateway. The primitives live in seed-lib.sh,
# shared with the acceptance runner (checks/verify.sh). Idempotent per
# project name; runs from the host against the ports the slim compose
# file publishes.
set -euo pipefail

cd -- "$(dirname -- "$0")"
# shellcheck source=seed-lib.sh
. ./seed-lib.sh

PROJECT_NAME=slim-demo
OBJECTIVE="slim demo: analyze the reference environment"

fail() { echo "bootstrap: $*" >&2; exit 1; }

# 1. Wait for both public faces (bounded).
seed_wait_ready 120 || fail "stack not ready"

# 2. Idempotence guard: the dispatch row is the seed's side effect, so its
#    presence (not the project row) means "seeded" — a partial run resumes.
if [ "$(seed_psql "SELECT 1 FROM facilitator.outbox WHERE recipient_endpoint_id = 'routing.kernel' AND envelope->'data'->>'actorEvidenceRef' = 'identity-evidence:slim-demo' LIMIT 1")" = "1" ]; then
  echo "already seeded"
  exit 0
fi

# 3. Operator token + project + Work (+ assignment intent: the kernel
#    routing funnel resolves the endpoint and writes the work.assign effect).
TOKEN=$(seed_mint_cli_token) || fail "could not mint an operator token"
PROJECT_ID=$(seed_project "$PROJECT_NAME") || fail "project seed failed"
WORK_ID=$(seed_work "$TOKEN" "$OBJECTIVE" "research.analyze" "$PROJECT_ID" "identity-evidence:slim-demo") || fail "work seed failed"

echo "seeded: project=$PROJECT_ID work=$WORK_ID (routing intent submitted; the funnel dispatches)"
