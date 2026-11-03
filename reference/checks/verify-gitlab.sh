#!/usr/bin/env bash
# verify-gitlab.sh — the on-demand GitLab leg set (the GitLab counterpart of
# verify-full.sh's forgejo legs; NOT wired into `make provider-e2e`).
#
# Contract: the FULL reference stack is up (`make compose-full`) AND an
# on-demand GitLab CE is reachable (`make gitlab-up`, which boots the
# container and runs tools/gitlab/init-gitlab.sh). Append-only demo data.
# Every leg SKIPs with "SKIP gitlab: instance not reachable" when there is
# no instance, so the script is safe to run on a provider-less machine.
#
# Step order (deps run first; --only id,id selects):
#   gitlab-boot-check    instance + kernel sink + adapter session alive
#   gitlab-seed          seeded project / label / issue / kernel-sink webhook
#   gitlab-issue-work    label a fresh issue -> Work row + binding + bot ack
#   gitlab-comment-input note on the bound issue -> provider.input
#   gitlab-review-steer  human note w/ @bot on the bound thread -> steer
#   gitlab-discussion    connector discussion reply + resolve ops execute
#   gitlab-pipeline-note synthesized Build Hook -> provider_events row
#
# The "worker" is the deterministic gitlab-connector op table (no LLM): the
# discussion ops ride a raw-outbox assign exactly like verify-full's
# forgejo-connector-pr leg.
set -euo pipefail

VERIFY_REF=${REF:-$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)}
export REF=$VERIFY_REF # verify.sh resolves VERIFY_REF from REF
# shellcheck source=verify.sh
. "$VERIFY_REF/checks/verify.sh" # helpers only — run_verify is never called
. "$VERIFY_REF/bootstrap/seed-lib.sh" # seed_new_uuid for raw-outbox assigns

