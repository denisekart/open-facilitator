#!/usr/bin/env bash
# verify-full.sh — the full-profile provider-ingest acceptance run (Plan 22
# step 5, the local gate; hosted ref-env CI is commented out by free-tier law).
# Sources verify.sh for the shared primitives (recording, polling, psql,
# kernel REST) and drives the Mattermost leg over the published provider port:
# a real @facilitator mention creates a Work, the deterministic worker runs it,
# the completion reply lands in the same thread, the bot's own posts echo
# without arm effects, a reaction audits but never consents, and /link in a DM
# mints a challenge that redeems against a Keycloak identity.
#
# Contract: the FULL stack is already up (`make compose-full` or
# `FULL=1 ./referencectl up slim`); append-only demo data; exit 0 iff every
# requested step passes. --only id,id selects steps (deps run first).
set -euo pipefail

VERIFY_REF=${REF:-$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)}
# shellcheck source=verify.sh
. "$VERIFY_REF/checks/verify.sh" # helpers only — run_verify is never called

VERIFY_MM=${MM_URL:-http://127.0.0.1:8065}
VERIFY_ADAPTER=${ADAPTER_URL:-http://127.0.0.1:8066}
VERIFY_MM_ADMIN_EMAIL=${MM_ADMIN_EMAIL:-admin@ofac.local}
# The admin password lives in the compose env file the stack itself booted with.
VERIFY_MM_ADMIN_PASSWORD=$(cd "$VERIFY_REF" && awk -F= '/^DEMO_MM_ADMIN_PASSWORD=/{print $2}' .env)
[ -n "$VERIFY_MM_ADMIN_PASSWORD" ] || { echo "verify-full: DEMO_MM_ADMIN_PASSWORD missing from reference/.env" >&2; exit 2; }

# seed-lib owns the token primitives (verify.sh only sources it inside
# run_verify, which we don't call).
export SEED_KERNEL=$VERIFY_KERNEL SEED_IDP=$VERIFY_IDP SEED_REALM=$VERIFY_REALM
# shellcheck source=../bootstrap/seed-lib.sh
. "$VERIFY_REF/bootstrap/seed-lib.sh"

VERIFY_RESULTS=$(mktemp)
trap 'rm -f "$VERIFY_RESULTS"' EXIT
VERIFY_T0=$(verify_now_ms)
VERIFY_REQUESTED=()

# Step order + declared dependencies (same continue-and-report law as slim).
FULL_ORDER=(provider-boot provider-link-challenge provider-mention
  provider-completion-reply provider-echo-suppression provider-reaction-no-consent)
full_deps_of() {
  case "$1" in
    provider-link-challenge) echo provider-boot ;;
    provider-mention) echo provider-boot provider-link-challenge ;;
    provider-completion-reply | provider-echo-suppression | provider-reaction-no-consent) echo provider-mention ;;
    *) ;;
  esac
}

# --- Mattermost REST helpers (session-token plane) ----------------------------
mm_token=""   # ofac-admin session token (the linked human identity)
mm_bot_id=""  # bot user id (echo author)

mm_api() { # mm_api METHOD PATH [BODY] — MM_TOKEN env carries the session
  local method=$1 path=$2 body=${3:-}
  local args=(-fsS --max-time 10 -X "$method" "$VERIFY_MM$path" -H 'Content-Type: application/json')
  [ -n "$body" ] && args+=(-d "$body")
  [ -n "$mm_token" ] && args+=(-H "Authorization: Bearer $mm_token")
  curl "${args[@]}"
}

# Kernel-plane psql against the full stack (verify.sh's helper pins the slim
# overlay only — same compose project, so postgres resolves identically).
full_psql() { full_compose exec -T postgres psql -U demo -d facilitator -tAc "$1"; }
full_logs() {
  local svc
  verify_human "--- last 20 lines: ${*} ---"
  for svc in "$@"; do
    full_compose logs --tail=20 --no-log-prefix "$svc" 2>&1 | sed "s/^/[$svc] /" >&2
  done
}
full_compose() {
  (cd "$VERIFY_REF" && docker compose --env-file versions.env --env-file .env \
    -f compose.slim.yaml -f compose.full.yaml -p facilitator-demo "$@")
}

# --- steps --------------------------------------------------------------------

