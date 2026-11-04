// Behavior laws (not snapshots): the security/placement invariants the
// reference deployment claims. Each assert names the law it guards.
import { Match, Template } from "aws-cdk-lib/assertions";
import { describe, expect, it } from "vitest";
import { stackOf, synthApp, exampleEnv } from "./helpers.js";

const { stacks } = synthApp();
const kernel = Template.fromStack(stackOf(stacks, "kernel"));
const data = Template.fromStack(stackOf(stacks, "data"));
const plane = Template.fromStack(stackOf(stacks, "team-team-alpha-plane"));
const network = Template.fromStack(stackOf(stacks, "network"));

describe("placement law: workloads never land in public subnets", () => {
  it("kernel Fargate service uses only private subnets", () => {
    kernel.resourceCountIs("AWS::ECS::Service", 1);
    kernel.hasResourceProperties("AWS::ECS::Service", {
      NetworkConfiguration: {
        AwsvpcConfiguration: {
          // Public subnets carry an `a` name tag pattern; private groups
          // are the only ones the selection may resolve to.
          Subnets: Match.anyValue(),
          AssignPublicIp: "DISABLED",
        },
      },
    });
    const svc = kernel.findResources("AWS::ECS::Service");
    const id = Object.keys(svc)[0]!;
    const subnets: unknown[] = svc[id]!.Properties.NetworkConfiguration.AwsvpcConfiguration.Subnets;
    // Cross-stack, subnet ids arrive as export names; the selection may
    // only reference the Private group, never Public/Nat.
    const json = JSON.stringify(subnets);
    expect(json).toContain("PrivateSubnet");
    expect(json).not.toMatch(/PublicSubnet|NatSubnet/);
  });

  it("team plane services use only private subnets", () => {
    plane.resourceCountIs("AWS::ECS::Service", 2);
    for (const svc of Object.values(plane.findResources("AWS::ECS::Service"))) {
      const json = JSON.stringify(svc.Properties.NetworkConfiguration.AwsvpcConfiguration.Subnets);
      expect(json).toContain("PrivateSubnet");
      expect(json).not.toMatch(/PublicSubnet|NatSubnet/);
      expect(svc.Properties.NetworkConfiguration.AwsvpcConfiguration.AssignPublicIp).toBe(
        "DISABLED",
      );
    }
  });
});

describe("ingress law: no 0.0.0.0/0 except the ALB", () => {
  const openCidr = (t: Template) => t.findResources("AWS::EC2::SecurityGroupIngress");

  it("only ALB-facing SGs admit 0.0.0.0/0, on 80/443", () => {
    for (const t of [kernel, data, plane, network]) {
      for (const ing of Object.values(openCidr(t))) {
        if (ing.Properties.CidrIp === "0.0.0.0/0") {
          const port = ing.Properties.IpProtocol === "-1" ? -1 : ing.Properties.FromPort;
          expect([80, 443, -1]).toContain(port);
          // -1 (all traffic) is only the ALB's own SG egress mirror; on a
          // plain ingress the ALBSG rule is always tcp 80/443.
          if (port === -1) {
            expect(
              Object.keys(t.findResources("AWS::ElasticLoadBalancingV2::LoadBalancer")).length,
            ).toBeGreaterThan(0);
          }
        }
      }
    }
  });

  it("RDS is private, MultiAZ follows the posture flag, 5432 from SG only", () => {
    data.hasResourceProperties("AWS::RDS::DBCluster", {
      ServerlessV2ScalingConfiguration: { MinCapacity: 0.5, MaxCapacity: 2 },
      StorageEncrypted: true,
      DeletionProtection: false,
    });
    for (const inst of Object.values(data.findResources("AWS::RDS::DBInstance"))) {
      expect(inst.Properties.PubliclyAccessible).toBe(false);
    }
    // 5432 admission is a KernelStack-owned standalone rule (one-way
    // stack graph): SG source, never a CIDR. The data template carries
    // no ingress rules at all.
    expect(Object.keys(data.findResources("AWS::EC2::SecurityGroupIngress"))).toHaveLength(0);
    const dbRule = Object.values(kernel.findResources("AWS::EC2::SecurityGroupIngress")).find(
      (ing) => ing.Properties.FromPort === 5432,
    )!;
    expect(dbRule.Properties.SourceSecurityGroupId).toBeDefined();
    expect(dbRule.Properties.CidrIp).toBeUndefined();
  });

  it("MultiAZ is a prop: default spans AZs, multiAz:false shrinks to the writer", () => {
    // Aurora's MultiAZ shape: an extra serverless reader beside the
    // writer. Default (multiAz unset => true) = writer + reader.
    data.resourceCountIs("AWS::RDS::DBInstance", 2);
    const env = exampleEnv();
    const { stacks: s2 } = synthApp({
      env: { ...env, platform: { ...env.platform, multiAz: false } },
    });
    Template.fromStack(stackOf(s2, "data")).resourceCountIs("AWS::RDS::DBInstance", 1);
  });

  it("enableWaf:false removes the WebACL and its association", () => {
    const env = exampleEnv();
    const { stacks: s2 } = synthApp({
      env: { ...env, platform: { ...env.platform, enableWaf: false } },
    });
    const t = Template.fromStack(stackOf(s2, "kernel"));
    t.resourceCountIs("AWS::WAFv2::WebACL", 0);
    t.resourceCountIs("AWS::WAFv2::WebACLAssociation", 0);
  });
});

