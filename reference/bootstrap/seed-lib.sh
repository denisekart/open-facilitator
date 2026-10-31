#!/usr/bin/env bash
# seed-lib.sh — the reference environment's shared seeding primitives,
# sourced by bootstrap.sh (the `up slim` demo path) and checks/verify.sh
# (the acceptance run). One implementation of "project + Work + assign
# outbox row" so both scripts can't drift apart. Every function echoes its
# product id on stdout; failures print to stderr and return non-zero.
#
# The stack layout (ports, project name) mirrors compose.slim.yaml; the
# kernel runs in Production so seeding rides the public API + direct SQL
# (no dev-only routes).

SEED_KERNEL=${SEED_KERNEL:-http://localhost:8088}
SEED_IDP=${SEED_IDP:-http://localhost:8080}
SEED_REALM=${SEED_REALM:-facilitator}
SEED_REF=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)

seed_compose() {
  (cd "$SEED_REF" && docker compose --env-file versions.env --env-file .env \
    -f compose.slim.yaml -p facilitator-demo "$@")
}

seed_psql() { seed_compose exec -T postgres psql -U demo -d facilitator -tAc "$1"; }

seed_new_uuid() { uuidgen 2>/dev/null || cat /proc/sys/kernel/random/uuid; }

seed_wait_ready() {
  # $1: bounded seconds (both public faces must answer).
  local deadline=$((SECONDS + ${1:-120}))
  until curl -fs --max-time 5 -o /dev/null "$SEED_IDP/realms/$SEED_REALM/.well-known/openid-configuration" \
    && curl -fs --max-time 5 -o /dev/null "$SEED_KERNEL/health"; do
    [ "$SECONDS" -lt "$deadline" ] || { echo "seed: kernel/IdP not reachable after $((deadline - SECONDS + ${1:-120}))s ($SEED_KERNEL, $SEED_IDP)" >&2; return 1; }
    sleep 2
  done
}

seed_mint_cli_token() {
  curl -fs --max-time 10 "$SEED_IDP/realms/$SEED_REALM/protocol/openid-connect/token" \
    -d grant_type=client_credentials -d client_id=facilitator-cli -d client_secret=e2e-cli-secret \
    | jq -re .access_token
}

seed_mint_user_token() {
  # $1/$2: username/password (public client — realm users, e.g. alice/alice).
  curl -fs --max-time 10 "$SEED_IDP/realms/$SEED_REALM/protocol/openid-connect/token" \
    -d grant_type=password -d client_id=facilitator-api \
    -d "username=$1" -d "password=$2" \
    | jq -re .access_token
}

seed_project() {
  # $1: project name (idempotent per name; the row a prior run left is reused).
  local name=$1 project_id
  project_id=$(seed_psql "SELECT project_id FROM facilitator.projects WHERE name = '$name'" | head -n1)
  if [ -z "$project_id" ]; then
    # -tA still prints the INSERT command tag after the RETURNING row; the id
    # is the first line, the tag would poison the JSON body with a newline.
    project_id=$(seed_psql "INSERT INTO facilitator.projects (project_id, team_id, name, config, created_at)
      VALUES (gen_random_uuid(), (SELECT team_id FROM facilitator.teams WHERE slug = 'team-alpha'),
              '$name', '{}', now())
      RETURNING project_id" | head -n1)
  fi
  [ -n "$project_id" ] || { echo "seed: project '$name' not found and insert returned nothing" >&2; return 1; }
  printf '%s' "$project_id"
}

seed_work() {
  # $1 token, $2 objective, $3 capabilityId, $4 projectId,
  # [$5 actorEvidenceRef, $6 targetId, $7 preferenceEndpointId] → WORK_ID.
  # The create carries the assignment INTENT (kernel routing spec): the
  # routing funnel resolves the endpoint and writes the work.assign effect —
  # no raw-SQL insert, no addressed gateway. The kernel may still be
  # finishing migrations/JWKS warmup behind /health, so the create rides
  # that window with bounded retries.
  local token=$1 objective=$2 capability=$3 project_id=$4
  local evidence=${5:-identity-evidence:seed} target=${6:-demo} prefer=${7:-}
  local resp code body work_id last_err="(no attempt)" json
  json=$(jq -cn --arg objective "$objective" --arg capability "$capability" --arg projectId "$project_id" \
    --arg evidence "$evidence" --arg target "$target" --arg prefer "$prefer" \
    '{objective: $objective, capabilityId: $capability, projectId: $projectId,
      actorEvidenceRef: $evidence, targetId: $target}
     + (if $prefer == "" then {} else {preferenceEndpointId: $prefer} end)')
  for _ in $(seq 1 30); do
    resp=$(curl -s --max-time 10 -w '\n%{http_code}' -X POST "$SEED_KERNEL/api/works" \
      -H "Authorization: Bearer $token" -H 'content-type: application/json' -d "$json")
    code=${resp##*$'\n'}
    body=${resp%$'\n'*}
    work_id=
    last_err="http=$code ${body:0:240}"
    if [ "$code" = 201 ] || [ "$code" = 200 ]; then
      work_id=$(printf '%s' "$body" | jq -re .workId 2>/dev/null) && break
      work_id=
    fi
    sleep 2
  done
  [ -n "$work_id" ] || { echo "seed: work create failed: $last_err" >&2; return 1; }
  printf '%s' "$work_id"
}

seed_redispatch() {
  # $1 token, $2 work_id, $3 capabilityId, [$4 actorEvidenceRef,
  # $5 targetId, $6 commandId, $7 inputs-json-object] → 0.
  # Operator re-dispatch verb: a fresh routing intent on an EXISTING work
  # (the retry / restart-with-context leg).
  local token=$1 work_id=$2 capability=$3
  local evidence=${4:-identity-evidence:seed} target=${5:-demo} command_id=${6:-} inputs=${7:-}
  local resp code last_err="(no attempt)" json
  json=$(jq -cn --arg capability "$capability" --arg evidence "$evidence" --arg target "$target" \
    --arg commandId "$command_id" --argjson inputs "${inputs:-null}" \
    '{capabilityId: $capability, actorEvidenceRef: $evidence, targetId: $target}
     + (if $commandId == "" then {} else {commandId: $commandId} end)
     + (if $inputs == null then {} else {inputs: $inputs} end)')
  resp=$(curl -s --max-time 10 -w '\n%{http_code}' -X POST "$SEED_KERNEL/api/works/$work_id/redispatch" \
    -H "Authorization: Bearer $token" -H 'content-type: application/json' -d "$json")
  code=${resp##*$'\n'}
  last_err="http=$code ${resp%$'\n'*}"
  [ "$code" = 202 ] || { echo "seed: redispatch failed: $last_err" >&2; return 1; }
  return 0
}
