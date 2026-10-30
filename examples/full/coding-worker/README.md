# Example: coding worker (full profile)

The spec §23.1 demo worker: an issue label becomes a merged-ready change.
`coding.implement` clones the assign's repo over git-http (bot PAT from
`FORGEJO_TOKEN_FILE`, read per run so rotation is the next run's truth),
appends a `CODE_CHANGELOG.md` entry + a deterministic test on branch
`ofac/<assignment8>`, runs `node --test`, pushes `--force-with-lease`, and
completes with a Result Manifest whose `structuredResult` the kernel reads:
`prRequest` (first run) and one `commentRequests` entry per incorporated
steer become connector ops — the worker itself calls no PR API; the team
token never leaves the team side (§9.2).

A steer after completion re-dispatches the original assign with the
directive in `inputs.steers`: the worker re-hydrates by adopting the
existing branch (checkout tracks the remote head), appends only the missing
steer lines, and re-publishes. The live legs run in
`reference/checks/verify-full.sh` (step ids `coding-*`).

## Where the secrets live

| Secret                                    | Holder                  | Path                                                                      |
| ----------------------------------------- | ----------------------- | ------------------------------------------------------------------------- |
| Bot PAT (git push + connector PR/comment) | the workspace container | `FORGEJO_TOKEN_FILE` (provisioned by `forgejo-init` into a shared volume) |
| Child enrollment secret                   | the worker unit         | `OFAC_CHILD_SECRET` (self-enrolls `coding-worker-child@team-coding`)      |
| Kernel token                              | the SDK session         | minted at enrollment; the kernel holds none of the team's provider tokens |

## Re-running the demo

```bash
make compose-full                                  # boots coding-workspace too
make provider-e2e ARGS="--only coding-issue-work"  # fresh labeled issue -> Work
make provider-e2e ARGS="--only coding-pr"          # -> PR via the connector
make provider-e2e ARGS="--only coding-review-steer" # review -> steer -> re-dispatch
make provider-e2e ARGS="--only coding-redispatch"  # offline steer -> restart leg
```

Append-only: each run labels a new issue; prior PRs stay. Unit tests need no
Docker: `pnpm -C examples/full/coding-worker test` (the lifecycle contract
runs the hooks `gitless: true`; the git units run against a local bare repo).
