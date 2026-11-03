#!/usr/bin/env bash
# init-gitlab.sh — idempotent API bootstrap for the on-demand GitLab
# instance (`make gitlab-up`). Mirrors the forgejo-init law: every step
# reads-before-creates, a rerun is a no-op, and everything is driven over
# the GitLab REST v4 API (no rails console except the one-time root PAT
# mint below).
#
# Auth plane: recent GitLab disables password app-auth by default (basic
# auth 401s even for root), so the script mints a deterministic root PAT
# IN-CONTAINER (`docker exec $GITLAB_CONTAINER gitlab-rails runner`) and
# rides PRIVATE-TOKEN. Without GITLAB_CONTAINER it falls back to root
# basic auth (older instances accept it).
#
# Seeded shape (all overridable via env):
#   group   gitlab-org                       (group = the "org")
#   project gitlab-org/gitlab-test           (from tools/gitlab/seed/)
#   label   ofac:work                        (the Work-trigger label)
#   issue   "Seed: summarize the repository layout"
#   bot     facilitator-bot + PAT -> $GITLAB_TOKEN_FILE (0600)
#   webhook project hook -> ${KERNEL_URL}/api/ingress/webhooks/gitlab
#                           with token $GITLAB_WEBHOOK_TOKEN
set -euo pipefail

