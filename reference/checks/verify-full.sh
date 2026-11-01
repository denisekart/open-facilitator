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
VERIFY_FORGEJO=${FORGEJO_URL:-http://127.0.0.1:3000}
VERIFY_FORGEJO_ADAPTER=${FORGEJO_ADAPTER_URL:-http://127.0.0.1:8067}
VERIFY_FORGEJO_ADMIN=${FORGEJO_ADMIN_USER:-ofac-admin}
# mm law: the admin password lives in the compose env file the stack booted with.
VERIFY_FORGEJO_ADMIN_PASSWORD=$(cd "$VERIFY_REF" && awk -F= '/^DEMO_FORGEJO_ADMIN_PASSWORD=/{print $2}' .env)
[ -n "$VERIFY_FORGEJO_ADMIN_PASSWORD" ] || { echo "verify-full: DEMO_FORGEJO_ADMIN_PASSWORD missing from reference/.env" >&2; exit 2; }
VERIFY_FORGEJO_HOOK_SECRET=$(cd "$VERIFY_REF" && awk -F= '/^DEMO_FORGEJO_WEBHOOK_SECRET=/{print $2}' .env)
[ -n "$VERIFY_FORGEJO_HOOK_SECRET" ] || { echo "verify-full: DEMO_FORGEJO_WEBHOOK_SECRET missing from reference/.env" >&2; exit 2; }

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
  provider-completion-reply provider-echo-suppression provider-reaction-no-consent
  forgejo-boot forgejo-seed forgejo-issue-work forgejo-comment-input
  forgejo-connector-pr forgejo-review-steer forgejo-ci-note
  coding-issue-work coding-pr coding-review-steer coding-redispatch
  two-repo-dependency provision-cold-start provision-kill9)
full_deps_of() {
  case "$1" in
    provider-link-challenge) echo provider-boot ;;
    provider-mention) echo provider-boot provider-link-challenge ;;
    provider-completion-reply | provider-echo-suppression | provider-reaction-no-consent) echo provider-mention ;;
    forgejo-boot) ;;
    forgejo-seed | forgejo-issue-work) echo forgejo-boot ;;
    forgejo-comment-input) echo forgejo-issue-work ;;
    forgejo-connector-pr) echo forgejo-issue-work ;;
    forgejo-review-steer | forgejo-ci-note) echo forgejo-connector-pr ;;
    coding-issue-work) echo forgejo-seed ;;
    coding-pr) echo coding-issue-work ;;
    coding-review-steer) echo coding-pr ;;
    coding-redispatch) echo coding-review-steer ;;
    two-repo-dependency) ;;
    provision-cold-start | provision-kill9) ;;
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
    -f compose.slim.yaml -f compose.provisioning.yaml -f compose.full.yaml -p facilitator-demo "$@")
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

# --- Forgejo REST helpers (Plan 23 step 6) ------------------------------------
# D6: every call rides HTTP Basic with the admin password (Forgejo accepts
# Basic wherever a token would; the plan's login-POST token adds nothing).
# Author = ofac-admin ≠ bot facilitator-bot, so these events pass the
# adapter's echo filter exactly as a human's would.
forgejo_api() { # forgejo_api METHOD PATH [BODY]
  local method=$1 path=$2 body=${3:-}
  local args=(-fsS --max-time 15 -X "$method" -u "$VERIFY_FORGEJO_ADMIN:$VERIFY_FORGEJO_ADMIN_PASSWORD" "$VERIFY_FORGEJO$path")
  [ -n "$body" ] && args+=(-H 'Content-Type: application/json' -d "$body")
  curl "${args[@]}"
}
full_inbox_has() { # <event_type> <work_id>
  full_psql "SELECT 1 FROM facilitator.inbox WHERE payload->>'type' = '$1' AND payload->'data'->>'workId' = '$2' LIMIT 1" | grep -q 1
}

FJ_ISSUE=""
FJ_WORK=""
FJ_PR=""
FJ_PR_SHA=""

step_forgejo_boot() {
  local health
  health=$(curl -fs --max-time 5 "$VERIFY_FORGEJO_ADAPTER/healthz") \
    || { step_fail "forgejo adapter /healthz unreachable on $VERIFY_FORGEJO_ADAPTER"; full_logs forgejo-adapter; return 1; }
  echo "$health" | jq -e '.ok == true and .endpointId == "forgejo-adapter"' >/dev/null \
    || { step_fail "forgejo adapter not ready: $health"; full_logs forgejo-adapter; return 1; }
  full_psql "SELECT 1 FROM facilitator.link_sessions WHERE logical_endpoint_id = 'forgejo-adapter' LIMIT 1" | grep -q 1 \
    || { step_fail "no live link_sessions row for the forgejo-adapter endpoint"; full_logs forgejo-adapter facilitator; return 1; }
  step_pass "forgejo adapter healthy, kernel sees the live session"
}

step_forgejo_seed() {
  forgejo_api GET /api/v1/repos/demo/widgets | jq -e '.full_name == "demo/widgets"' >/dev/null \
    || { step_fail "seed repo demo/widgets missing"; full_logs forgejo-init forgejo; return 1; }
  forgejo_api GET /api/v1/repos/demo/widgets/issues/1 | jq -e '.title == "Seed: summarize the repository layout"' >/dev/null \
    || { step_fail "sample issue #1 missing"; full_logs forgejo-init; return 1; }
  forgejo_api GET /api/v1/repos/demo/widgets/hooks \
    | jq -e '[.[] | select(.config.url == "http://forgejo-adapter:8067/hooks/forgejo")] | length >= 1' >/dev/null \
    || { step_fail "no webhook registered at the adapter"; full_logs forgejo-init forgejo-adapter; return 1; }
  step_pass "seed repo, sample issue, and adapter webhook all exist"
}

step_forgejo_issue_work() {
  local title="verify forgejo $(date +%s)$RANDOM" label_id
  FJ_ISSUE=$(forgejo_api POST /api/v1/repos/demo/widgets/issues \
    "$(jq -cn --arg t "$title" '{title:$t, body:"verify leg: labeling adds a Work"}')" | jq -re .number) \
    || { step_fail "issue creation refused"; return 1; }
  label_id=$(forgejo_api GET /api/v1/repos/demo/widgets/labels | jq -re '[.[] | select(.name=="ofac:work")] | .[0].id') \
    || { step_fail "ofac:work label missing"; full_logs forgejo-init; return 1; }
  forgejo_api POST "/api/v1/repos/demo/widgets/issues/$FJ_ISSUE/labels" \
    "$(jq -cn --argjson l "$label_id" '{labels:[$l]}')" >/dev/null \
    || { step_fail "labeling refused"; return 1; }
  fj_binding() {
    FJ_WORK=$(full_psql "SELECT work_id FROM facilitator.communication_bindings WHERE provider = 'forgejo' AND channel = 'demo/widgets' AND root_message_id = '$FJ_ISSUE' LIMIT 1" | head -n1)
    [ -n "$FJ_WORK" ]
  }
  poll_until 45 fj_binding >/dev/null \
    || { step_fail "no binding row 45s after labeling issue #$FJ_ISSUE"; full_logs forgejo-adapter facilitator; return 1; }
  fj_ack() {
    forgejo_api GET "/api/v1/repos/demo/widgets/issues/$FJ_ISSUE/comments" |
      jq -e '[.[] | select(.user.login == "facilitator-bot")] | length >= 1' >/dev/null
  }
  poll_until 30 fj_ack \
    || { step_fail "no facilitator-bot ack comment on issue #$FJ_ISSUE"; full_logs forgejo-adapter; return 1; }
  step_pass "labeled issue #$FJ_ISSUE created Work $FJ_WORK + bot ack comment"
}

