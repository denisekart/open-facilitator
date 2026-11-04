import { DEFAULT_METRIC_NAMESPACE, adotContainerOptions } from "./adot.js";
import { Duration, Fn, Stack, Tags } from "aws-cdk-lib";
import {
  AwsLogDriver,
  ContainerImage,
  FargatePlatformVersion,
  FargateService,
  FargateTaskDefinition,
} from "aws-cdk-lib/aws-ecs";
import type { ICluster } from "aws-cdk-lib/aws-ecs";
import { Policy, PolicyStatement } from "aws-cdk-lib/aws-iam";
import type { IRole } from "aws-cdk-lib/aws-iam";
import { LogGroup, RetentionDays } from "aws-cdk-lib/aws-logs";
import { SecurityGroup } from "aws-cdk-lib/aws-ec2";
import type { IVpc, SubnetSelection } from "aws-cdk-lib/aws-ec2";
import {
  ApplicationTargetGroup,
  ApplicationProtocol,
  TargetType,
} from "aws-cdk-lib/aws-elasticloadbalancingv2";
import type { IApplicationTargetGroup } from "aws-cdk-lib/aws-elasticloadbalancingv2";
import type { IBucket } from "aws-cdk-lib/aws-s3";
import { Construct } from "constructs";

/**
 * The kernel on Fargate: two replicas (the outbox dispatcher is a DB-lease
 * consumer and migrations take pg_advisory_lock, so concurrent instances
 * are safe — the same shape the two-replica compose overlay proves).
 * Lease identity: `Kernel:ReplicaId` falls back to machine-name/GUID per
 * process, and each Fargate task is its own micro-VM hostname, so the
 * default already yields a unique-per-replica identity.
 *
 * Credentials: the task role IS the credential (zero static keys) — the
 * S3 store resolves it through the SDK default chain; the RDS connection
 * string arrives from Secrets Manager. Env mirrors
 * `reference/config/facilitator.env` 1:1 in variable names, values from
 * construct outputs.
 *
 * OTLP: with `observability` on, an ADOT collector sidecar receives the
 * kernel's OTLP/HTTP on localhost and forwards to CloudWatch/X-Ray; with
 * it off the kernel simply has no exporter target (the endpoint is the
 * kernel's own on/off switch). Autoscaling is CPU/memory target-tracking
 * only; the outbox-depth gauge rides OTLP→CloudWatch (namespace confirmed
 * at first deploy, see README).
 */
export interface KernelServiceProps {
  readonly vpc: IVpc;
  readonly vpcSubnets: SubnetSelection;
  readonly cluster: ICluster;
  readonly imageUri: string;
  /** RDS master secret NAME (same account/region, deterministic). The
   *  connection string embeds it in a secretsmanager dynamic reference,
   *  so KernelStack carries NO cross-stack Ref into DataStack at all —
   *  the stack graph stays a DAG by construction, and secret rotation
   *  re-resolves at change-set time without touching this template. */
  readonly dbSecretName: string;
  /** RDS writer endpoint address (DataStack output). */
  readonly dbHost: string;
  /** CMK behind the secret (same-account): IAM-side decrypt, because a
   *  key-policy grant from the other stack would point DataStack at
   *  KernelStack and a CFN stack graph must stay a DAG. */
  readonly dbEncryptionKeyArn?: string;
  readonly artifactBucket: IBucket;
  /** `https://cognito-idp.<region>.amazonaws.com/<poolId>` — byte-match. */
  readonly oidcAuthority: string;
  readonly oidcAudience: string;
  readonly uiOidcAuthority: string;
  readonly uiOidcClientId: string;
  readonly desiredCount?: number;
  readonly cpu?: number;
  readonly memoryMiB?: number;
  readonly observability?: boolean;
  /** OTLP→CW namespace for the sidecar config; the SAME value the
   *  Observability alarms use, threaded from the stack (no drift). */
  readonly metricNamespace?: string;
  /** Statements contributed by the Observability stack. */
  readonly extraTaskPolicy?: PolicyStatement[];
}

export class KernelService extends Construct {
  readonly service: FargateService;
  readonly targetGroup: IApplicationTargetGroup;
  readonly securityGroup: SecurityGroup;
  readonly taskRole: IRole;
  /** Container port; the stack opens the ALB SG to it. */
  readonly port: number;