GITLAB=${GITLAB_URL:-http://127.0.0.1:8929}
GITLAB_CONTAINER=${GITLAB_CONTAINER:-}
ROOT_PASSWORD=${GITLAB_ROOT_PASSWORD:-0fac-Rt7Vm4Xq2Lz}
KERNEL_URL=${KERNEL_URL:-http://127.0.0.1:8088}
HOOK_TOKEN=${GITLAB_WEBHOOK_TOKEN:?GITLAB_WEBHOOK_TOKEN required}
GROUP=${GITLAB_INIT_GROUP:-gitlab-org}
PROJECT=${GITLAB_INIT_PROJECT:-gitlab-test}
ISSUE_TITLE=${GITLAB_INIT_ISSUE_TITLE:-Seed: summarize the repository layout}
BOT_USER=${GITLAB_BOT_USER:-facilitator-bot}
BOT_PASSWORD=${GITLAB_BOT_PASSWORD:-F4c1l-B0t-9xQ2v}
WORK_LABEL=${GITLAB_WORK_LABEL:-ofac:work}
TOKEN_FILE=${GITLAB_TOKEN_FILE:?GITLAB_TOKEN_FILE required}
SEED_DIR=${SEED_DIR:-$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/seed" && pwd)}
ROOT_TOKEN=${GITLAB_ROOT_PAT:-ofac-root-init-token}

api() { # api METHOD PATH [BODY] — root-token plane
  local method=$1 path=$2 body=${3:-}
  local args=(-fsS --max-time 20 -X "$method" -H "PRIVATE-TOKEN: $ROOT_TOKEN" "$GITLAB$path")
  [ -n "$body" ] && args+=(-H 'Content-Type: application/json' -d "$body")
  curl "${args[@]}"
}

# --- wait for boot --------------------------------------------------------------
# Recent GitLab restricts /-/readiness to in-container IPs, so the boot gate
# rides the sign-in page: a 200 there means rails + puma + nginx all answer.
ready() { curl -fs --max-time 5 "$GITLAB/users/sign_in" >/dev/null 2>&1; }
deadline=$((SECONDS + ${GITLAB_WAIT_SECS:-600}))
until ready; do
  [ "$SECONDS" -lt "$deadline" ] || { echo "init-gitlab: $GITLAB not ready within ${GITLAB_WAIT_SECS:-600}s" >&2; exit 1; }
  sleep 5
done

# --- root PAT -------------------------------------------------------------------
# Mint (idempotently) a deterministic root PAT in-container; capped at the
# instance's max token lifetime. Then prove it against /api/v4/version; a
# 401 with no container means an older instance — basic auth there.
if [ -n "$GITLAB_CONTAINER" ]; then
  docker exec "$GITLAB_CONTAINER" gitlab-rails runner "
    u = User.find_by_username('root')
    t = u.personal_access_tokens.find_or_initialize_by(name: 'ofac-init')
    t.scopes = [:api]
    t.expires_at = 6.months.from_now
    t.set_token('$ROOT_TOKEN')
    t.save!
    ApplicationSetting.current.update!(
      allow_local_requests_from_web_hooks_and_services: true,
      allow_local_requests_from_system_hooks: true,
      outbound_local_requests_allowlist_raw: '127.0.0.1/32')
    puts 'PAT-OK'" >/dev/null
elif ! curl -fs --max-time 10 -u "root:$ROOT_PASSWORD" "$GITLAB/api/v4/version" >/dev/null; then
  echo "init-gitlab: no container for the PAT mint and root basic auth is refused (recent GitLab disables password app-auth); set GITLAB_CONTAINER" >&2
  exit 1
fi
# From here on, prefer the PAT; basic-auth fallback only when no container.
if [ -n "$GITLAB_CONTAINER" ] && curl -fs --max-time 10 -H "PRIVATE-TOKEN: $ROOT_TOKEN" "$GITLAB/api/v4/version" >/dev/null; then
  api() {
    local method=$1 path=$2 body=${3:-}
    local args=(-fsS --max-time 20 -X "$method" -H "PRIVATE-TOKEN: $ROOT_TOKEN" "$GITLAB$path")
    [ -n "$body" ] && args+=(-H 'Content-Type: application/json' -d "$body")
    curl "${args[@]}"
  }
else
  ROOT_TOKEN="$ROOT_PASSWORD" # basic-auth plane
  api() {
    local method=$1 path=$2 body=${3:-}
    local args=(-fsS --max-time 20 -X "$method" -u "root:$ROOT_PASSWORD" "$GITLAB$path")
    [ -n "$body" ] && args+=(-H 'Content-Type: application/json' -d "$body")
    curl "${args[@]}"
  }
fi

# --- group ----------------------------------------------------------------------
group_id=$(api GET "/api/v4/groups/$GROUP" | jq -r '.id // empty' 2>/dev/null || true)
if [ -z "$group_id" ]; then
  group_id=$(api POST /api/v4/groups "$(jq -cn --arg n "$GROUP" --arg p "$GROUP" \
    '{name:$n, path:$p, visibility:"internal", description:"Facilitator on-demand demo group"}')" | jq -re '.id')
fi
echo "group $GROUP id=$group_id"

# --- project (create empty, then push the seed repo) ----------------------------
encoded=$(jq -rn --arg p "$GROUP/$PROJECT" '$p|@uri')
project_id=$(api GET "/api/v4/projects/$encoded" | jq -r '.id // empty' 2>/dev/null || true)
if [ -z "$project_id" ]; then
  project_id=$(api POST /api/v4/projects "$(jq -cn --argjson ns "$group_id" --arg p "$PROJECT" \
    '{name:$p, namespace_id:$ns, visibility:"internal", initialize_with_readme:false, default_branch:"main"}')" | jq -re '.id')
  # Push the seed repo over HTTP git (root credentials).
  tmp=$(mktemp -d)
  trap 'rm -rf "$tmp"' EXIT
  cp -r "$SEED_DIR/." "$tmp/"
  git -C "$tmp" init -q -b main
  git -C "$tmp" -c user.email=ofac@ofac.local -c user.name=ofac add -A
  git -C "$tmp" -c user.email=ofac@ofac.local -c user.name=ofac commit -qm "seed: sample repository"
  scheme=${GITLAB%%://*}; host=${GITLAB#*://}
  git -C "$tmp" push -q "${scheme}://root:${ROOT_PASSWORD}@${host}/${GROUP}/${PROJECT}.git" main
  # Point the default branch at the pushed head.
  api PUT "/api/v4/projects/$project_id" '{"default_branch":"main"}' >/dev/null
fi
echo "project $GROUP/$PROJECT id=$project_id"

# --- work label -------------------------------------------------------------------
# Recent GitLab renamed the label key `title` -> `name`; send both so the
# call works across versions, and match either shape on read.
label_id=$(api GET "/api/v4/projects/$project_id/labels" | jq -r --arg n "$WORK_LABEL" '[.[] | select((.name // .title)==$n)] | .[0].id // empty')
if [ -z "$label_id" ]; then
  label_id=$(api POST "/api/v4/projects/$project_id/labels" \
    "$(jq -cn --arg n "$WORK_LABEL" '{name:$n, title:$n, color:"#6f42c1", description:"Facilitator work trigger"}')" | jq -re '.id')
fi
echo "label $WORK_LABEL id=$label_id"

# --- sample issue -------------------------------------------------------------------
issue_iid=$(api GET "/api/v4/projects/$project_id/issues?state=all" | jq -r --arg t "$ISSUE_TITLE" '[.[] | select(.title==$t)] | .[0].iid // empty')
if [ -z "$issue_iid" ]; then
  issue_iid=$(api POST "/api/v4/projects/$project_id/issues" \
    "$(jq -cn --arg t "$ISSUE_TITLE" --arg b "Seeded by tools/gitlab/init-gitlab.sh; label it '$WORK_LABEL' to arm a Work." \
      --argjson l "$label_id" '{title:$t, description:$b, labels:[$l]}')" | jq -re '.iid')
fi
echo "issue #$issue_iid"

# --- bot user + PAT -> token file -----------------------------------------------------
bot_id=$(api GET "/api/v4/users?username=$BOT_USER" | jq -r '.[0].id // empty')
if [ -z "$bot_id" ]; then
  bot_id=$(api POST /api/v4/users "$(jq -cn --arg u "$BOT_USER" --arg pw "$BOT_PASSWORD" \
    '{email:($u+"@ofac.local"), username:$u, name:"Facilitator Bot", password:$pw, skip_confirmation:true}')" | jq -re '.id')
fi
# Bot must be able to act on the project (maintainer: MR + notes + statuses).
api POST "/api/v4/projects/$project_id/members" \
  "$(jq -cn --argjson id "$bot_id" '{user_id:$id, access_level:40}')" >/dev/null 2>&1 || true
# Mint a fresh PAT (GitLab cannot read an old secret back; a rerun replaces
# the file and the old token stays valid-but-unused, which is harmless).
mkdir -p "$(dirname "$TOKEN_FILE")"
api POST "/api/v4/users/$bot_id/personal_access_tokens" \
  "$(jq -cn '{name:"ofac-connector", scopes:["api","read_repository","write_repository"], expires_at:"2027-01-01"}')" |
  jq -re '.token' >"$TOKEN_FILE"
chmod 600 "$TOKEN_FILE"
echo "bot $BOT_USER id=$bot_id token -> $TOKEN_FILE"

# --- project webhook -> the kernel sink -----------------------------------------------
hook_url="$KERNEL_URL/api/ingress/webhooks/gitlab"
hook_present=$(api GET "/api/v4/projects/$project_id/hooks" | jq -r --arg u "$hook_url" '[.[] | select(.url==$u)] | .[0].id // empty')
if [ -z "$hook_present" ]; then
  api POST "/api/v4/projects/$project_id/hooks" \
    "$(jq -cn --arg u "$hook_url" --arg t "$HOOK_TOKEN" '{
      url:$u, token:$t, push_events:false, tag_push_events:false,
      merge_requests_events:true, issues_events:true, note_events:true,
      pipeline_events:true, build_events:true, job_events:true}')" >/dev/null
else
  api PUT "/api/v4/projects/$project_id/hooks/$hook_present" \
    "$(jq -cn --arg u "$hook_url" --arg t "$HOOK_TOKEN" '{url:$u, token:$t}')" >/dev/null
fi
echo "webhook -> $hook_url"
echo "init-gitlab: done"
