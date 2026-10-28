#!/usr/bin/env bash
# Seed the slim reference environment's demo path: a team-alpha project
# (SQL — no REST project route exists), one Work for it over the public API,
# and one work.assign outbox row (+ pg_notify) so the dispatcher delivers to
# the linked demo-workspace Gateway. Runs from the host against the ports the
# slim compose file publishes. Idempotent per project name.
set -euo pipefail

KERNEL=http://localhost:8088
IDP=http://localhost:8080
REALM=facilitator
PROJECT_NAME=slim-demo
OBJECTIVE="slim demo: analyze the reference environment"

cd -- "$(dirname -- "$0")"

compose() { docker compose --env-file ../versions.env --env-file ../.env -f ../compose.slim.yaml "$@"; }
psql() { compose exec -T postgres psql -U demo -d facilitator -tAc "$1"; }

fail() { echo "bootstrap: $*" >&2; exit 1; }

# 1. Wait for both public faces (bounded).
deadline=$((SECONDS + 120))
until curl -fs --max-time 5 -o /dev/null "$IDP/realms/$REALM/.well-known/openid-configuration" \
  && curl -fs --max-time 5 -o /dev/null "$KERNEL/health"; do
  [ "$SECONDS" -lt "$deadline" ] || fail "kernel/IdP not reachable after 120s ($KERNEL, $IDP)"
  sleep 2
done

# 2. Idempotence guard: the dispatch row is the seed's side effect, so its
#    presence (not the project row) means "seeded" — a partial run resumes.
if [ "$(psql "SELECT 1 FROM facilitator.outbox WHERE recipient_endpoint_id = 'link.gw.team-alpha' AND envelope->'data'->>'actorEvidenceRef' = 'identity-evidence:slim-demo' LIMIT 1")" = "1" ]; then
  echo "already seeded"
  exit 0
fi

# 3. Operator token (client credentials, the e2e CLI client).
TOKEN=$(curl -fs --max-time 10 "$IDP/realms/$REALM/protocol/openid-connect/token" \
  -d grant_type=client_credentials -d client_id=facilitator-cli -d client_secret=e2e-cli-secret \
  | jq -re .access_token) || fail "could not mint an operator token"

# 4. Project row (SQL: the kernel exposes no project-create route yet);
#    reuse the row a previous partial run may have left.
PROJECT_ID=$(psql "SELECT project_id FROM facilitator.projects WHERE name = '$PROJECT_NAME'" | head -n1)
if [ -z "$PROJECT_ID" ]; then
  # -tA still prints the INSERT command tag after the RETURNING row; the id
  # is the first line, the tag would poison the JSON body with a newline.
  PROJECT_ID=$(psql "INSERT INTO facilitator.projects (project_id, team_id, name, config, created_at)
    VALUES (gen_random_uuid(), (SELECT team_id FROM facilitator.teams WHERE slug = 'team-alpha'),
            '$PROJECT_NAME', '{}', now())
    RETURNING project_id" | head -n1)
fi

# 5. Work over the public API (capability + project ride the body). The
# kernel may still be finishing migrations/JWKS warmup behind /health; ride
# the window with bounded retries.
WORK_ID=
LAST_ERR="(no attempt)"
for _ in $(seq 1 30); do
  RESP=$(curl -s --max-time 10 -w '\n%{http_code}' -X POST "$KERNEL/api/works" \
    -H "Authorization: Bearer $TOKEN" -H 'content-type: application/json' \
    -d "{\"objective\":\"$OBJECTIVE\",\"capabilityId\":\"research.analyze\",\"projectId\":\"$PROJECT_ID\"}")
  CODE=${RESP##*$'\n'}
  BODY=${RESP%$'\n'*}
  WORK_ID=
  LAST_ERR="http=$CODE ${BODY:0:240}"
  if [ "$CODE" = 201 ] || [ "$CODE" = 200 ]; then
    WORK_ID=$(printf '%s' "$BODY" | jq -re .workId 2>/dev/null) && break
    WORK_ID=
  fi
  sleep 2
done
[ -n "$WORK_ID" ] || fail "work create failed: $LAST_ERR"

# 6. Dispatch: a work.assign outbox row addressed to the demo Gateway, shaped
#    exactly like the e2e tier's assignEnvelope. Until the routing slice
#    assigns works itself, this SQL insert is the Production-mode assign path;
#    the dispatcher wakes on the NOTIFY (and covers it with its poll anyway).
new_uuid() { uuidgen 2>/dev/null || cat /proc/sys/kernel/random/uuid; }
COMMAND_ID=$(new_uuid)
ASSIGNMENT_ID=$(new_uuid)
ENVELOPE=$(jq -cn \
  --arg commandId "$COMMAND_ID" --arg workId "$WORK_ID" --arg assignmentId "$ASSIGNMENT_ID" \
  --arg objective "$OBJECTIVE" '{
    specversion: "1.0",
    id: $commandId,
    source: "urn:facilitator:instance:demo",
    type: "org.facilitator.command.work.assign.v1",
    subject: "work/\($workId)/assignment/\($assignmentId)",
    time: now | todate,
    datacontenttype: "application/json",
    dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
    data: {
      commandId: $commandId,
      actorEvidenceRef: "identity-evidence:slim-demo",
      workId: $workId,
      targetId: "demo",
      capabilityId: "research.analyze",
      expectedContextVersion: 1,
      inputs: { objective: $objective, sourceRefs: [] },
      expectedDeliverables: [{ kind: "analysis.report", mediaType: "text/markdown" }],
      deadline: "2036-10-23T18:00:00Z",
      assignmentId: $assignmentId
    }
  }')
compose exec -T postgres psql -U demo -d facilitator -tA \
  -v workId="$WORK_ID" -v commandId="$COMMAND_ID" -v envelope="$ENVELOPE" <<'SQL' >/dev/null
INSERT INTO facilitator.outbox
    (outbox_id, recipient_endpoint_id, aggregate_type, aggregate_id, event_type,
     command_id, envelope, work_id, status, available_at, attempts, created_at)
  VALUES (gen_random_uuid(), 'link.gw.team-alpha', 'work', :'workId',
          'org.facilitator.command.work.assign.v1', :'commandId',
          :'envelope'::jsonb, :'workId', 'pending', now(), 0, now());
SELECT pg_notify('facilitator_outbox', '');
SQL

echo "seeded: project=$PROJECT_ID work=$WORK_ID assignment=$ASSIGNMENT_ID (dispatched to link.gw.team-alpha)"
