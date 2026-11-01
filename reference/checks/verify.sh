#!/usr/bin/env bash
# verify.sh — the slim reference environment's acceptance run (sourced by
# `referencectl verify`; run_verify is the entry point). Every spec 21.1 hop
# is proven through existing read-model APIs, the Postgres planes, S3, and
# the Aspire dashboard — no product-code hooks, no dev-only routes.
#
# Contract: self-contained against a booted-but-unverified stack
# (`up slim --no-seed`); seeds its OWN project + works, cleans up nothing
# (append-only demo data; teardown is `down -v`). Per-step continue-and-
# report: a failed step records the failure, dependent steps are skipped,
# independent steps still run. Exit 0 iff every requested step passes.
#
# Law: waits ride timeline/inbox event kinds (poll_until), never wall-clock
# sleeps. Trace correlation is the facilitator's own stamp read out of the
# outbox row — never a timing join.

VERIFY_KERNEL=${KERNEL:-http://localhost:8088}
VERIFY_IDP=${IDP:-http://localhost:8080}
VERIFY_REALM=${REALM:-facilitator}
VERIFY_REF=${REF:?verify.sh expects REF (reference/ dir) from referencectl}
VERIFY_RUN_TAG="verify-$(date +%s)-$$"
VERIFY_S3_BUCKET=facilitator-artifacts

# Step order + declared dependencies (skipped when a requested dep fails).
VERIFY_ORDER=(idp-login work-create dispatch-link progress artifact-stored
  memory-candidate approval-roundtrip worker-restart-resume
  kernel-restart-resume trace-correlation dead-letters-empty)
verify_deps_of() {
  case "$1" in
    work-create) echo idp-login ;;
    dispatch-link) echo work-create ;;
    progress) echo dispatch-link ;;
    artifact-stored) echo dispatch-link ;;
    memory-candidate) echo artifact-stored ;;
    approval-roundtrip) echo idp-login ;;
    worker-restart-resume) echo idp-login work-create ;;
    kernel-restart-resume) echo approval-roundtrip ;;
    trace-correlation) echo progress artifact-stored ;;
    dead-letters-empty) echo idp-login ;;
    *) ;;
  esac
}

VERIFY_RESULTS=""       # file: one `id|status|durationMs|detail` per line
VERIFY_REQUESTED=()     # ids selected by --only (or all)
VERIFY_T0=$(date +%s%3N)
VERIFY_TOKEN_CLI=""
VERIFY_TOKEN_ALICE=""

verify_now_ms() { date +%s%3N; }
verify_human() { printf '%s\n' "$*" >&2; }

verify_record() { # id status durationMs detail
  printf '%s|%s|%s|%s\n' "$1" "$2" "$3" "${4//|/ }" >>"$VERIFY_RESULTS"
}

verify_step_pass() { verify_record "$1" pass "$2" "${3:-}"; verify_human "PASS $1 (${2}ms)${3:+ — $3}"; }
verify_step_fail() {
  verify_record "$1" fail "$2" "${3:-}"
  verify_human "FAIL $1 (${2}ms) — ${3:-}"
}
verify_step_skip() {
  verify_record "$1" skipped 0 "$2"
  verify_human "SKIP $1 — $2"
}

# Logs that contextualize a failing step (last 20 lines, human plane only).
verify_dump_logs() {
  local svc
  verify_human "--- last 20 lines: ${*} ---"
  for svc in "$@"; do
    (cd "$VERIFY_REF" && docker compose --env-file versions.env --env-file .env \
      -f compose.slim.yaml -f compose.provisioning.yaml -p facilitator-demo logs --tail=20 --no-log-prefix "$svc" 2>&1) \
      | sed "s/^/[$svc] /" >&2
  done
}

poll_until() { # <deadline_s> <cmd...>
  local deadline=$((SECONDS + $1)); shift
  until "$@" >/dev/null 2>&1; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 1
  done
}

verify_inbox_has() { # <event_type> <work_id> -> the payload text (non-zero: no row)
  # The adapter's progress subject is a bare assignmentId, so the work join
  # rides payload data.workId (completed carries it too).
  local row
  row=$(verify_psql "SELECT payload::text FROM facilitator.inbox WHERE payload->>'type' = '$1' AND payload->'data'->>'workId' = '$2' LIMIT 1" | head -n1)
  [ -n "$row" ] || return 1
  printf '%s' "$row"
}
verify_psql() { (cd "$VERIFY_REF" && docker compose --env-file versions.env --env-file .env \
  -f compose.slim.yaml -f compose.provisioning.yaml -p facilitator-demo exec -T postgres psql -U demo -d facilitator -tAc "$1"); }

verify_get() { # <token> <path> -> body (non-2xx: non-zero)
  curl -fs --max-time 10 "$VERIFY_KERNEL$2" -H "Authorization: Bearer $1"
}
verify_post() { # <token> <path> <json> -> body (non-2xx: non-zero)
  curl -fs --max-time 10 -X POST "$VERIFY_KERNEL$2" \
    -H "Authorization: Bearer $1" -H 'content-type: application/json' -d "$3"
}