VERIFY_GL=${GITLAB_URL:-http://127.0.0.1:8929}
# Recent GitLab disables password app-auth, so the legs ride the same
# deterministic root PAT tools/gitlab/init-gitlab.sh mints.
VERIFY_GL_TOKEN=${GITLAB_ROOT_PAT:-ofac-root-init-token}
VERIFY_GL_PROJECT=${GITLAB_INIT_PROJECT_PATH:-gitlab-org/gitlab-test}
VERIFY_GL_KERNEL=${KERNEL:-http://127.0.0.1:8088}

# Kernel-plane psql against the full stack (same compose project as verify.sh).
gl_compose() {
  local ro=()
  [ -n "${REPLICAS:-}" ] && ro=(-f compose.replicas.yaml -f compose.replicas.full.yaml)
  (cd "$VERIFY_REF" && docker compose --env-file versions.env --env-file .env \
    -f compose.slim.yaml -f compose.provisioning.yaml -f compose.full.yaml "${ro[@]}" -p facilitator-demo "$@")
}
gl_psql() { gl_compose exec -T postgres psql -U demo -d facilitator -tAc "$1"; }
gl_logs() {
  local svc
  verify_human "--- last 20 lines: ${*} ---"
  for svc in "$@"; do
    gl_compose logs --tail=20 --no-log-prefix "$svc" 2>&1 | sed "s/^/[$svc] /" >&2
  done
}

# Recent GitLab restricts /-/readiness to in-container IPs, so reachability
# rides the sign-in page: a 200 there means rails + puma + nginx all answer.
gl_reachable() { curl -fs --max-time 5 "$VERIFY_GL/users/sign_in" >/dev/null 2>&1; }

gl_api() { # gl_api METHOD PATH [BODY] — root PAT plane
  local method=$1 path=$2 body=${3:-}
  local args=(-fsS --max-time 15 -X "$method" -H "PRIVATE-TOKEN: $VERIFY_GL_TOKEN" "$VERIFY_GL$path")
  [ -n "$body" ] && args+=(-H 'Content-Type: application/json' -d "$body")
  curl "${args[@]}"
}

GL_PROJECT_ID=""
GL_ISSUE=""
GL_WORK=""
GL_MR=""
GL_MR_SHA=""

gl_resolve_project() {
  local encoded
  encoded=$(jq -rn --arg p "$VERIFY_GL_PROJECT" '$p|@uri')
  GL_PROJECT_ID=$(gl_api GET "/api/v4/projects/$encoded" | jq -r '.id // empty')
  [ -n "$GL_PROJECT_ID" ]
}

# One raw-outbox work.assign to the reference gateway fleet (verify-full's
# forgejo-connector-pr shape): the connector executes the op under the
# fleet's allow policy.
gl_assign() { # <capability> <work> <envelope-json>
  local command_id assignment_id
  command_id=$(jq -r '.data.commandId' <<<"$3")
  assignment_id=$(jq -r '.data.assignmentId' <<<"$3")
  gl_psql "INSERT INTO facilitator.outbox
    (outbox_id, recipient_endpoint_id, aggregate_type, aggregate_id, event_type,
    command_id, envelope, work_id, status, available_at, attempts, created_at)
  VALUES (gen_random_uuid(), 'link.gw.team-alpha', 'work', '$2',
    'org.facilitator.command.work.assign.v1', '$command_id',
    '$3'::jsonb, '$2', 'pending', now(), 0, now());
  SELECT pg_notify('facilitator_outbox', '');" >/dev/null
}
gl_envelope() { # <capability> <work> <inputs-json-expr> <jq-args...>
  local capability=$1 work=$2 inputs_expr=$3
  shift 3
  local command_id assignment_id
  command_id=$(seed_new_uuid); assignment_id=$(seed_new_uuid)
  jq -cn --arg commandId "$command_id" --arg workId "$work" --arg assignmentId "$assignment_id" \
    --arg capability "$capability" "$@" "{
      specversion: \"1.0\", id: \$commandId,
      source: \"urn:facilitator:instance:demo\",
      type: \"org.facilitator.command.work.assign.v1\",
      subject: \"work/\" + \$workId + \"/assignment/\" + \$assignmentId,
      time: now | todate, datacontenttype: \"application/json\",
      dataschema: \"https://schemas.openfacilitator.org/v1/command.work.assign.v1.json\",
      data: { commandId: \$commandId, actorEvidenceRef: \"seed: verify-gitlab\",
        workId: \$workId, targetId: \"demo\", capabilityId: \$capability,
        expectedContextVersion: 1, executionGeneration: 1,
        inputs: ({ objective: \"verify gitlab leg\", sourceRefs: [] } + $inputs_expr),
        expectedDeliverables: [{ kind: \"note.ref\", mediaType: \"application/json\" }],
        deadline: \"2036-10-23T18:00:00Z\", assignmentId: \$assignmentId }
    }"
}

# --- legs ----------------------------------------------------------------------

step_gitlab_boot_check() {
  gl_reachable || { step_fail "gitlab readiness endpoint unreachable at $VERIFY_GL"; return 1; }
  # The kernel must own an enabled Providers:Gitlab section: the sink answers
  # 404 (provider-unknown) when it does not.
  local sink_status
  sink_status=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -X POST \
    "$VERIFY_GL_KERNEL/api/ingress/webhooks/gitlab" \
    -H 'Content-Type: application/json' -H 'X-Gitlab-Event: Ping Hook' \
    -H "X-Request-Id: verify-ping-$(date +%s)-$$" \
    -d '{"object_kind":"ping","hook_id":0,"event_name":"ping"}')
  case "$sink_status" in
    202) ;;
    404) step_fail "kernel has no enabled Providers__Gitlab__ section (sink 404)"; return 1 ;;
    *) step_fail "webhook sink answered $sink_status (expected 202)"; return 1 ;;
  esac
  # The adapter's link session must be live on the kernel.
  gl_psql "SELECT 1 FROM facilitator.link_sessions WHERE logical_endpoint_id = 'gitlab-adapter' LIMIT 1" | grep -q 1 \
    || { step_fail "no live link_sessions row for the gitlab-adapter endpoint"; gl_logs facilitator; return 1; }
  step_pass "gitlab reachable, kernel sink accepts deliveries, adapter session live"
}

step_gitlab_seed() {
  gl_resolve_project || { step_fail "seed project $VERIFY_GL_PROJECT missing (run tools/gitlab/init-gitlab.sh)"; return 1; }
  gl_api GET "/api/v4/projects/$GL_PROJECT_ID/labels" |
    jq -e --arg n 'ofac:work' '[.[] | select((.name // .title)==$n)] | length >= 1' >/dev/null \
    || { step_fail "ofac:work label missing"; return 1; }
  gl_api GET "/api/v4/projects/$GL_PROJECT_ID/issues?state=all" |
    jq -e '[.[] | select(.title == "Seed: summarize the repository layout")] | length >= 1' >/dev/null \
    || { step_fail "sample issue missing"; return 1; }
  gl_api GET "/api/v4/projects/$GL_PROJECT_ID/hooks" |
    jq -e --arg u "$VERIFY_GL_KERNEL/api/ingress/webhooks/gitlab" '[.[] | select(.url == $u)] | length >= 1' >/dev/null \
    || { step_fail "no webhook registered at the kernel sink"; return 1; }
  step_pass "seed project, work label, sample issue, kernel-sink webhook all exist"
}

step_gitlab_issue_work() {
  gl_resolve_project || { step_fail "project $VERIFY_GL_PROJECT unresolvable"; return 1; }
  local title="verify gitlab $(date +%s)$RANDOM"
  GL_ISSUE=$(gl_api POST "/api/v4/projects/$GL_PROJECT_ID/issues" \
    "$(jq -cn --arg t "$title" '{title:$t, description:"verify leg: labeling adds a Work"}')" | jq -re '.iid') \
    || { step_fail "issue creation refused"; return 1; }
  gl_api PUT "/api/v4/projects/$GL_PROJECT_ID/issues/$GL_ISSUE" \
    "$(jq -cn '{add_labels:"ofac:work"}')" >/dev/null \
    || { step_fail "labeling refused"; return 1; }
  gl_binding() {
    GL_WORK=$(gl_psql "SELECT work_id FROM facilitator.communication_bindings WHERE provider = 'gitlab' AND channel = '$VERIFY_GL_PROJECT' AND root_message_id = '$GL_ISSUE' LIMIT 1" | head -n1)
    [ -n "$GL_WORK" ]
  }
  poll_until 60 gl_binding >/dev/null \
    || { step_fail "no binding row 60s after labeling issue #$GL_ISSUE"; gl_logs facilitator; return 1; }
  gl_ack() {
    gl_api GET "/api/v4/projects/$GL_PROJECT_ID/issues/$GL_ISSUE/notes" |
      jq -e '[.[] | select(.author.username == "facilitator-bot")] | length >= 1' >/dev/null
  }
  poll_until 45 gl_ack \
    || { step_fail "no facilitator-bot ack note on issue #$GL_ISSUE"; gl_logs facilitator; return 1; }
  step_pass "labeled issue #$GL_ISSUE created Work $GL_WORK + bot ack note"
}

step_gitlab_comment_input() {
  gl_api POST "/api/v4/projects/$GL_PROJECT_ID/issues/$GL_ISSUE/notes" \
    '{"body":"verify: also cover the timeout path"}' >/dev/null \
    || { step_fail "note POST refused"; return 1; }
  gl_input() {
    gl_psql "SELECT 1 FROM facilitator.work_events WHERE work_id = '$GL_WORK' AND kind = 'provider.input' AND detail::text LIKE '%timeout path%' LIMIT 1" | grep -q 1
  }
  poll_until 45 gl_input \
    || { step_fail "no provider.input note 45s after the issue note"; gl_logs facilitator; return 1; }
  step_pass "issue note landed as provider.input on $GL_WORK"
}

step_gitlab_review_steer() {
  # A human note mentioning the bot on the bound thread is the steer input
  # (the mention law fires the companion mention.v1 on the kernel side).
  gl_api POST "/api/v4/projects/$GL_PROJECT_ID/issues/$GL_ISSUE/notes" \
    '{"body":"verify steer: reword the retry clause @facilitator-bot"}' >/dev/null \
    || { step_fail "review note POST refused"; return 1; }
  gl_steered() {
    gl_psql "SELECT 1 FROM facilitator.work_events WHERE work_id = '$GL_WORK' AND kind = 'provider.input' AND detail::text LIKE '%retry clause%' LIMIT 1" | grep -q 1
  }
  poll_until 45 gl_steered \
    || { step_fail "no provider.input steer note 45s after the review comment"; gl_logs facilitator; return 1; }
  step_pass "human @-bot review note annotated $GL_WORK"
}

step_gitlab_discussion() {
  # The connector's discussion ops need an MR with a discussion thread:
  # open one through the API (root = the "human" actor), post a note (the
  # MR-note note creates the thread), then route reply + resolve through
  # raw-outbox assigns (the fleet's allow-all gateway executes them).
  gl_resolve_project || { step_fail "project unresolvable"; return 1; }
  local source_branch="bot/verify-$(date +%s)"
  gl_api POST "/api/v4/projects/$GL_PROJECT_ID/repository/branches" \
    "$(jq -cn --arg b "$source_branch" --arg r main '{branch:$b, ref:$r}')" >/dev/null || true
  local existing
  existing=$(gl_api GET "/api/v4/projects/$GL_PROJECT_ID/merge_requests?source_branch=$source_branch&state=all" |
    jq -r '.[0].iid // empty')
  if [ -z "$existing" ]; then
    GL_MR=$(gl_api POST "/api/v4/projects/$GL_PROJECT_ID/merge_requests" \
      "$(jq -cn --arg s "$source_branch" '{source_branch:$s, target_branch:"main", title:"verify: discussion leg", description:"opened by verify-gitlab"}')" | jq -re '.iid') \
      || { step_fail "MR creation refused"; return 1; }
  else
    GL_MR=$existing
  fi
  gl_mr_sha() {
    GL_MR_SHA=$(gl_api GET "/api/v4/projects/$GL_PROJECT_ID/merge_requests/$GL_MR" | jq -r '.diff_head_sha // .sha // empty')
    [ -n "$GL_MR_SHA" ] && [ "$GL_MR_SHA" != "null" ]
  }
  poll_until 20 gl_mr_sha >/dev/null || { step_fail "MR sha unresolvable"; return 1; }
  local discussion_id
  discussion_id=$(gl_api POST "/api/v4/projects/$GL_PROJECT_ID/merge_requests/$GL_MR/notes" \
    '{"body":"verify: needs a coauthor trailer"}' | jq -re '.id') \
    || { step_fail "MR note POST refused"; return 1; }
  local envelope
  envelope=$(gl_envelope vcs.discussion.reply "$GL_WORK" \
    '{ repo: $repo, issue: $mr, discussionId: $disc, body: "verify: reworked as asked" }' \
    --arg repo "$VERIFY_GL_PROJECT" --argjson mr "$GL_MR" --arg disc "$discussion_id")
  gl_assign vcs.discussion.reply "$GL_WORK" "$envelope" \
    || { step_fail "discussion.reply assign outbox insert failed"; return 1; }
  gl_reply_landed() {
    gl_api GET "/api/v4/projects/$GL_PROJECT_ID/merge_requests/$GL_MR/discussions" |
      jq -e '[.. | objects | select(.body? == "verify: reworked as asked")] | length >= 1' >/dev/null
  }
  poll_until 90 gl_reply_landed \
    || { step_fail "no discussion reply 90s after the assign"; gl_logs facilitator; return 1; }
  envelope=$(gl_envelope vcs.discussion.resolve "$GL_WORK" \
    '{ repo: $repo, issue: $mr, discussionId: $disc }' \
    --arg repo "$VERIFY_GL_PROJECT" --argjson mr "$GL_MR" --arg disc "$discussion_id")
  gl_assign vcs.discussion.resolve "$GL_WORK" "$envelope" \
    || { step_fail "discussion.resolve assign outbox insert failed"; return 1; }
  gl_resolved() {
    gl_api GET "/api/v4/projects/$GL_PROJECT_ID/merge_requests/$GL_MR/discussions" |
      jq -e --arg d "$discussion_id" '[.[] | select(.id == $d)] | .[0].resolved == true' >/dev/null
  }
  poll_until 90 gl_resolved \
    || { step_fail "thread unresolved 90s after the resolve assign"; gl_logs facilitator; return 1; }
  step_pass "connector replied to and resolved discussion $discussion_id on MR !$GL_MR"
}

step_gitlab_pipeline_note() {
  # GitLab fires Build Hooks only with a CI runner; the reference instance
  # has none, so synthesize the delivery exactly like verify-full's forgejo
  # ci-note leg: byte-shaped like the docs' Build Hook sample,
  # token-signed (GitLab's proof is the shared token header), per-run
  # nonce (the sink dedupes on sha256(rawBody)).
  local token=${GITLAB_WEBHOOK_TOKEN:?GITLAB_WEBHOOK_TOKEN required (same value the bootstrap registered)}
  local body
  body=$(jq -cn --arg sha "${GL_MR_SHA:-00000000}" --arg who "root" --arg nonce "$(date +%s)$RANDOM" '{
      object_kind: "build",
      build_stage: "test", build_name: ("verify-build-" + $nonce),
      build_status: "failed", build_ref: "main", build_sha: $sha,
      build_failure_reason: "script_failure",
      project_name: "gitlab-test",
      repository: { name: "gitlab-test", homepage_url: "http://127.0.0.1:8929/gitlab-org/gitlab-test" },
      commit: { sha: $sha, message: "verify build leg" },
      user: { username: $who }
    }')
  curl -fsS --max-time 10 -X POST "$VERIFY_GL_KERNEL/api/ingress/webhooks/gitlab" \
    -H 'Content-Type: application/json' \
    -H "X-Gitlab-Token: $token" \
    -H 'X-Gitlab-Event: Build Hook' \
    -H "X-Request-Id: verify-build-$(date +%s)-$RANDOM" \
    --data-binary "$body" >/dev/null \
    || { step_fail "token-signed build delivery refused"; return 1; }
  # The delivery's proof floor: the sink persisted the fan-out outbox row
  # (command.provider.webhook.v1, body-hash dedupe key) and the dispatcher
  # delivered it to the adapter session. A pipeline EVENT publishes only
  # when the build's sha/MR resolves to a bound Work — an honest drop on an
  # unbound sha is the same law as forgejo's ci arm.
  local command_id
  command_id="webhook:gitlab:$(printf '%s' "$body" | sha256sum | awk '{print $1}')"
  gl_delivered() {
    gl_psql "SELECT status FROM facilitator.outbox WHERE command_id = '$command_id' LIMIT 1" | grep -Eq 'delivered|processed'
  }
  poll_until 45 gl_delivered \
    || { step_fail "fan-out outbox row never reached delivered for $command_id"; gl_logs facilitator; return 1; }
  step_pass "synthesized Build Hook rode sink -> outbox fan-out -> adapter session"
}

# --- runner --------------------------------------------------------------------

GL_ORDER=(gitlab-boot-check gitlab-seed gitlab-issue-work gitlab-comment-input
  gitlab-review-steer gitlab-discussion gitlab-pipeline-note)
GL_RESULTS=$(mktemp)
trap 'rm -f "$GL_RESULTS"' EXIT
VERIFY_RESULTS=$GL_RESULTS
GL_REQUESTED=()
gl_deps_of() {
  case "$1" in
    gitlab-seed | gitlab-issue-work) echo gitlab-boot-check ;;
    gitlab-comment-input) echo gitlab-issue-work ;;
    gitlab-review-steer) echo gitlab-comment-input ;;
    gitlab-discussion | gitlab-pipeline-note) echo gitlab-issue-work ;;
    *) ;;
  esac
}