describe("artifact store law", () => {
  it("artifacts bucket: versioned, public-access-blocked, SSE-KMS", () => {
    const buckets = data.findResources("AWS::S3::Bucket");
    const artifact = Object.values(buckets).find((b) => b.Properties.VersioningConfiguration);
    expect(artifact).toBeDefined();
    expect(artifact!.Properties.PublicAccessBlockConfiguration).toEqual({
      BlockPublicAcls: true,
      BlockPublicPolicy: true,
      IgnorePublicAcls: true,
      RestrictPublicBuckets: true,
    });
    expect(
      artifact!.Properties.BucketEncryption.ServerSideEncryptionConfiguration[0]
        .ServerSideEncryptionByDefault.SSEAlgorithm,
    ).toBe("aws:kms");
  });
});

describe("kernel task law: zero static keys, DB secret wired", () => {
  it("no Storage__S3__AccessKey/SecretKey; connstring references the secret ARN", () => {
    const defs = kernel.findResources("AWS::ECS::TaskDefinition");
    const def = Object.values(defs)[0]!;
    const container = def.Properties.ContainerDefinitions.find(
      (c: { Name: string }) => c.Name === "kernel",
    );
    const envNames = (container.Environment as { Name: string }[]).map((e) => e.Name);
    expect(envNames).not.toContain("Storage__S3__AccessKey");
    expect(envNames).not.toContain("Storage__S3__SecretKey");
    expect(envNames).toContain("ConnectionStrings__Facilitator");
    // The dynamic reference embeds the (cross-stack) DB secret ARN via Sub.
    const conn = (container.Environment as { Name: string; Value: { "Fn::Sub": unknown } }[]).find(
      (e) => e.Name === "ConnectionStrings__Facilitator",
    )!;
    const json = JSON.stringify(conn.Value);
    expect(json).toContain("resolve:secretsmanager");
    // host + arn arrive as Sub variables, not baked literals.
    const subVars = (conn.Value["Fn::Sub"] as [string, Record<string, unknown>])[1];
    expect(JSON.stringify(subVars)).toContain("facilitator/example-db");
    expect(json).toContain("Host=");
    // S3 settings resolve to the regional endpoint, path-style off.
    const env = container.Environment as { Name: string; Value: string }[];
    expect(env.find((e) => e.Name === "Storage__S3__Endpoint")!.Value).toBe(
      "https://s3.eu-west-1.amazonaws.com",
    );
    expect(env.find((e) => e.Name === "Storage__S3__ForcePathStyle")!.Value).toBe("false");
  });

  it("two replicas, target tracking at 60%", () => {
    kernel.hasResourceProperties("AWS::ECS::Service", { DesiredCount: 2 });
    kernel.hasResourceProperties("AWS::ApplicationAutoScaling::ScalableTarget", {
      MaxCapacity: 6,
    });
    const policies = kernel.findResources("AWS::ApplicationAutoScaling::ScalingPolicy");
    expect(Object.keys(policies).length).toBe(2);
    for (const p of Object.values(policies)) {
      expect(p.Properties.TargetTrackingScalingPolicyConfiguration.TargetValue).toBe(60);
    }
  });
});