verify_timeline_has() { # <token> <work_id> <kind>
  verify_get "$1" "/api/works/$2/timeline" \
    | jq -e --arg kind "$3" '[.[] | select(.kind==$kind)] | length >= 1' >/dev/null
}

verify_gateway_status() {
  (cd "$VERIFY_REF" && docker compose --env-file versions.env --env-file .env \
    -f compose.slim.yaml -f compose.provisioning.yaml -p facilitator-demo exec -T demo-workspace node -e \
    "fetch('http://127.0.0.1:8099/status').then(r=>r.ok?r.json():Promise.reject(r.status)).then(s=>process.stdout.write(JSON.stringify(s)))")
}

verify_child_idle() { # connected child with zero in-flight executions
  verify_gateway_status \
    | jq -e '[.children[] | select(.state=="connected") | .inFlight] | length >= 1 and (map(. == 0) | all)' >/dev/null
}

verify_token_cli() {
  [ -n "$VERIFY_TOKEN_CLI" ] || VERIFY_TOKEN_CLI=$(seed_mint_cli_token) || return 1
  [ -n "$VERIFY_TOKEN_CLI" ]
}
verify_token_alice() {
  [ -n "$VERIFY_TOKEN_ALICE" ] || VERIFY_TOKEN_ALICE=$(seed_mint_user_token alice alice) || return 1
  [ -n "$VERIFY_TOKEN_ALICE" ]
}

# Seeds a work end-to-end (project + work carrying its assignment intent —
# the kernel routing funnel resolves the endpoint) and echoes the work id.
verify_seed_leg() { # <evidence-tag> <capability> <objective>
  local tag=$1 capability=$2 objective=$3 token project work
  verify_token_cli || return 1
  token=$VERIFY_TOKEN_CLI
  project=$(seed_project slim-demo) || return 1
  work=$(seed_work "$token" "$objective" "$capability" "$project" "identity-evidence:$tag") || return 1
  printf '%s' "$work"
}

# --- steps -------------------------------------------------------------------

step_idp_login() {
  verify_token_cli || { step_fail "could not mint a client_credentials token"; return 1; }
  verify_token_alice || { step_fail "could not mint an alice password-grant token"; return 1; }
  # Both tokens must decode ( JWT 3 segments ) and the kernel must accept them.
  local t
  for t in "$VERIFY_TOKEN_CLI" "$VERIFY_TOKEN_ALICE"; do
    [ "$(printf '%s' "$t" | awk -F. '{print NF}')" = 3 ] \
      || { step_fail "token is not a JWT"; return 1; }
  done
  verify_get "$VERIFY_TOKEN_CLI" /api/admin/deliveries?state=dead_letter >/dev/null \
    || { step_fail "kernel rejected the client_credentials token"; return 1; }
  verify_get "$VERIFY_TOKEN_ALICE" /api/admin/deliveries?state=dead_letter >/dev/null \
    || { step_fail "kernel rejected the alice token"; return 1; }
  # Admin read surface (Plan 20): the alice (framework-admin) token must see
  # the summary roll-up — proves the admin.read binding end-to-end.
  verify_get "$VERIFY_TOKEN_ALICE" /api/admin/summary | jq -e '.workByState' >/dev/null \
    || { step_fail "admin summary unreadable for alice"; return 1; }
  step_pass "both grants minted and accepted by the kernel"
  return 0
}

step_work_create() {
  WORK_MAIN=$(verify_seed_leg verify-main research.analyze \
    "slim verify: analyze the reference environment") \
    || { step_fail "seeding the main work failed (see seed: stderr lines)"; return 1; }
  VERIFY_MAIN_OUTBOX_WHERE="work_id = '$WORK_MAIN' AND event_type = 'org.facilitator.command.work.assign.v1'"
  step_pass "work $WORK_MAIN created + assigned (tag verify-main)"
  return 0
}

step_dispatch_link() {
  # Durable kernel-side witness: the dispatcher rode the assign over the
  # link plane (sink ack flips the row to delivered). No command.accepted
  # timeline row exists on this path — the gateway answers with a receipt,
  # not a posted command.
  if ! poll_until 60 verify_outbox_delivered "$VERIFY_MAIN_OUTBOX_WHERE"; then
    step_fail "assign for $WORK_MAIN never reached status=delivered within 60s"
    verify_dump_logs facilitator demo-workspace
    return 1
  fi
  local status
  status=$(verify_gateway_status) \
    || { step_fail "gateway /status probe failed"; verify_dump_logs demo-workspace; return 1; }
  printf '%s' "$status" | jq -e '.state=="ready" and ([.children[] | select(.state=="connected")] | length >= 1)' >/dev/null \
    || { step_fail "gateway not ready/child-connected: $status"; verify_dump_logs demo-workspace; return 1; }
  step_pass "assign delivered over the link plane + gateway ready with a connected child"
  return 0
}

