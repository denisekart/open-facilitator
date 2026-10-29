// Plan 22 step 4: Mattermost provisioning one-shot for the full profile.
// A fresh team-edition server has no users, and 11.x removed both the
// unauthenticated setup endpoint and shell tools from the TE image, so
// bootstrapping rides OPEN SIGNUP over REST: with EnableOpenServer the first
// POST /api/v4/users succeeds unauthenticated and the first account carries
// sysadmin (documented first-signup-admin behavior). Then: bot `facilitator`
// + a PAT — whose VALUE only exists in the create/list responses — published
// to a shared volume file (/provision/mm-bot-token) that both the adapter
// (MATTERMOST_BOT_TOKEN_FILE) and the kernel (Providers:Mattermost:
// BotTokenFile) read. Idempotent: login-first, signup only when the account
// is absent; a minted token is reused when its description already exists.
// Runs on the node-runtime image (plain JS, node 22 fetch), not the TE image.
import { writeFileSync, renameSync } from "node:fs";

const MM_URL = process.env["MM_URL"] ?? "http://mattermost:8065";
const ADMIN_EMAIL = process.env["MM_ADMIN_EMAIL"] ?? "admin@ofac.local";
const ADMIN_PASSWORD = process.env["MM_ADMIN_PASSWORD"];
const BOT_USERNAME = process.env["MM_BOT_USERNAME"] ?? "facilitator";
const TEAM_NAME = process.env["MM_TEAM"] ?? "team-alpha";
const TOKEN_FILE = process.env["MM_TOKEN_FILE"] ?? "/provision/mm-bot-token";
if (!ADMIN_PASSWORD) throw new Error("MM_ADMIN_PASSWORD unset");

const fail = (msg) => {
  throw new Error(`mm-init: ${msg}`);
};

async function api(method, path, body, token) {
  const res = await fetch(`${MM_URL}${path}`, {
    method,
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  let json;
  try {
    json = JSON.parse(text);
  } catch {
    /* non-JSON body: status + text suffice */
  }
  // The session token rides in the `Token` response header (Mattermost
  // login law; Set-Cookie is suppressed for non-browser clients).
  return { status: res.status, json, text, auth: res.headers.get("token") };
}
// 1. Wait for the server (bounded ~10min: first-boot DB migrations under
//    amd64 emulation are slow).
let up = false;
for (let i = 0; i < 300 && !up; i++) {
  // ECONNREFUSED during first-boot migrations is expected: fetch throws
  // before the listener exists, so the probe must swallow it.
  try {
    up = (await api("GET", "/api/v4/system/ping")).status === 200;
  } catch {
    up = false;
  }
  if (!up) await new Promise((r) => setTimeout(r, 2000));
}
if (!up) fail("mattermost never answered /system/ping");

// 2. Admin session: login; signup (first account ⇒ sysadmin) when the
//    account is absent, then login. Email verification is pinned off on the
//    service so a signup account logs in immediately.
async function login() {
  const r = await api("POST", "/api/v4/users/login", {
    login_id: ADMIN_EMAIL,
    password: ADMIN_PASSWORD,
  });
  if (r.status === 200) return r.auth;
  // First-boot law: /system/ping answers 200 BEFORE the schema migrations
  // finish — user-store calls ride through 500 store_error for a while.
  // Signup is idempotent-by-email here (400 ⇒ account exists), so a bounded
  // retry on 5xx is the honest readiness wait.
  let signup;
  for (let i = 0; i < 120; i++) {
    signup = await api("POST", "/api/v4/users", {
      email: ADMIN_EMAIL,
      username: "ofac-admin",
      password: ADMIN_PASSWORD,
    });
    if (signup.status < 500) break;
    await new Promise((r) => setTimeout(r, 2000));
  }
  if (signup.status !== 201) {
    fail(`signup failed (http ${signup.status}): ${signup.text}`);
  }
  const retry = await api("POST", "/api/v4/users/login", {
    login_id: ADMIN_EMAIL,
    password: ADMIN_PASSWORD,
  });
  if (retry.status !== 200) fail(`login after signup http ${retry.status}: ${retry.text}`);
  return retry.auth;
}
const adminToken = await login();
if (!adminToken) fail("admin login produced no session token");

// 3. Bot account (400 username-exists ⇒ already provisioned; resolve it).
let botId;
const botCreate = await api(
  "POST",
  "/api/v4/bots",
  {
    username: BOT_USERNAME,
    display_name: "Facilitator",
    description: "Open Facilitator provider adapter",
  },
  adminToken,
);
if (botCreate.status === 201) {
  botId = botCreate.json?.user_id;
} else if (botCreate.status === 400) {
  const found = await api("POST", "/api/v4/users/usernames", [BOT_USERNAME], adminToken);
  botId = found.json?.[0]?.id;
} else {
  fail(`bot create returned http ${botCreate.status}: ${botCreate.text}`);
}
if (!botId) fail("could not resolve the bot user id");

// 4. PAT: reuse the ofac-adapter token when present, else mint.
let botToken = "";
const tokenList = await api("GET", `/api/v4/users/${botId}/tokens`, undefined, adminToken);
if (tokenList.status === 200) {
  const reuse = (tokenList.json ?? []).find(
    (t) => t.description === "ofac-adapter" && t.is_active,
  );
  botToken = reuse?.token ?? "";
}
if (!botToken) {
  const mint = await api(
    "POST",
    `/api/v4/users/${botId}/tokens`,
    { description: "ofac-adapter" },
    adminToken,
  );
  if (mint.status !== 201 && mint.status !== 200) {
    fail(`token mint returned http ${mint.status}: ${mint.text}`);
  }
  botToken = mint.json?.token ?? "";
}
if (!botToken) fail("no usable bot token");

// 5. Public team + memberships so the bot sees channel traffic. The Mattermost
//    team is a provider-side container for channels; the kernel team slug is a
//    separate concept (400 name-exists ⇒ already there).
const teamCreate = await api(
  "POST",
  "/api/v4/teams",
  { name: TEAM_NAME, display_name: TEAM_NAME, type: "O" },
  adminToken,
);
// 400 name-exists ⇒ team already provisioned; resolve its id either way —
// the membership route only exists in the id form, and 11.11 requires the
// body to repeat team_id.
const team =
  teamCreate.status === 201
    ? teamCreate.json
    : (await api("GET", `/api/v4/teams/name/${TEAM_NAME}`, undefined, adminToken)).json;
const teamId = team?.id ?? fail("team unresolved");
for (const userId of [botId, (await api("GET", "/api/v4/users/me", undefined, adminToken)).json?.id]) {
  if (!userId) continue;
  await api(
    "POST",
    `/api/v4/teams/${teamId}/members`,
    { team_id: teamId, user_id: userId, roles: "team_user" },
    adminToken,
  );
}

// 6. Publish the token atomically (0444: the kernel container user differs
//    from the node user that writes it).
writeFileSync(`${TOKEN_FILE}.tmp`, botToken, { mode: 0o444 });
renameSync(`${TOKEN_FILE}.tmp`, TOKEN_FILE);
console.log(`mm-init: provisioned bot ${BOT_USERNAME} (id ${botId}), token at ${TOKEN_FILE}`);
