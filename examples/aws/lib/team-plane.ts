import { DEFAULT_METRIC_NAMESPACE, adotContainerOptions } from "./adot.js";
import { Duration, RemovalPolicy, Tags } from "aws-cdk-lib";
import {
  AwsLogDriver,
  ContainerImage,
  FargatePlatformVersion,
  FargateService,
  FargateTaskDefinition,
  Secret as EcsSecret,
} from "aws-cdk-lib/aws-ecs";
import type { ICluster } from "aws-cdk-lib/aws-ecs";
import { SecurityGroup } from "aws-cdk-lib/aws-ec2";
import type { IVpc, SubnetSelection } from "aws-cdk-lib/aws-ec2";
import { AccessPoint, FileSystem, LifecyclePolicy } from "aws-cdk-lib/aws-efs";
import type { IFileSystem } from "aws-cdk-lib/aws-efs";
import { PolicyStatement } from "aws-cdk-lib/aws-iam";
import { LogGroup, RetentionDays } from "aws-cdk-lib/aws-logs";
import { Secret } from "aws-cdk-lib/aws-secretsmanager";
import { Construct } from "constructs";

/**
 * TeamPlane: everything a second team runs — gateway + workspace services
 * as outbound-only Fargate tasks on the team cluster, plus the team EFS
 * volume. NO ingress at all: teams dial OUT to the platform kernel's web
 * face over the Link protocol (re-enroll on 401 is SDK law), so this
 * plane has no ALB, no SG ingress rule, and can live in any account/VPC
 * with working egress.
 *
 * Per-service EFS access points give each service a POSIX-owning root for
 * its connector SQLite state; workspace scratch dirs ride the same volume
 * under /workspaces (the Fleet's mount when the team opts in).
 */
export interface TeamServiceSpec {
  readonly imageUri: string;
  /** SQLite/state path mounted from the service's access point. */
  readonly statePath: string;
  /** POSIX owner (uid==gid) for the service's access point. */
  readonly uid: string;
}

export interface TeamPlaneProps {
  readonly vpc: IVpc;
  readonly vpcSubnets: SubnetSelection;
  readonly cluster: ICluster;
  /** Public URL of the platform kernel (`https://<kernelDomain>`). */
  readonly kernelUrl: string;
  readonly gateway: TeamServiceSpec;
  readonly workspace: TeamServiceSpec;
  /** Link credentials injected into every team task (complete ARNs). */
  readonly linkSecretArns: Record<string, string>;
  /** Prod posture: RETAIN the volume. */
  readonly production?: boolean;
  /** Fleet enabled for this team: create + admit the fleet task SG. */
  readonly fleetEnabled?: boolean;
  /** ADOT sidecar on every plane task (same shared shape as the kernel;
   *  the team account carries its own publish policy — no cross-account
   *  ref into the platform Observability stack). */
  readonly observability?: boolean;
  /** OTLP→CW namespace baked into the sidecar config (alarms in the
   *  team account read the same value when they are added). */
  readonly metricNamespace?: string;
}

export class TeamPlane extends Construct {
  readonly fileSystem: IFileSystem;
  readonly gatewayService: FargateService;
  readonly workspaceService: FargateService;
  /** Access point the Fleet (when enabled) mounts as /workspaces. */
  readonly fleetAccessPointId: string;
  /** Fleet task SG (created only when fleetEnabled): mount-target member. */
  readonly fleetSg?: SecurityGroup;