verify_outbox_delivered() { # <sql-where> — row exists with status delivered
  local n
  n=$(verify_psql "SELECT count(*) FROM facilitator.outbox WHERE $1 AND status = 'delivered'")
  [ "${n:-0}" -ge 1 ]
}

step_progress() {
  if ! VERIFY_PROGRESS_PAYLOAD=$(poll_capture 60 verify_inbox_has \
      org.facilitator.event.work.progress.v1 "$WORK_MAIN"); then
    step_fail "no work.progress.v1 inbox row for $WORK_MAIN within 60s"
    verify_dump_logs facilitator demo-workspace
    return 1
  fi
  step_pass "worker progress reached the kernel inbox"
  return 0
}

# Runs probe until success; echoes the probe's stdout (bounded waits only).
poll_capture() {
  local deadline=$((SECONDS + $1)); shift
  local out
  until out=$("$@" 2>/dev/null) && [ -n "$out" ]; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 1
  done
  printf '%s' "$out"
}

step_artifact_stored() {
  local completed manifest manifest_doc artifact digest key bucket_ls content
  if ! completed=$(poll_capture 90 verify_inbox_has \
      org.facilitator.event.assignment.completed.v1 "$WORK_MAIN"); then
    step_fail "no assignment.completed.v1 for $WORK_MAIN within 90s"
    verify_dump_logs facilitator demo-workspace
    return 1
  fi
  VERIFY_COMPLETED_PAYLOAD=$completed
  manifest=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$WORK_MAIN/manifests" \
    | jq -c 'if length == 1 then .[0] else "MANIFEST_COUNT=" + (length|tostring) end')
  case $manifest in
    MANIFEST_COUNT=*) step_fail "expected exactly 1 manifest for $WORK_MAIN, got ${manifest#MANIFEST_COUNT=}"; verify_dump_logs facilitator; return 1 ;;
    MANIFEST_COUNT*) step_fail "manifest list not readable as alice: $(printf '%.200s' "$manifest")"; verify_dump_logs facilitator; return 1 ;;
  esac
  # The manifest row names its OWN artifact (the manifest doc, JSON). The
  # report rides inside that doc's outputs[] (entry field artifactId; the
  # logical artifact:// ref sits on storageUrl).
  # Poll: the completed witness can land microseconds before the manifest
  # doc's row settles to stored, so give the read model a bounded window.
  manifest_doc=$(poll_capture 30 verify_fetch_in_network \
      "$VERIFY_KERNEL/api/artifacts/$(jq -re .artifactId <<<"$manifest")/content" "$VERIFY_TOKEN_ALICE") \
    || { step_fail "manifest doc unreadable for $WORK_MAIN"; verify_dump_logs facilitator artifacts; return 1; }
  artifact=$(jq -re '.outputs[0].artifactId' <<<"$manifest_doc" 2>/dev/null) \
    || { step_fail "manifest doc carries no outputs[] (report missing?)"; verify_dump_logs facilitator; return 1; }
  local row
  poll_until 30 verify_artifact_stored "$artifact" \
    || { step_fail "report artifact $artifact never reached stored+verified"; verify_dump_logs facilitator artifacts; return 1; }
  row=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/artifacts/$artifact")
  # Blob probe (negative guard baked in): the row must be backed by the
  # digest-derived object key in the bucket, or this step FAILS.
  digest=$(jq -re .digest <<<"$row"); key=${digest#sha256:}
  bucket_ls=$(verify_s3_ls "${key:0:2}/$key") \
    || { step_fail "blob $key absent from s3://$VERIFY_S3_BUCKET (confirm-upload regression?)"; verify_dump_logs artifacts facilitator; return 1; }
  content=$(poll_capture 30 verify_fetch_in_network \
      "$VERIFY_KERNEL/api/artifacts/$artifact/content" "$VERIFY_TOKEN_ALICE") \
    || { step_fail "presigned content download failed for $artifact"; verify_dump_logs facilitator artifacts; return 1; }
  grep -q '^#\+ ' <<<"$content" \
    || { step_fail "artifact content is not the Markdown report"; verify_dump_logs facilitator; return 1; }
  VERIFY_ARTIFACT=$artifact
  step_pass "manifest doc + report stored/verified + blob $key in S3 + Markdown bytes served (bucket ls: $(head -n1 <<<"$bucket_ls"))"
  return 0
}


# Read-model probe: the artifact row exists and reads stored+verified.
verify_artifact_stored() { # <artifact_id>
  verify_get "$VERIFY_TOKEN_ALICE" "/api/artifacts/$1" \
    | jq -e '.state=="stored" and .verification=="verified"' >/dev/null
}

