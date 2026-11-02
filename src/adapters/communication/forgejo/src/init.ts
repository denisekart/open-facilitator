// Idempotent seed init (decision 7, the mm-init.mjs law in package
// code): login-first, 409-reconcile. Against FORGEJO_URL as the admin:
//   1. login; no account yet ⇒ POST /user/sign_up — with zero users a forge
//      promotes the first signup to admin (step 0: no anonymous first-admin
//      REST route; the contingency applies). INSTALL_LOCK=true with no
//      admin still serves /user/sign_up.
//   2. a PAT (409-reconcile: reruns mint under a fresh name).
//   3. org `demo`, repo `<org>/widgets` with an initial README (409-reconcile).
//   4. label `ofac:work` + a sample issue carrying it (title-keyed reconcile).
//   5. the repo webhook → the adapter's in-network hook URL with the shared
//      secret (list-first: one hook only).
//   6. write the bot token file 0444-atomic (the handoff law; the
//      adapter/connector read FORGEJO_TOKEN_FILE).
// Throws on any un-reconcilable failure; a completed rerun is a no-op.
import { chmodSync, renameSync, writeFileSync } from "node:fs";

export interface InitConfig {
  forgejoUrl: string;
  adminUser: string;
  adminPassword: string;
  adminEmail: string;
  /** Distinct non-admin bot account (decision 8): the write identity for
   *  comments, so admin-authored issues pass the adapter's echo filter. */
  botUser: string;
  botEmail: string;
  org: string;
  repo: string;
  sampleTitle: string;
  workLabel: string;
  /** Optional second seed (coding lane): an issue titled thus, carrying this
   *  label — same reconcile law as the work-label sample. Unset ⇒ no-op. */
  codingIssueTitle?: string | undefined;
  codingLabel?: string | undefined;
  hookUrl: string;
  webhookSecret: string;
  botTokenFile: string;
}

export interface InitDeps {
  fetchImpl?: typeof fetch;
  log?(line: string): void;
}

export interface InitReport {
  admin: "login" | "signup";
  bot: "created" | "exists";
  org: "created" | "exists";
  repo: "created" | "exists";
  issue: "created" | "exists";
  codingIssue?: "created" | "exists" | undefined;
  webhook: "created" | "exists";
}

type Fetch = typeof fetch;

function baseOf(url: string): string {
  return url.replace(/\/+$/, "");
}

async function call(
  fetchImpl: Fetch,
  base: string,
  method: string,
  path: string,
  opts: { token?: string; basic?: string; json?: unknown; form?: URLSearchParams } = {},
): Promise<{ status: number; json: unknown }> {
  const response = await fetchImpl(`${base}${path}`, {
    method,
    headers: {
      ...(opts.token === undefined
        ? opts.basic === undefined
          ? {}
          : { authorization: `Basic ${Buffer.from(opts.basic).toString("base64")}` }
        : { authorization: `token ${opts.token}` }),
      ...(opts.json === undefined ? {} : { "content-type": "application/json" }),
      ...(opts.form === undefined ? {} : { "content-type": "application/x-www-form-urlencoded" }),
    },
    ...(opts.json === undefined ? {} : { body: JSON.stringify(opts.json) }),
    ...(opts.form === undefined ? {} : { body: opts.form.toString() }),
  });
  const text = await response.text();
  let json: unknown;
  try {
    json = text === "" ? undefined : JSON.parse(text);
  } catch {
    json = text;
  }

  return { status: response.status, json };
}

function asRecord(value: unknown): Record<string, unknown> {
  return typeof value === "object" && value !== null ? (value as Record<string, unknown>) : {};
}

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