step_forgejo_comment_input() {
  forgejo_api POST "/api/v1/repos/demo/widgets/issues/$FJ_ISSUE/comments" \
    '{"body":"verify: also cover the timeout path"}' >/dev/null \
    || { step_fail "comment POST refused"; return 1; }
  fj_input() {
    full_psql "SELECT 1 FROM facilitator.work_events WHERE work_id = '$FJ_WORK' AND kind = 'provider.input' AND detail::text LIKE '%timeout path%' LIMIT 1" | grep -q 1
  }
  poll_until 30 fj_input \
    || { step_fail "no provider.input note 30s after the issue comment"; full_logs forgejo-adapter facilitator; return 1; }
  step_pass "issue comment landed as provider.input on $FJ_WORK"
}

step_forgejo_connector_pr() {
  # Head branch exists so the PR has something to point at (409 on rerun:
  # tolerated, the connector reconciles by head anyway).
  forgejo_api POST /api/v1/repos/demo/widgets/branches \
    '{"new_branch_name":"bot/topic","old_branch_name":"main"}' >/dev/null || true
  VERIFY_TOKEN_ALICE=${VERIFY_TOKEN_ALICE:-$(seed_mint_user_token alice alice)} \
    || { step_fail "alice token mint failed"; return 1; }
  # The raw-outbox assign the plan prescribes (seed_dispatch's shape, the
  # connector's op inputs): confirm-held by policy, so an approval row must
  # appear and alice's respond must release exactly one PR.
  local command_id assignment_id envelope
  command_id=$(seed_new_uuid); assignment_id=$(seed_new_uuid)
  envelope=$(jq -cn --arg commandId "$command_id" --arg workId "$FJ_WORK" --arg assignmentId "$assignment_id" '{
      specversion: "1.0", id: $commandId,
      source: "urn:facilitator:instance:demo",
      type: "org.facilitator.command.work.assign.v1",
      subject: "work/\($workId)/assignment/\($assignmentId)",
      time: now | todate, datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
      data: { commandId: $commandId, actorEvidenceRef: "seed: verify-pr",
        workId: $workId, targetId: "demo", capabilityId: "vcs.change_request.create",
        expectedContextVersion: 1, executionGeneration: 1,
        inputs: { objective: "verify connector leg", sourceRefs: [],
          repo: "demo/widgets", head: "bot/topic", base: "main",
          title: "verify: connector leg", body: "opened by the forgejo verify leg" },
        expectedDeliverables: [{ kind: "pr.ref", mediaType: "application/json" }],
        deadline: "2036-10-23T18:00:00Z", assignmentId: $assignmentId }
    }')
  full_psql "INSERT INTO facilitator.outbox
    (outbox_id, recipient_endpoint_id, aggregate_type, aggregate_id, event_type,
    command_id, envelope, work_id, status, available_at, attempts, created_at)
  VALUES (gen_random_uuid(), 'link.gw.team-alpha', 'work', '$FJ_WORK',
    'org.facilitator.command.work.assign.v1', '$command_id',
    '$envelope'::jsonb, '$FJ_WORK', 'pending', now(), 0, now());
  SELECT pg_notify('facilitator_outbox', '');" >/dev/null \
    || { step_fail "assign outbox insert failed"; return 1; }
  fj_approval_id() {
    verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$FJ_WORK/approvals" \
      | jq -re '[.[] | select(.state == "pending")] | .[0].approvalId // empty' 2>/dev/null
  }
  local approval_id
  approval_id=$(poll_capture 60 fj_approval_id) \
    || { step_fail "no approval row 60s after the pr.create assign"; full_logs demo-workspace facilitator; return 1; }
  verify_post "$VERIFY_TOKEN_ALICE" "/api/works/$FJ_WORK/approvals/$approval_id/respond" \
    '{"decision":"approved","comment":"verify auto-approve"}' >/dev/null \
    || { step_fail "approval respond refused ($approval_id)"; full_logs facilitator; return 1; }
  poll_until 90 full_pr_open >/dev/null \
    || { step_fail "no exactly-one open PR from bot/topic 90s after approval"; full_logs demo-workspace facilitator; return 1; }
  poll_until 60 full_connector_completed >/dev/null \
    || { step_fail "connector assignment.completed never landed for $FJ_WORK"; full_logs demo-workspace facilitator; return 1; }
  # D5: the as-built pull_request arm binds by (forgejo, repo, PR number) —
  # the branch→issue link resolver is Plan 24's. Seed the PR-number binding
  # for the review/CI legs (same fixture style as this step's outbox row).
  full_psql "INSERT INTO facilitator.communication_bindings
      (binding_id, provider, channel, root_message_id, work_id, idempotency_key, created_at)
    VALUES (gen_random_uuid(), 'forgejo', 'demo/widgets', '$FJ_PR', '$FJ_WORK',
      'verify-pr-link-$FJ_PR', now())
    ON CONFLICT (provider, channel, root_message_id)
      DO UPDATE SET work_id = EXCLUDED.work_id;" >/dev/null \
    || { step_fail "PR-number binding insert failed"; return 1; }
  step_pass "approved pr.create opened exactly one PR #$FJ_PR (sha ${FJ_PR_SHA:0:8}), binding seeded for review/CI legs"
}
full_pr_open() {
  local rows
  rows=$(forgejo_api GET "/api/v1/repos/demo/widgets/pulls?state=all" \
    | jq -c '[.[] | select(.head.ref == "bot/topic")]') || return 1
  [ "$(echo "$rows" | jq -r '[.[] | select(.state=="open")] | length')" = "1" ] || return 1
  FJ_PR=$(echo "$rows" | jq -r '.[0].number')
  FJ_PR_SHA=$(echo "$rows" | jq -r '.[0].head.sha')
  [ "$FJ_PR" != null ] && [ -n "$FJ_PR_SHA" ] && [ "$FJ_PR_SHA" != null ]
}
full_connector_completed() {
  full_inbox_has org.facilitator.event.assignment.completed.v1 "$FJ_WORK"
}

