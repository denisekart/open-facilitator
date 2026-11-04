import { Duration, Stack } from "aws-cdk-lib";
import { Schedule } from "aws-cdk-lib/aws-autoscaling";
import {
  AwsLogDriver,
  ContainerImage,
  FargatePlatformVersion,
  FargateService,
  FargateTaskDefinition,
  Secret as EcsSecret,
} from "aws-cdk-lib/aws-ecs";
import type { ICluster } from "aws-cdk-lib/aws-ecs";
import type { IVpc, SubnetSelection, ISecurityGroup } from "aws-cdk-lib/aws-ec2";
import { LogGroup, RetentionDays } from "aws-cdk-lib/aws-logs";
import { Policy, PolicyStatement } from "aws-cdk-lib/aws-iam";
import type { IFileSystem } from "aws-cdk-lib/aws-efs";
import type { ISecret } from "aws-cdk-lib/aws-secretsmanager";
import { Secret } from "aws-cdk-lib/aws-secretsmanager";
import { Tags } from "aws-cdk-lib";
import { Construct } from "constructs";

/** Secret references (complete ARNs) injected into launcher tasks as ECS secrets. */
export type LauncherSecrets = Record<string, string>;

/** Optional EFS workspace volume mounted at /workspaces in launcher tasks. */
export interface FleetVolume {
  readonly fileSystem: IFileSystem;
  readonly accessPointId: string;
}

export interface FleetProps {
  readonly vpc: IVpc;
  readonly vpcSubnets: SubnetSelection;
  readonly cluster: ICluster;
  /** Digest-pinned launcher image; the registry is opaque. */
  readonly launcherImage: string;
  readonly secrets: LauncherSecrets;
  /** Workspace scratch volume; omit for ephemeral-overlay launchers. */
  readonly efs?: FleetVolume;
  /** Tasks the fleet may run at once (per-workspace launcher task model). */
  readonly maxFleetSize?: number;
  /** Tasks kept warm regardless of schedule. */
  readonly minTasks?: number;
  /** Task SG (needed so the EFS mount target can admit exactly these tasks). */
  readonly securityGroup?: ISecurityGroup;
  /** Public URL of the platform kernel the launchers dial out to. */
  readonly kernelUrl: string;
}

/**
 * Launcher fleet: one Fargate task definition (the fleet-launcher image)
 * plus a baseline service so the family exists and capacity scales.
 * Real fan-out is the fleet-launcher backend itself: it calls ECS RunTask
 * per workspace rule against THIS task definition family, so the
 * permissions that matter are on the task/execution roles declared here.
 *
 * Scaling is schedule-shaped, not CPU-shaped: launcher load follows the
 * team's working hours, not the tasks' own CPU.
 */
export class Fleet extends Construct {
  readonly service: FargateService;
  readonly logGroup: LogGroup;

  constructor(scope: Construct, id: string, props: FleetProps) {
    super(scope, id);
    this.logGroup = new LogGroup(this, "Logs", {
      retention: RetentionDays.TWO_YEARS,
    });
    const taskDef = new FargateTaskDefinition(this, "TaskDef", {
      cpu: 1024,
      memoryLimitMiB: 2048,
    });
    const secrets: Record<string, ISecret> = Object.fromEntries(
      Object.entries(props.secrets).map(([name, arn]) => [
        name,
        Secret.fromSecretCompleteArn(this, `Ref${name}`, arn),
      ]),
    );
    const container = taskDef.addContainer("launcher", {
      image: ContainerImage.fromRegistry(props.launcherImage),
      logging: new AwsLogDriver({
        logGroup: this.logGroup,
        streamPrefix: "fleet",
      }),
      environment: {
        KERNEL_URL: props.kernelUrl,
        WORKSPACE_ROOT: "/workspaces",
      },
      secrets: Object.fromEntries(
        Object.entries(secrets).map(([name, secret]) => [
          name,
          EcsSecret.fromSecretsManager(secret),
        ]),
      ),
    });
    for (const secret of Object.values(secrets)) {
      // The execution agent (not the app) resolves ECS secrets at start.
      secret.grantRead(taskDef.executionRole!);
    }
    if (props.efs) {
      taskDef.addVolume({
        name: "workspaces",
        efsVolumeConfiguration: {
          fileSystemId: props.efs.fileSystem.fileSystemId,
          transitEncryption: "ENABLED",
          authorizationConfig: {
            accessPointId: props.efs.accessPointId,
            // IAM auth keeps the mount to tasks holding this role.
            iam: "ENABLED",
          },
        },
      });
      container.addMountPoints({
        sourceVolume: "workspaces",
        containerPath: "/workspaces",
        readOnly: false,
      });
      props.efs.fileSystem.grantRead(taskDef.taskRole);
      // grantRead omits ClientWrite; workspace dirs need write through the
      // access point, so the task role carries mount+write on the volume.
      props.efs.fileSystem.grant(
        taskDef.taskRole,
        "elasticfilesystem:ClientMount",
        "elasticfilesystem:ClientWrite",
      );
    }
    // Placement rides the cluster (Fargate); vpc/vpcSubnets props stay for
    // the security-group wiring the EFS mount target needs.
    this.service = new FargateService(this, "Baseline", {
      cluster: props.cluster,
      taskDefinition: taskDef,
      ...(props.securityGroup ? { securityGroup: props.securityGroup } : {}),
      platformVersion: FargatePlatformVersion.LATEST,
      desiredCount: props.minTasks ?? 0,
      // Same rollout posture as the plane services: drain one, roll fast.
      minHealthyPercent: 0,
      maxHealthyPercent: 200,
      circuitBreaker: { rollback: true },
      healthCheckGracePeriod: Duration.minutes(2),
    });
    const scalable = this.service.autoScaleTaskCount({
      minCapacity: props.minTasks ?? 0,
      maxCapacity: props.maxFleetSize ?? 10,
    });
    scalable.scaleOnSchedule("WorkHours", {
      // 07:00 weekdays UTC keeps a warm slot through the working day.
      schedule: Schedule.cron({ minute: "0", hour: "7", day: "MON-FRI" }),
      minCapacity: Math.max(1, props.minTasks ?? 0),
    });
    Tags.of(this.service).add("ofac:role", "fleet-launcher");
    // The launcher's fan-out law (non-production reference): RunTask on
    // THIS family only, PassRole only to this task def's own roles, and
    // stop/describe on the cluster for lifecycle. Anything broader lets a
    // compromised launcher task mint arbitrary workloads.
    taskDef.taskRole.attachInlinePolicy(
      new Policy(this, "RunTaskPolicy", {
        statements: [
          new PolicyStatement({
            actions: ["ecs:RunTask", "ecs:StopTask", "ecs:DescribeTasks"],
            resources: [
              taskDef.taskDefinitionArn,
              `arn:${Stack.of(this).partition}:ecs:${Stack.of(this).region}:${Stack.of(this).account}:task/${props.cluster.clusterName}/*`,
            ],
          }),
          new PolicyStatement({
            actions: ["iam:PassRole"],
            resources: [taskDef.taskRole.roleArn, taskDef.executionRole!.roleArn],
            conditions: {
              StringEquals: { "iam:PassedToService": "ecs-tasks.amazonaws.com" },
            },
          }),
        ],
      }),
    );
  }
}