# Presigned GET rides the compose-internal S3 host (the SigV4 signature
# covers it, so no host-side rewrite) — fetch it inside the network, where
# both the kernel and the artifact store resolve.
verify_fetch_in_network() { # <url> <bearer>
  # Inside the compose network the kernel answers as `facilitator`, not the
  # host-mapped origin; the 302's presigned URL (artifacts:8333) already
  # resolves here, and the SigV4 signature forbids rewriting it.
  local inurl=${1/$VERIFY_KERNEL/http:\/\/facilitator:8080}
  (cd "$VERIFY_REF" && env VERIFY_FETCH_URL="$inurl" VERIFY_FETCH_TOKEN="$2" \
    docker compose --env-file versions.env --env-file .env \
    -f compose.slim.yaml -f compose.provisioning.yaml -p facilitator-demo exec -T \
    -e VERIFY_FETCH_URL -e VERIFY_FETCH_TOKEN demo-workspace node -e \
    'fetch(process.env.VERIFY_FETCH_URL,{headers:{authorization:"Bearer "+process.env.VERIFY_FETCH_TOKEN}}).then(r=>r.ok?r.text():Promise.reject(r.status)).then(t=>process.stdout.write(t))')
}

verify_s3_ls() { # <key> -> aws-cli ls line (host-probe idiom, CI-proven)
  local ak sk
  ak=$(grep '^DEMO_S3_ACCESS_KEY=' "$VERIFY_REF/.env" | cut -d= -f2)
  sk=$(grep '^DEMO_S3_SECRET_KEY=' "$VERIFY_REF/.env" | cut -d= -f2)
  local tag
  tag=$(grep '^AWSCLI_TAG=' "$VERIFY_REF/versions.env" | cut -d= -f2 | tr -d '[:space:]')
  docker run --rm --network host \
    -e AWS_ACCESS_KEY_ID="$ak" -e AWS_SECRET_ACCESS_KEY="$sk" -e AWS_DEFAULT_REGION=us-east-1 \
    "amazon/aws-cli:$tag" --endpoint-url http://127.0.0.1:8333 \
    s3 ls "s3://$VERIFY_S3_BUCKET/$1"
}

step_memory_candidate() {
  local now event resp
  now=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  event=$(jq -cn --arg work "$WORK_MAIN" --arg art "$VERIFY_ARTIFACT" --arg now "$now" \
    --arg tag "$VERIFY_RUN_TAG" --arg src "urn:workspace:referencectl-verify" '{
      specversion: "1.0",
      id: ("verify-candidate-" + $tag),
      source: $src,
      type: "org.facilitator.event.memory.candidate.v1",
      subject: ("work/" + $work + "/assignment/verify"),
      time: $now,
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/event.memory.candidate.v1.json",
      data: {
        scope: "project",
        contentSummary: ("slim verify lesson " + $tag + ": the acceptance run stores artifacts."),
        provenance: {
          source: $src,
          evidenceRef: ("artifact://" + $art),
          createdBy: "user:alice",
          createdAt: $now
        },
        policyLabel: "team-only"
      }
    }')
  # /api/ingress/events rides work.write -> kernel-operator only: CLI token.
  resp=$(verify_post "$VERIFY_TOKEN_CLI" /api/ingress/events "$event") \
    || { step_fail "POST /api/ingress/events refused the candidate event"; verify_dump_logs facilitator; return 1; }
  if ! poll_until 20 verify_timeline_has "$VERIFY_TOKEN_ALICE" "$WORK_MAIN" memory.candidate; then
    step_fail "no memory.candidate timeline row after ingress ($resp)"
    verify_dump_logs facilitator
    return 1
  fi
  local hits
  hits=$(verify_post "$VERIFY_TOKEN_ALICE" /api/memory/recall \
    "$(jq -cn --arg work "$WORK_MAIN" --arg q "slim verify lesson $VERIFY_RUN_TAG" \
      '{query:$q, purpose:"assignment.context", workId:$work}')" \
    | jq '.entries | length')
  [ "${hits:-0}" -ge 1 ] \
    || { step_fail "recall found no entry for the fresh candidate"; verify_dump_logs facilitator; return 1; }
  step_pass "candidate ingressed + timeline row + $hits recall hit(s)"
  return 0
}