step_forgejo_review_steer() {
  forgejo_api POST "/api/v1/repos/demo/widgets/pulls/$FJ_PR/reviews" \
    '{"body":"approve with the retry clause reworded","event":"APPROVED"}' >/dev/null \
    || { step_fail "review POST refused"; return 1; }
  fj_steered() {
    full_psql "SELECT 1 FROM facilitator.work_events WHERE work_id = '$FJ_WORK' AND kind = 'steer' LIMIT 1" | grep -q 1
  }
  poll_until 45 fj_steered \
    || { step_fail "no steer timeline row 45s after the PR review"; full_logs forgejo-adapter facilitator; return 1; }
  local version
  version=$(full_psql "SELECT version FROM facilitator.works WHERE work_id = '$FJ_WORK'" | head -n1)
  [ "${version:-0}" -ge 2 ] \
    || { step_fail "Work version not bumped by the steer (got ${version:-?})"; return 1; }
  step_pass "PR review steered $FJ_WORK (version now $version)"
}

step_forgejo_ci_note() {
  # 1. the PR head carries a failure status (the API accepts + stores it).
  forgejo_api POST "/api/v1/repos/demo/widgets/statuses/$FJ_PR_SHA" \
    '{"state":"failure","description":"verify ci leg","context":"ofac/verify"}' >/dev/null \
    || { step_fail "commit status POST refused"; return 1; }
  # 2. 16.0.7 does NOT fire `status` webhooks at all — the event name is
  # absent from the hook vocabulary (open feature request forgejo/forgejo
  # #9325). So the delivery is synthesized here with the shared hook secret,
  # byte-identical to the shape a 16 with #9325 will send: this proves the
  # adapter's HMAC verify + sha→PR resolution + ci.v1 normalization and the
  # kernel's ci arm; only Forgejo's own queueing of this one event is stubbed.
  local body dlv sig
  body=$(jq -cn --arg repo "demo/widgets" --arg sha "$FJ_PR_SHA" \
    --arg who "ofac-admin" '{
      status: "failure", sha: $sha, description: "verify ci leg",
      context: "ofac/verify",
      repository: { full_name: $repo },
      sender: { login: $who }
    }')
  dlv=$(seed_new_uuid)
  sig=$(printf '%s' "$body" |
    openssl dgst -sha256 -hmac "$VERIFY_FORGEJO_HOOK_SECRET" -hex | awk '{print $NF}')
  curl -fsS --max-time 10 -X POST "$VERIFY_FORGEJO_ADAPTER/hooks/forgejo" \
    -H 'Content-Type: application/json' \
    -H "X-Forgejo-Delivery: $dlv" \
    -H "X-Forgejo-Event: status" \
    -H "X-Forgejo-Signature: $sig" \
    --data-binary "$body" >/dev/null \
    || { step_fail "signed status delivery refused"; return 1; }
  fj_ci_note() {
    full_psql "SELECT 1 FROM facilitator.work_events WHERE work_id = '$FJ_WORK' AND kind = 'provider.input' AND detail::text LIKE '%failure%' LIMIT 1" | grep -q 1
  }
  poll_until 45 fj_ci_note \
    || { step_fail "no provider.input note with the failure verdict 45s after the status"; full_logs forgejo-adapter facilitator; return 1; }
  step_pass "commit-status failure annotated $FJ_WORK (status on PR head + signed ci delivery)"
}
# --- Coding-worker legs (Plan 24 step 6: §23.1) --------------------------------
# A fresh ofac:coding-labeled issue arms a coding Work (rule-matched assign to
# gw.coding, team-coding), the coding worker clones/changes/pushes, the kernel
# manifest reader opens the PR through the coding connector, a PR review
# steers through re-dispatch (restart-with-context), and the re-dispatched
# run lands a "steer incorporated" comment. Append-only on the seed repo.

FJ_CODING_ISSUE=""
FJ_CODING_WORK=""
FJ_CODING_PR=""
FJ_CODING_HEAD=""

fj_coding_outbox() { # <command_id>
  full_psql "SELECT 1 FROM facilitator.outbox WHERE work_id = '$FJ_CODING_WORK' AND command_id = '$1' LIMIT 1" | grep -q 1
}
fj_coding_envelope_has() { # <LIKE pattern> — envelope text search on this work's rows
  full_psql "SELECT 1 FROM facilitator.outbox WHERE work_id = '$FJ_CODING_WORK' AND envelope::text LIKE '%$1%' LIMIT 1" | grep -q 1
}
fj_issue_comment_with() { # <needle> — bot comment carrying the text on the coding issue
  forgejo_api GET "/api/v1/repos/demo/widgets/issues/$FJ_CODING_ISSUE/comments" |
    jq -e --arg n "$1" '[.[] | select(.user.login == "facilitator-bot" and (.body | contains($n)))] | length >= 1' >/dev/null
}
fj_coding_steers() { # -> steer timeline row count for the coding work
  full_psql "SELECT count(*) FROM facilitator.work_events WHERE work_id = '$FJ_CODING_WORK' AND kind = 'steer'" | head -n1
}

