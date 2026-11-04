// cdk-nag v3: AwsSolutionsChecks is a validation PLUGIN (not an aspect).
// Run it per stack via validateScope; suppressions go through the CDK
// Validations.acknowledge mechanism (recorded on the stack — the plugin
// walks ancestor metadata), each with a written reason. v3 matches
// acknowledgment ids EXACTLY, findings included (`Rule[Resource::*]`),
// so the table carries the full fired ids. Zero remaining errors is the
// gate: any new finding fails until fixed or argued here.
import { Validations } from "aws-cdk-lib";
import { AwsSolutionsChecks } from "cdk-nag";
import { describe, expect, it } from "vitest";
import { synthApp } from "./helpers.js";

interface Suppression {
  readonly id: string;
  readonly reason: string;
}

const IAM5_STAR: Suppression = {
  // Wildcard resource on CDK-assembled policies: OTLP publish (metrics are
  // namespace-scoped in the request, not the ARN) + SMS publish role.
  id: "AwsSolutions-IAM5[Resource::*]",
  reason:
    "service has no resource ARNs for this action (metric/SNS publish); scope is request-side",
};

const SUPPRESSIONS: Record<string, Suppression[]> = {
  "ofac-example-network": [
    // VPC flow logs off: a demo deployment trades forensic depth for cost;
    // the README's prod table says to turn them on.
    { id: "AwsSolutions-VPC7", reason: "flow logs are a prod posture knob (cost)" },
    {
      // The rule chokes on the tokenized VPC-CIDR in the endpoints' SG
      // ingress; those rules are intra-VPC by construction, not 0.0.0.0/0.
      id: "AwsSolutions-EC23",
      reason: "rule cannot evaluate tokenized VPC-CIDR ingress; endpoint SGs are intra-VPC only",
    },
  ],
  "ofac-example-data": [
    { id: "AwsSolutions-RDS10", reason: "deletion protection is prod-gated by design" },
    { id: "AwsSolutions-RDS11", reason: "PITR default; extended backup window is a prod knob" },
    { id: "AwsSolutions-RDS13", reason: "performance insights billed per instance; dev posture" },
    {
      // The check targets non-Aurora instances; this is an Aurora cluster
      // whose writer/reader already span AZs.
      id: "AwsSolutions-RDS6",
      reason: "Aurora cluster: Multi-AZ is a cluster property, per-instance flag N/A",
    },
    {
      id: "AwsSolutions-S1",
      reason: "the access-logs bucket itself does not log to itself (recursion)",
    },
    {
      id: "AwsSolutions-SMG4",
      reason:
        "RDS rotation rides the managed Lambda at deploy; the app-secret placeholder is a manual hand-off by design",
    },
  ],
  "ofac-example-identity": [
    {
      id: "AwsSolutions-COG1",
      reason: "password policy is managed-login/IdP-federated; no local passwords by default",
    },
    {
      id: "AwsSolutions-COG2",
      reason:
        "MFA enforced at the corporate IdP when federation is on; demo pool has no local users",
    },
    {
      id: "AwsSolutions-COG3",
      reason: "advanced security features billed per MAU; dev posture",
    },
    {
      id: "AwsSolutions-COG4",
      reason: "the ALB forward-auth is the resource server check; no API Gateway here",
    },
    {
      // Cognito creates an unauthenticated-identities role for the pool;
      // the user POOL (what we serve) never grants unauthenticated logins.
      id: "AwsSolutions-COG8",
      reason: "managed identity-pool artifact role; access is by user-pool federation only",
    },
    {
      id: "AwsSolutions-COG7",
      reason: "managed-login domain is CloudFront-fronted (TLS) by Cognito itself",
    },
    IAM5_STAR,
  ],
  "ofac-example-kernel": [
    {
      id: "AwsSolutions-ECS2",
      reason: "container env carries only non-secret config; secrets ride dynamic refs + task role",
    },
    {
      id: "AwsSolutions-ECS4",
      reason: "cluster container-insights OFF is a deliberate cost knob (prop-documented)",
    },
    {
      id: "AwsSolutions-EC23",
      reason:
        "0.0.0.0/0 ingress exists only on the ALB SG (the web face); asserted in behavior tests",
    },
    {
      id: "AwsSolutions-ELB2",
      reason:
        "ALB access logs to a dedicated bucket are a cost knob; WAF + CW metrics carry the demo",
    },
    {
      id: "AwsSolutions-S1",
      reason: "the ALB access-logs bucket does not log to itself (recursion)",
    },
    IAM5_STAR,
    {
      // The secret `name-*` suffix (CFN appends 6 random chars) — least
      // privilege expressed WITH the mandatory wildcard, nothing broader.
      id: "AwsSolutions-IAM5[Resource::arn:aws:secretsmanager:eu-west-1:111111111111:secret:facilitator/example-db-*]",
      reason: " Secrets Manager ARNs require the name suffix wildcard",
    },
    {
      // Object-level access on the artifact bucket: the bucket-scoped
      // sibling statement already pins Get/Put to this one bucket.
      id: "AwsSolutions-IAM5[Resource::<ArtifactsD26CF9E8.Arn>/*]",
      reason: "bucket-object paths: the object wildcard under one pinned bucket ARN",
    },
    {
      id: "AwsSolutions-L1",
      reason: "lambda runtimes are CDK-managed custom resources, not our code",
    },
  ],
  "ofac-example-observability": [
    {
      id: "AwsSolutions-SNS2",
      reason: "pager topic SSE-KMS is a prod knob; the CMK would cross stacks here",
    },
    {
      id: "AwsSolutions-SNS3",
      reason: "topic policy restricted to CloudWatch actions (in-template)",
    },
  ],
  "ofac-example-team-team-alpha-network": [
    { id: "AwsSolutions-VPC7", reason: "flow logs are a prod posture knob (cost)" },
    {
      id: "AwsSolutions-EC23",
      reason: "rule cannot evaluate tokenized VPC-CIDR ingress; endpoint SGs are intra-VPC only",
    },
  ],
  "ofac-example-team-team-alpha-plane": [
    {
      id: "AwsSolutions-ECS2",
      reason: "link tokens inject via ECS secrets, not plaintext env",
    },
    {
      id: "AwsSolutions-ECS4",
      reason: "cluster container-insights OFF is a deliberate cost knob (prop-documented)",
    },
    {
      id: "AwsSolutions-ECS7",
      reason:
        "read-only root FS would break the connector's SQLite state writes (EFS mount carries the state)",
    },
    {
      id: "AwsSolutions-EFS1",
      reason:
        "EFS is VPC-internal (encrypted at rest+in transit); public access is structurally impossible",
    },
  ],
};

describe("cdk-nag", () => {
  it("zero un-suppressed errors on every stack", () => {
    const { stacks } = synthApp();
    const failures: string[] = [];
    for (const [name, stack] of stacks) {
      for (const s of SUPPRESSIONS[name] ?? []) {
        Validations.of(stack).acknowledge(s);
      }
      const report = new AwsSolutionsChecks().validateScope(stack);
      for (const v of report.violations) {
        // Warnings (advisory level) don't fail the gate; errors do.
        if (v.severity === "error") {
          for (const r of v.violatingResources) {
            failures.push(`${name}: ${v.ruleName} @ ${r.constructPath}`);
          }
        }
      }
    }
    expect(failures).toEqual([]);
  });
});
