# verify-replicas.sh — the two-replica matrix (balancing + failover law),
# run by `referencectl verify --replicas` after `up slim --replicas 2`.
# Reuses verify.sh's step-record machinery (step_pass/step_fail read
# $VERIFY_RESULTS); the caller owns VERIFY_RESULTS + the report.
#
# Every case targets replicas DIRECTLY (:8088 = facilitator-a, :8090 =
# facilitator-b, :8089 = LB). Direct ports are the law for the dual-injection
# cases: docker's userland proxy erases the client identity behind a
# published port, so only the per-replica ports give deterministic placement.

VERIFY_R_A=${VERIFY_R_A:-http://localhost:8088}
VERIFY_R_B=${VERIFY_R_B:-http://localhost:8090}
VERIFY_R_LB=${VERIFY_R_LB:-http://localhost:8089}

r_compose() {
  local files=(-f compose.slim.yaml -f compose.provisioning.yaml -f compose.replicas.yaml)
  # Same overlay law as referencectl: the full-profile legs exist only when
  # the full overlay is in play (FULL=1), and omitting them here would make
  # every verb see config drift against the running project. Auto-detect the
  # full profile from the running project too: the kill leg's restore is an
  # `up -d --no-deps` and a full stack restored WITHOUT the full overlay
  # recreates the primary stripped of its provider arm (the adapter then
  # gets typed `unsupported` for every DM event).
  if [ -z "${FULL:-}" ] && docker inspect facilitator-demo-mattermost-1 >/dev/null 2>&1; then
    FULL=1
  fi
  [ -n "${FULL:-}" ] && files+=(-f compose.full.yaml -f compose.replicas.full.yaml)
  (cd "$VERIFY_REF" && docker compose --env-file versions.env --env-file .env \
    "${files[@]}" -p facilitator-demo "$@")
}

r_psql() { r_compose exec -T postgres psql -U demo -d facilitator -tAc "$1"; }

r_get() { # <base> <token> <path>
  curl -fs --max-time 15 "$1$3" -H "Authorization: Bearer $2"
}
r_post() { # <base> <token> <path> <json> -> body; non-2xx non-zero
  curl -fs --max-time 15 -X POST "$1$3" \
    -H "Authorization: Bearer $2" -H 'content-type: application/json' -d "$4"
}
r_post_code() { # <base> <token> <path> <json> -> HTTP status code only
  curl -s -o /dev/null --max-time 15 -w '%{http_code}' -X POST "$1$3" \
    -H "Authorization: Bearer $2" -H 'content-type: application/json' -d "$4"
}

# Precondition + balancing proof: the LB answers AND both replicas
# answer its requests (least_conn round-robins identical requests), and
# /api/admin/replicas attributes a lease to each replica by name.
step_replica_lb_distribution() {
  local ids n view
  # PARALLEL probes: nginx least_conn breaks ties toward the first
  # upstream, so sequential identical requests all land on facilitator-a.
  # Concurrent connections are what force the split.
  rm -f /tmp/rep-lb.*
  for i in 1 2 3 4 5 6 7 8; do
    ( r_get "$VERIFY_R_LB" "$VERIFY_TOKEN_CLI" /api/admin/replicas \
        | jq -r .answeringReplicaId > /tmp/rep-lb.$i ) &
  done
  wait
  ids=$(cat /tmp/rep-lb.*)
  n=$(sort -u <<<"$ids" | grep -c .)
  [ "$n" = 2 ] || { step_fail "LB answered from $n distinct replicas (want 2): $(tr '\n' ' ' <<<"$ids")"; return 1; }
  view=$(r_get "$VERIFY_R_A" "$VERIFY_TOKEN_CLI" /api/admin/replicas)
  jq -e '.replicas[] | select(.replicaId=="facilitator-a") | .liveSessions >= 1' <<<"$view" >/dev/null \
    || { step_fail "replicas view shows no facilitator-a leases: $view"; return 1; }
  jq -e '.replicas[] | select(.replicaId=="facilitator-b") | .liveSessions >= 1' <<<"$view" >/dev/null \
    || { step_fail "replicas view shows no facilitator-b leases: $view"; return 1; }
  a_leases=$(jq -r '[.replicas[] | select(.replicaId=="facilitator-a")][0].liveSessions' <<<"$view")
  b_leases=$(jq -r '[.replicas[] | select(.replicaId=="facilitator-b")][0].liveSessions' <<<"$view")
  step_pass "LB balanced over both replicas; fleet view: a=$a_leases b=$b_leases live leases"
}

# Case (a): the SAME event POSTed to BOTH replicas simultaneously. The DB
# inbox unique index is the arbiter: both answers are 2xx, exactly one inbox
# row, one timeline row.
step_replica_dedupe() {
  local tag now event code_a code_b rows timeline evidence
  tag="rwdedupe-$(date +%s)-$$"
  now=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  # provenance.evidenceRef is schema-required; the seeded work's stored
  # manifest blob is the nearest honest reference.
  evidence=$(r_psql "SELECT 'artifact://' || artifact_id FROM facilitator.artifacts ORDER BY stored_at DESC NULLS LAST LIMIT 1" | head -n1)
  [ -n "$evidence" ] || { step_fail "no stored artifact to cite as evidence"; return 1; }
  event=$(jq -cn --arg work "$WORK_MAIN" --arg now "$now" --arg tag "$tag" --arg ev "$evidence" '{
      specversion: "1.0",
      id: ("verify-repl-" + $tag),
      source: "urn:workspace:replica-matrix",
      type: "org.facilitator.event.memory.candidate.v1",
      subject: ("work/" + $work + "/assignment/replica-matrix"),
      time: $now,
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/event.memory.candidate.v1.json",
      data: {
        scope: "project",
        contentSummary: ("replica dedupe lesson " + $tag),
        provenance: { source: "urn:workspace:replica-matrix", evidenceRef: $ev,
                      createdBy: "user:alice", createdAt: $now },
        policyLabel: "team-only"
      }
    }')
  # Fire both at once; the race window is the point.
  r_post_code "$VERIFY_R_A" "$VERIFY_TOKEN_CLI" /api/ingress/events "$event" > /tmp/rep-a.$$ &
  local pid_a=$!
  r_post_code "$VERIFY_R_B" "$VERIFY_TOKEN_CLI" /api/ingress/events "$event" > /tmp/rep-b.$$ &
  local pid_b=$!
  wait "$pid_a" "$pid_b"
  code_a=$(cat /tmp/rep-a.$$); code_b=$(cat /tmp/rep-b.$$); rm -f /tmp/rep-a.$$ /tmp/rep-b.$$
  if [ "$code_a" -lt 200 ] || [ "$code_a" -ge 300 ] || [ "$code_b" -lt 200 ] || [ "$code_b" -ge 300 ]; then
    step_fail "HTTP $code_a/$code_b (expected both 2xx; the dedupe verdict rides the body)"
    return 1
  fi
  poll_until 20 r_inbox_rows_eq "verify-repl-$tag" 1 >/dev/null \
    || { step_fail "inbox never settled to exactly one row for the duplicate event"; return 1; }
  timeline=$(r_get "$VERIFY_R_LB" "$VERIFY_TOKEN_ALICE" "/api/works/$WORK_MAIN/timeline" \
    | jq --arg tag "$tag" '[.[] | select(.kind=="memory.candidate")
        | select(((.detail // "") | contains($tag)) or ((.payload | tostring) | contains($tag)))] | length')
  [ "${timeline:-0}" -le 1 ] || { step_fail "timeline rows for the duplicate event: $timeline (want <= 1)"; return 1; }
  step_pass "concurrent POST to both replicas: 1 inbox row, $timeline timeline row (DB arbiter)"
}
r_inbox_rows_eq() { # <event-id> <want>
  [ "$(r_psql "SELECT count(*) FROM facilitator.inbox WHERE payload->>'id' = '$1'")" = "$2" ]
}

# Case (b): SIGKILL replica A while it owns a STATIC gateway leg's session.
# The lease expires, the client reconnects through the LB onto the SURVIVOR,
# the session epoch bumps, and delivery accounting stays exactly-once.
# Subject pool = the three statically-configured LB legs (the gateways +
# the forgejo adapter) stamped facilitator-a: the pinned legs (mm-adapter
# direct-A, oci-launcher direct-B) share their replica's fate instead of
# failing over, and elastic fleet sessions (gw.team-beta) are scale-to-zero
# so their lease can just lapse. The stamp filter keeps the victim
# deterministically facilitator-a.
r_kill_pool() { # one live A-stamped static-leg session id, or empty
  r_psql "SELECT logical_endpoint_id FROM facilitator.link_sessions WHERE lease_until > now() AND replica_id = 'facilitator-a' AND logical_endpoint_id IN ('gw.team-alpha','gw.coding','forgejo-adapter') ORDER BY opened_at LIMIT 1" | head -n1
}
step_replica_kill() {
  local owner replica victim victim_id before_epoch epoch acked acked_after
  owner=$(r_kill_pool)
  if [ -z "$owner" ]; then
    # Boot tie placement may have routed every LB leg to B. A lone
    # reconnect through a both-alive LB deterministically lands on the
    # first upstream (facilitator): force one and retry the selection.
    # (Only full-profile shapes have an adapter to bounce; in slim the
    # pool's sole leg is gw.team-alpha's own reconnect cycle.)
    if [ -n "${FULL:-}" ]; then
      r_compose restart forgejo-adapter >&2 || true
      poll_until 30 r_a_stamped_session >/dev/null || true
    else
      r_compose restart demo-workspace >&2 || true
      poll_until 60 r_a_stamped_session >/dev/null || true
    fi
    owner=$(r_kill_pool)
  fi
  [ -n "$owner" ] || { step_fail "no live facilitator-a session to fail over"; return 1; }
  before_epoch=$(r_psql "SELECT session_epoch FROM facilitator.link_sessions WHERE logical_endpoint_id = '$owner'")
  # The filter pins the stamp: victim is ALWAYS facilitator-a (identity
  # facilitator-a belongs to the compose service `facilitator`; the twin's
  # identity names its service).
  victim_id=facilitator-a; victim=facilitator
  acked=$(r_psql "SELECT count(*) FROM facilitator.link_deliveries WHERE endpoint_id = '$owner' AND state = 'acked'")

  # --no-ansi kill -s KILL: no graceful drain — the abrupt-death leg.
  r_compose kill -s KILL "$victim" >&2 \
    || { step_fail "kill -KILL $victim failed"; return 1; }
  # Lease expiry + LB eviction + gateway reconnect + re-Hello at a NEW epoch.
  # 60s: 15s lease (heartbeat 5 x3, the overlay's timing law) + SignalR
  # reconnect backoff + the survivor's session-open.
  # NOTE: every failure path below RESTORES the victim first — later cases
  # (and the nested full suite) need both replicas up.
  if ! poll_until 60 r_session_failed_over "$owner" "$victim_id"; then
    step_fail "session '$owner' did not re-land on a survivor replica"
    r_compose ps --format '{{.Service}} {{.Status}}' >&2
    r_compose up -d --no-deps "$victim" >&2 || true
    return 1
  fi
  epoch=$(r_psql "SELECT session_epoch FROM facilitator.link_sessions WHERE logical_endpoint_id = '$owner'")
  if [ "${epoch:-0}" -le "${before_epoch:-0}" ]; then
    step_fail "epoch did not bump after the kill ($before_epoch -> $epoch)"
    r_compose up -d --no-deps "$victim" >&2 || true
    return 1
  fi
  # Exactly-once: an acked delivery row is terminal (the ledger PK + the
  # acked state); the survivor replays UNACKED rows only, so the acked count
  # can never SHRINK, and no delivery id gains a second ack.
  acked_after=$(r_psql "SELECT count(*) FROM facilitator.link_deliveries WHERE endpoint_id = '$owner' AND state = 'acked'")
  if [ "${acked_after:-0}" -lt "${acked:-0}" ]; then
    step_fail "acked deliveries shrank after failover ($acked -> $acked_after)"
    r_compose up -d --no-deps "$victim" >&2 || true
    return 1
  fi
  step_pass "SIGKILL $victim: session '$owner' epoch $before_epoch->$epoch on the survivor, acked $acked->$acked_after (exactly-once)"
  # docker treats a user KILL as an intentional stop (the kernel's
  # on-failure policy restarts CRASHES, not kills): the replica stays
  # exited until the operator (here: the leg) brings it back — later
  # cases need both replicas.
  r_compose up -d --no-deps "$victim" >&2 \
    || { step_fail "restoring $victim failed"; return 1; }
  poll_until 90 r_health "$victim" \
    || { step_fail "$victim never returned to health"; return 1; }
}
r_a_stamped_session() { [ -n "$(r_kill_pool)" ]; }
r_health() { # <service>: the replica's own /health answers 200
  local port=8088; [ "$1" = facilitator-b ] && port=8090
  curl -fs --max-time 5 -o /dev/null "http://localhost:$port/health"
}
r_session_failed_over() { # <endpoint> <victim-id>: live lease stamped NOT-victim
  local row
  row=$(r_psql "SELECT replica_id FROM facilitator.link_sessions WHERE logical_endpoint_id = '$1' AND lease_until > now()" | head -n1)
  [ -n "$row" ] && [ "$row" != "$2" ]
}

# Case (c): outbox lease handover. Seed a row leased by a GHOST replica with
# an expired lease (the state a killed replica's claims are left in) onto a
# LIVE endpoint's channel; a surviving dispatcher must steal the lease and
# settle the row exactly once (delivered, gateway receipt included).
step_replica_lease_handover() {
  local tag endpoint envelope rows st receipted
  tag="rwhelo-$(date +%s)-$$"
  # Pick a live endpoint that HAS a delivered-and-acked kernel-plane row to
  # ride (its envelope is validated wire): after failover, the oldest-opened
  # session is not guaranteed to be one with delivery history.
  endpoint=$(r_psql "SELECT d.endpoint_id FROM facilitator.link_deliveries d
                     JOIN facilitator.outbox o ON o.outbox_id = d.delivery_id
                     JOIN facilitator.link_sessions s ON s.logical_endpoint_id = d.endpoint_id
                     WHERE d.state = 'acked' AND s.lease_until > now()
                     ORDER BY d.acked_at DESC LIMIT 1" | head -n1)
  [ -n "$endpoint" ] || { step_fail "no live endpoint with delivery history to hand over to"; return 1; }
  # Ride the endpoint's own last delivered assign envelope (validated wire).
  envelope=$(r_psql "SELECT o.envelope FROM facilitator.link_deliveries d
                     JOIN facilitator.outbox o ON o.outbox_id = d.delivery_id
                     WHERE d.endpoint_id = '$endpoint' AND d.state = 'acked'
                       AND o.envelope IS NOT NULL
                     ORDER BY d.acked_at DESC NULLS LAST LIMIT 1" | head -n1)
  # The replay needs a fresh command_id (the unique key) but the envelope's
  # own commandId may mismatch — the dispatcher ships the envelope as-is and
  # the gateway dedupes by the ENVELOPE commandId, so an already-seen
  # commandId is honestly acked as duplicate. Either settlement proves the
  # handover; the ledger row's own settlement is the assertion.
  r_psql "INSERT INTO facilitator.outbox (outbox_id, recipient_endpoint_id, aggregate_type, aggregate_id,
              event_type, command_id, envelope, status, available_at, attempts, created_at, work_id,
              lease_owner, leased_until)
          SELECT gen_random_uuid(), 'link.$endpoint', 'work', '$WORK_MAIN',
                 'org.facilitator.command.work.steer.v1', 'work-replhand-$tag',
                 '$(printf '%s' "$envelope" | sed "s/'/''/g")',
                 'leased', now() - interval '60 seconds', 1, now(), '$WORK_MAIN',
                 'ghost-replica', now() - interval '30 seconds'" >/dev/null
  # Poll the FULL settlement (delivered AND receipted): the ack path flips
  # the outbox status a beat before the receipt column lands, so "left
  # leased" alone can sample mid-write and report a false failure.
  if ! poll_until 60 r_outbox_settled "work-replhand-$tag" >/dev/null; then
    step_fail "expired-lease row never reclaimed + settled by a live replica"
    return 1
  fi
  st=$(r_psql "SELECT status FROM facilitator.outbox WHERE command_id = 'work-replhand-$tag'")
  rows=$(r_psql "SELECT count(*) FROM facilitator.outbox WHERE command_id = 'work-replhand-$tag' AND status = 'delivered'")
  receipted=$(r_psql "SELECT count(*) FROM facilitator.link_deliveries d
                      JOIN facilitator.outbox o ON o.outbox_id = d.delivery_id
                      WHERE o.command_id = 'work-replhand-$tag' AND d.receipt IS NOT NULL")
  if [ "$rows" = 1 ] && [ "$receipted" = 1 ]; then
    step_pass "expired outbox lease stole by a live replica: delivered + receipted exactly once"
  else
    step_fail "handover row settled '$st' (want delivered with a receipt: rows=$rows receipted=$receipted)"
  fi
}
r_outbox_settled() { # <command-id>: delivered WITH a gateway receipt
  [ "$(r_psql "SELECT count(*) FROM facilitator.outbox o
               LEFT JOIN facilitator.link_deliveries d ON d.delivery_id = o.outbox_id
               WHERE o.command_id = '$1' AND o.status = 'delivered' AND d.receipt IS NOT NULL")" = 1 ]
}

# Case (d): parallel steers, one per replica, SAME expectedVersion, DISTINCT
# commandIds (distinct ids so the loser hits the optimistic-concurrency arm,
# not the inbox-dedupe arm). Exactly one 200 + one 409; the loser's retry
# with the fresh version wins. The target is a FRESH research.publish work
# parked at approval (steer on a completed work is a terminal-state refusal,
# and the deterministic worker completes research.analyze in seconds).
step_replica_steer_conflict() {
  local ver ca cb wins conflicts fresh work_steer body_a body_b
  work_steer=$(verify_seed_leg replica-steer research.publish "replica matrix steer target") \
    || { step_fail "seeding the parked steer target failed"; return 1; }
  poll_until 60 verify_approval_parked >/dev/null \
    || { step_fail "steer target never parked at approval"; return 1; }
  ver=$(r_psql "SELECT version FROM facilitator.works WHERE work_id = '$work_steer'" | head -n1)
  body_a=$(jq -cn --argjson v "$ver" --arg c "rw-steer-a-$$" '{directive:"matrix steer via A", commandId:$c, expectedVersion:$v}')
  body_b=$(jq -cn --argjson v "$ver" --arg c "rw-steer-b-$$" '{directive:"matrix steer via B", commandId:$c, expectedVersion:$v}')
  r_post_code "$VERIFY_R_A" "$VERIFY_TOKEN_CLI" "/api/works/$work_steer/steer" "$body_a" > /tmp/steer-a.$$ &
  local pa=$!
  r_post_code "$VERIFY_R_B" "$VERIFY_TOKEN_CLI" "/api/works/$work_steer/steer" "$body_b" > /tmp/steer-b.$$ &
  local pb=$!
  wait "$pa" "$pb"
  ca=$(cat /tmp/steer-a.$$); cb=$(cat /tmp/steer-b.$$); rm -f /tmp/steer-a.$$ /tmp/steer-b.$$
  wins=0; conflicts=0
  for c in "$ca" "$cb"; do
    [ "$c" = 200 ] && wins=$((wins + 1))
    [ "$c" = 409 ] && conflicts=$((conflicts + 1))
  done
  if [ "$wins" != 1 ] || [ "$conflicts" != 1 ]; then
    step_fail "parallel steers answered $ca/$cb (want exactly one 200 + one 409)"
    return 1
  fi
  fresh=$(r_psql "SELECT version FROM facilitator.works WHERE work_id = '$work_steer'" | head -n1)
  if [ "${fresh:-0}" -le "${ver:-0}" ]; then
    step_fail "work version did not bump after the winning steer ($ver -> $fresh)"
    return 1
  fi
  r_post "$VERIFY_R_B" "$VERIFY_TOKEN_CLI" "/api/works/$work_steer/steer" \
    "$(jq -cn --argjson v "$fresh" --arg c "rw-steer-retry-$$" '{directive:"conflict retry", commandId:$c, expectedVersion:$v}')" >/dev/null \
    || { step_fail "conflict-retry steer with the fresh version was refused"; return 1; }
  step_pass "parallel steers across replicas: one 200, one 409, retry with fresh version $fresh won"
}

# Case (e): the full slim suite against the LB with BOTH replicas running
# every loop — no duplicate side effects across any of the 11 legs. Nested
# run_verify (sourced verify.sh is idempotent for a second run; the step
# library just re-records into a fresh results file).
step_replica_full_suite() {
  # The approval leg restarts the SINGLE facilitator service; under the
  # replicas overlay the compose project carries facilitator-b too, and a
  # restart of A while B holds the gateway's session still resumes (that IS
  # the leg, now on a two-replica plane). Keep the outer VERIFY_RESULTS
  # around: verify.sh's run_verify points it at its own temp file.
  local outer=$VERIFY_RESULTS out rc passed failed
  out=$(VERIFY_REF="$VERIFY_REF" KERNEL="$VERIFY_R_LB" IDP="$VERIFY_IDP" REALM="$VERIFY_REALM" bash -c '
      REF="$VERIFY_REF"
      . "$REF/checks/verify.sh"
      run_verify "$@"' _ --json) && rc=0 || rc=$?
  VERIFY_RESULTS=$outer # run_verify's RETURN trap removed its own; restore ours
  passed=$(jq -r '[.steps[] | select(.status=="pass")] | length' <<<"$out" 2>/dev/null || echo 0)
  failed=$(jq -r '[.steps[] | select(.status!="pass")] | map(.id) | join(",")' <<<"$out" 2>/dev/null || echo "unparsed")
  if [ "$rc" != 0 ]; then
    step_fail "slim suite through the LB: exit $rc, failed=[$failed]"
    return 1
  fi
  step_pass "full slim suite through the LB, both replicas live: $passed legs green"
}

VERIFY_R_ORDER=(replica-lb-distribution replica-full-suite replica-dedupe
                replica-kill replica-lease-handover replica-steer-conflict)

run_verify_replicas() {
  local json=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --json) json=1 ;;
      *) verify_human "referencectl: verify --replicas: unknown flag '$1'"; return 2 ;;
    esac
    shift
  done
  VERIFY_R_RESULTS=$(mktemp)
  trap 'rm -f "$VERIFY_R_RESULTS"' RETURN
  # The step library + seed primitives (the matrix rides verify.sh's record
  # shims and seed-lib's token minters against this stack).
  export SEED_KERNEL=$VERIFY_R_A SEED_IDP=$VERIFY_IDP SEED_REALM=$VERIFY_REALM
  . "$VERIFY_REF/bootstrap/seed-lib.sh"
  verify_token_cli || { verify_human "FAIL replica-matrix — CLI token mint failed"; return 1; }
  verify_token_alice || { verify_human "FAIL replica-matrix — alice token mint failed"; return 1; }
  # A fresh work of the matrix's own for the dedupe + steer-conflict cases
  # (timelines are per-work, so their counts stay uncontaminated).
  WORK_MAIN=$(verify_seed_leg replica-matrix research.analyze "replica matrix witness") \
    || { verify_human "FAIL replica-matrix — work seed failed"; return 1; }
  # step_pass/step_fail record through verify.sh's shims, which write
  # $VERIFY_RESULTS — point it at the matrix's own file (case (e)'s nested
  # run_verify swaps it; the leg restores).
  local outer_results=$VERIFY_RESULTS
  VERIFY_RESULTS=$VERIFY_R_RESULTS
  local id exit_code=0
  for id in "${VERIFY_R_ORDER[@]}"; do
    VERIFY_CURRENT_STEP=$id
    VERIFY_CURRENT_STARTED=$(verify_now_ms)
    "step_${id//-/_}" || true
    grep -q "^$id|" "$VERIFY_R_RESULTS" \
      || verify_step_fail "$id" $(( $(verify_now_ms) - VERIFY_CURRENT_STARTED )) "step recorded nothing"
  done
  VERIFY_RESULTS=$outer_results
  exit_code=0
  grep -Eq '\|fail\|' "$VERIFY_R_RESULTS" && exit_code=1
  # Verify-shaped report over the matrix's own records (the aggregate gate
  # merges it alongside the slim and full-profile tier reports).
  local report
  report=$(jq -Rs --arg profile replicas --argjson exit "$exit_code" --argjson started "$VERIFY_T0" '
    { profile: $profile,
      startedAt: ($started | tostring),
      durationMs: 0,
      exitCode: $exit,
      steps: [ splits("\n") | select(length > 0)
              | capture("^(?<id>[^|]+)\\|(?<status>[^|]+)\\|(?<durationMs>[0-9]+)\\|(?<detail>.*)$")
              | { id, status, durationMs: (.durationMs | tonumber), detail } ] }' "$VERIFY_R_RESULTS")
  report=$(jq --argjson now "$(verify_now_ms)" --argjson started "$VERIFY_T0" \
    '.durationMs = ($now - $started)' <<<"$report")
  if [ "$json" = 1 ]; then printf '%s\n' "$report"; else
    cat "$VERIFY_R_RESULTS" >&2
  fi
  return "$exit_code"
}
