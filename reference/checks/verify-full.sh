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
  forgejo-connector-pr forgejo-review-steer forgejo-ci-note)
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
        expectedContextVersion: 1,
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