describe("web face law: rule order + infra-auth scope", () => {
  const rules = Object.values(
    kernel.findResources("AWS::ElasticLoadBalancingV2::ListenerRule"),
  ).sort((a, b) => a.Properties.Priority - b.Properties.Priority);
  const pathPattern = (r: (typeof rules)[number]): string[] =>
    (r.Properties.Conditions as { PathPatternConfig?: { Values: string[] } }[]).flatMap(
      (c) => c.PathPatternConfig?.Values ?? [],
    );

  it("/api/ingress/* outranks the link plane, which outranks the authed catch-all", () => {
    const webhook = rules.find((r) => pathPattern(r).includes("/api/ingress/*"))!;
    const link = rules.find((r) => pathPattern(r).includes("/hubs/link"))!;
    const admin = rules.find((r) => pathPattern(r).includes("/api/admin/*"))!;
    expect(webhook.Properties.Priority).toBeLessThan(link.Properties.Priority);
    expect(link.Properties.Priority).toBeLessThan(admin.Properties.Priority);
    expect(pathPattern(link)).toEqual(
      expect.arrayContaining(["/api/link/v1/*", "/hubs/link", "/events", "/deliveries", "/health"]),
    );
    // The forward-auth hop sits on the admin/SPA rule only…
    expect(JSON.stringify(admin.Properties.Actions)).toContain("cognito");
    // …never on the webhook or link rules.
    expect(JSON.stringify(webhook.Properties.Actions)).not.toContain("cognito");
    expect(JSON.stringify(link.Properties.Actions)).not.toContain("cognito");
  });

  it("ALB lives in the public subnets only", () => {
    const lb = Object.values(kernel.findResources("AWS::ElasticLoadBalancingV2::LoadBalancer"))[0]!;
    expect(lb.Properties.Scheme).toBe("internet-facing");
    // gateway-nat posture: the public group is the Nat one; never Private.
    const json = JSON.stringify(lb.Properties.Subnets);
    expect(json).toMatch(/NatSubnet|PublicSubnet/);
    expect(json).not.toContain("PrivateSubnet");
  });

  it("WAF: rate rule + the three AWS managed groups, associated with the ALB", () => {
    kernel.resourceCountIs("AWS::WAFv2::WebACL", 1);
    const acl = Object.values(kernel.findResources("AWS::WAFv2::WebACL"))[0]!;
    const names = (acl.Properties.Rules as { Name: string }[]).map((r) => r.Name);
    expect(names).toEqual(
      expect.arrayContaining([
        "ofac-rate-public-paths",
        "ofac-KnownBadInputs",
        "ofac-IPReputation",
        "ofac-SQLi",
      ]),
    );
    kernel.resourceCountIs("AWS::WAFv2::WebACLAssociation", 1);
  });
});

describe("infraAuth off: no cognito anywhere on the face", () => {
  it("skips the forward config when platform.infraAuth=false", () => {
    const env = exampleEnv();
    const noAuth = {
      ...env,
      platform: { ...env.platform, infraAuth: false },
    };
    const { stacks: s2 } = synthApp({ env: noAuth });
    const t = Template.fromStack(stackOf(s2, "kernel"));
    // The listener carries no forward-auth hop (the task env's OIDC
    // issuer string still names cognito-idp: app-level auth is separate).
    expect(JSON.stringify(t.toJSON())).not.toContain("AuthenticateCognito");
  });
});

