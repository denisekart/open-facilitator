Reference infra for AWS

Goal: a presentable "this is how you deploy open-facilitator on AWS"
picture — docs + reference CDK (TypeScript) stacks, per workload, in-repo
(e.g. `reference/deployment/aws/`). Not a product; an opinionated, boring-defaults
starting point teams fork.

## Shape

- Assume separate AWS accounts (AWS Tenants within an organization): a **platform account**
  (the facilitator — one persistent kernel per the framework docs) and one
  or more **team accounts** (gateways, workspaces, connectors, harnesses).
  Trust flows one way: team accounts assume roles / hold enroll secrets;
  the kernel never reaches into a team account (execution authority stays
  team-side; webhooks are the only inbound wire).
- Secure setup as the default, not an option: private subnets for compute,
  no public IPs, Secrets Manager for enroll tokens/DB creds, IAM roles per
  task (no static keys), TLS everywhere (ACM + ALB), security groups that
  only open the webhook/ingress path.

## Candidate stacks (CDK, one workload each, composed by a small app)

1. **network stack** — VPC (2 AZs), private subnets + NAT, VPC endpoints
   (S3, Secrets Manager, ECR, Logs — keeps the launch-credential flow off
   NAT), security-group baselines. Per account. Some teams can also have this 
   I set up already, so either deploying the network stack or attaching to one would be good choices
2. **data stack (platform acct)** — RDS Postgres (Multi-AZ for "real",
   single-AZ cheapest option to keep a demo tier), S3 artifacts bucket
   (the kernel's artifact-store seam is S3-compatible already → S3 native;
   versioned, lifecycle rules for GC, presigned URLs so blobs never transit
   the kernel), Secrets Manager, (optional) ElastiCache if caching lands.
3. **kernel stack (platform acct)** — ECS Fargate service, 2 tasks = the
   two-replica shape the repo already proves (Kernel:ReplicaId per task,
   shared Postgres, advisory-lock migrations), ALB in front. ALB rules:
   `/api/ingress/*` public (webhook sink + events; anonymous-gated,
   rate-limited, HMAC is adapter-side — this is the ONE web-exposed face),
   WAF on it; `/api/admin/*` + `/api/works` + SPA behind an auth hop
   (ALB-Cognito or a later-acting corporate IdP — open question).
   Route53 + ACM. Autoscale on CPU/queue depth (`facilitator.outbox.queue_depth`
   exists as a metric).
4. **identity** — kernel is vendor-neutral OIDC. Reference defaults:
   Cognito user pool (managed, boring) with a note that self-hosted
   Keycloak on ECS is the 1:1 port of the reference env; the
   Mattermost↔identity link rides the same /link challenge either way.
5. **team-plane stack (team acct, instantiated per team)** — gateway +
   workspace(s) as ECS tasks or a small ASG (harnesses may need docker/git
   in the runner image), outbound-only (no ALB), per-team secrets
   (enrollment secret, forge tokens, bot creds) via Secrets Manager,
   state volumes (connector SQLite) on EBS or a small EFS.
6. **elastic fleet** — the provisioning launcher's Docker-socket law does
   not survive lift-and-shift: AWS story = launcher calls ECS `RunTask`
   (or a thin adapter over the Fargate API) against a team-account
   cluster; image allowlist + network pinning map to an ECS task-template
   constraint. Worth its own thin stack + a doc note on the seam.
7. **observability stack** — OTLP already emitted: ADOT collector sidecar
   or direct OTLP→CloudWatch/AWS-X-Ray (or Grafana/ADOT→AMP); log groups
   per service, metric alarms on dead-letters + outbox depth. Keep it a
   separate stack so teams can delete it.
8. **provider legs** — webhook sources (GitHub/GitLab/Forgejo/Mattermost,
   incl. the plan-37 sink) all terminate at the kernel ALB
   (`/api/ingress/webhooks/{provider}`); self-hosted forge/chat on ECS as
   an optional stack mirroring the compose full-overlay; SaaS providers
   need nothing but an ALB rule + secret.

## Docs deliverable

`deploy/aws/README.md` (or docs/deploy-aws.md): one architecture diagram
(accounts, what crosses account boundaries, the single web face), stack
by stack: what it is, cost posture, what to change for prod; a
"dev-cheap vs prod" table (single-AZ vs Multi-AZ, Fargate vs
Provisioned-concurrency, Cognito vs corporate SSO).

## Open questions (for the plan)

- GitHub Actions OIDC → ECR + cdk deploy (repo already ships GHA) vs
  CodePipeline; probably GHA + `cdk deploy --require-approval broadening`. - a: no need for cdk deploy to be included anywhere in any of the live pipelines. Make sure to outline the approach in the readme.
- CDK in-repo language: TypeScript (matches workspace-link/adapters
  toolchain, one pnpm workspace member under `deploy/aws/`). - a: typescript!
- Secrets rotation story for link tokens (short-lived enrollment is the
  protocol's answer; Secrets Manager rotation lambda may be overkill). - a: short lived enrollment
- Whether the reference ships an "all-in-one small" tier (Fargate + RDS
  serverless v2 + one NAT) so a small team can run the whole platform - a: no, make it compatible with every usecase, focus on gitlab if we need to have a focus. Otherwise, just a reference implementation, nothing else. We never ship gitlab platform or any other platforms we interact with (we can ship a small setup for aws native OIDC solution - probably federating from another IDP)
  account cheaply — mirrors the slim profile's role.
- Data transfer/VPC-endpoint sizing for artifact-heavy workloads.

## Non-goals

- Kubernetes/EKS variant (a doc note: "EKS is a valid other answer,
  unbuilt"), multi-region, Terraform parity.


Any tests or verifications during development should completely simulate AI - there will be no AI available during ci/development/release.
Feel free to use services adjacent to AWS when testing and developing (localstack and similar - as long as open source and free) - no AWS credentials will be available anytime during development
Make sure that proper documentation exists - especially the reasoning behind choosing specific services and not the alternatives.
The implementor (after plan approval) can also install any skills or other aid necessary to properly and securely and cleanly develop the CDK solution before starting implementation.
VPCs, IDPs and other things that make sense to exist in companies already can of course exist - keep this in mind when developing IaC - the option is either to create or reference. Databases and other solution local infractructure should always be created, never referenced.
Try wrapping common building blocks into components instead of putting everything in a stack directly. It should be easy for the teams to adopt parts of the reference infra if needed.

This is a very rough outline. Let's plan it together and refine it before making an actual plan for implementation. Ask as many questions as needed - do not assume anything - ask the question.

Prefer asking more questions than less - more rounds of questions are expected, not just a single round. Let's nail down the functionality before we implement it

Let's refine and build up a proper plan together.