  constructor(scope: Construct, id: string, props: KernelServiceProps) {
    super(scope, id);
    const stack = Stack.of(this);
    const port = 8080;
    const desired = props.desiredCount ?? 2;
    const taskDef = new FargateTaskDefinition(this, "TaskDef", {
      cpu: props.cpu ?? 1024,
      memoryLimitMiB: props.memoryMiB ?? 2048,
    });
    const logGroup = new LogGroup(this, "Logs", {
      logGroupName: `/ofac/${stack.stackName}/kernel`,
      retention: RetentionDays.TWO_YEARS,
    });
    const container = taskDef.addContainer("kernel", {
      image: ContainerImage.fromRegistry(props.imageUri),
      logging: new AwsLogDriver({ logGroup, streamPrefix: "kernel" }),
      environment: {
        // 1:1 with reference/config/facilitator.env (values from outputs).
        Storage__Provider: "s3",
        Storage__S3__Endpoint: `https://s3.${stack.region}.amazonaws.com`,
        Storage__S3__Bucket: props.artifactBucket.bucketName,
        Storage__S3__Region: stack.region,
        Storage__S3__ForcePathStyle: "false",
        // No AccessKey/SecretKey: the task role IS the credential.
        Link__HeartbeatSeconds: "5",
        Otel__Endpoint: "http://127.0.0.1:4318",
        OTEL_EXPORTER_OTLP_ENDPOINT: "http://127.0.0.1:4318",
        OTEL_EXPORTER_OTLP_PROTOCOL: "http/protobuf",
        OTEL_SERVICE_NAME: "facilitator",
        identity__oidc__authority: props.oidcAuthority,
        identity__oidc__audience: props.oidcAudience,
        identity__oidc__requireHttps: "true",
        ui__oidcAuthority: props.uiOidcAuthority,
        ui__oidcClientId: props.uiOidcClientId,
        // The kernel feeds ConnectionStrings__Facilitator to UseNpgsql,
        // so it must be a connection string, not the raw secret JSON.
        // CloudFormation resolves the secretsmanager dynamic reference
        // embedded in the Sub at change-set time (rotation re-deploys;
        // no runtime SDK hop). The generated RDS password excludes
        // `;`/`,`/quotes, so it needs no connection-string quoting.
        ConnectionStrings__Facilitator: Fn.sub(
          "Host=${Host};Database=facilitator;Username=facilitator;Password={{resolve:secretsmanager:${Name}:SecretString:password}}",
          { Host: props.dbHost, Name: props.dbSecretName },
        ),
      },
      healthCheck: {
        command: ["CMD-SHELL", `curl -fsS http://127.0.0.1:${port}/health || exit 1`],
        interval: Duration.seconds(30),
        timeout: Duration.seconds(5),
        retries: 3,
        startPeriod: Duration.seconds(60),
      },
    });
    container.addPortMappings({ containerPort: port });
    if (props.observability) {
      const adotLogs = new LogGroup(this, "AdotLogs", {
        logGroupName: `/ofac/${stack.stackName}/adot`,
        retention: RetentionDays.TWO_YEARS,
      });
      taskDef.addContainer(
        "adot",
        adotContainerOptions(adotLogs, props.metricNamespace ?? DEFAULT_METRIC_NAMESPACE),
      );
    }
    // Task-role read on the DB secret by NAME pattern (plain strings —
    // an L2 import would plant a cross-stack Ref; the dynamic reference
    // itself is resolved by CloudFormation, this covers runtime seams).
    new Policy(this, "DbSecretPolicy", {
      roles: [taskDef.taskRole],
      statements: [
        new PolicyStatement({
          actions: ["secretsmanager:GetSecretValue"],
          resources: [
            `arn:aws:secretsmanager:${stack.region}:${stack.account}:secret:${props.dbSecretName}-*`,
          ],
        }),
      ],
    });
    if (props.dbEncryptionKeyArn) {
      new Policy(this, "DbSecretKeyPolicy", {
        roles: [taskDef.taskRole],
        statements: [
          new PolicyStatement({
            actions: ["kms:Decrypt", "kms:DescribeKey"],
            resources: [props.dbEncryptionKeyArn],
          }),
        ],
      });
    }
    // Least-privilege artifact access: this bucket's objects only.
    new Policy(this, "ArtifactsPolicy", {
      roles: [taskDef.taskRole],
      statements: [
        new PolicyStatement({
          resources: [props.artifactBucket.bucketArn, `${props.artifactBucket.bucketArn}/*`],
          actions: [
            "s3:GetObject",
            "s3:PutObject",
            "s3:DeleteObject",
            "s3:ListBucket",
            "s3:AbortMultipartUpload",
            "s3:CopyObject",
          ],
        }),
      ],
    });
    for (const statement of props.extraTaskPolicy ?? []) {
      taskDef.taskRole.addToPrincipalPolicy(statement);
    }
    const sg = new SecurityGroup(this, "ServiceSg", {
      vpc: props.vpc,
      description: "ofac kernel: ingress from the ALB only",
      allowAllOutbound: true,
    });
    // Exposed concretely so the STACK wires WebFace's SG as the sole
    // ingress source after both constructs exist (no construction cycle).
    this.securityGroup = sg;
    this.port = port;
    this.service = new FargateService(this, "Service", {
      cluster: props.cluster,
      taskDefinition: taskDef,
      desiredCount: desired,
      minHealthyPercent: 50,
      maxHealthyPercent: 200,
      circuitBreaker: { rollback: true },
      vpcSubnets: props.vpcSubnets,
      platformVersion: FargatePlatformVersion.LATEST,
      healthCheckGracePeriod: Duration.seconds(120),
      securityGroups: [sg],
    });
    const scalable = this.service.autoScaleTaskCount({
      minCapacity: desired,
      maxCapacity: 6,
    });
    scalable.scaleOnCpuUtilization("Cpu", { targetUtilizationPercent: 60 });
    scalable.scaleOnMemoryUtilization("Memory", { targetUtilizationPercent: 60 });
    this.targetGroup = new ApplicationTargetGroup(this, "Tg", {
      vpc: props.vpc,
      port,
      protocol: ApplicationProtocol.HTTP,
      targetType: TargetType.IP,
      healthCheck: {
        path: "/health",
        interval: Duration.seconds(30),
        timeout: Duration.seconds(10),
        healthyThresholdCount: 2,
        unhealthyThresholdCount: 3,
      },
    });
    this.service.attachToApplicationTargetGroup(this.targetGroup);
    Tags.of(this.service).add("ofac:role", "kernel");
    this.taskRole = taskDef.taskRole;
  }
}