describe("team plane law: outbound-only, EFS-mounted", () => {
  it("team tasks SG has zero ingress rules; EFS transit encryption on", () => {
    const planeStack = stackOf(stacks, "team-team-alpha-plane");
    const t = Template.fromStack(planeStack);
    t.resourceCountIs("AWS::EC2::SecurityGroupIngress", 0);
    const defs = t.findResources("AWS::ECS::TaskDefinition");
    for (const def of Object.values(defs)) {
      const vol = def.Properties.Volumes[0];
      expect(vol.EFSVolumeConfiguration.TransitEncryption).toBe("ENABLED");
      expect(vol.EFSVolumeConfiguration.AuthorizationConfig.IAM).toBe("ENABLED");
    }
    t.resourceCountIs("AWS::ElasticLoadBalancingV2::LoadBalancer", 0);
  });
});

describe("observability law: namespace prop, alarms present", () => {
  it("outbox + dead-letter + 5xx alarms exist with the default namespace", () => {
    const t = Template.fromStack(stackOf(stacks, "observability"));
    const alarms = t.findResources("AWS::CloudWatch::Alarm");
    const json = JSON.stringify(alarms);
    expect(json).toContain('"Namespace":"facilitator"');
    expect(Object.keys(alarms).length).toBeGreaterThanOrEqual(3);
  });
});

describe("adot sidecar law: config rides the container", () => {
  it("kernel sidecar ships --config=env:ADOT_CONFIG + the shared namespace", () => {
    const defs = kernel.findResources("AWS::ECS::TaskDefinition");
    const def = Object.values(defs)[0]!;
    const adot = (def.Properties.ContainerDefinitions as Record<string, unknown>[]).find(
      (c) => c.Name === "adot",
    )!;
    expect(adot.Essential).not.toBe(false);
    expect(adot.Command).toEqual(["--config=env:ADOT_CONFIG"]);
    const env = adot.Environment as { Name: string; Value: string }[];
    const ns = env.find((e) => e.Name === "ADOT_NAMESPACE")!.Value;
    const cfg = env.find((e) => e.Name === "ADOT_CONFIG")!.Value;
    // One source: the sidecar's namespace expansion and the alarms match.
    expect(ns).toBe("facilitator");
    const obs = JSON.stringify(
      Template.fromStack(stackOf(stacks, "observability")).findResources("AWS::CloudWatch::Alarm"),
    );
    expect(obs).toContain(`"Namespace":"${ns}"`);
    // The config is real: OTLP receivers + awsemf + awsxray pipelines.
    expect(cfg).toContain("4317");
    expect(cfg).toContain("4318");
    expect(cfg).toContain("awsemf");
    expect(cfg).toContain("awsxray");
  });

  it("team plane gets the sidecar only when team.observability=true", () => {
    // example env: off ⇒ no sidecar on plane tasks.
    for (const def of Object.values(plane.findResources("AWS::ECS::TaskDefinition"))) {
      const names = (def.Properties.ContainerDefinitions as { Name: string }[]).map((c) => c.Name);
      expect(names).not.toContain("adot");
    }
    const env = exampleEnv();
    const { stacks: s2 } = synthApp({
      env: {
        ...env,
        teams: env.teams.map((t) => ({ ...t, observability: true })),
      },
    });
    const t = Template.fromStack(stackOf(s2, "team-team-alpha-plane"));
    for (const def of Object.values(t.findResources("AWS::ECS::TaskDefinition"))) {
      const adot = (def.Properties.ContainerDefinitions as Record<string, unknown>[]).find(
        (c) => c.Name === "adot",
      );
      expect(adot).toBeDefined();
      expect(adot!.Command).toEqual(["--config=env:ADOT_CONFIG"]);
    }
    // Self-contained publish policy in the TEAM account. The plane may
    // import its OWN team-network exports, but never anything from the
    // platform stacks (no ref into the platform Observability plane).
    const policies = JSON.stringify(t.findResources("AWS::IAM::Policy"));
    expect(policies).toContain("cloudwatch:PutMetricData");
    expect(policies).toContain("xray:PutTraceSegments");
    expect(JSON.stringify(t.toJSON())).not.toMatch(
      /ofac-example-(kernel|observability|data|identity):/,
    );
  });
});