# The approval leg doubles as the kernel-restart leg: the Work parks durably
# in awaitingApproval, the facilitator restarts under it, and the response
# (after restart) drives decided → completed → manifest.
step_approval_and_kernel_restart() {
  local started=$1
  local approval_id body
  WORK_APPROVAL=$(verify_seed_leg verify-approval research.publish \
    "slim verify: publish the analysis") \
    || {
      step_fail_record approval-roundtrip "$(( $(verify_now_ms) - started ))" "seeding the approval work failed"
      step_fail_record kernel-restart-resume "$(( $(verify_now_ms) - started ))" "approval leg did not start"
      return 1
    }
  if ! poll_until 60 verify_approval_parked; then
    step_fail_record approval-roundtrip "$(( $(verify_now_ms) - started ))" "no awaitingApproval on the gateway within 60s"
    step_fail_record kernel-restart-resume 0 "approval leg did not park"
    verify_dump_logs facilitator demo-workspace
    return 1
  fi

  # Parked: the durable-state checkpoint — restart the kernel under it.
  (cd "$VERIFY_REF" && docker compose --env-file versions.env --env-file .env \
    -f compose.slim.yaml -f compose.provisioning.yaml -p facilitator-demo restart --no-deps facilitator) >&2 \
    || {
      step_fail_record approval-roundtrip "$(( $(verify_now_ms) - started ))" "facilitator restart command failed"
      step_fail_record kernel-restart-resume "$(( $(verify_now_ms) - started ))" "restart command failed"
      return 1
    }
  if ! poll_until 90 curl_probe_health; then
    step_fail_record approval-roundtrip "$(( $(verify_now_ms) - started ))" "kernel not healthy 90s after restart"
    step_fail_record kernel-restart-resume "$(( $(verify_now_ms) - started ))" "kernel did not come back"
    verify_dump_logs facilitator
    return 1
  fi
  # Still parked after the restart: the approval survived on durable state.
  if ! poll_until 60 verify_approval_parked; then
    step_fail_record approval-roundtrip "$(( $(verify_now_ms) - started ))" "approval vanished from gateway after kernel restart"
    step_fail_record kernel-restart-resume "$(( $(verify_now_ms) - started ))" "awaitingApproval not durable across restart"
    verify_dump_logs facilitator demo-workspace
    return 1
  fi
  step_pass_record kernel-restart-resume "$(( $(verify_now_ms) - started ))" \
    "approval parked across a facilitator restart (awaitingApproval survived)"

  # The kernel row rides the gateway's approval.required event, which may
  # only land after the restart + link reconnect: poll the read model.
  approval_id=$(poll_capture 60 verify_approval_row_id) \
    || {
      step_fail_record approval-roundtrip "$(( $(verify_now_ms) - started ))" "approval row not visible on the kernel read model"
      verify_dump_logs facilitator
      return 1
    }
  body=$(verify_post "$VERIFY_TOKEN_ALICE" "/api/works/$WORK_APPROVAL/approvals/$approval_id/respond" '{"decision":"approved","comment":"verify auto-approve"}') \
    || {
      step_fail_record approval-roundtrip "$(( $(verify_now_ms) - started ))" "approval respond refused (approval $approval_id)"
      verify_dump_logs facilitator
      return 1
    }
  if ! poll_until 60 verify_timeline_has "$VERIFY_TOKEN_ALICE" "$WORK_APPROVAL" approval.decided; then
    step_fail_record approval-roundtrip "$(( $(verify_now_ms) - started ))" "no approval.decided timeline row (response: ${body:0:200})"
    verify_dump_logs facilitator demo-workspace
    return 1
  fi
  if ! poll_until 90 verify_inbox_has org.facilitator.event.assignment.completed.v1 "$WORK_APPROVAL"; then
    step_fail_record approval-roundtrip "$(( $(verify_now_ms) - started ))" "approved work never completed within 90s"
    verify_dump_logs facilitator demo-workspace
    return 1
  fi
  local n
  n=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$WORK_APPROVAL/manifests" | jq 'length')
  [ "${n:-0}" = 1 ] \
    || { step_fail_record approval-roundtrip "$(( $(verify_now_ms) - started ))" "expected 1 manifest for $WORK_APPROVAL, got ${n:-0}"; return 1; }
  step_pass_record approval-roundtrip "$(( $(verify_now_ms) - started ))" \
    "parked durably, resumed exactly: approved after restart, decided, completed, 1 manifest"
  return 0
}

verify_approval_parked() {
  verify_gateway_status | jq -e '.awaitingApproval >= 1' >/dev/null
}

# Echoes the approval row's id once the kernel read model shows it (the
# gateway's approval.required event may lag a kernel restart).
verify_approval_row_id() {
  local id
  id=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$WORK_APPROVAL/approvals" \
    | jq -re '.[0].approvalId' 2>/dev/null) || return 1
  [ -n "$id" ] || return 1
  printf '%s' "$id"
}
curl_probe_health() { curl -fs --max-time 5 -o /dev/null "$VERIFY_KERNEL/health"; }

step_pass_record() { verify_record "$1" pass "$2" "${3:-}"; verify_human "PASS $1 (${2}ms)${3:+ — $3}"; }
step_fail_record() {
  verify_record "$1" fail "$2" "${3:-}"
  verify_human "FAIL $1 (${2}ms) — ${3:-}"
}
# Implicit-id/result shims for the simple steps (the runner sets the pair).
step_pass() { step_pass_record "$VERIFY_CURRENT_STEP" "$(( $(verify_now_ms) - VERIFY_CURRENT_STARTED ))" "$*"; }
step_fail() { step_fail_record "$VERIFY_CURRENT_STEP" "$(( $(verify_now_ms) - VERIFY_CURRENT_STARTED ))" "$*"; }