step_provider_boot() {
  # /healthz answers with the bot identity + WS state; the kernel must see a
  # live link_sessions row for the adapter's logical endpoint.
  local health
  health=$(curl -fs --max-time 5 "$VERIFY_ADAPTER/healthz") \
    || { step_fail "adapter /healthz unreachable on $VERIFY_ADAPTER"; full_logs mattermost-adapter; return 1; }
  echo "$health" | jq -e '.ok == true and .ws == "up"' >/dev/null \
    || { step_fail "adapter not ready: $health"; full_logs mattermost-adapter; return 1; }
  full_psql "SELECT 1 FROM facilitator.link_sessions WHERE logical_endpoint_id = 'mm-adapter' LIMIT 1" | grep -q 1 \
    || { step_fail "no live link_sessions row for the mm-adapter endpoint"; full_logs mattermost-adapter facilitator; return 1; }
  # The session token rides in the `Token` response header (11.11 login law
  # for non-browser clients: no Set-Cookie, no body field).
  mm_token=$(curl -fsS --max-time 10 -o /dev/null -D - -X POST "$VERIFY_MM/api/v4/users/login" \
    -H 'Content-Type: application/json' \
    -d "{\"login_id\":\"$VERIFY_MM_ADMIN_EMAIL\",\"password\":\"$VERIFY_MM_ADMIN_PASSWORD\"}" \
    | tr -d '\r' | awk -F': ' 'tolower($1)=="token"{print $2}')
  [ -n "$mm_token" ] \
    || { step_fail "could not log into Mattermost as $VERIFY_MM_ADMIN_EMAIL"; full_logs mattermost-init mattermost; return 1; }
  mm_bot_id=$(mm_api POST /api/v4/users/usernames '["facilitator"]' | jq -re '.[0].id') \
    || { step_fail "bot user 'facilitator' not resolvable"; full_logs mattermost-init; return 1; }
  step_pass "adapter healthy (ws up, bot $mm_bot_id), kernel sees the live session"
}

step_provider_link_challenge() {
  local admin_id dm_chan dm_post challenge_code
  admin_id=$(mm_api GET /api/v4/users/me | jq -re .id)
  # 1. DM the bot "/link" (the §11 entry point for an unlinked account).
  dm_chan=$(mm_api POST /api/v4/channels/direct "[\"$mm_bot_id\", \"$admin_id\"]" | jq -re .id) \
    || { step_fail "could not open the bot DM channel"; full_logs mattermost-adapter; return 1; }
  dm_post=$(mm_api POST /api/v4/posts \
    "$(jq -cn --arg ch "$dm_chan" '{channel_id:$ch, message:"/link"}')" | jq -re .id) \
    || { step_fail "could not post /link into the bot DM"; return 1; }
  full_challenge_for() { # $1 account -> challenge row count
    full_psql "SELECT 1 FROM facilitator.provider_link_challenges WHERE account_id = '$1' LIMIT 1" | grep -q 1
  }
  poll_until 30 full_challenge_for "$admin_id" \
    || { step_fail "no challenge row 30s after the /link DM"; full_logs mattermost-adapter facilitator; return 1; }
  challenge_code=$(full_psql "SELECT envelope->'data'->>'text' FROM facilitator.outbox WHERE command_id = 'link:$dm_post:dm'" |
    grep -oE 'link code: [A-Z2-9]{6}' | awk '{print $3}')
  [ -n "$challenge_code" ] \
    || { step_fail "challenge row exists but the DM reply carries no code"; full_logs facilitator; return 1; }

  # 2. Redeem on the human plane as a Keycloak'd identity (deviation D2).
  local alice_token
  alice_token=$(seed_mint_user_token alice alice) \
    || { step_fail "could not mint the alice token"; return 1; }
  verify_post "$alice_token" /api/link-identities/redeem \
    "$(jq -cn --arg code "$challenge_code" '{code:$code}')" >/dev/null \
    || { step_fail "redeem of $challenge_code refused"; full_logs facilitator; return 1; }
  full_psql "SELECT 1 FROM facilitator.provider_identities WHERE provider = 'mattermost' AND account_id = '$admin_id' LIMIT 1" | grep -q 1 \
    || { step_fail "provider_identities row missing after redeem"; return 1; }

  # 3. Unlinked actor: a freshly signed-up account mentions the bot in a
  #    public channel → typed reply, no Work.
  local team_id guest guest_id guest_chan guest_post
  team_id=$(mm_api GET /api/v4/teams/name/team-alpha | jq -re .id)
  local guest_name; guest_name="guest$(date +%s)$RANDOM"
  guest=$(curl -fsS --max-time 10 -X POST "$VERIFY_MM/api/v4/users" -H 'Content-Type: application/json' \
    -d "{\"email\":\"$guest_name@ofac.local\",\"username\":\"$guest_name\",\"password\":\"$VERIFY_MM_ADMIN_PASSWORD\"}") \
    || { step_fail "guest signup refused"; return 1; }
  guest_id=$(jq -re .id <<<"$guest")
  # 11.11 law: the body must repeat team_id, not just user_id.
  mm_api POST "/api/v4/teams/$team_id/members" \
    "$(jq -cn --arg u "$guest_id" --arg t "$team_id" '{team_id:$t, user_id:$u, roles:"team_user"}')" >/dev/null
  guest_chan=$(mm_api POST /api/v4/channels \
    "$(jq -cn --arg t "$team_id" --arg n "$guest_name" '{team_id:$t, type:"O", name:$n, display_name:$n}')" | jq -re .id) \
    || { step_fail "guest channel creation failed"; return 1; }
  # The WS listener only sees channels the bot belongs to: add both humans
  # and the bot.
  for member in "$guest_id" "$mm_bot_id"; do
    mm_api POST "/api/v4/channels/$guest_chan/members" \
      "$(jq -cn --arg u "$member" '{user_id:$u}')" >/dev/null
  done
  # The mention must carry the GUEST's identity: post with the guest's own
  # session token, not the admin's (the kernel gates on the author).
  local guest_token
  guest_token=$(curl -fsS --max-time 10 -o /dev/null -D - -X POST "$VERIFY_MM/api/v4/users/login" \
    -H 'Content-Type: application/json' \
    -d "{\"login_id\":\"$guest_name\",\"password\":\"$VERIFY_MM_ADMIN_PASSWORD\"}" \
    | tr -d '\r' | awk -F': ' 'tolower($1)=="token"{print $2}')
  [ -n "$guest_token" ] || { step_fail "guest login refused"; return 1; }
  guest_post=$(curl -fsS --max-time 10 -X POST "$VERIFY_MM/api/v4/posts" \
    -H 'Content-Type: application/json' -H "Authorization: Bearer $guest_token" \
    -d "$(jq -cn --arg ch "$guest_chan" '{channel_id:$ch, message:"@facilitator hi from an unlinked account"}')" | jq -re .id)
  poll_until 30 full_unlinked_reply "$guest_post" \
    || { step_fail "no unlinked typed-reply outbox row for post $guest_post"; full_logs mattermost-adapter facilitator; return 1; }
  step_pass "/link DM minted a code, redeem linked $admin_id, unlinked mention got the typed reply"
}
full_unlinked_reply() {
  full_psql "SELECT 1 FROM facilitator.outbox WHERE command_id LIKE 'mention:%:unlinked' AND envelope::text LIKE '%$1%' LIMIT 1" | grep -q 1
}

