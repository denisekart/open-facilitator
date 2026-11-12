# Example: seed repositories

Forgejo repositories seeded for the full reference environment demos.

`forgejo-adapter init` (idempotent, safe to re-run) provisions everything
the full-profile live-proof chain rides on, against the forge named by
`FORGEJO_URL`:

1. **First admin, login-first.** If the forge has no admin account (fresh
   `INSTALL_LOCK` install), signs one up via the `POST /user/sign_up`
   form route with `admin_password`/`admin_confirm_password`; otherwise
   authenticates the existing admin with Basic auth. Credentials come
   from `DEMO_FORGEJO_ADMIN_USER|PASSWORD|EMAIL`.
2. **Bot account + PAT.** `facilitator-bot` (created as admin, no password
   login) with a fresh PAT minted to `FORGEJO_TOKEN_FILE` (0400). The PAT
   is provider-generated and unpinnable, so re-runs rotate the file —
   consumers read it from the shared `forgejo_provision` volume.
3. **Org `demo` + repo `demo/widgets`** with an initial README
   (`auto_init`, 409-reconcile on re-run), and the bot granted repo write.
4. **Label `ofac:work` + a sample issue carrying it** — labeling an issue
   with a `FORGEJO_WORK_LABELS` name is what opens a Work. Title-keyed
   reconcile: re-runs re-label instead of duplicating.
5. **Webhook** on `demo/widgets` (Gitea-family type) pointing at
   `FORGEJO_HOOK_URL`, signed with `FORGEJO_WEBHOOK_SECRET`. Events are
   registered as the coarse names Forgejo 16's vocabulary accepts
   (`issues`, `issue_comment`, `pull_request`) — unknown names are
   silently dropped server-side, and a stale/partial event list on an
   existing hook is reconciled by PATCH.

Reports one JSON line: `{"admin":...,"bot":...,"org":...,"repo":...,
"issue":...,"webhook":"exists|created"}`. Exit 0 on success, 2 on config
error.

Live proof that the seeded state works: `make provider-e2e` (the
`forgejo-seed` step checks the repo, sample issue, and hook exist; the
issue→Work, comment-input, PR-connector, review-steer, and CI-note steps
exercise the rest).