step_worker_restart_resume() {
  # Plan 35 Phase C inversion: the killed worker is the kernel-PROVISIONED
  # elastic container (team-beta), not the compose demo-workspace. The
  # continuity law: kill-9 => exactly ONE honest retryable failed => the
  # kernel's worker-lost arm relaunches the container through the launcher
  # AND re-queues the work's routing intent (new generation) => completion
  # with exactly one manifest revision 1 — with NO operator redispatch verb.
  local started=$1 n records rev revof failed nfailed
  verify_token_cli \
    || { step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "token mint failed"; return 1; }
  # Own the scale law for this leg: min1/max1 keeps min-shortfall from
  # racing the kill-restart; idle 10 minutes keeps the idle arm from
  # firing mid-leg.
  seed_workload_scale_policy "$VERIFY_TOKEN_CLI" 1 1 10 >/dev/null \
    || { step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "workload-scale policy PATCH failed"; return 1; }
  local leg_t0
  leg_t0=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  # The elastic fleet must be live before seeding team-beta work: min1
  # boots it at policy time; wait for the child to register + idle.
  if ! poll_elastic_idle 120; then
    step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "provisioned elastic never went live+idle (launcher/kernel logs)"
    verify_dump_logs facilitator provisioning-launcher
    return 1
  fi
  local project work
  project=$(seed_project team-beta-witness team-beta) \
    || { step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "team-beta witness project seed failed"; return 1; }
  work=$(seed_work "$VERIFY_TOKEN_CLI" "slim verify: provisioned-worker restart witness" research.analyze "$project" "identity-evidence:verify-restart-w") \
    || { step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "seeding the restart witness work failed"; return 1; }
  WORK_W3=$work
  # 120s: a link plane still settling can delay first delivery (lease
  # reclaim cycles) — the witness must not race that convergence.
  if ! poll_until 120 verify_inbox_has org.facilitator.event.work.progress.v1 "$WORK_W3"; then
    step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "witness run never reported progress"
    verify_dump_logs facilitator provisioning-launcher
    return 1
  fi
  # Kill-9 the provisioned container mid-flight (the progress window just
  # opened). Compose knows nothing about it — the launcher owns it, so the
  # kill rides the docker socket the launcher shares.
  docker kill ofac-gw-team-beta >&2 \
    || { step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "docker kill ofac-gw-team-beta failed (container name/label law drifted?)"; return 1; }
  failed=$(poll_capture 90 verify_inbox_has org.facilitator.event.assignment.failed.v1 "$WORK_W3") \
    || { step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "killed run never reported a terminal event within 90s"
          verify_dump_logs facilitator provisioning-launcher; return 1; }
  jq -e '.data.retryable == true' <<<"$failed" >/dev/null \
    || { step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "failed event is not retryable: $(jq -c .data <<<"$failed")"; return 1; }
  nfailed=$(verify_psql "SELECT count(*) FROM facilitator.inbox WHERE payload->>'type' = 'org.facilitator.event.assignment.failed.v1' AND payload->'data'->>'workId' = '$WORK_W3'")
  [ "${nfailed:-0}" = 1 ] \
    || { step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "expected exactly 1 failed event, got $nfailed"; return 1; }
  # NO seed_redispatch here (the old leg's operator verb): the kernel's
  # worker-lost arm relaunches AND re-dispatches at the new generation.
  # 180s covers relaunch (docker create+start + gateway hello) + re-route
  # + run.
  if ! poll_until 180 verify_inbox_has org.facilitator.event.assignment.completed.v1 "$WORK_W3"; then
    step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "witness work did not complete 180s after kernel auto-relaunch"
    verify_dump_logs facilitator provisioning-launcher
    return 1
  fi
  verify_token_alice || { step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "alice token mint failed"; return 1; }
  # The inbox row lands before ResultIngress files the manifest — ride the
  # read model, never a single-shot GET (an unrecorded manifest must fail
  # the poll, not errexit the leg).
  if ! poll_until 60 verify_timeline_has "$VERIFY_TOKEN_ALICE" "$WORK_W3" manifest.recorded; then
    step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "resume completed but no manifest.recorded timeline row"
    verify_dump_logs facilitator provisioning-launcher
    return 1
  fi
  n=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$WORK_W3/manifests" | jq 'length')
  records=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$WORK_W3/timeline" | jq '[.[] | select(.kind=="manifest.recorded")] | length')
  rev=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$WORK_W3/manifests" | jq '.[0].revision')
  revof=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$WORK_W3/manifests" | jq -c '.[0].revisionOf')
  { [ "${n:-0}" = 1 ] && [ "${records:-0}" = 1 ] && [ "${rev:-0}" = 1 ] && { [ "$revof" = null ] || [ "$revof" = '"null"' ]; }; } \
    || { step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "resume duplicated side effects (manifests=$n manifest.recorded=$records revision=$rev revisionOf=$revof)"; return 1; }
  # The continuity law's ledger face: a worker-lost start op exists since
  # leg start (the kernel ordered the relaunch, the launcher obeyed).
  verify_psql "SELECT 1 FROM facilitator.provisioning_operations WHERE workload_id = 'gw.team-beta' AND operation = 'start' AND reason ILIKE 'worker-lost%' AND created_at >= '$leg_t0' LIMIT 1" | grep -q 1 \
    || { step_fail_record worker-restart-resume "$(( $(verify_now_ms) - started ))" "no worker-lost relaunch op in the provisioning ledger"; return 1; }
  step_pass_record worker-restart-resume "$(( $(verify_now_ms) - started ))" \
    "provisioned worker killed mid-run: honest retryable failed, kernel auto-relaunch+re-dispatch completed with exactly 1 manifest revision 1"
  return 0
}

