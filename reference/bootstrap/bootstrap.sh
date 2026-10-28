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
if [ "$(seed_psql "SELECT 1 FROM facilitator.outbox WHERE recipient_endpoint_id = 'link.gw.team-alpha' AND envelope->'data'->>'actorEvidenceRef' = 'identity-evidence:slim-demo' LIMIT 1")" = "1" ]; then
  echo "already seeded"
  exit 0
fi

# 3. Operator token + project + Work + dispatch.
TOKEN=$(seed_mint_cli_token) || fail "could not mint an operator token"
PROJECT_ID=$(seed_project "$PROJECT_NAME") || fail "project seed failed"
WORK_ID=$(seed_work "$TOKEN" "$OBJECTIVE" "research.analyze" "$PROJECT_ID") || fail "work seed failed"
ASSIGNMENT_ID=$(seed_dispatch "$WORK_ID" "$PROJECT_ID" "research.analyze" "identity-evidence:slim-demo" "$OBJECTIVE") || fail "dispatch seed failed"

echo "seeded: project=$PROJECT_ID work=$WORK_ID assignment=$ASSIGNMENT_ID (dispatched to link.gw.team-alpha)"