export async function runInit(config: InitConfig, deps: InitDeps = {}): Promise<InitReport> {
  const fetchImpl = deps.fetchImpl ?? fetch;
  const log = deps.log ?? ((line: string) => process.stderr.write(`forgejo-init: ${line}\n`));
  const base = baseOf(config.forgejoUrl);
  const basic = `Basic ${Buffer.from(`${config.adminUser}:${config.adminPassword}`).toString("base64")}`;

  // 1. admin: login-first; first-signup fallback when no account exists.
  let admin: InitReport["admin"];
  if ((await fetchImpl(`${base}/api/v1/user`, { headers: { authorization: basic } })).ok) {
    admin = "login";
  } else {
    const signup = await fetchImpl(`${base}/user/sign_up`, {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        user_name: config.adminUser,
        email: config.adminEmail,
        password: config.adminPassword,
        retype: config.adminPassword,
        admin_confirm_password: "I understand and accept",
      }).toString(),
      redirect: "manual",
    });
    if (signup.status >= 400) {
      throw new Error(`first-admin sign_up failed: HTTP ${signup.status}`);
    }

    admin = "signup";
    if (!(await fetchImpl(`${base}/api/v1/user`, { headers: { authorization: basic } })).ok) {
      throw new Error("login after sign_up failed");
    }
  }

  // 1b. bot: a distinct non-admin account (admin-created; 409 = exists).
  // The bot PAT belongs to THIS account, so adapter comments carry the bot
  // identity the echo filter drops (an admin-authored event must NOT be
  // echo-dropped — step 6 seeds issues AS the admin).
  const botCreate = await call(fetchImpl, base, "POST", "/api/v1/admin/users", {
    basic: `${config.adminUser}:${config.adminPassword}`,
    json: {
      username: config.botUser,
      email: config.botEmail,
      password: config.adminPassword,
      // Forgejo 16 flags admin-created accounts must-change-password (403 on
      // the bot's own token mint) unless explicitly cleared — verified live.
      must_change_password: false,
    },
  });
  const bot: InitReport["bot"] =
    botCreate.status === 201
      ? "created"
      : botCreate.status === 409 || botCreate.status === 422
        ? "exists"
        : (() => {
            throw new Error(`bot create failed: HTTP ${botCreate.status}`);
          })();

  // 2. PAT (admin, via Basic): a 409 name collision (rerun) mints fresh.
  // Forgejo 16 requires a scope on mint and parses this endpoint FORM-encoded
  // (verified live on 16.0.7: JSON `scope` is ignored → 400 "must have a
  // scope"; form `name&scopes=all` → 201). `all` — the bot writes
  // issues/comments/statuses and reads PRs.
  const adminBasic = `${config.adminUser}:${config.adminPassword}`;
  const mint = async (owner: string, auth: string, name: string): Promise<string> => {
    const response = await call(fetchImpl, base, "POST", `/api/v1/users/${owner}/tokens`, {
      basic: auth,
      form: new URLSearchParams({ name, scopes: "all" }),
    });
    const sha1 = asRecord(response.json)["sha1"];
    return typeof sha1 === "string" ? sha1 : "";
  };
  let token = await mint(config.adminUser, adminBasic, "ofac-forgejo-init");
  if (token === "")
    token = await mint(config.adminUser, adminBasic, `ofac-forgejo-init-${Date.now()}`);
  if (token === "") throw new Error("no PAT issued");

  // 3. org + repo (409-reconcile).
  const orgCreate = await call(fetchImpl, base, "POST", "/api/v1/orgs", {
    token,
    json: { username: config.org, full_name: config.org },
  });
  const org: InitReport["org"] =
    orgCreate.status === 201
      ? "created"
      : orgCreate.status === 409 || orgCreate.status === 422
        ? "exists"
        : (() => {
            throw new Error(`org create failed: HTTP ${orgCreate.status}`);
          })();

  const repoName = config.repo.includes("/") ? config.repo.split("/")[1] : config.repo;
  const repoCreate = await call(fetchImpl, base, "POST", `/api/v1/orgs/${config.org}/repos`, {
    token,
    json: { name: repoName, auto_init: true, default_branch: "main" },
  });
  const repo: InitReport["repo"] =
    repoCreate.status === 201
      ? "created"
      : repoCreate.status === 409
        ? "exists"
        : (() => {
            throw new Error(`repo create failed: HTTP ${repoCreate.status}`);
          })();
  log(`org/repo ${config.repo}: ${org}/${repo}`);

  // 3b. bot gets repo write (comments/labels on seeded + future issues).
  await call(
    fetchImpl,
    base,
    "PUT",
    `/api/v1/repos/${config.repo}/collaborators/${config.botUser}`,
    {
      basic: adminBasic,
      json: { permission: "write" },
    },
  );

  // 4. labels + sample issues (title-keyed reconcile, one pass per seed:
  // the ofac:work sample always, the coding-lane pair only when configured).
  // Forgejo 16 renamed the label field `title`→`name` (422 "[Name]: Required"
  // verified live); send both — pre-16 ignores `name`, 16 ignores `title`.
  // List-first (hook law): a rerun POSTing blind grows duplicates.
  let issue: InitReport["issue"] = "exists";
  let codingIssue: InitReport["codingIssue"];
  const seeds: { label: string; title: string; mark: (r: "created" | "exists") => void }[] = [
    {
      label: config.workLabel,
      title: config.sampleTitle,
      mark: (r) => {
        issue = r;
      },
    },
    ...(config.codingIssueTitle !== undefined && config.codingLabel !== undefined
      ? [
          {
            label: config.codingLabel,
            title: config.codingIssueTitle,
            mark: (r: "created" | "exists") => {
              codingIssue = r;
            },
          },
        ]
      : []),
  ];
  let labelRows = asArray(
    (await call(fetchImpl, base, "GET", `/api/v1/repos/${config.repo}/labels`, { token })).json,
  );
  const issues = asArray(
    (await call(fetchImpl, base, "GET", `/api/v1/repos/${config.repo}/issues?state=all`, { token }))
      .json,
  );
  for (const seed of seeds) {
    if (!labelRows.map(asRecord).some((row) => (row["name"] ?? row["title"]) === seed.label)) {
      await call(fetchImpl, base, "POST", `/api/v1/repos/${config.repo}/labels`, {
        token,
        json: { title: seed.label, name: seed.label, color: "#1d76db" },
      });
      labelRows = asArray(
        (await call(fetchImpl, base, "GET", `/api/v1/repos/${config.repo}/labels`, { token })).json,
      );
    }
    let number: number;
    if (!issues.some((row) => asRecord(row)["title"] === seed.title)) {
      const created = await call(fetchImpl, base, "POST", `/api/v1/repos/${config.repo}/issues`, {
        token,
        json: {
          title: seed.title,
          body: `Seeded by forgejo-init: label ${seed.label} triggers a Work.`,
        },
      });
      if (created.status !== 201) throw new Error(`issue create failed: HTTP ${created.status}`);
      seed.mark("created");
      number = Number(asRecord(created.json)["number"] ?? 0);
    } else {
      seed.mark("exists");
      number = Number(
        issues.map(asRecord).find((row) => row["title"] === seed.title)?.["number"] ?? 0,
      );
    }

    // Label the sample whether it was created now or on an earlier run (a run
    // that predates the label step must converge, not stay half-seeded).
    // Re-check the issue's own labels first: a labeled issue means the
    // labeled webhook already fired once.
    const labelId = labelRows
      .map(asRecord)
      .find((row) => (row["name"] ?? row["title"]) === seed.label)?.["id"];
    const detail = asRecord(
      (
        await call(fetchImpl, base, "GET", `/api/v1/repos/${config.repo}/issues/${number}`, {
          token,
        })
      ).json,
    );
    const mine = asArray(detail["labels"] ?? []);
    if (
      labelId !== undefined &&
      number > 0 &&
      !mine.map(asRecord).some((row) => (row["name"] ?? row["title"]) === seed.label)
    ) {
      await call(fetchImpl, base, "POST", `/api/v1/repos/${config.repo}/issues/${number}/labels`, {
        token,
        json: { labels: [labelId] },
      });
      log(`sample issue #${number} labeled ${seed.label}`);
    }
  }

  // 5. webhook: list-first, exactly one pointing at the adapter. 16.x event
  // vocabulary (verified live): coarse names expand server-side; unknown
  // names are SILENTLY dropped — `pull_request_review` and `status` are not
  // names at all on 16 (commit-status webhooks: open feature request
  // forgejo/forgejo#9325). The three coarse names cover every arm.
  let webhook: InitReport["webhook"];
  const hookRows = asArray(
    (await call(fetchImpl, base, "GET", `/api/v1/repos/${config.repo}/hooks`, { token })).json,
  );
  const hookEvents = ["issues", "issue_comment", "pull_request"];
  const ourHook = hookRows
    .map(asRecord)
    .find((row) => String(asRecord(row["config"])["url"]) === config.hookUrl);
  if (ourHook !== undefined) {
    webhook = "exists";
    // A half-configured hook (missing event) must converge, not stay deaf.
    const have = new Set(asArray(ourHook["events"]).map((e) => String(e)));
    if (!hookEvents.every((e) => have.has(e))) {
      await call(
        fetchImpl,
        base,
        "PATCH",
        `/api/v1/repos/${config.repo}/hooks/${String(ourHook["id"])}`,
        {
          token,
          json: { events: hookEvents },
        },
      );
      log(`hook events → ${hookEvents.join(",")}`);
    }
  } else {
    const hook = await call(fetchImpl, base, "POST", `/api/v1/repos/${config.repo}/hooks`, {
      token,
      json: {
        type: "gitea",
        active: true,
        events: hookEvents,
        config: { url: config.hookUrl, content_type: "json", secret: config.webhookSecret },
      },
    });
    if (hook.status !== 201) throw new Error(`webhook create failed: HTTP ${hook.status}`);
    webhook = "created";
    log(`webhook → ${config.hookUrl}`);
  }

  // 6. bot token handoff: the BOT's own PAT (comment identity = bot, so the
  // adapter's echo filter drops its writes and admin events flow through),
  // written 0444-atomic. Bot password = adminPassword (created above).
  const botBasic = `${config.botUser}:${config.adminPassword}`;
  let botToken = await mint(config.botUser, botBasic, "ofac-forgejo-bot");
  if (botToken === "")
    botToken = await mint(config.botUser, botBasic, `ofac-forgejo-bot-${Date.now()}`);
  if (botToken === "") {
    // A bot created before the flag was cleared (or by another tool) still
    // carries must-change-password: PATCH it off over the admin API, re-mint.
    await call(fetchImpl, base, "PATCH", `/api/v1/admin/users/${config.botUser}`, {
      basic: `${config.adminUser}:${config.adminPassword}`,
      json: { must_change_password: false },
    });
    botToken = await mint(config.botUser, botBasic, `ofac-forgejo-bot-r${Date.now()}`);
  }
  if (botToken === "") throw new Error("no bot PAT issued");
  const tmp = `${config.botTokenFile}.${process.pid}.${Date.now()}.tmp`;
  writeFileSync(tmp, `${botToken}\n`, { mode: 0o444 });
  chmodSync(tmp, 0o444);
  renameSync(tmp, config.botTokenFile);
  log(`bot token → ${config.botTokenFile}`);

  return { admin, bot, org, repo, issue, codingIssue, webhook };
}
