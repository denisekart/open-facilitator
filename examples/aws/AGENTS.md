# examples/aws/

## Purpose
AWS reference deployment (fork-me example, not a product): CDK v2 app,
one stack per workload, driven by `envs/<name>.json`. Platform account
(kernel/data/identity, the only web face) + team accounts (gateway/
workspaces/fleet launcher, outbound-only). Operator guide: `README.md`.

## Invariants (invisible in code)
- **No `cdk deploy` in any pipeline.** Gate = synth snapshots + cdk-nag
  (`pnpm test`) + local-only `./deployctl smoke`. Deploys are human-run.
- **Create-or-reference law:** VPC / Route53 zone / IdP federation are
  created only when the env file has no id for them; RDS/S3/secrets/EFS
  are always created here (`lib/env.ts` encodes the contract).
- **Zero static keys:** task roles ARE the credentials — the kernel's S3
  store resolves the SDK default chain when AccessKey/SecretKey are unset
  as a PAIR (`../../src/facilitator/Kernel/Shared/Storage/S3ArtifactStore.cs`).
- **Offline synth:** no AWS creds exist in dev/CI — lookups are
  pre-seeded/explicit-id only (`lib/hosted-zone.ts`); a credential-less
  `cdk synth -c envFile=envs/example.json` must keep exiting 0.
- **One-way stack graph:** data/identity/network never reference kernel/
  team stacks (cross-stack seams are strings/ARN-name dynamic refs — an
  L2 grant across stacks re-introduces the CFN cycle).
- **The fleet launcher is non-production** (`fleet-launcher/`): unsupported,
  opt-in per team via `"fleet": true`.

## Truth anchors
- `lib/env.ts` — env-file contract (+ optional `hostedZoneId`,
  `infraAuth`, `observability`, `enableWaf`, `production`).
- `lib/build.ts` — wiring factory + stack naming (`ofac-{env}-…`) + tags.
- `lib/kernel-service.ts` — kernel env 1:1 with
  `../../reference/config/facilitator.env` (no S3 keys); ADOT image pin.
- `lib/web-face.ts` — listener-rule order 10/20/100 + forward-auth law.
- `test/behavior.test.ts` — the security laws asserted, not just snapshotted.
- `envs/example.json` — the template; `deployctl` — the operator verbs
  (plan/diff/deploy/destroy/smoke; `--account platform|team:<name>`).