FULL_WORK=""
FULL_CHANNEL=""
FULL_MENTION_POST=""

step_provider_mention() {
  local team_id
  team_id=$(mm_api GET /api/v4/teams/name/team-alpha | jq -re .id)
  local chan_name; chan_name="verify$(date +%s)$RANDOM"
  FULL_CHANNEL=$(mm_api POST /api/v4/channels \
    "$(jq -cn --arg t "$team_id" --arg n "$chan_name" \
      '{team_id:$t, type:"O", name:$n, display_name:$n}')" | jq -re .id) \
    || { step_fail "public channel creation failed"; return 1; }
  # WS law: the listener only sees channels the bot belongs to.
  for member in "$mm_bot_id" "$(mm_api GET /api/v4/users/me | jq -re .id)"; do
    mm_api POST "/api/v4/channels/$FULL_CHANNEL/members" \
      "$(jq -cn --arg u "$member" '{user_id:$u}')" >/dev/null
  done
  FULL_MENTION_POST=$(mm_api POST /api/v4/posts \
    "$(jq -cn --arg ch "$FULL_CHANNEL" '{channel_id:$ch, message:"@facilitator verify: analyze the reference chat"}')" | jq -re .id) \
    || { step_fail "mention post refused"; return 1; }
  poll_until 45 full_binding_work >/dev/null \
    || { step_fail "no binding row 45s after the mention"; full_logs mattermost-adapter facilitator demo-workspace; return 1; }
  poll_until 30 full_ack_reaction \
    || { step_fail "no white_check_mark ack reaction on the mention post"; full_logs mattermost-adapter; return 1; }
  VERIFY_TOKEN_ALICE=$(seed_mint_user_token alice alice) || { step_fail "token mint failed"; return 1; }
  poll_until 30 verify_timeline_has "$VERIFY_TOKEN_ALICE" "$FULL_WORK" created \
    || { step_fail "Work $FULL_WORK has no created timeline row"; full_logs facilitator; return 1; }
  step_pass "mention created Work $FULL_WORK, binding row + ack reaction present"
}
full_binding_work() {
  FULL_WORK=$(full_psql "SELECT work_id FROM facilitator.communication_bindings WHERE channel = '$FULL_CHANNEL' LIMIT 1" | head -n1)
  [ -n "$FULL_WORK" ]
}
full_ack_reaction() {
  mm_api GET "/api/v4/posts/$FULL_MENTION_POST/reactions" |
    jq -e '[.[] | select(.emoji_name=="white_check_mark")] | length >= 1' >/dev/null
}