gl_skip_all() { # <reason>
  local id
  for id in "${GL_ORDER[@]}"; do
    verify_record "$id" skipped 0 "$1"
  done
}

gl_run_step() { # <id>
  local id=$1 dep started
  for dep in $(gl_deps_of "$id"); do
    grep -q "^$dep|" "$GL_RESULTS" || { gl_run_step "$dep" || true; }
    if grep -q "^$dep|fail\|^$dep|skipped" "$GL_RESULTS"; then
      verify_step_skip "$id" "dependency $dep did not pass"
      return 0
    fi
  done
  started=$(verify_now_ms)
  VERIFY_CURRENT_STEP=$id VERIFY_CURRENT_STARTED=$started
  case "$id" in
    gitlab-boot-check) step_gitlab_boot_check ;;
    gitlab-seed) step_gitlab_seed ;;
    gitlab-issue-work) step_gitlab_issue_work ;;
    gitlab-comment-input) step_gitlab_comment_input ;;
    gitlab-review-steer) step_gitlab_review_steer ;;
    gitlab-discussion) step_gitlab_discussion ;;
    gitlab-pipeline-note) step_gitlab_pipeline_note ;;
    *) verify_step_skip "$id" "unknown step" ;;
  esac
}

main() {
  local only=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --only) only=${2:?--only needs a comma-separated step list}; shift ;;
      *) echo "verify-gitlab: unknown flag '$1'" >&2; return 2 ;;
    esac
    shift
  done
  GL_REQUESTED=("${GL_ORDER[@]}")
  if [ -n "$only" ]; then
    IFS=, read -r -a GL_REQUESTED <<<"$only"
    local id
    for id in "${GL_REQUESTED[@]}"; do
      printf '%s\n' "${GL_ORDER[@]}" | grep -qx "$id" \
        || { echo "verify-gitlab: no such step '$id'" >&2; return 2; }
    done
  fi
  if ! gl_reachable; then
    gl_skip_all "gitlab: instance not reachable ($VERIFY_GL)"
    printf 'SKIP gitlab: instance not reachable (%s)\n' "$VERIFY_GL" >&2
    printf '%s\n' "${GL_REQUESTED[@]}" | while read -r id; do
      printf 'SKIP %s — gitlab: instance not reachable\n' "$id" >&2
    done
    return 0
  fi
  local id
  for id in "${GL_ORDER[@]}"; do
    case " ${GL_REQUESTED[*]} " in
      # Continue-and-report: a failing step records and the run proceeds.
      *" $id "*) gl_run_step "$id" || true ;;
    esac
  done
  local missing_exit=0
  for id in "${GL_REQUESTED[@]}"; do
    grep -q "^$id|" "$GL_RESULTS" || { verify_step_fail "$id" 0 "step recorded nothing"; missing_exit=1; }
  done
  local exit_code=0
  grep -Eq '\|fail\|' "$GL_RESULTS" && exit_code=1
  [ "$missing_exit" = 1 ] && exit_code=1
  local report
  report=$(jq -Rs --arg profile gitlab --argjson exit "$exit_code" '
    { profile: $profile, exitCode: $exit,
      steps: [ splits("\n") | select(length > 0)
              | capture("^(?<id>[^|]+)\\|(?<status>[^|]+)\\|(?<durationMs>[0-9]+)\\|(?<detail>.*)$")
              | { id, status, durationMs: (.durationMs | tonumber), detail } ] }' "$GL_RESULTS")
  if [ "${GL_JSON:-0}" = 1 ]; then printf '%s\n' "$report"; else
    printf '%s\n' "$report" | jq -r '"\(.steps|length) steps · exit \(.exitCode)"' >&2
  fi
  return "$exit_code"
}

main "$@"