  constructor(scope: Construct, id: string, props: TeamPlaneProps) {
    super(scope, id);
    const prod = props.production ?? false;
    this.fileSystem = new FileSystem(this, "Workspaces", {
      vpc: props.vpc,
      vpcSubnets: props.vpcSubnets,
      encrypted: true,
      // IA lifecycle after 30 idle days: harness scratch goes cold fast.
      lifecyclePolicy: LifecyclePolicy.AFTER_30_DAYS,
      removalPolicy: prod ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
    });
    Tags.of(this.fileSystem).add("ofac:plane", "team");
    if (props.fleetEnabled) {
      // The fleet stack IMPORTS this plane, never the reverse: the SG its
      // launcher tasks wear is created here, on the volume's own stack.
      this.fleetSg = new SecurityGroup(this, "FleetSg", {
        vpc: props.vpc,
        allowAllOutbound: true,
        description: "ofac fleet launcher: egress-only",
      });
      this.fileSystem.connections.allowDefaultPortFrom(this.fleetSg);
    }

    // Outbound-only posture: egress everywhere, zero ingress rules.
    const sg = new SecurityGroup(this, "TasksSg", {
      vpc: props.vpc,
      allowAllOutbound: true,
      description: "ofac team tasks: egress-only (link plane dials out)",
    });
    const gwAp = new AccessPoint(this, "GatewayAp", {
      fileSystem: this.fileSystem,
      path: "/state/gateway",
      createAcl: { ownerUid: props.gateway.uid, ownerGid: props.gateway.uid, permissions: "700" },
      posixUser: { uid: props.gateway.uid, gid: props.gateway.uid },
    });
    const wsAp = new AccessPoint(this, "WorkspaceAp", {
      fileSystem: this.fileSystem,
      path: "/state/workspace",
      createAcl: {
        ownerUid: props.workspace.uid,
        ownerGid: props.workspace.uid,
        permissions: "700",
      },
      posixUser: { uid: props.workspace.uid, gid: props.workspace.uid },
    });
    this.fleetAccessPointId = new AccessPoint(this, "FleetAp", {
      fileSystem: this.fileSystem,
      path: "/workspaces",
      createAcl: { ownerUid: "1000", ownerGid: "1000", permissions: "775" },
      posixUser: { uid: "1000", gid: "1000" },
    }).accessPointId;

    const service = (name: string, spec: TeamServiceSpec, ap: AccessPoint): FargateService => {
      const logGroup = new LogGroup(this, `${name}Logs`, {
        retention: RetentionDays.ONE_YEAR,
      });
      const taskDef = new FargateTaskDefinition(this, `${name}Task`, {
        cpu: 1024,
        memoryLimitMiB: 2048,
      });
      const container = taskDef.addContainer(name.toLowerCase(), {
        image: ContainerImage.fromRegistry(spec.imageUri),
        logging: new AwsLogDriver({ logGroup, streamPrefix: name.toLowerCase() }),
        environment: {
          OFAC_KERNEL_URL: props.kernelUrl,
          STATE_PATH: spec.statePath,
        },
        secrets: Object.fromEntries(
          Object.entries(props.linkSecretArns).map(([k, arn]) => [
            k,
            EcsSecret.fromSecretsManager(
              Secret.fromSecretCompleteArn(this, `${name}Secret${k}`, arn),
            ),
          ]),
        ),
      });
      for (const [k, arn] of Object.entries(props.linkSecretArns)) {
        // The execution agent (not the app) resolves ECS secrets at start.
        Secret.fromSecretCompleteArn(taskDef, `${name}Grant${k}`, arn).grantRead(
          taskDef.executionRole!,
        );
      }
      taskDef.addVolume({
        name: "state",
        efsVolumeConfiguration: {
          fileSystemId: this.fileSystem.fileSystemId,
          transitEncryption: "ENABLED",
          authorizationConfig: { accessPointId: ap.accessPointId, iam: "ENABLED" },
        },
      });
      container.addMountPoints({
        sourceVolume: "state",
        containerPath: spec.statePath,
        readOnly: false,
      });
      // Mount+write through the access point; IAM auth keeps it to this role.
      this.fileSystem.grant(
        taskDef.taskRole,
        "elasticfilesystem:ClientMount",
        "elasticfilesystem:ClientWrite",
      );
      if (props.observability) {
        // Same shared sidecar shape as the kernel (adot.ts). Self-contained
        // publish identity in the TEAM account: write-only telemetry verbs,
        // account-scoped `*` (PutMetricData carries no resource ARN) — no
        // ref into the platform Observability stack.
        const adotLogs = new LogGroup(this, `${name}AdotLogs`, {
          retention: RetentionDays.ONE_YEAR,
        });
        taskDef.addContainer(
          "adot",
          adotContainerOptions(adotLogs, props.metricNamespace ?? DEFAULT_METRIC_NAMESPACE),
        );
        taskDef.taskRole.addToPrincipalPolicy(
          new PolicyStatement({
            actions: [
              "cloudwatch:PutMetricData",
              "logs:CreateLogStream",
              "logs:PutLogEvents",
              "logs:DescribeLogGroups",
              "logs:DescribeLogStreams",
              "logs:CreateLogGroup",
              "xray:PutTraceSegments",
              "xray:PutTelemetryRecords",
              "xray:GetSamplingRules",
              "xray:GetSamplingTargets",
            ],
            resources: ["*"],
          }),
        );
      }
      const svc = new FargateService(this, `${name}Service`, {
        cluster: props.cluster,
        taskDefinition: taskDef,
        desiredCount: 1,
        // Explicit rollout posture: one task may drain while the new one
        // comes up (the warning-default is otherwise implicit), and a
        // circuit breaker fails a bad deploy fast.
        minHealthyPercent: 0,
        maxHealthyPercent: 200,
        circuitBreaker: { rollback: true },
        vpcSubnets: props.vpcSubnets,
        platformVersion: FargatePlatformVersion.LATEST,
        securityGroups: [sg],
        healthCheckGracePeriod: Duration.minutes(2),
      });
      Tags.of(svc).add("ofac:plane", "team");
      return svc;
    };
    this.gatewayService = service("Gateway", props.gateway, gwAp);
    this.workspaceService = service("Workspace", props.workspace, wsAp);
  }
}