step_coding_issue_work() {
  local title="verify coding $(date +%s)$RANDOM" label_id
  FJ_CODING_ISSUE=$(forgejo_api POST /api/v1/repos/demo/widgets/issues \
    "$(jq -cn --arg t "$title" '{title:$t, body:"verify leg: the coding lane turns a labeled issue into a change"}')" | jq -re .number) \
    || { step_fail "coding issue creation refused"; return 1; }
  label_id=$(forgejo_api GET /api/v1/repos/demo/widgets/labels | jq -re '[.[] | select(.name=="ofac:coding")] | .[0].id') \
    || { step_fail "ofac:coding label missing"; full_logs forgejo-init; return 1; }
  forgejo_api POST "/api/v1/repos/demo/widgets/issues/$FJ_CODING_ISSUE/labels" \
    "$(jq -cn --argjson l "$label_id" '{labels:[$l]}')" >/dev/null \
    || { step_fail "coding label attach refused"; return 1; }
  fj_coding_binding() {
    FJ_CODING_WORK=$(full_psql "SELECT work_id FROM facilitator.communication_bindings WHERE provider = 'forgejo' AND channel = 'demo/widgets' AND root_message_id = '$FJ_CODING_ISSUE' LIMIT 1" | head -n1)
    [ -n "$FJ_CODING_WORK" ]
  }
  poll_until 60 fj_coding_binding >/dev/null \
    || { step_fail "no binding row 60s after labeling issue #$FJ_CODING_ISSUE"; full_logs forgejo-adapter facilitator; return 1; }
  # Rule-matched routing intent resolved by the funnel: coding command id
  # (effect ledger), gw.coding chosen (rule endpoint preference satisfied —
  # the coding gateway advertises coding.implement on team-coding), and the
  # issue/repo re-hydration inputs.
  local effect_key=${FJ_CODING_WORK//-/}
  poll_until 30 fj_coding_outbox "work-assign-coding:$effect_key" \
    || { step_fail "no work-assign-coding outbox row for $FJ_CODING_WORK"; full_logs facilitator coding-workspace; return 1; }
  full_psql "SELECT 1 FROM facilitator.outbox WHERE command_id = 'work-assign-coding:$effect_key'
      AND recipient_endpoint_id = 'link.gw.coding'
      AND envelope->'data'->>'capabilityId' = 'coding.implement'
      AND envelope->'data'->'inputs'->>'issue' = '$FJ_CODING_ISSUE' LIMIT 1" | grep -q 1 \
    || { step_fail "coding assign row is not rule-shaped (endpoint/capability/inputs)"; full_logs facilitator; return 1; }
  # The rule's Team threads the Work's own team (manifest ledger law).
  full_psql "SELECT t.slug FROM facilitator.works w
      JOIN facilitator.projects p ON p.project_id = w.project_id
      JOIN facilitator.teams t ON t.team_id = p.team_id
    WHERE w.work_id = '$FJ_CODING_WORK'" | grep -q team-coding \
    || { step_fail "coding Work is not on team-coding"; full_logs facilitator; return 1; }
  fj_coding_ack() {
    forgejo_api GET "/api/v1/repos/demo/widgets/issues/$FJ_CODING_ISSUE/comments" |
      jq -e '[.[] | select(.user.login == "facilitator-bot")] | length >= 1' >/dev/null
  }
  poll_until 30 fj_coding_ack \
    || { step_fail "no facilitator-bot ack comment on coding issue #$FJ_CODING_ISSUE"; full_logs forgejo-adapter; return 1; }
  step_pass "labeled issue #$FJ_CODING_ISSUE armed team-coding Work $FJ_CODING_WORK (rule assign to gw.coding + ack)"
}

step_coding_pr() {
  # Worker: clone -> ofac/<assignment8> -> changelog + test -> push. Kernel
  # reader: prRequest -> pr-create row (no approval: team-coding policy is
  # allow). Connector: opens exactly one PR carrying "Closes #<issue>".
  # Resolver: the branch:<head> marker is copied onto a PR-number binding.
  fj_coding_pr_open() {
    local rows
    rows=$(forgejo_api GET "/api/v1/repos/demo/widgets/pulls?state=all" \
      | jq -c --argjson i "$FJ_CODING_ISSUE" \
        '[.[] | select(.head.ref | startswith("ofac/")) | select(.body | contains("Closes #\($i)"))]') || return 1
    [ "$(echo "$rows" | jq -r 'length')" = "1" ] || return 1
    [ "$(echo "$rows" | jq -r '.[0].state')" = open ] || return 1
    FJ_CODING_PR=$(echo "$rows" | jq -r '.[0].number')
    FJ_CODING_HEAD=$(echo "$rows" | jq -r '.[0].head.ref')
    [ "$FJ_CODING_PR" != null ] && [ -n "$FJ_CODING_HEAD" ]
  }
  poll_until 180 fj_coding_pr_open >/dev/null \
    || { step_fail "no coding PR 180s after labeling issue #$FJ_CODING_ISSUE"; full_logs coding-workspace facilitator; return 1; }
  forgejo_api GET "/api/v1/repos/demo/widgets/pulls/$FJ_CODING_PR" \
    | jq -e --argjson i "$FJ_CODING_ISSUE" '(.body | contains("Closes #\($i)"))' >/dev/null \
    || { step_fail "PR #$FJ_CODING_PR body carries no Closes # reference"; return 1; }
  fj_coding_pr_binding() {
    full_psql "SELECT 1 FROM facilitator.communication_bindings WHERE provider = 'forgejo' AND channel = 'demo/widgets' AND root_message_id = '$FJ_CODING_PR' AND work_id = '$FJ_CODING_WORK' LIMIT 1" | grep -q 1
  }
  poll_until 60 fj_coding_pr_binding >/dev/null \
    || { step_fail "head-branch resolver never bound PR #$FJ_CODING_PR to $FJ_CODING_WORK"; full_logs forgejo-adapter facilitator; return 1; }
  fj_coding_completed_manifest() {
    full_psql "SELECT 1 FROM facilitator.inbox WHERE payload->>'type' = 'org.facilitator.event.assignment.completed.v1'
        AND payload->'data'->>'workId' = '$FJ_CODING_WORK'
        AND payload->'data'->>'resultManifestRef' IS NOT NULL LIMIT 1" | grep -q 1
  }
  poll_until 30 fj_coding_completed_manifest >/dev/null \
    || { step_fail "coding completed event carries no manifest ref"; full_logs coding-workspace facilitator; return 1; }
  local approvals
  approvals=$(full_psql "SELECT count(*) FROM facilitator.approvals WHERE work_id = '$FJ_CODING_WORK'" | head -n1)
  [ "${approvals:-9}" = "0" ] \
    || { step_fail "team-coding allow-policy lane raised $approvals approval rows"; return 1; }
  step_pass "coding PR #$FJ_CODING_PR (head $FJ_CODING_HEAD) opened unattended, Closes #$FJ_CODING_ISSUE, resolver bound it, manifest recorded"
}

step_coding_review_steer() {
  local directive="reword the changelog entry for clarity"
  # Forgejo's own review-state literals are COMMENT/APPROVED/REQUEST_CHANGES
  # (16.x `structs.ReviewStateType`): a GitHub-style "COMMENTED" is an unknown
  # event and lands as a PENDING review that fires no webhook at all.
  forgejo_api POST "/api/v1/repos/demo/widgets/pulls/$FJ_CODING_PR/reviews" \
    "$(jq -cn --arg b "$directive" '{body:$b, event:"COMMENT"}')" >/dev/null \
    || { step_fail "review POST refused"; return 1; }
  fj_coding_steered() {
    full_psql "SELECT 1 FROM facilitator.work_events WHERE work_id = '$FJ_CODING_WORK' AND kind = 'steer' AND detail::text LIKE '%changelog entry for clarity%' LIMIT 1" | grep -q 1
  }
  poll_until 45 fj_coding_steered \
    || { step_fail "no steer timeline row 45s after the PR review"; full_logs forgejo-adapter facilitator; return 1; }
  # No live execution (the first run completed): §23.1 re-dispatch branch —
  # a cloned assign envelope keyed work-redispatch:<steerId> carrying the
  # directive in inputs.steers, addressed to the ORIGINAL recipient.
  fj_coding_redispatched() {
    full_psql "SELECT 1 FROM facilitator.outbox WHERE work_id = '$FJ_CODING_WORK'
        AND command_id LIKE 'work-redispatch:%' AND envelope::text LIKE '%changelog entry for clarity%'
        AND recipient_endpoint_id = 'link.gw.coding' LIMIT 1" | grep -q 1
  }
  poll_until 30 fj_coding_redispatched \
    || { step_fail "no work-redispatch row carrying the directive"; full_logs facilitator; return 1; }
  # Re-hydration proof: the re-dispatched run posts its steer comment.
  poll_until 120 fj_issue_comment_with "steer incorporated: $directive" \
    || { step_fail "no 'steer incorporated' comment 120s after the re-dispatch"; full_logs coding-workspace facilitator; return 1; }
  local version
  version=$(full_psql "SELECT version FROM facilitator.works WHERE work_id = '$FJ_CODING_WORK'" | head -n1)
  [ "${version:-0}" -ge 2 ] \
    || { step_fail "Work version not bumped by the steer (got ${version:-?})"; return 1; }
  step_pass "PR review steered $FJ_CODING_WORK -> re-dispatch to gw.coding -> steer comment on issue #$FJ_CODING_ISSUE (version $version)"
}

step_coding_redispatch() {
  # §23.1's restart leg: the owning workspace is DOWN when the second review
  # lands. The steer still records + queues the re-dispatch kernel-side (the
  # routing INTENT row commits with the steer; the funnel holds it pending
  # `no-capacity` while the endpoint is offline, so the effect row only
  # appears once the workspace is back); restart delivers it, the worker
  # reconciles, the comment lands.
  full_compose stop coding-workspace \
    || { step_fail "could not stop coding-workspace"; return 1; }
  local directive2="add the timeout clause to the changelog note"
  forgejo_api POST "/api/v1/repos/demo/widgets/pulls/$FJ_CODING_PR/reviews" \
    "$(jq -cn --arg b "$directive2" '{body:$b, event:"APPROVED"}')" >/dev/null \
    || { step_fail "second review POST refused"; full_compose up -d --no-deps coding-workspace >/dev/null; return 1; }
  fj_queued() {
    full_psql "SELECT 1 FROM facilitator.outbox WHERE work_id = '$FJ_CODING_WORK'
        AND command_id LIKE 'routing:work-redispatch:%' AND envelope::text LIKE '%timeout clause%' LIMIT 1" | grep -q 1
  }
  poll_until 45 fj_queued \
    || { step_fail "no queued re-dispatch while coding-workspace is down"; full_logs facilitator; full_compose up -d --no-deps coding-workspace >/dev/null; return 1; }
  # compose `start` has no --no-deps and blocks on the one-shot init legs;
  # `up -d --no-deps` starts the stopped container in place.
  full_compose up -d --no-deps coding-workspace \
    || { step_fail "could not restart coding-workspace"; return 1; }
  poll_until 180 fj_issue_comment_with "steer incorporated: $directive2" \
    || { step_fail "post-restart steer comment never landed"; full_logs coding-workspace facilitator; return 1; }
  local steers
  steers=$(fj_coding_steers)
  [ "${steers:-0}" -ge 2 ] \
    || { step_fail "expected >= 2 steer timeline rows, got ${steers:-?}"; return 1; }
  step_pass "review while offline queued the re-dispatch; restart re-hydrated and the second steer comment landed ($steers steers on $FJ_CODING_WORK)"
}

step_two_repo_dependency() {
  # §23.2 live proof: one Work, TWO assignment intents with a dependsOn edge.
  # The funnel gates sibling 2 (audit basis deps-unmet) until sibling 1's
  # completion wakes it; a steer while execution 2 is LIVE rides the live
  # generation; a steer after completion re-dispatches at generation 2; a
  # straggler terminal event stamped generation 1 fences 422 stale-generation.
  VERIFY_TOKEN_CLI=${VERIFY_TOKEN_CLI:-$(seed_mint_cli_token)}
  local project tag objective body work code
  project=$(seed_project two-repo) || { step_fail "two-repo project missing"; return 1; }
  tag=$(date +%s)$RANDOM
  objective="verify two-repo $tag: contract change across two lanes"
  body=$(jq -cn --arg objective "$objective" --arg projectId "$project" --arg tag "$tag" \
    '{objective: $objective, projectId: $projectId, actorEvidenceRef: ("identity-evidence:two-repo-" + $tag),
      assignments: [
        {capabilityId: "research.analyze", objective: ($objective + " [api]"), targetId: "demo-api"},
        {capabilityId: "research.analyze", objective: ($objective + " [ui]"), targetId: "demo-ui",
        dependsOn: [{index: 0, condition: "completed"}]}
      ]}')
  code=$(curl -s --max-time 15 -o /tmp/tr-create.json -w '%{http_code}' -X POST "$VERIFY_KERNEL/api/works" \
    -H "Authorization: Bearer $VERIFY_TOKEN_CLI" -H 'content-type: application/json' -d "$body")
  [ "$code" = 201 ] || { step_fail "two-assignment create refused (HTTP $code): $(cat /tmp/tr-create.json)"; return 1; }
  work=$(jq -re '.workId' /tmp/tr-create.json)
  local wln=${work//-/}
  # Both intents exist; sibling 0's effect lands, sibling 1 stays gated.
  fj_tr_gated() {
    full_psql "SELECT 1 FROM facilitator.routing_decisions WHERE work_id = '$work'
        AND status = 'pending' AND basis = 'deps-unmet' LIMIT 1" | grep -q 1
  }
  poll_until 30 fj_tr_gated >/dev/null \
    || { step_fail "sibling 2 never gated deps-unmet for $work"; full_logs facilitator; return 1; }
  fj_tr_sibling0() {
    full_psql "SELECT 1 FROM facilitator.outbox WHERE work_id = '$work'
        AND command_id = 'work-assign:$wln' AND recipient_endpoint_id = 'link.gw.team-alpha' LIMIT 1" | grep -q 1
  }
  poll_until 30 fj_tr_sibling0 >/dev/null \
    || { step_fail "sibling 0 assign effect missing for $work"; full_logs facilitator demo-workspace; return 1; }
  # The gate holds: exactly ONE assignment row while sibling 0 runs.
  local rows
  rows=$(full_psql "SELECT count(*) FROM facilitator.assignments WHERE work_id = '$work'" | head -n1)
  [ "${rows:-9}" = "1" ] \
    || { step_fail "gated sibling created its assignment row early ($rows rows)"; return 1; }
  # Sibling 0 executes + records its manifest: the real worker completes it.
  fj_tr_manifest1() {
    full_psql "SELECT 1 FROM facilitator.result_manifests WHERE work_id = '$work' LIMIT 1" | grep -q 1
  }
  poll_until 120 fj_tr_manifest1 >/dev/null \
    || { step_fail "sibling 0 manifest never recorded for $work"; full_logs demo-workspace facilitator; return 1; }
  # The wake law: completion releases the gate — sibling 1 dispatches.
  fj_tr_sibling1() {
    full_psql "SELECT 1 FROM facilitator.outbox WHERE work_id = '$work'
        AND command_id = 'work-assign:$wln-1' AND recipient_endpoint_id = 'link.gw.team-alpha' LIMIT 1" | grep -q 1
  }
  poll_until 45 fj_tr_sibling1 >/dev/null \
    || { step_fail "wake never released the gated sibling for $work"; full_logs facilitator; return 1; }
  fj_tr_manifest2() {
    [ "$(full_psql "SELECT count(*) FROM facilitator.result_manifests WHERE work_id = '$work'" | head -n1)" -ge 2 ]
  }
  poll_until 120 fj_tr_manifest2 >/dev/null \
    || { step_fail "execution 2 manifest never recorded for $work"; full_logs demo-workspace facilitator; return 1; }
  # Both executions have ended (the manifest record and the terminal walk
  # are one atomic ingest), so the steer lands the re-dispatch branch — the
  # §23.1 shape the review leg proved, now on the two-lane Work.
  local version2
  version2=$(full_psql "SELECT version FROM facilitator.works WHERE work_id = '$work'" | head -n1)
  code=$(curl -s --max-time 15 -o /tmp/tr-steer.json -w '%{http_code}' -X POST "$VERIFY_KERNEL/api/works/$work/steer" \
    -H "Authorization: Bearer $VERIFY_TOKEN_CLI" -H 'content-type: application/json' \
    -d "$(jq -cn --arg v "$version2" --arg c "tr-steer-$RANDOM" '{directive:"rename the summary section", commandId:$c, expectedVersion:($v|tonumber)}')")
  [ "$code" = 200 ] || { step_fail "steer refused (HTTP $code): $(cat /tmp/tr-steer.json)"; return 1; }
  fj_tr_steer_row() {
    full_psql "SELECT 1 FROM facilitator.work_events WHERE work_id = '$work' AND kind = 'steer' LIMIT 1" | grep -q 1
  }
  poll_until 15 fj_tr_steer_row >/dev/null \
    || { step_fail "no steer timeline row for $work"; full_logs facilitator; return 1; }
  # Re-dispatch lands generation 2 for the assignment line (the steer had no
  # live attempt: both executions had ended — the 24 re-dispatch branch).
  fj_tr_gen2() {
    full_psql "SELECT 1 FROM facilitator.outbox WHERE work_id = '$work'
        AND command_id LIKE 'routing:work-redispatch:%'
        AND envelope::text LIKE '%rename the summary section%' LIMIT 1" | grep -q 1
  }
  poll_until 45 fj_tr_gen2 >/dev/null \
    || { step_fail "no re-dispatch intent carrying the directive for $work"; full_logs facilitator; return 1; }
  fj_tr_gen2_effect() {
    full_psql "SELECT 1 FROM facilitator.outbox WHERE work_id = '$work'
        AND command_id LIKE 'work-redispatch:%'
        AND envelope->'data'->>'executionGeneration' = '2' LIMIT 1" | grep -q 1
  }
  poll_until 60 fj_tr_gen2_effect >/dev/null \
    || { step_fail "generation-2 re-dispatch effect missing for $work"; full_logs facilitator demo-workspace; return 1; }
  # The straggler: a terminal event stamped generation 1 against the
  # re-dispatched assignment — whose row sits at generation 2, so the
  # §7.4 fence bites. (Sibling 0 sits completed AT gen 1: a re-delivery of
  # its own event is legal, not a straggler.)
  local rdassign straggler
  rdassign=$(full_psql "SELECT envelope->'data'->>'assignmentId' FROM facilitator.outbox
      WHERE work_id = '$work' AND command_id LIKE 'work-redispatch:%' LIMIT 1" | head -n1)
  straggler=$(jq -cn --arg work "$work" --arg now "$(date -u +%Y-%m-%dT%H:%M:%SZ)" --arg tag "$tag" --arg a "$rdassign" '{
      specversion: "1.0", id: ("tr-straggler-" + $tag), source: "urn:workspace:two-repo-verify",
      type: "org.facilitator.event.assignment.completed.v1",
      subject: ("work/" + $work + "/assignment/" + $a), time: $now,
      datacontenttype: "application/json",
      dataschema: "https://schemas.openfacilitator.org/v1/event.assignment.completed.v1.json",
      data: {workId: $work, assignmentId: $a, executionId: ("tr-exec-" + $tag),
            executionGeneration: 1, resultManifest: {manifestId: ("tr-manifest-" + $tag), outputs: []}}}')
  local fence_body
  fence_body=$(mktemp)
  code=$(curl -s --max-time 15 -o "$fence_body" -w '%{http_code}' -X POST "$VERIFY_KERNEL/api/ingress/events" \
    -H "Authorization: Bearer $VERIFY_TOKEN_CLI" -H 'content-type: application/json' -d "$straggler")
  [ "$code" = 422 ] && grep -q 'stale-generation' "$fence_body" \
    || { step_fail "straggler gen-1 event answered HTTP $code (want 422 stale-generation): $(cat "$fence_body")"; rm -f "$fence_body"; return 1; }
  rm -f "$fence_body"
  # The fenced event had NO effect: it settled refused (inbox 'failed') and
  # its manifest never entered the ledger.
  full_psql "SELECT 1 FROM facilitator.inbox WHERE event_id = 'tr-straggler-$tag' AND state = 'failed' LIMIT 1" | grep -q 1 \
    || { step_fail "fenced straggler settled processed instead of refused"; return 1; }
  full_psql "SELECT 1 FROM facilitator.result_manifests WHERE manifest_id = 'tr-manifest-$tag' LIMIT 1" | grep -q . \
    && { step_fail "fenced straggler recorded its manifest anyway"; return 1; }
    true
  # The create-time DAG law: a mutually-dependent batch is a 422 with nothing created.
  code=$(curl -s --max-time 15 -o /tmp/tr-cycle.json -w '%{http_code}' -X POST "$VERIFY_KERNEL/api/works" \
    -H "Authorization: Bearer $VERIFY_TOKEN_CLI" -H 'content-type: application/json' \
    -d "$(jq -cn --arg projectId "$project" '{objective:"two-repo cycle reject", projectId: $projectId,
        actorEvidenceRef: "identity-evidence:two-repo-cycle",
        assignments: [
          {capabilityId: "research.analyze", dependsOn: [{index: 1, condition: "completed"}]},
          {capabilityId: "research.analyze", dependsOn: [{index: 0, condition: "completed"}]}
        ]}')")
  [ "$code" = 422 ] && grep -q 'dependency-violation' /tmp/tr-cycle.json \
    || { step_fail "cycle batch answered HTTP $code without dependency-violation: $(cat /tmp/tr-cycle.json)"; return 1; }
  step_pass "two-repo Work: gated dispatch -> wake -> 2 executions, steer -> gen-2 re-dispatch, straggler fenced 422, cycle rejected"
}

# --- provisioning legs (plan 35 Phase C) -------------------------------------

elastic_running() { # container exists and is running
  [ "$(docker inspect -f '{{.State.Running}}' ofac-gw-team-beta 2>/dev/null || true)" = true ]
}

elastic_stopped() { ! elastic_running; } # poll_until takes a command, not a shell word

elastic_new_id() { docker inspect -f '{{.Id}}' ofac-gw-team-beta 2>/dev/null || echo gone; }

step_provision_cold_start() {
  # The full idle->zero->cold-start chain on the provisioning plane:
  # min1 bootstraps the enrollment (the fleet's first contact), min0 +
  # idle=1 lets the kernel scale it to ZERO through its own stop op, then a
  # capability-referenced work arrives at a fleet with an advertised slot
  # and no live session: the funnel audits no-capacity, the kernel orders
  # the start, the launcher boots the container from nothing, the requeued
  # intent routes to it, and the work completes with exactly one manifest.
  verify_token_cli || { step_fail "token mint failed"; return 1; }
  verify_token_alice || { step_fail "alice token mint failed"; return 1; }
  local leg_t0
  leg_t0=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  # Phase 1: claim min1 so a first-run stack (no enrollment anywhere)
  # bootstraps the elastic workload through the min-shortfall arm.
  seed_workload_scale_policy "$VERIFY_TOKEN_CLI" 1 1 1 >/dev/null \
    || { step_fail "workload-scale min1 PATCH failed"; return 1; }
  if ! poll_elastic_idle 180; then
    step_fail "elastic never went live+idle under min1 (launcher offline? kernel sweep cadence?)"
    full_logs facilitator provisioning-launcher
    return 1
  fi
  local old_id
  old_id=$(elastic_new_id)
  # Phase 2: min0 + idle=1 — the kernel owns the scale-down: no live
  # assignment, idle window elapses, stop op, container gone, ledger row.
  seed_workload_scale_policy "$VERIFY_TOKEN_CLI" 0 1 1 >/dev/null \
    || { step_fail "workload-scale min0 PATCH failed"; return 1; }
  if ! poll_until 150 elastic_stopped; then
    step_fail "elastic never idled to zero after min0+idle=1 (idle arm silent)"
    full_logs facilitator provisioning-launcher
    return 1
  fi
  full_psql "SELECT 1 FROM facilitator.provisioning_operations WHERE workload_id = 'gw.team-beta'
      AND operation = 'stop' AND reason ILIKE 'idle-shutdown%' AND created_at >= '$leg_t0' LIMIT 1" | grep -q 1 \
    || { step_fail "no idle-shutdown stop op in the provisioning ledger"; return 1; }
  # Phase 3: cold start — team-beta work arrives; the advertised slot (the
  # enrollment the bootstrap left behind, unexpired) makes this no-capacity,
  # not no-candidate, so the funnel requeues instead of dead-lettering and
  # the kernel orders a start for the missing capacity.
  local project work
  project=$(seed_project team-beta-witness team-beta) \
    || { step_fail "team-beta witness project seed failed"; return 1; }
  work=$(seed_work "$VERIFY_TOKEN_CLI" "full verify: cold-start witness" research.analyze "$project" "identity-evidence:provision-cold") \
    || { step_fail "cold-start witness work seed failed"; return 1; }
  full_ps_nc() {
    full_psql "SELECT 1 FROM facilitator.routing_decisions WHERE work_id = '$work'
        AND basis = 'no-capacity' LIMIT 1" | grep -q 1
  }
  poll_until 30 full_ps_nc >/dev/null \
    || { step_fail "cold-start intent never audited basis=no-capacity for $work"; full_logs facilitator; return 1; }
  full_ps_startop() {
    full_psql "SELECT 1 FROM facilitator.provisioning_operations WHERE workload_id = 'gw.team-beta'
        AND operation = 'start' AND reason ILIKE 'no-capacity%' AND created_at >= '$leg_t0' LIMIT 1" | grep -q 1
  }
  # The kernel sweep loop runs on its own cadence (default 60 s): the arm
  # fires on the first sweep AFTER the audit row, so poll, don't peek.
  poll_until 90 full_ps_startop >/dev/null \
    || { step_fail "no no-capacity start op ordered for the cold start"; full_logs facilitator provisioning-launcher; return 1; }
  if ! poll_until 90 elastic_running; then
    step_fail "launcher never booted the container for the no-capacity start op"
    full_logs facilitator provisioning-launcher
    return 1
  fi
  [ "$(elastic_new_id)" != "$old_id" ] \
    || { step_fail "relaunched container kept the old id (launcher reconcile skipped the stop? no-capacity op was a no-op)"; return 1; }
  # The requeued intent must route once the child registers: same audit
  # row, basis flips to routed (the funnel re-upsert per attempt).
  full_ps_routed() {
    full_psql "SELECT 1 FROM facilitator.routing_decisions WHERE work_id = '$work'
        AND status = 'routed' AND chosen_endpoint_id = 'gw.team-beta' LIMIT 1" | grep -q 1
  }
  poll_until 90 full_ps_routed >/dev/null \
    || { step_fail "no-capacity intent never routed to gw.team-beta after relaunch"; full_logs facilitator; return 1; }
  if ! poll_until 120 verify_inbox_has org.facilitator.event.assignment.completed.v1 "$work"; then
    step_fail "cold-start witness never completed"
    full_logs facilitator provisioning-launcher
    return 1
  fi
  local n rev
  n=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$work/manifests" | jq 'length')
  rev=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$work/manifests" | jq '.[0].revision')
  { [ "${n:-0}" = 1 ] && [ "${rev:-0}" = 1 ]; } \
    || { step_fail "cold start duplicated side effects (manifests=$n revision=$rev)"; return 1; }
  step_pass "idle->zero->cold-start: min1 bootstrap, idle-shutdown to zero, no-capacity audit + kernel-ordered boot, routed, 1 manifest rev 1"
}

step_provision_kill9() {
  # Kill-9 continuity ON the provisioning plane with NO operator verb: the
  # kernel's worker-lost arm relaunches (fresh container id, reason
  # 'worker-lost') AND re-dispatches the work at generation 2 with one
  # deterministic intent/effect pair; the old attempt's straggler terminal
  # event is fenced 422 stale-generation; exactly one manifest rev 1 lands.
  verify_token_cli || { step_fail "token mint failed"; return 1; }
  verify_token_alice || { step_fail "alice token mint failed"; return 1; }
  local leg_t0
  leg_t0=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  # min0: the ONLY relaunch path allowed is the worker-lost arm.
  seed_workload_scale_policy "$VERIFY_TOKEN_CLI" 0 1 10 >/dev/null \
    || { step_fail "workload-scale min0 PATCH failed"; return 1; }
  # The fleet may be live (cold-start leg just ran it) or zero (idle arm
  # took it); either way min0 + a fresh no-capacity boot gets us live.
  if ! elastic_running; then
    seed_workload_scale_policy "$VERIFY_TOKEN_CLI" 1 1 10 >/dev/null \
      || { step_fail "workload-scale min1 PATCH failed"; return 1; }
    poll_until 120 elastic_running >/dev/null \
      || { step_fail "elastic never booted under min1"; full_logs facilitator provisioning-launcher; return 1; }
    seed_workload_scale_policy "$VERIFY_TOKEN_CLI" 0 1 10 >/dev/null
  fi
  poll_elastic_idle 60 \
    || { step_fail "elastic live but child never idled"; full_logs facilitator provisioning-launcher; return 1; }
  local project work old_id
  project=$(seed_project team-beta-witness team-beta) \
    || { step_fail "team-beta witness project seed failed"; return 1; }
  work=$(seed_work "$VERIFY_TOKEN_CLI" "full verify: kill9 continuity witness" research.analyze "$project" "identity-evidence:provision-kill9") \
    || { step_fail "kill9 witness work seed failed"; return 1; }
  if ! poll_until 120 verify_inbox_has org.facilitator.event.work.progress.v1 "$work"; then
    step_fail "kill9 witness never reported progress"
    full_logs facilitator provisioning-launcher
    return 1
  fi
  old_id=$(elastic_new_id)
  docker kill ofac-gw-team-beta >&2 \
    || { step_fail "docker kill ofac-gw-team-beta failed"; return 1; }
  local failed
  failed=$(poll_capture 90 verify_inbox_has org.facilitator.event.assignment.failed.v1 "$work") \
    || { step_fail "killed run never reported a terminal event within 90s"; full_logs facilitator provisioning-launcher; return 1; }
  jq -e '.data.retryable == true' <<<"$failed" >/dev/null \
    || { step_fail "failed event is not retryable: $(jq -c .data <<<"$failed")"; return 1; }
  local nfailed
  nfailed=$(full_psql "SELECT count(*) FROM facilitator.inbox WHERE payload->>'type' = 'org.facilitator.event.assignment.failed.v1' AND payload->'data'->>'workId' = '$work'" | head -n1)
  [ "${nfailed:-0}" = 1 ] \
    || { step_fail "expected exactly 1 failed event, got $nfailed"; return 1; }
  # The worker-lost ledger face: a start op ordered BY THE KERNEL for the
  # dead assignment, and a fresh container behind it. The relaunch arm rides
  # the provisioning sweep (10 s), so the row lands AFTER the synthesized
  # failed event — poll it, don't grep once.
  ps_worker_lost_op() {
    full_psql "SELECT 1 FROM facilitator.provisioning_operations WHERE workload_id = 'gw.team-beta'
        AND operation = 'start' AND reason ILIKE 'worker-lost%' AND created_at >= '$leg_t0' LIMIT 1" | grep -q 1
  }
  poll_until 60 ps_worker_lost_op >/dev/null \
    || { step_fail "no worker-lost relaunch op after kill-9"; full_logs facilitator provisioning-launcher; return 1; }
  poll_until 120 elastic_running >/dev/null \
    || { step_fail "launcher never relaunched the container after kill-9"; full_logs provisioning-launcher; return 1; }
  [ "$(elastic_new_id)" != "$old_id" ] \
    || { step_fail "relaunch reused the killed container id"; return 1; }
  # NO operator redispatch: exactly ONE auto re-dispatch pair, generation 2.
  local npair
  full_ps_rd() {
    full_psql "SELECT 1 FROM facilitator.outbox WHERE work_id = '$work'
        AND command_id LIKE 'routing:work-redispatch:%' LIMIT 1" | grep -q 1
  }
  poll_until 90 full_ps_rd >/dev/null \
    || { step_fail "no kernel-ordered re-dispatch intent after worker-lost"; full_logs facilitator; return 1; }
  # The funnel converts INTENT -> effect assign on its own sweep, seconds
  # after the intent row: poll for the effect, then assert exactly-one law.
  full_ps_effect() {
    [ "$(full_psql "SELECT count(*) FROM facilitator.outbox WHERE work_id = '$work'
        AND command_id LIKE 'work-redispatch:%'" | head -n1)" -ge 1 ]
  }
  poll_until 90 full_ps_effect >/dev/null \
    || { step_fail "no kernel-ordered re-dispatch effect after worker-lost"; full_logs facilitator; return 1; }
  npair=$(full_psql "SELECT count(*) FROM facilitator.outbox WHERE work_id = '$work'
      AND command_id LIKE 'work-redispatch:%'" | head -n1)
  [ "${npair:-0}" = 1 ] \
    || { step_fail "expected exactly 1 auto re-dispatch effect, got $npair"; return 1; }
  full_psql "SELECT 1 FROM facilitator.outbox WHERE work_id = '$work'
      AND command_id LIKE 'work-redispatch:%'
      AND envelope->'data'->>'executionGeneration' = '2' LIMIT 1" | grep -q 1 \
    || { step_fail "auto re-dispatch is not at generation 2"; return 1; }
  if ! poll_until 180 verify_inbox_has org.facilitator.event.assignment.completed.v1 "$work"; then
    step_fail "work never completed on the relaunched worker"
    full_logs facilitator provisioning-launcher
    return 1
  fi
  local n rev revof
  n=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$work/manifests" | jq 'length')
  rev=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$work/manifests" | jq '.[0].revision')
  revof=$(verify_get "$VERIFY_TOKEN_ALICE" "/api/works/$work/manifests" | jq -c '.[0].revisionOf')
  { [ "${n:-0}" = 1 ] && [ "${rev:-0}" = 1 ] && { [ "$revof" = null ] || [ "$revof" = '"null"' ]; }; } \
    || { step_fail "continuity duplicated side effects (manifests=$n revision=$rev revisionOf=$revof)"; return 1; }
  step_pass "kill-9 on provisioned worker: 1 retryable failed, kernel worker-lost relaunch+gen-2 re-dispatch, 1 manifest rev 1, no operator verb"
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
    forgejo-boot) step_forgejo_boot ;;
    forgejo-seed) step_forgejo_seed ;;
    forgejo-issue-work) step_forgejo_issue_work ;;
    forgejo-comment-input) step_forgejo_comment_input ;;
    forgejo-connector-pr) step_forgejo_connector_pr ;;
    forgejo-review-steer) step_forgejo_review_steer ;;
    forgejo-ci-note) step_forgejo_ci_note ;;
    coding-issue-work) step_coding_issue_work ;;
    coding-pr) step_coding_pr ;;
    coding-review-steer) step_coding_review_steer ;;
    coding-redispatch) step_coding_redispatch ;;
    two-repo-dependency) step_two_repo_dependency ;;
    provision-cold-start) step_provision_cold_start ;;
    provision-kill9) step_provision_kill9 ;;
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
              | { id, status, durationMs: (.durationMs | tonumber), detail } ] }' "$VERIFY_RESULTS")
  report=$(jq --argjson now "$(verify_now_ms)" --argjson started "$VERIFY_T0" \
    '.durationMs = ($now - $started)' <<<"$report")
  if [ "$json" = 1 ]; then printf '%s\n' "$report"; else
    printf '%s\n' "$report" | jq -r '"\(.steps|length) steps · exit \(.exitCode) · \(.durationMs) ms"' >&2
  fi
  return "$exit_code"
}

run_verify_full "$@"