step_provider_completion_reply() {
  VERIFY_TOKEN_ALICE=${VERIFY_TOKEN_ALICE:-$(seed_mint_user_token alice alice)}
  VERIFY_TOKEN_CLI=${VERIFY_TOKEN_CLI:-$(seed_mint_cli_token)}
  # The assign interim rides to gw.team-alpha; the deterministic worker runs
  # research.analyze and lands the manifest — the typed timeline row is the
  # witness (the Work itself stays wherever its humans left it).
  full_manifest_recorded() {
    full_psql "SELECT 1 FROM facilitator.work_events WHERE work_id = '$FULL_WORK' AND kind = 'manifest.recorded' LIMIT 1" | grep -q 1
  }
  poll_until 120 full_manifest_recorded \
    || { step_fail "no manifest.recorded for $FULL_WORK (assign interim)"; full_logs facilitator demo-workspace mattermost-adapter; return 1; }
  # Completion is an explicit human transition (§4.1: no implicit jumps, the
  # guard table allows only open→planning→active→review→completed). Walking
  # the chain as alice fires the provider completion hook on the LAST edge.
  local to state version
  for to in planning active review completed; do
    state=$(full_psql "SELECT state || '|' || version FROM facilitator.works WHERE work_id = '$FULL_WORK'" | head -n1)
    version=${state#*|}
    # The work.write gate in this deployment binds kernel-operator (same law
    # the slim tier follows for /api/ingress writes): the CLI token walks.
    verify_post "$VERIFY_TOKEN_CLI" "/api/works/$FULL_WORK/transitions" \
      "$(jq -cn --arg t "$to" --argjson v "$version" '{to:$t, expectedVersion:$v, reason:"provider e2e"}')" >/dev/null \
      || { step_fail "transition to $to refused"; full_logs facilitator; return 1; }
  done
  poll_until 45 full_thread_reply \
    || { step_fail "no bot reply in the mention thread after completion"; full_logs mattermost-adapter mattermost; return 1; }
  step_pass "manifest recorded; alice's terminal transition posted the completion reply under root $FULL_MENTION_POST"
}
full_thread_reply() {
  mm_api GET "/api/v4/posts/$FULL_MENTION_POST/thread" |
    jq -e --arg bot "$mm_bot_id" '[.posts[] | select(.user_id==$bot and .root_id=="'$FULL_MENTION_POST'")] | length >= 1' >/dev/null
}

step_provider_echo_suppression() {
  # The bot's own reply posts normalize to message.v1 and land as pure
  # ledger writes: judgment echo:post-ledger, no arm effects (no second Work,
  # no input notes beyond the mention leg's own).
  poll_until 30 full_echo_row \
    || { step_fail "no echo:post-ledger provider_events row for the thread"; full_logs facilitator mattermost-adapter; return 1; }
  local notes
  notes=$(full_psql "SELECT count(*) FROM facilitator.work_events WHERE work_id = '$FULL_WORK' AND kind = 'provider.input'" | head -n1)
  [ "${notes:-9}" = "0" ] \
    || { step_fail "bot echo produced $notes provider.input timeline rows (expected 0)"; return 1; }
  step_pass "bot echo recorded as post-ledger only, zero arm effects"
}
full_echo_row() {
  full_psql "SELECT 1 FROM facilitator.provider_events WHERE verified_reference = 'echo:post-ledger' AND body->'reference'->>'channelOrResourceId' = '$FULL_CHANNEL' LIMIT 1" | grep -q 1
}

step_provider_reaction_no_consent() {
  # §13.3: a human thumbsup on the mention post audits + configured ack, and
  # NEVER a consent/transition signal.
  local admin_id
  admin_id=$(mm_api GET /api/v4/users/me | jq -re .id)
  mm_api POST /api/v4/reactions \
    "$(jq -cn --arg u "$admin_id" --arg p "$FULL_MENTION_POST" --arg now "$(date +%s%3N)" \
      '{user_id:$u, post_id:$p, emoji_name:"thumbsup", create_at:($now|tonumber)}')" >/dev/null \
    || { step_fail "reaction POST refused"; return 1; }
  VERIFY_TOKEN_ALICE=${VERIFY_TOKEN_ALICE:-$(seed_mint_user_token alice alice)}
  poll_until 30 full_reaction_audited \
    || { step_fail "no provider.reaction audit row 30s after the thumbsup"; full_logs mattermost-adapter facilitator; return 1; }
  poll_until 30 full_reaction_acked \
    || { step_fail "no eye ack reaction for the reaction event"; return 1; }
  local transitions
  transitions=$(full_psql "SELECT count(*) FROM facilitator.work_events WHERE work_id = '$FULL_WORK' AND kind IN ('approval.decided','approval.required')" | head -n1)
  [ "${transitions:-9}" = "0" ] \
    || { step_fail "reaction leg touched approvals ($transitions approval rows)"; return 1; }
  step_pass "reaction audited + acked, zero approval/consent effects"
}
full_reaction_audited() {
  full_psql "SELECT 1 FROM facilitator.work_events WHERE work_id = '$FULL_WORK' AND kind = 'provider.reaction' LIMIT 1" | grep -q 1
}
full_reaction_acked() {
  mm_api GET "/api/v4/posts/$FULL_MENTION_POST/reactions" |
    jq -e '[.[] | select(.emoji_name=="eye")] | length >= 1' >/dev/null
}

# --- runner (same record/report shape as the slim verify) ---------------------

full_run_step() { # <id>
  local id=$1 dep started
  for dep in $(full_deps_of "$id"); do
    grep -q "^$dep|" "$VERIFY_RESULTS" || full_run_step "$dep"
    if grep -q "^$dep|fail\|^$dep|skipped" "$VERIFY_RESULTS"; then
      verify_step_skip "$id" "dependency $dep did not pass"
      return 0
    fi
  done
  started=$(verify_now_ms)
  VERIFY_CURRENT_STEP=$id VERIFY_CURRENT_STARTED=$started
  case "$id" in
    provider-boot) step_provider_boot ;;
    provider-link-challenge) step_provider_link_challenge ;;
    provider-mention) step_provider_mention ;;
    provider-completion-reply) step_provider_completion_reply ;;
    provider-echo-suppression) step_provider_echo_suppression ;;
    provider-reaction-no-consent) step_provider_reaction_no_consent ;;
    *) verify_step_skip "$id" "unknown step" ;;
  esac
}