poll_elastic_idle() { # <deadline_s> — elastic live (session lease) + zero in-flight
  local deadline=$((SECONDS + $1))
  until verify_psql "SELECT 1 FROM facilitator.link_sessions WHERE logical_endpoint_id = 'gw.team-beta' AND lease_until > now() LIMIT 1" | grep -q 1; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 2
  done
  # The child connects as a gateway CHILD (elastic-worker-child); the
  # gateway's own diagnostics report in-flight executions.
  local deadline2=$((SECONDS + 30))
  until elastic_child_idle; do
    [ "$SECONDS" -lt "$deadline2" ] || return 1
    sleep 2
  done
}

elastic_child_idle() {
  # Same in-container probe + quota law as verify_child_idle, aimed at the
  # launcher-managed container (it shares the compose network).
  docker exec ofac-gw-team-beta node -e \
    "fetch('http://127.0.0.1:8099/status').then(r=>r.ok?r.json():Promise.reject(r.status)).then(s=>process.stdout.write(JSON.stringify(s)))" 2>/dev/null \
    | jq -e '[.children[] | select(.state=="connected") | .inFlight] | length >= 1 and (map(. == 0) | all)' >/dev/null
}

step_trace_correlation() {
  # The facilitator's OWN stamp, read out of the outbox row (locked: no
  # timing join). The dispatch path must have stamped every link recipient.
  local stamp trace_id progress_tp
  stamp=$(verify_psql "SELECT envelope->>'traceparent' FROM facilitator.outbox WHERE $VERIFY_MAIN_OUTBOX_WHERE LIMIT 1" | head -n1)
  if ! grep -Eq '^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$' <<<"$stamp"; then
    step_fail "assign envelope carries no W3C traceparent (dispatcher stamp missing): '$stamp'"
    verify_dump_logs facilitator
    return 1
  fi
  trace_id=${stamp#00-}; trace_id=${trace_id%%-*}

  # Causality proof (REQUIRED, backend-independent): the worker echoed the
  # stamped traceparent verbatim onto its progress + completed envelopes.
  progress_tp=$(jq -r '.traceparent // ""' <<<"${VERIFY_PROGRESS_PAYLOAD:-{\}}")
  [ "$progress_tp" = "$stamp" ] \
    || { step_fail "progress envelope traceparent ('$progress_tp') != facilitator stamp ('$stamp')"; return 1; }
  local completed_tp
  completed_tp=$(jq -r '.traceparent // ""' <<<"${VERIFY_COMPLETED_PAYLOAD:-{\}}")
  [ "$completed_tp" = "$stamp" ] \
    || { step_fail "completed envelope traceparent ('$completed_tp') != facilitator stamp ('$stamp')"; return 1; }

  # Dashboard half (bonus strength): the kernel's spans under that trace id
  # arrived at the Aspire query API.
  local spans
  spans=$(poll_capture 60 verify_dashboard_trace "$trace_id") \
    || { step_fail "no spans for facilitator trace $trace_id at the dashboard within 60s"; verify_dump_logs facilitator aspire; return 1; }
  step_pass "stamp $stamp echoed by worker envelopes + $spans dashboard span(s) for facilitator"
  return 0
}

verify_dashboard_trace() {
  local out
  out=$(curl -fs --max-time 10 "http://localhost:18888/api/telemetry/spans?traceId=$1") || return 1
  local n
  n=$(jq -r '.returnedCount // 0' <<<"$out")
  [ "$n" -ge 1 ] && printf '%s' "$n"
}

step_dead_letters_empty() {
  local n
  n=$(verify_get "$VERIFY_TOKEN_CLI" "/api/admin/deliveries?state=dead_letter" | jq '.items | length') || \
    { step_fail "dead-letters unreadable"; verify_dump_logs facilitator; return 1; }
  [ "${n:-0}" = 0 ] \
    || { step_fail "$n dead-letter row(s) — a hop failed hard somewhere"; verify_dump_logs facilitator; return 1; }
  step_pass "no dead letters"
  return 0
}

# --- runner ------------------------------------------------------------------

verify_run_step() { # <id> — dep gate + timing + dispatch
  local id=$1 started
  local dep
  # A dep with no record yet was not requested: run it as a prerequisite
  # (steps own run-global state — tokens, WORK_MAIN — that later legs
  # read; a --only selection must not start from a hole). A dep that ran
  # and did not pass skips this step.
  for dep in $(verify_deps_of "$id"); do
    grep -q "^$dep|" "$VERIFY_RESULTS" || verify_run_step "$dep"
    if grep -q "^$dep|fail\|^$dep|skipped" "$VERIFY_RESULTS"; then
      verify_step_skip "$id" "dependency $dep did not pass"
      return 0
    fi
  done
  started=$(verify_now_ms)
  VERIFY_CURRENT_STEP=$id VERIFY_CURRENT_STARTED=$started
  case "$id" in
    idp-login) step_idp_login ;;
    work-create) step_work_create ;;
    dispatch-link) step_dispatch_link ;;
    progress) step_progress ;;
    artifact-stored) step_artifact_stored ;;
    memory-candidate) step_memory_candidate ;;
    approval-roundtrip) step_approval_and_kernel_restart "$started" ;;
    worker-restart-resume) step_worker_restart_resume "$started" ;;
    # The approval leg records this id itself (interleaved checkpoint).
    kernel-restart-resume)
      grep -q '^kernel-restart-resume|' "$VERIFY_RESULTS" \
        || verify_step_skip "$id" "approval leg did not reach the restart checkpoint" ;;
    trace-correlation) step_trace_correlation ;;
    dead-letters-empty) step_dead_letters_empty ;;
    *) verify_step_skip "$id" "unknown step" ;;
  esac
}