run_verify_full() {
  local json=0 only=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --json) json=1 ;;
      --only) only=${2:?--only needs a comma-separated step list}; shift ;;
      *) echo "verify-full: unknown flag '$1'" >&2; return 2 ;;
    esac
    shift
  done
  VERIFY_REQUESTED=("${FULL_ORDER[@]}")
  if [ -n "$only" ]; then
    IFS=, read -r -a VERIFY_REQUESTED <<<"$only"
    local id
    for id in "${VERIFY_REQUESTED[@]}"; do
      printf '%s\n' "${FULL_ORDER[@]}" | grep -qx "$id" \
        || { echo "verify-full: no such step '$id'" >&2; return 2; }
    done
  fi
  local id
  for id in "${FULL_ORDER[@]}"; do
    case " ${VERIFY_REQUESTED[*]} " in
      *" $id "*) full_run_step "$id" ;;
    esac
  done
  local missing_exit=0
  for id in "${VERIFY_REQUESTED[@]}"; do
    grep -q "^$id|" "$VERIFY_RESULTS" || { verify_step_fail "$id" 0 "step recorded nothing"; missing_exit=1; }
  done
  local exit_code=0
  grep -Eq '\|fail\||\|skipped' "$VERIFY_RESULTS" && exit_code=1
  [ "$missing_exit" = 1 ] && exit_code=1
  local report
  report=$(jq -Rs --arg profile full --argjson exit "$exit_code" --argjson started "$VERIFY_T0" '
    { profile: $profile,
      startedAt: ($started | tostring),
      exitCode: $exit,
      steps: [ splits("\n") | select(length > 0)
               | capture("^(?<id>[^|]+)\\|(?<status>[^|]+)\\|(?<durationMs>[0-9]+)\\|(?<detail>.*)$")
               | { id, status, durationMs: (.durationMs | tonumber), detail } ] }')
  report=$(jq --argjson now "$(verify_now_ms)" --argjson started "$VERIFY_T0" \
    '.durationMs = ($now - $started)' <<<"$report")
  if [ "$json" = 1 ]; then printf '%s\n' "$report"; else
    printf '%s\n' "$report" | jq -r '"\(.steps|length) steps · exit \(.exitCode) · \(.durationMs) ms"' >&2
  fi
  return "$exit_code"
}

run_verify_full "$@"