run_verify() {
  local json=0 only=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --json) json=1 ;;
      --only) only=${2:?--only needs a comma-separated step list}; shift ;;
      *) verify_human "referencectl: verify: unknown flag '$1'"; return 2 ;;
    esac
    shift
  done

  VERIFY_REQUESTED=("${VERIFY_ORDER[@]}")
  if [ -n "$only" ]; then
    IFS=, read -r -a VERIFY_REQUESTED <<<"$only"
    local id
    for id in "${VERIFY_REQUESTED[@]}"; do
      printf '%s\n' "${VERIFY_ORDER[@]}" | grep -qx "$id" \
        || { verify_human "referencectl: verify: no such step '$id'"; return 2; }
    done
  fi

  # seed-lib owns the compose/psql/token primitives; point it at this stack.
  # shellcheck source=../bootstrap/seed-lib.sh
  export SEED_KERNEL=$VERIFY_KERNEL SEED_IDP=$VERIFY_IDP SEED_REALM=$VERIFY_REALM
  . "$VERIFY_REF/bootstrap/seed-lib.sh"

  VERIFY_RESULTS=$(mktemp)
  trap 'rm -f "$VERIFY_RESULTS"' RETURN

  local id
  for id in "${VERIFY_ORDER[@]}"; do
    case " ${VERIFY_REQUESTED[*]} " in
      *" $id "*) verify_run_step "$id" ;;
    esac
  done

  # An unrecorded requested step (leg bailed early) counts as a fail.
  local missing_exit=0
  for id in "${VERIFY_REQUESTED[@]}"; do
    grep -q "^$id|" "$VERIFY_RESULTS" || { verify_step_fail "$id" 0 "step recorded nothing"; missing_exit=1; }
  done

  local exit_code=0
  grep -Eq '\|fail\||\|skipped' "$VERIFY_RESULTS" && exit_code=1
  [ "$missing_exit" = 1 ] && exit_code=1

  local report
  report=$(jq -Rs --arg profile slim --argjson exit "$exit_code" --argjson started "$VERIFY_T0" '
    { profile: $profile,
      startedAt: ($started | tostring),
      durationMs: 0,
      exitCode: $exit,
      steps: [ splits("\n") | select(length > 0)
              | capture("^(?<id>[^|]+)\\|(?<status>[^|]+)\\|(?<durationMs>[0-9]+)\\|(?<detail>.*)$")
              | { id, status, durationMs: (.durationMs | tonumber), detail } ] }' "$VERIFY_RESULTS")
  report=$(jq --argjson now "$(verify_now_ms)" --argjson started "$VERIFY_T0" \
    '.durationMs = ($now - $started)' <<<"$report")

  if [ "$json" = 1 ]; then
    printf '%s\n' "$report"
  else
    printf '%s\n' "$report" | jq -r '"\(.steps|length) steps · exit \(.exitCode) · \(.durationMs) ms"' >&2
  fi
  return "$exit_code"
}
